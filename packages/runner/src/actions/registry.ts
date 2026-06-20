import { join } from "node:path"
import { randomUUID } from "node:crypto"
import type { ActionContext, ActionResult, JsonObject, WorkItem } from "../core/types.js"
import { arrayInput, numberInput, objectInput, stringInput } from "../core/json.js"
import { deleteFile, exists, readText, runCommand, writeText } from "../system/process.js"
import { acpAgentAction } from "./acp-agent.js"
import { resolveActionPath } from "./expectations.js"
import { archiveChangeAction, openspecSyncAction, openspecTasksAction } from "./openspec.js"
import {
  buildCleanupWith,
  resolveMaxCleanupAttempts,
  type WorktreeSnapshot,
} from "../runtime/worktree-cleanup.js"
import {
  abortRebaseIfInProgressAction,
  applyWorkflowAgentDefault,
  combinedRebaseGitOutput,
  rebaseAction,
  rebaseConflictFiles,
  rebaseStatusAction,
  runRebaseResolverFollowup,
  runRebaseConflictResolver,
  verifyRebaseCompleteAction,
} from "./rebase.js"
import { git as defaultGit } from "./git.js"
import { LandingWorkspaceInfo, WorkspaceManager, defaultRunnerRoot } from "../runtime/workspace.js"

export type ActionHandler = (context: ActionContext) => Promise<ActionResult>
type GitRunner = typeof defaultGit
type ExistsChecker = typeof exists
type WorkLike = { variables?: JsonObject | null; workflowRunId?: string | null }

export interface DeliveryWorkspaceManager {
  createLandingWorkspace(work: WorkLike, signal: AbortSignal): Promise<LandingWorkspaceInfo>
  disposeLandingWorkspace(landing: LandingWorkspaceInfo | string, signal: AbortSignal): Promise<{ path: string; disposed: boolean; error?: string }>
}

let git: GitRunner = defaultGit
let pathExists: ExistsChecker = exists
let workspaceManager: DeliveryWorkspaceManager = new WorkspaceManager(defaultRunnerRoot())

export function setDeliveryGitRunnerForTest(runner: GitRunner | null) {
  git = runner ?? defaultGit
}

export function setDeliveryExistsCheckerForTest(checker: ExistsChecker | null) {
  pathExists = checker ?? exists
}

export function setDeliveryWorkspaceManagerForTest(manager: DeliveryWorkspaceManager | null) {
  workspaceManager = manager ?? new WorkspaceManager(defaultRunnerRoot())
}

export function getDeliveryWorkspaceManager(): WorkspaceManager {
  return workspaceManager as WorkspaceManager
}

export class ActionRegistry {
  private readonly actions = new Map<string, ActionHandler>()

  register(uses: string, handler: ActionHandler) {
    this.actions.set(uses.toLowerCase(), handler)
  }

  resolve(uses?: string | null) {
    if (!uses) return undefined
    return this.actions.get(uses.toLowerCase())
  }
}

export function createDefaultRegistry() {
  const registry = new ActionRegistry()
  registry.register("core/process", processAction)
  registry.register("core/script", scriptAction)
  registry.register("core/artifact-exists", artifactExistsAction)
  registry.register("core/marker", markerAction)
  registry.register("mohist/acp-agent", acpAgentAction)
  registry.register("mohist/openspec-tasks", openspecTasksAction)
  registry.register("mohist/openspec-sync", openspecSyncAction)
  registry.register("mohist/archive-change", archiveChangeAction)
  registry.register("mohist/rebase", rebaseAction)
  registry.register("mohist/rebase-status", rebaseStatusAction)
  registry.register("mohist/merge-ready", mergeReadyAction)
  registry.register("mohist/prepare", prepareAction)
  registry.register("mohist/publish", publishAction)
  return registry
}

async function processAction(context: ActionContext): Promise<ActionResult> {
  const command = context.uses === "core/process" ? stringInput(context.with, "command") : context.uses
  if (!command) return { status: "failure", message: "Process action requires command" }
  const result = await runCommand(command, arrayInput(context.with, "args").map(String), context.workDir, context.signal)
  return result.exitCode === 0
    ? { status: "success", message: "Process completed", output: result.stdout.trim(), exitCode: result.exitCode }
    : { status: "failure", message: result.stderr.trim() || `Process exited with code ${result.exitCode}`, output: result.stdout.trim(), exitCode: result.exitCode }
}

async function scriptAction(context: ActionContext): Promise<ActionResult> {
  const run = stringInput(context.with, "run")
  if (!run?.trim()) return { status: "failure", message: "Script action requires 'run'" }
  const shell = stringInput(context.with, "shell") || (process.platform === "win32" ? "pwsh" : "bash")
  const file = join(context.workDir, `_${randomUUID().replace(/-/g, "")}${process.platform === "win32" ? ".ps1" : ".sh"}`)
  await writeText(file, run)
  try {
    const timeoutMs = numberInput(context.with, "timeout")
    const signal = timeoutMs ? timeoutSignal(context.signal, timeoutMs) : context.signal
    const result = await runCommand(shell, [file], context.workDir, signal)
    return {
      status: result.exitCode === 0 ? "success" : "failure",
      message: result.exitCode === 0 ? "Script completed" : `Script failed: ${firstLine(run)}`,
      output: JSON.stringify({ kind: "script", run, shell, exitCode: result.exitCode, stdout: trim(result.stdout), stderr: trim(result.stderr) }),
      exitCode: result.exitCode,
    }
  } finally {
    await deleteFile(file)
  }
}

async function artifactExistsAction(context: ActionContext): Promise<ActionResult> {
  const path = resolveActionPath(context, stringInput(context.with, "path"))
  if (!path) return { status: "failure", message: "Artifact check requires 'path'" }
  const found = exists(path)
  const output = JSON.stringify({ kind: "artifact-exists", path, exists: found })
  return found ? { status: "success", message: `Artifact exists: ${path}`, output } : { status: "failure", message: `Artifact missing: ${path}`, output }
}

async function markerAction(context: ActionContext): Promise<ActionResult> {
  const path = resolveActionPath(context, stringInput(context.with, "path"))
  const expect = stringInput(context.with, "expect") ?? stringInput(context.with, "contains")
  if (!path || !expect) return { status: "failure", message: "Marker check requires 'path' and 'expect'" }
  if (!exists(path)) return { status: "failure", message: `Marker file missing: ${path}` }
  const content = await readText(path)
  const found = matchesMarker(content, expect)
  const output = JSON.stringify({ kind: "marker", path, marker: expect, found })
  return found ? { status: "success", message: `Marker found in ${path}`, output } : { status: "failure", message: `Marker missing in ${path}`, output }
}

function matchesMarker(content: string, expect: string) {
  if (isPromiseVerdict(expect)) {
    const verdicts = [...content.matchAll(/<promise>\s*(PASS|FAIL)\s*<\/promise>/g)].map((match) => `<promise>${match[1]}</promise>`)
    return verdicts.length === 1 && verdicts[0] === expect
  }

  return content.includes(expect)
}

function isPromiseVerdict(value: string) {
  return /^<promise>\s*(PASS|FAIL)\s*<\/promise>$/.test(value)
}

export async function mergeReadyAction(context: ActionContext): Promise<ActionResult> {
  const baseBranch = stringInput(context.with, "baseBranch") ?? stringAt(context.variables, ["repository", "baseBranch"]) ?? stringAt(context.variables, ["project", "defaultBranch"]) ?? stringAt(context.variables, ["project", "baseBranch"]) ?? "main"
  const source = stringInput(context.with, "source") ?? "HEAD"
  const workDir = stringAt(context.variables, ["project", "path"]) ?? context.workDir

  // The merge-ready preflight is ref-safe: read-only `rev-parse` and
  // `merge-base` against the workflow workspace, then run the actual
  // `merge --squash --no-commit` probe in an isolated landing workspace
  // so the workflow workspace never leaves `workspace.branch`.
  const base = await git(workDir, ["rev-parse", baseBranch], context.signal)
  if (!base.success) return mergeReadyResult(false, baseBranch, null, null, null, `Could not resolve base branch '${baseBranch}'`, base.exitCode, [], new Date().toISOString())

  const head = await git(workDir, ["rev-parse", source], context.signal)
  if (!head.success) return mergeReadyResult(false, baseBranch, base.stdout.trim(), null, null, "Could not resolve source", head.exitCode, [], new Date().toISOString())

  const mergeBase = await git(workDir, ["merge-base", baseBranch, source], context.signal)
  const checkedAt = new Date().toISOString()
  const preflight = await runSquashMergePreflight(buildPublishWorkItem(context), baseBranch, source, context.signal)

  return mergeReadyResult(
    preflight.canMerge,
    baseBranch,
    base.stdout.trim(),
    head.stdout.trim(),
    mergeBase.success ? mergeBase.stdout.trim() : null,
    preflight.error,
    preflight.exitCode,
    preflight.conflictFiles,
    checkedAt,
  )
}

export async function prepareAction(context: ActionContext): Promise<ActionResult> {
  const baseBranch = stringInput(context.with, "baseBranch") ?? "main"
  const remote = stringInput(context.with, "remote") ?? "origin"
  const maxRetries = numberInput(context.with, "maxConflictRetries") ?? 3
  const conflictResolver = objectInput(context.with, "conflictResolver")
  const workDir = context.workDir

  const abortResult = await abortRebaseIfInProgressAction(context)
  if (!abortResult.success) {
    return prepareOutput(false, baseBranch, null, null, [], 0, abortResult.combinedOutput, "retry-safe", abortResult.exitCode)
  }

  const initialStatus = await git(workDir, ["status", "--porcelain"], context.signal)
  if (!initialStatus.success) {
    return prepareOutput(false, baseBranch, null, null, [], 0, initialStatus.combinedOutput, "retry-safe", initialStatus.exitCode)
  }
  if (initialStatus.stdout.trim()) {
    if (isOnlyUntracked(initialStatus.stdout)) {
      const clean = await cleanUntrackedWorktree(context, initialStatus.stdout, "Prepare auto-cleaned untracked files before rebase:")
      if (clean.ok) {
        return await prepareAfterInitialClean(context, baseBranch, remote, maxRetries, conflictResolver ?? null, clean.output)
      }
      return prepareDirtyOutput(baseBranch, null, null, [], 0, clean.output, "Prepare aborted: worktree is dirty before rebase. Commit or clean the workspace before retrying.")
    }
    if (conflictResolver) {
      const cleanup = await prepareResolverCleanup(
        context,
        conflictResolver,
        [],
        0,
        "before-rebase",
      )
      const output = [initialStatus.stdout, ...cleanup.outputs].filter(Boolean).join("\n\n")
      if (cleanup.ok) {
        return await prepareAfterInitialClean(context, baseBranch, remote, maxRetries, conflictResolver, output)
      }
      return prepareDirtyOutput(baseBranch, null, null, [], 0, output, cleanup.message ?? "Prepare aborted: worktree is dirty before rebase. Commit or clean the workspace before retrying.")
    }
    return prepareDirtyOutput(baseBranch, null, null, [], 0, initialStatus.stdout, "Prepare aborted: worktree is dirty before rebase. Commit or clean the workspace before retrying.")
  }

  return await prepareAfterInitialClean(context, baseBranch, remote, maxRetries, conflictResolver ?? null, "")
}

async function prepareAfterInitialClean(
  context: ActionContext,
  baseBranch: string,
  remote: string,
  maxRetries: number,
  conflictResolver: JsonObject | null,
  initialOutput: string,
): Promise<ActionResult> {
  const workDir = context.workDir

  const fetch = await git(workDir, ["fetch", remote, baseBranch], context.signal)
  if (!fetch.success) {
    return prepareOutput(false, baseBranch, null, null, [], 0, fetch.combinedOutput, "retry-safe", fetch.exitCode)
  }

  const baseRef = `${remote}/${baseBranch}`
  const baseShaResult = await git(workDir, ["rev-parse", baseRef], context.signal)
  const preparedBaseSha = baseShaResult.success ? baseShaResult.stdout.trim() : null
  if (!baseShaResult.success) {
    return prepareOutput(false, baseBranch, preparedBaseSha, null, [], 0, baseShaResult.combinedOutput, "retry-safe", baseShaResult.exitCode)
  }

  const before = await git(workDir, ["rev-parse", "HEAD"], context.signal)
  const beforeSha = before.success ? before.stdout.trim() : null

  const rebaseResult = await git(workDir, ["rebase", baseRef], context.signal)
  const rebaseOutput = [initialOutput, rebaseResult.combinedOutput].filter(Boolean).join("\n\n")
  if (rebaseResult.success) {
    const after = await git(workDir, ["rev-parse", "HEAD"], context.signal)
    const preparedHeadSha = after.success ? after.stdout.trim() : null
    const clean = await prepareCleanWorktreeResult(context, baseBranch, preparedBaseSha, preparedHeadSha, [], 0, rebaseOutput, null)
    if (clean) return clean
    return prepareOutput(true, baseBranch, preparedBaseSha, preparedHeadSha, [], 0, rebaseOutput, undefined)
  }

  let conflicts = await rebaseConflictFiles(context)
  if (conflicts.length === 0) {
    await git(workDir, ["rebase", "--abort"], context.signal)
    return prepareOutput(false, baseBranch, preparedBaseSha, null, [], 0, rebaseOutput, "retry-safe", rebaseResult.exitCode)
  }

  if (!conflictResolver) {
    await git(workDir, ["rebase", "--abort"], context.signal)
    return prepareOutput(false, baseBranch, preparedBaseSha, null, conflicts, 0, rebaseResult.combinedOutput, "conflict", 1)
  }

  const allConflicts: string[][] = [conflicts]
  const gitOutputs: string[] = [initialOutput, rebaseResult.combinedOutput].filter(Boolean)
  let attempts = 0

  while (attempts < maxRetries) {
    attempts++
    const agentResult = await runRebaseConflictResolver(context, conflictResolver, conflicts, baseBranch, attempts)
    if (agentResult.output) gitOutputs.push(agentResult.output)
    if (agentResult.status !== "success") {
      await git(workDir, ["rebase", "--abort"], context.signal)
      return prepareOutput(false, baseBranch, preparedBaseSha, null, conflicts, attempts, combinedRebaseGitOutput(gitOutputs), "conflict", 1)
    }

    const verified = await verifyRebaseCompleteAction(context, baseRef)
    gitOutputs.push(verified.output)
    if (verified.ok) {
      const after = await git(workDir, ["rev-parse", "HEAD"], context.signal)
      const preparedHeadSha = after.success ? after.stdout.trim() : null
      const cleanup = await prepareResolverCleanup(
        context,
        conflictResolver,
        allConflicts.flat(),
        attempts,
        "after-rebase",
      )
      gitOutputs.push(...cleanup.outputs)
      if (!cleanup.ok) {
        return prepareDirtyOutput(
          baseBranch,
          preparedBaseSha,
          preparedHeadSha,
          allConflicts.flat(),
          attempts,
          combinedRebaseGitOutput(gitOutputs),
          cleanup.message ?? "Prepare failed: worktree remained dirty after rebase.",
        )
      }
      return prepareOutput(true, baseBranch, preparedBaseSha, preparedHeadSha, allConflicts.flat(), attempts, combinedRebaseGitOutput(gitOutputs), undefined)
    }

    conflicts = await rebaseConflictFiles(context)
    if (conflicts.length > 0) allConflicts.push(conflicts)
  }

  await git(workDir, ["rebase", "--abort"], context.signal)
  return prepareOutput(false, baseBranch, preparedBaseSha, null, allConflicts.flat(), attempts, combinedRebaseGitOutput(gitOutputs), "conflict", 1)
}

async function prepareCleanWorktreeResult(
  context: ActionContext,
  baseBranch: string,
  preparedBaseSha: string | null,
  preparedHeadSha: string | null,
  conflicts: string[],
  resolveAttempts: number,
  gitOutput: string,
  dirtyMessage: string | null,
): Promise<ActionResult | null> {
  const status = await git(context.workDir, ["status", "--porcelain"], context.signal)
  if (!status.success) {
    return prepareOutput(false, baseBranch, preparedBaseSha, preparedHeadSha, conflicts, resolveAttempts, status.combinedOutput, "retry-safe", status.exitCode)
  }
  if (!status.stdout.trim()) return null
  if (isOnlyUntracked(status.stdout)) {
    const cleaned = await cleanUntrackedWorktree(context, status.stdout, "Prepare auto-cleaned untracked files left after rebase:")
    const output = [
      gitOutput,
      cleaned.output,
    ].filter(Boolean).join("\n\n")
    if (!cleaned.ok) {
      return prepareOutput(false, baseBranch, preparedBaseSha, preparedHeadSha, conflicts, resolveAttempts, output, "retry-safe", cleaned.exitCode)
    }
    return prepareOutput(true, baseBranch, preparedBaseSha, preparedHeadSha, conflicts, resolveAttempts, output, undefined)
  }
  const output = [gitOutput, "Prepare left a dirty worktree after rebase:", status.stdout.trim()].filter(Boolean).join("\n\n")
  return prepareDirtyOutput(baseBranch, preparedBaseSha, preparedHeadSha, conflicts, resolveAttempts, output, dirtyMessage ?? "Prepare failed: worktree remained dirty after rebase.")
}

async function prepareResolverCleanup(
  context: ActionContext,
  conflictResolver: JsonObject,
  conflicts: string[],
  resolveAttempts: number,
  phase: "before-rebase" | "after-rebase",
): Promise<{ ok: boolean; outputs: string[]; message: string | null }> {
  const outputs: string[] = []
  let snapshot = await prepareWorktreeSnapshot(context)
  if (snapshot.kind === "error") {
    return {
      ok: false,
      outputs: [snapshot.output],
      message: phase === "before-rebase"
        ? "Prepare failed: could not inspect dirty worktree before rebase."
        : "Prepare failed: could not inspect worktree after rebase conflict resolution.",
    }
  }
  if (snapshot.snapshot.isClean) return { ok: true, outputs, message: null }

  const maxCleanupAttempts = resolveMaxCleanupAttempts(context.variables)
  let cleanupAttempts = 0
  while (!snapshot.snapshot.isClean && cleanupAttempts < maxCleanupAttempts) {
    cleanupAttempts += 1
    const cleanupWork = prepareConflictCleanupWorkItem(context, resolveAttempts, phase)
    const cleanupWith = buildCleanupWith(cleanupWork, {
      prompt: prepareConflictCleanupBasePrompt(conflicts, phase),
      ...objectInput(conflictResolver, "with"),
    }, snapshot.snapshot, cleanupAttempts)
    cleanupWith["session"] = prepareConflictResolverSession(context, conflictResolver, resolveAttempts)
    applyWorkflowAgentDefault(cleanupWith, context.variables)
    const cleanupResult = await runRebaseResolverFollowup(
      context,
      stringInput(conflictResolver, "title") ?? "Clean up rebase conflict resolution",
      cleanupWith,
      `conflict-cleanup-${resolveAttempts}-${cleanupAttempts}`,
    )
    if (cleanupResult.output) outputs.push(cleanupResult.output)
    if (cleanupResult.status !== "success") {
      return {
        ok: false,
        outputs,
        message: `Prepare failed: conflict resolver cleanup attempt ${cleanupAttempts} failed: ${cleanupResult.message ?? cleanupResult.status}`,
      }
    }

    snapshot = await prepareWorktreeSnapshot(context)
    if (snapshot.kind === "error") {
      return {
        ok: false,
        outputs: [...outputs, snapshot.output],
        message: "Prepare failed: could not inspect worktree after prepare cleanup.",
      }
    }
  }

  if (snapshot.snapshot.isClean) {
    if (cleanupAttempts > 0) outputs.push(`Prepare resolver cleaned ${phase === "before-rebase" ? "pre-rebase" : "post-rebase"} worktree after ${cleanupAttempts} cleanup attempt(s).`)
    return { ok: true, outputs, message: null }
  }

  outputs.push(formatPrepareDirtySnapshot("Prepare resolver left a dirty worktree after cleanup:", snapshot.snapshot))
  return {
    ok: false,
    outputs,
    message: `Prepare failed: worktree remained dirty after ${cleanupAttempts} conflict resolver cleanup attempt(s).`,
  }
}

async function prepareWorktreeSnapshot(context: ActionContext): Promise<
  | { kind: "ok"; snapshot: WorktreeSnapshot }
  | { kind: "error"; output: string; exitCode: number }
> {
  const status = await git(context.workDir, ["status", "--porcelain"], context.signal)
  if (!status.success) {
    return {
      kind: "error",
      output: status.combinedOutput || `git status --porcelain failed with exit ${status.exitCode}`,
      exitCode: status.exitCode,
    }
  }
  return { kind: "ok", snapshot: parsePorcelainSnapshot(status.stdout) }
}

function parsePorcelainSnapshot(status: string): WorktreeSnapshot {
  const staged: string[] = []
  const unstaged: string[] = []
  const untracked: string[] = []

  for (const rawLine of status.split(/\r?\n/)) {
    if (!rawLine.trim()) continue
    if (rawLine.startsWith("?? ")) {
      untracked.push(rawLine.slice(3).trim())
      continue
    }
    const indexStatus = rawLine[0]
    const worktreeStatus = rawLine[1]
    const path = rawLine.slice(3).trim()
    if (!path) continue
    if (indexStatus !== " " && indexStatus !== "?") staged.push(path)
    if (worktreeStatus !== " " && worktreeStatus !== "?") unstaged.push(path)
  }

  return {
    staged: [...new Set(staged)],
    unstaged: [...new Set(unstaged)],
    untracked: [...new Set(untracked)],
    isClean: staged.length === 0 && unstaged.length === 0 && untracked.length === 0,
  }
}

function prepareConflictCleanupWorkItem(context: ActionContext, resolveAttempts: number, phase: "before-rebase" | "after-rebase"): WorkItem {
  return {
    workflowRunId: context.workflowRunId,
    workId: `${context.workId}-${phase}-cleanup-${resolveAttempts}`,
    workType: "task",
    stage: context.stage,
    title: phase === "before-rebase" ? "Clean up prepare retry worktree" : "Clean up rebase conflict resolution",
    uses: "mohist/acp-agent",
    with: null,
    variables: context.variables,
    projectId: context.projectId,
    issueNumber: context.issueNumber,
  }
}

function prepareConflictResolverSession(context: ActionContext, conflictResolver: JsonObject, resolveAttempts: number) {
  return stringInput(objectInput(conflictResolver, "with"), "session") ?? `${context.workId}-conflict-resolve-${resolveAttempts}`
}

function prepareConflictCleanupBasePrompt(conflicts: string[], phase: "before-rebase" | "after-rebase") {
  const list = conflicts.length > 0
    ? conflicts.map((file) => `- ${file}`).join("\n")
    : "- (no conflicted files recorded)"
  return [
    phase === "before-rebase"
      ? "The prepare action is retrying, but the worktree already contains uncommitted changes from an earlier prepare/rebase attempt."
      : "The rebase conflict resolver completed and `git rebase` is no longer in progress, but the prepare action found uncommitted worktree changes.",
    phase === "before-rebase"
      ? "Clean up only the leftover changes from the earlier prepare attempt before the runner starts a new rebase."
      : "Clean up only the leftover changes from the rebase conflict resolution.",
    "",
    "Original conflict files:",
    list,
  ].join("\n")
}

function formatPrepareDirtySnapshot(label: string, snapshot: WorktreeSnapshot) {
  return [
    label,
    `Staged:\n${formatPrepareFileList(snapshot.staged)}`,
    `Unstaged:\n${formatPrepareFileList(snapshot.unstaged)}`,
    `Untracked:\n${formatPrepareFileList(snapshot.untracked)}`,
  ].join("\n")
}

function formatPrepareFileList(files: string[]) {
  return files.length === 0 ? "- (none)" : files.map((file) => `- ${file}`).join("\n")
}

function isOnlyUntracked(status: string) {
  const lines = status.split(/\r?\n/).map((line) => line.trim()).filter(Boolean)
  return lines.length > 0 && lines.every((line) => line.startsWith("?? "))
}

async function cleanUntrackedWorktree(context: ActionContext, status: string, label: string) {
  const dirty = status.trim()
  const clean = await git(context.workDir, ["clean", "-fd"], context.signal)
  const recheck = clean.success
    ? await git(context.workDir, ["status", "--porcelain"], context.signal)
    : clean
  const recheckDirty = recheck.stdout.trim()
  const output = [
    label,
    dirty,
    clean.combinedOutput,
    recheckDirty ? `Worktree remained dirty after auto-clean:\n${recheckDirty}` : "",
  ].filter(Boolean).join("\n\n")
  return {
    ok: clean.success && recheck.success && !recheckDirty,
    output,
    exitCode: clean.success ? recheck.exitCode : clean.exitCode,
  }
}

function prepareOutput(
  prepared: boolean,
  baseBranch: string,
  preparedBaseSha: string | null,
  preparedHeadSha: string | null,
  conflicts: string[],
  resolveAttempts: number,
  gitOutput: string,
  failureKind: "conflict" | "retry-safe" | undefined,
  exitCode: number | null = null,
  failureMessage: string | null = null,
): ActionResult {
  // Schema convention: `failureKind` is always present (null on success).
  // Downstream renderers (CLI DeliveryFailureGuidance, web delivery-failure.ts)
  // detect the kind from the JSON `failureKind` field first and fall back to
  // parsing the human-readable message. The closed set is `conflict` and
  // `retry-safe` for prepare; `base-moved` and `retry-safe` for publish.
  const output = JSON.stringify({
    kind: "prepare",
    status: prepared ? "completed" : "failed",
    baseBranch,
    preparedBaseSha,
    preparedHeadSha,
    prepared,
    conflicts,
    resolveAttempts,
    failureKind: failureKind ?? null,
    output: gitOutput,
  })
  return prepared
    ? { status: "success", message: "Prepare completed", output }
    : { status: "failure", message: failureMessage ?? `Prepare failed${failureKind ? ` (${failureKind})` : ""}: ${gitOutput || "unknown error"}`, output, exitCode: exitCode ?? 1 }
}

function prepareDirtyOutput(
  baseBranch: string,
  preparedBaseSha: string | null,
  preparedHeadSha: string | null,
  conflicts: string[],
  resolveAttempts: number,
  gitOutput: string,
  message: string,
): ActionResult {
  return prepareOutput(false, baseBranch, preparedBaseSha, preparedHeadSha, conflicts, resolveAttempts, gitOutput, "retry-safe", 1, message)
}

export async function publishAction(context: ActionContext): Promise<ActionResult> {
  const source = stringInput(context.with, "source") ?? "HEAD"
  const target = stringInput(context.with, "target") ?? stringAt(context.variables, ["project", "baseBranch"]) ?? stringAt(context.variables, ["project", "defaultBranch"]) ?? "main"
  const message = stringInput(context.with, "message") ?? `Complete issue #${context.issueNumber ?? ""}`.trim()
  const remote = stringInput(context.with, "remote") ?? "origin"
  const workDir = stringAt(context.variables, ["project", "path"]) ?? context.workDir
  const remoteTarget = `${remote}/${target}`

  // Step 1: workflow workspace is read-only. The base-branch landing
  // commit is built in an isolated landing workspace created by
  // WorkspaceManager so the workflow workspace stays on
  // `workspace.branch` for the entire publish task.
  const sourceResolve = await git(workDir, ["rev-parse", source], context.signal)
  if (!sourceResolve.success) {
    return publishOutput(false, source, target, workDir, null, false, sourceResolve.combinedOutput, "retry-safe", sourceResolve.exitCode)
  }

  // Step 2: create an isolated landing workspace clone from the workflow
  // workspace. Its `origin` is reset to the configured gitUrl so push
  // targets the real upstream. The clone's object store shares the
  // workflow workspace's objects via alternates (read-only), so removing
  // the landing directory cannot corrupt the workflow workspace.
  let landing: LandingWorkspaceInfo | null = null
  try {
    landing = await workspaceManager.createLandingWorkspace(buildPublishWorkItem(context), context.signal)
  } catch (err) {
    const messageText = err instanceof Error ? err.message : String(err)
    return publishOutput(false, source, target, workDir, null, false, `Publish aborted: failed to create isolated landing workspace: ${messageText}`, "retry-safe", 1)
  }

  try {
    return await publishInLandingWorkspace(context, workDir, landing, source, target, remote, remoteTarget, message)
  } finally {
    if (landing) await workspaceManager.disposeLandingWorkspace(landing, context.signal)
  }
}

async function publishInLandingWorkspace(
  context: ActionContext,
  workDir: string,
  landing: LandingWorkspaceInfo,
  source: string,
  target: string,
  remote: string,
  remoteTarget: string,
  message: string,
): Promise<ActionResult> {
  const landingDir = landing.path
  const signal = context.signal

  const fetch = await git(landingDir, ["fetch", remote, target], signal)
  if (!fetch.success) {
    return publishOutput(false, source, target, workDir, null, false, fetch.combinedOutput, "retry-safe", fetch.exitCode)
  }

  const remoteHead = await git(landingDir, ["rev-parse", remoteTarget], signal)
  if (!remoteHead.success) {
    return publishOutput(false, source, target, workDir, null, false, remoteHead.combinedOutput, "retry-safe", remoteHead.exitCode)
  }
  let restoreSha = remoteHead.stdout.trim()

  // The landing clone can carry stale local target refs from an older
  // workflow workspace. Anchor the disposable landing branch directly to
  // the fetched remote target before creating the squash commit.
  const checkout = await git(landingDir, ["checkout", "-B", target, remoteTarget], signal)
  if (!checkout.success) {
    const rebaseMerge = await git(landingDir, ["rev-parse", "--git-path", "rebase-merge"], signal)
    const rebaseApply = await git(landingDir, ["rev-parse", "--git-path", "rebase-apply"], signal)
    const mergeHead = await git(landingDir, ["rev-parse", "--git-path", "MERGE_HEAD"], signal)
    if (
      (rebaseMerge.success && pathExists(resolveGitDirPath(landingDir, rebaseMerge.stdout.trim())))
      || (rebaseApply.success && pathExists(resolveGitDirPath(landingDir, rebaseApply.stdout.trim())))
    ) {
      await git(landingDir, ["rebase", "--abort"], signal)
    } else if (mergeHead.success && pathExists(resolveGitDirPath(landingDir, mergeHead.stdout.trim()))) {
      await git(landingDir, ["merge", "--abort"], signal)
    }
    await git(landingDir, ["reset", "--hard", restoreSha], signal)
    return publishOutput(false, source, target, workDir, null, false, checkout.combinedOutput, "retry-safe", checkout.exitCode)
  }

  const status = await git(landingDir, ["status", "--porcelain"], signal)
  if (status.success && status.stdout.trim()) {
    const dirty = status.stdout.trim()
    await git(landingDir, ["reset", "--hard", restoreSha], signal)
    return publishOutput(
      false,
      source,
      target,
      workDir,
      null,
      false,
      `Publish aborted: target branch '${target}' had a dirty working tree in the landing workspace. Discarded untracked/user-modified files:\n${dirty}`,
      "retry-safe",
      status.exitCode,
    )
  }

  const sourceContainsTarget = await git(landingDir, ["merge-base", "--is-ancestor", remoteTarget, source], signal)
  if (!sourceContainsTarget.success) {
    await git(landingDir, ["reset", "--hard", restoreSha], signal)
    return publishOutput(
      false,
      source,
      target,
      workDir,
      null,
      false,
      `Publish aborted: source '${source}' is not prepared against latest '${remoteTarget}'. Re-run prepare before publishing.`,
      "base-moved",
      sourceContainsTarget.exitCode,
    )
  }

  const squash = await git(landingDir, ["merge", "--squash", source], signal)
  if (!squash.success) {
    await git(landingDir, ["merge", "--abort"], signal)
    await git(landingDir, ["reset", "--hard", restoreSha], signal)
    return publishOutput(false, source, target, workDir, null, false, squash.combinedOutput, "base-moved", squash.exitCode)
  }

  const commitMessage = buildPublishCommitMessage(message, landingDir, source, target, context)
  const commit = await git(landingDir, ["commit", ...commitMessage], signal)
  if (!commit.success) {
    await git(landingDir, ["reset", "--hard", restoreSha], signal)
    return publishOutput(false, source, target, workDir, null, false, commit.combinedOutput, "retry-safe", commit.exitCode)
  }

  const head = await git(landingDir, ["rev-parse", "HEAD"], signal)
  const landedCommit = head.success ? head.stdout.trim() : null

  const push = await git(landingDir, ["push", remote, target], signal)
  if (!push.success) {
    await git(landingDir, ["reset", "--hard", restoreSha], signal)
    const failureKind = looksLikeNonFastForward(push.combinedOutput) ? "base-moved" : "retry-safe"
    return publishOutput(false, source, target, workDir, landedCommit, false, push.combinedOutput, failureKind, push.exitCode)
  }

  return publishOutput(true, source, target, workDir, landedCommit, true, push.combinedOutput, undefined, push.exitCode)
}

function buildPublishWorkItem(context: ActionContext): WorkItem {
  return {
    workflowRunId: context.workflowRunId,
    workId: context.workId,
    workType: context.workType,
    stage: context.stage ?? null,
    title: context.title ?? null,
    uses: context.uses ?? null,
    with: context.with ?? null,
    variables: context.variables,
    projectId: context.projectId ?? null,
    issueNumber: context.issueNumber ?? null,
  }
}

function publishOutput(
  published: boolean,
  source: string,
  target: string,
  workDir: string,
  landedCommit: string | null,
  pushed: boolean,
  gitOutput: string,
  failureKind: "base-moved" | "retry-safe" | undefined,
  exitCode: number,
): ActionResult {
  // Schema convention: `failureKind` is always present (null on success).
  // Downstream renderers (CLI DeliveryFailureGuidance, web delivery-failure.ts)
  // detect the kind from the JSON `failureKind` field first and fall back to
  // parsing the human-readable message. Keeping `null` on success lets the
  // resolvers treat success and unknown-failure uniformly.
  const output = JSON.stringify({
    kind: "publish",
    status: published ? "completed" : "failed",
    source,
    target,
    workDir,
    landedCommit,
    pushed,
    failureKind: failureKind ?? null,
    output: gitOutput,
  })
  return published
    ? { status: "success", message: "Publish completed", output, exitCode }
    : { status: "failure", message: `Publish failed${failureKind ? ` (${failureKind})` : ""}: ${gitOutput || "unknown error"}`, output, exitCode: exitCode || 1 }
}

function buildPublishCommitMessage(message: string, workDir: string, source: string, target: string, context: ActionContext) {
  const numberStr = typeof context.issueNumber === "number" && context.issueNumber > 0
    ? String(context.issueNumber)
    : numberAtString(context.variables, ["issue", "number"])
  const title = stringAt(context.variables, ["issue", "title"]) ?? message
  const header = numberStr ? `${title} (#${numberStr})` : title
  return ["-m", header, "-m", `${source} into ${target}`]
}

function looksLikeNonFastForward(text: string) {
  // Match git's actual push-rejection shapes so transient network/auth errors
  // do not get mis-classified as base-moved. Real non-fast-forward messages
  // contain either `! [rejected]` followed by a hint in parens, or an
  // explicit "non-fast-forward" / "fetch first" hint.
  return /non[-\s]?fast-forward|fetch first/i.test(text)
    || /!\s*\[rejected\][^\n]*\((stale info|stale|fetch first|non[-\s]?fast-forward|behind[^\)]*)\)/i.test(text)
}

function resolveGitDirPath(workDir: string, path: string) {
  return path.match(/^[A-Za-z]:[\\/]|^\//) ? path : join(workDir, path)
}

function mergeReadyResult(canMerge: boolean, baseBranch: string, baseSha: string | null, headSha: string | null, mergeBaseSha: string | null, error: string | null, exitCode: number | null, conflictFiles: string[], checkedAt: string): ActionResult {
  const output = JSON.stringify({ kind: "merge-ready", targetBranch: baseBranch, strategy: "squash", baseSha: baseSha ?? "", candidateHeadSha: headSha ?? "", mergeBaseSha: mergeBaseSha ?? "", canMerge, conflictFiles, checkedAt, error })
  return canMerge ? { status: "success", message: "Merge ready", output, exitCode } : { status: "failure", message: error ?? "Merge is not ready", output, exitCode }
}

async function runSquashMergePreflight(work: WorkLike, target: string, source: string, signal: AbortSignal): Promise<{ canMerge: boolean; conflictFiles: string[]; error: string | null; exitCode: number | null }> {
  // Ref-safe preflight: the workflow workspace never has its branch
  // switched. The `merge --squash --no-commit` probe runs against an
  // isolated landing workspace that is created from the workflow
  // workspace's refs and disposed when the probe finishes. The
  // structured output (`canMerge`, `conflictFiles`, etc.) is preserved.
  let landing: LandingWorkspaceInfo | null = null
  try {
    landing = await workspaceManager.createLandingWorkspace(work, signal)
  } catch (err) {
    const messageText = err instanceof Error ? err.message : String(err)
    return { canMerge: false, conflictFiles: [], error: `Merge-ready preflight aborted: failed to create isolated landing workspace: ${messageText}`, exitCode: 1 }
  }

  try {
    return await runSquashMergePreflightInLanding(landing.path, target, source, signal)
  } finally {
    // Best-effort dispose. A dispose failure does not flip a detected
    // conflict into a passing result: we already captured the merge
    // outcome above. The landing dir is an isolated clone of the
    // workflow workspace, so leaving it behind cannot affect the
    // workflow workspace's branch or working tree.
    await workspaceManager.disposeLandingWorkspace(landing, signal)
  }
}

async function runSquashMergePreflightInLanding(landingDir: string, target: string, source: string, signal: AbortSignal): Promise<{ canMerge: boolean; conflictFiles: string[]; error: string | null; exitCode: number | null }> {
  // The landing clone is materialized from the workflow workspace and
  // has its `origin` reset to the real upstream gitUrl. We fetch the
  // base branch fresh from upstream before running the probe so the
  // preflight matches what `integrate:publish` would actually land.
  const fetch = await git(landingDir, ["fetch", "origin", target], signal)
  if (!fetch.success) {
    return { canMerge: false, conflictFiles: [], error: fetch.combinedOutput, exitCode: fetch.exitCode }
  }

  const checkout = await git(landingDir, ["checkout", target], signal)
  if (!checkout.success) {
    return { canMerge: false, conflictFiles: [], error: checkout.combinedOutput, exitCode: checkout.exitCode }
  }

  const merge = await git(landingDir, ["merge", "--squash", "--no-commit", source], signal)

  let conflictFiles: string[] = []
  if (!merge.success) {
    const status = await git(landingDir, ["diff", "--name-only", "--diff-filter=U"], signal)
    if (status.success && status.stdout.trim()) {
      conflictFiles = [...new Set(status.stdout.split("\n").map((line) => line.trim()).filter(Boolean))]
    }
  }

  // Reset the landing workspace back to the base branch ref so the
  // disposed dir is clean. The workflow workspace is untouched.
  await git(landingDir, ["reset", "--hard", `origin/${target}`], signal)

  return {
    canMerge: merge.success,
    conflictFiles,
    error: merge.success ? null : merge.combinedOutput,
    exitCode: merge.exitCode,
  }
}

function firstLine(value: string) {
  return value.replace(/\r\n/g, "\n").trim().split("\n")[0]
}

function trim(value: string) {
  return value.length <= 20_000 ? value : value.slice(0, 20_000)
}

function timeoutSignal(parent: AbortSignal, timeoutMs: number) {
  const controller = new AbortController()
  const abort = () => controller.abort(parent.reason)
  if (parent.aborted) {
    abort()
  } else {
    const onAbort = () => {
      clearTimeout(timer)
      abort()
    }
    const timer = setTimeout(() => {
      controller.abort(new Error(`Timed out after ${timeoutMs / 1000}s`))
      parent.removeEventListener("abort", onAbort)
    }, timeoutMs)
    parent.addEventListener("abort", onAbort, { once: true })
  }
  return controller.signal
}

function stringAt(value: unknown, path: string[]) {
  const found = path.reduce<unknown>((current, part) => {
    if (typeof current !== "object" || current === null || Array.isArray(current)) return undefined
    return (current as Record<string, unknown>)[part]
  }, value)
  return typeof found === "string" ? found : undefined
}

function numberAtString(value: unknown, path: string[]) {
  const found = path.reduce<unknown>((current, part) => {
    if (typeof current !== "object" || current === null || Array.isArray(current)) return undefined
    return (current as Record<string, unknown>)[part]
  }, value)
  return typeof found === "number" ? String(found) : undefined
}

//
// Backward-compatible aliases for merge pipeline tests
//

type ConflictResolverRunner = (context: ActionContext) => Promise<ActionResult>

let conflictResolverRunner: ConflictResolverRunner = acpAgentAction

export function setMergeGitRunnerForTest(runner: GitRunner | null) {
  setDeliveryGitRunnerForTest(runner)
}

export function setMergeConflictResolverForTest(runner: ConflictResolverRunner | null) {
  conflictResolverRunner = runner ?? acpAgentAction
}

export async function mergeAction(context: ActionContext): Promise<ActionResult> {
  const prepareCtx: ActionContext = {
    ...context,
    with: {
      ...(context.with as Record<string, unknown>),
      baseBranch: stringInput(context.with, "baseBranch") ?? stringInput(context.with, "target") ?? "main",
      remote: stringInput(context.with, "remote") ?? "origin",
    },
  }
  const prepareResult = await prepareAction(prepareCtx)
  if (prepareResult.status === "failure") return prepareResult

  const publishResult = await publishAction(context)
  return publishResult
}
