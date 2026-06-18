import { join } from "node:path"
import { randomUUID } from "node:crypto"
import type { ActionContext, ActionResult, JsonObject } from "../core/types.js"
import { arrayInput, numberInput, objectInput, stringInput } from "../core/json.js"
import { deleteFile, exists, readText, runCommand, writeText } from "../system/process.js"
import { acpAgentAction } from "./acp-agent.js"
import { resolveActionPath } from "./expectations.js"
import { archiveChangeAction, openspecSyncAction, openspecTasksAction } from "./openspec.js"
import {
  abortRebaseIfInProgressAction,
  applyWorkflowAgentDefault,
  combinedRebaseGitOutput,
  rebaseAction,
  rebaseConflictFiles,
  rebaseStatusAction,
  runRebaseConflictResolver,
  verifyRebaseCompleteAction,
} from "./rebase.js"
import { git as defaultGit } from "./git.js"

export type ActionHandler = (context: ActionContext) => Promise<ActionResult>
type GitRunner = typeof defaultGit
type ExistsChecker = typeof exists

let git: GitRunner = defaultGit
let pathExists: ExistsChecker = exists

export function setDeliveryGitRunnerForTest(runner: GitRunner | null) {
  git = runner ?? defaultGit
}

export function setDeliveryExistsCheckerForTest(checker: ExistsChecker | null) {
  pathExists = checker ?? exists
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

  const base = await git(context.workDir, ["rev-parse", baseBranch], context.signal)
  if (!base.success) return mergeReadyResult(false, baseBranch, null, null, null, `Could not resolve base branch '${baseBranch}'`, base.exitCode, [], new Date().toISOString())

  const head = await git(context.workDir, ["rev-parse", source], context.signal)
  if (!head.success) return mergeReadyResult(false, baseBranch, base.stdout.trim(), null, null, "Could not resolve source", head.exitCode, [], new Date().toISOString())

  const mergeBase = await git(context.workDir, ["merge-base", baseBranch, source], context.signal)
  const checkedAt = new Date().toISOString()
  const preflight = await runSquashMergePreflight(context.workDir, baseBranch, source, context.signal)

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
    return prepareDirtyOutput(baseBranch, null, null, [], 0, initialStatus.stdout, "Prepare aborted: worktree is dirty before rebase. Commit or clean the workspace before retrying.")
  }

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
  if (rebaseResult.success) {
    const after = await git(workDir, ["rev-parse", "HEAD"], context.signal)
    const preparedHeadSha = after.success ? after.stdout.trim() : null
    const clean = await prepareCleanWorktreeResult(context, baseBranch, preparedBaseSha, preparedHeadSha, [], 0, rebaseResult.combinedOutput)
    if (clean) return clean
    return prepareOutput(true, baseBranch, preparedBaseSha, preparedHeadSha, [], 0, rebaseResult.combinedOutput, undefined)
  }

  let conflicts = await rebaseConflictFiles(context)
  if (conflicts.length === 0) {
    await git(workDir, ["rebase", "--abort"], context.signal)
    return prepareOutput(false, baseBranch, preparedBaseSha, null, [], 0, rebaseResult.combinedOutput, "retry-safe", rebaseResult.exitCode)
  }

  if (!conflictResolver) {
    await git(workDir, ["rebase", "--abort"], context.signal)
    return prepareOutput(false, baseBranch, preparedBaseSha, null, conflicts, 0, rebaseResult.combinedOutput, "conflict", 1)
  }

  const allConflicts: string[][] = [conflicts]
  const gitOutputs: string[] = [rebaseResult.combinedOutput]
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
      const clean = await prepareCleanWorktreeResult(context, baseBranch, preparedBaseSha, preparedHeadSha, allConflicts.flat(), attempts, combinedRebaseGitOutput(gitOutputs))
      if (clean) return clean
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
): Promise<ActionResult | null> {
  const status = await git(context.workDir, ["status", "--porcelain"], context.signal)
  if (!status.success) {
    return prepareOutput(false, baseBranch, preparedBaseSha, preparedHeadSha, conflicts, resolveAttempts, status.combinedOutput, "retry-safe", status.exitCode)
  }
  if (!status.stdout.trim()) return null
  const output = [gitOutput, "Prepare left a dirty worktree after rebase:", status.stdout.trim()].filter(Boolean).join("\n\n")
  return prepareDirtyOutput(baseBranch, preparedBaseSha, preparedHeadSha, conflicts, resolveAttempts, output, "Prepare failed: worktree remained dirty after rebase.")
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

  const prePublish = await git(workDir, ["rev-parse", target], context.signal)
  if (!prePublish.success) {
    return publishOutput(false, source, target, workDir, null, false, prePublish.combinedOutput, "retry-safe", prePublish.exitCode)
  }
  let restoreSha = prePublish.stdout.trim()

  const status = await git(workDir, ["status", "--porcelain"], context.signal)
  if (status.success && status.stdout.trim()) {
    const dirty = status.stdout.trim()
    await git(workDir, ["reset", "--hard", restoreSha], context.signal)
    return publishOutput(
      false,
      source,
      target,
      workDir,
      null,
      false,
      `Publish aborted: target branch '${target}' had a dirty working tree. Destructive 'git reset --hard ${restoreSha}' was used to restore the workspace. Untracked or user-modified files that were discarded:\n${dirty}`,
      "retry-safe",
      status.exitCode,
    )
  }

  const fetch = await git(workDir, ["fetch", remote, target], context.signal)
  if (!fetch.success) {
    return publishOutput(false, source, target, workDir, null, false, fetch.combinedOutput, "retry-safe", fetch.exitCode)
  }

  const remoteHead = await git(workDir, ["rev-parse", remoteTarget], context.signal)
  if (!remoteHead.success) {
    return publishOutput(false, source, target, workDir, null, false, remoteHead.combinedOutput, "retry-safe", remoteHead.exitCode)
  }

  const checkout = await git(workDir, ["checkout", target], context.signal)
  if (!checkout.success) {
    const rebaseMerge = await git(workDir, ["rev-parse", "--git-path", "rebase-merge"], context.signal)
    const rebaseApply = await git(workDir, ["rev-parse", "--git-path", "rebase-apply"], context.signal)
    const mergeHead = await git(workDir, ["rev-parse", "--git-path", "MERGE_HEAD"], context.signal)
    if (
      (rebaseMerge.success && pathExists(resolveGitDirPath(workDir, rebaseMerge.stdout.trim())))
      || (rebaseApply.success && pathExists(resolveGitDirPath(workDir, rebaseApply.stdout.trim())))
    ) {
      await git(workDir, ["rebase", "--abort"], context.signal)
    } else if (mergeHead.success && pathExists(resolveGitDirPath(workDir, mergeHead.stdout.trim()))) {
      await git(workDir, ["merge", "--abort"], context.signal)
    }
    await git(workDir, ["reset", "--hard", restoreSha], context.signal)
    return publishOutput(false, source, target, workDir, null, false, checkout.combinedOutput, "retry-safe", checkout.exitCode)
  }

  const fastForward = await git(workDir, ["merge", "--ff-only", remoteTarget], context.signal)
  if (!fastForward.success) {
    await git(workDir, ["reset", "--hard", restoreSha], context.signal)
    return publishOutput(false, source, target, workDir, null, false, fastForward.combinedOutput, "base-moved", fastForward.exitCode)
  }

  restoreSha = remoteHead.stdout.trim()

  const sourceContainsTarget = await git(workDir, ["merge-base", "--is-ancestor", remoteTarget, source], context.signal)
  if (!sourceContainsTarget.success) {
    await git(workDir, ["reset", "--hard", restoreSha], context.signal)
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

  const squash = await git(workDir, ["merge", "--squash", source], context.signal)
  if (!squash.success) {
    await git(workDir, ["merge", "--abort"], context.signal)
    await git(workDir, ["reset", "--hard", restoreSha], context.signal)
    return publishOutput(false, source, target, workDir, null, false, squash.combinedOutput, "base-moved", squash.exitCode)
  }

  const commitMessage = buildPublishCommitMessage(message, workDir, source, target, context)
  const commit = await git(workDir, ["commit", ...commitMessage], context.signal)
  if (!commit.success) {
    await git(workDir, ["reset", "--hard", restoreSha], context.signal)
    return publishOutput(false, source, target, workDir, null, false, commit.combinedOutput, "retry-safe", commit.exitCode)
  }

  const head = await git(workDir, ["rev-parse", "HEAD"], context.signal)
  const landedCommit = head.success ? head.stdout.trim() : null

  const push = await git(workDir, ["push", remote, target], context.signal)
  if (!push.success) {
    await git(workDir, ["reset", "--hard", restoreSha], context.signal)
    const failureKind = looksLikeNonFastForward(push.combinedOutput) ? "base-moved" : "retry-safe"
    return publishOutput(false, source, target, workDir, landedCommit, false, push.combinedOutput, failureKind, push.exitCode)
  }

  return publishOutput(true, source, target, workDir, landedCommit, true, push.combinedOutput, undefined, push.exitCode)
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

async function runSquashMergePreflight(workDir: string, target: string, source: string, signal: AbortSignal): Promise<{ canMerge: boolean; conflictFiles: string[]; error: string | null; exitCode: number | null }> {
  const originalRef = await git(workDir, ["rev-parse", "--abbrev-ref", "HEAD"], signal)
  const originalSha = await git(workDir, ["rev-parse", "HEAD"], signal)

  const checkout = await git(workDir, ["checkout", target], signal)
  if (!checkout.success) {
    return { canMerge: false, conflictFiles: [], error: checkout.combinedOutput, exitCode: checkout.exitCode }
  }

  const merge = await git(workDir, ["merge", "--squash", "--no-commit", source], signal)

  let conflictFiles: string[] = []
  if (!merge.success) {
    const status = await git(workDir, ["diff", "--name-only", "--diff-filter=U"], signal)
    if (status.success && status.stdout.trim()) {
      conflictFiles = [...new Set(status.stdout.split("\n").map((line) => line.trim()).filter(Boolean))]
    }
  }

  await git(workDir, ["reset", "--hard"], signal)
  if (originalRef.success && originalRef.stdout.trim() && originalRef.stdout.trim() !== "HEAD") {
    await git(workDir, ["checkout", originalRef.stdout.trim()], signal)
  } else if (originalSha.success) {
    await git(workDir, ["checkout", originalSha.stdout.trim()], signal)
  }

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
