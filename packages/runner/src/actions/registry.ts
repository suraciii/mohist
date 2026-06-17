import { join } from "node:path"
import { randomUUID } from "node:crypto"
import type { ActionContext, ActionResult, JsonObject } from "../core/types.js"
import { arrayInput, numberInput, objectInput, stringInput } from "../core/json.js"
import { deleteFile, exists, readText, runCommand, writeText } from "../system/process.js"
import { acpAgentAction } from "./acp-agent.js"
import { resolveActionPath } from "./expectations.js"
import { archiveChangeAction, openspecSyncAction, openspecTasksAction } from "./openspec.js"
import { applyWorkflowAgentDefault, rebaseAction, rebaseStatusAction } from "./rebase.js"
import { git as defaultGit } from "./git.js"

export type ActionHandler = (context: ActionContext) => Promise<ActionResult>
type GitRunner = typeof defaultGit
type ConflictResolverRunner = typeof acpAgentAction
type GitResult = Awaited<ReturnType<GitRunner>>
type RemoteAlignResult = GitResult & { aligned?: boolean }

let git: GitRunner = defaultGit
let mergeConflictResolverRunner: ConflictResolverRunner = acpAgentAction

export function setMergeGitRunnerForTest(runner: GitRunner | null) {
  git = runner ?? defaultGit
}

export function setMergeConflictResolverForTest(runner: ConflictResolverRunner | null) {
  mergeConflictResolverRunner = runner ?? acpAgentAction
}

const DEFAULT_MAX_CONFLICT_RETRIES = 3
const DEFAULT_MAX_PUSH_RETRY = 5

type MergePhase = "source-cleanup" | "fetch" | "rebase-conflict" | "landing-validation" | "push"

interface MergeEvidence {
  kind: "merge"
  phase?: MergePhase
  source: string
  target: string
  remote: string
  strategy: string
  push: boolean
  pushRemote?: string
  pushEnabled: boolean
  baseSha?: string | null
  rebasedSha?: string | null
  landingSha?: string | null
  remoteRef?: string | null
  pushRetryAttempts?: number
  lastRemoteSha?: string | null
  resolveAttempts?: number
  dirty?: { staged: string[]; unstaged: string[]; untracked: string[] }
  conflicts?: string[]
  output?: string
  message?: string
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
  registry.register("mohist/merge", mergeAction)
  registry.register("mohist/push", pushAction)
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
  const baseBranch = stringInput(context.with, "baseBranch") ?? stringAt(context.variables, ["repository", "baseBranch"]) ?? "main"
  const source = stringInput(context.with, "source") ?? "HEAD"
  const targetBranch = baseBranch

  const base = await git(context.workDir, ["rev-parse", targetBranch], context.signal)
  if (!base.success) return mergeReadyResult(false, targetBranch, null, null, null, `Could not resolve base branch '${targetBranch}'`, base.exitCode, [], new Date().toISOString())

  const head = await git(context.workDir, ["rev-parse", source], context.signal)
  if (!head.success) return mergeReadyResult(false, targetBranch, base.stdout.trim(), null, null, "Could not resolve source", head.exitCode, [], new Date().toISOString())

  const mergeBase = await git(context.workDir, ["merge-base", targetBranch, source], context.signal)
  const mergeBaseSha = mergeBase.success ? mergeBase.stdout.trim() : null
  const checkedAt = new Date().toISOString()

  const preflight = await runSquashMergePreflight(context.workDir, targetBranch, source, context.signal)

  return mergeReadyResult(
    preflight.canMerge,
    targetBranch,
    base.stdout.trim(),
    head.stdout.trim(),
    mergeBaseSha,
    preflight.error,
    preflight.exitCode,
    preflight.conflictFiles,
    checkedAt,
  )
}

export async function mergeAction(context: ActionContext): Promise<ActionResult> {
  const source = stringInput(context.with, "source") ?? "HEAD"
  const target = stringInput(context.with, "target")
  const strategy = (stringInput(context.with, "strategy") ?? "squash").toLowerCase()
  const message = stringInput(context.with, "message") ?? "Mohist merge"
  const remote = stringInput(context.with, "remote") ?? "origin"
  const push = boolInput(context.with, "push") ?? false
  const maxConflictRetries = numberInput(context.with, "maxConflictRetries") ?? DEFAULT_MAX_CONFLICT_RETRIES
  const maxPushRetry = numberInput(context.with, "maxPushRetry") ?? DEFAULT_MAX_PUSH_RETRY
  const conflictResolver = objectInput(context.with, "conflictResolver") ?? {}
  const workDir = stringAt(context.variables, ["project", "path"]) ?? context.workDir

  const outputs: string[] = []

  if (!target?.trim()) {
    return mergeFailure({
      source,
      target: target ?? "",
      remote,
      strategy,
      push,
      pushEnabled: push,
      phase: undefined,
      message: "merge action requires 'target'",
      output: combinedGitOutput(outputs),
    })
  }

  const dirty = await collectDirtyWorktree(workDir, context.signal)
  if (dirty && !isWorktreeClean(dirty)) {
    return mergeFailure({
      source,
      target,
      remote,
      strategy,
      push,
      pushEnabled: push,
      phase: "source-cleanup",
      message: `Source worktree is dirty; refusing to merge.\n${describeDirty(dirty)}`,
      output: combinedGitOutput(outputs),
      dirty,
    })
  }

  // `maxPushRetry` is the total number of fetch→rebase→land→push
  // cycles the merge action will run before giving up. With
  // `maxPushRetry: 1` the action performs exactly 1 push attempt;
  // with `maxPushRetry: 5` (the design default) it performs 5.
  let lastRemoteSha: string | null = null
  let baseSha: string | null = null
  let landingSha: string | null = null
  let rebasedSha: string | null = null
  let resolveAttemptsTotal = 0
  let resolvedConflicts: string[] = []
  let pushAttemptsConsumed = 0

  for (let attempt = 0; attempt < maxPushRetry; attempt++) {
    const fetched = await fetchRemote(workDir, remote, target, context.signal)
    if (!fetched.success) {
      outputs.push(fetched.combinedOutput)
      return fetchFailure({
        source,
        target,
        remote,
        strategy,
        push,
        landingSha,
        output: combinedGitOutput(outputs),
        detail: `Fetch from '${remote}' failed: ${fetched.combinedOutput}`,
      })
    }
    outputs.push(fetched.combinedOutput)

    const remoteTracking = `${remote}/${target}`
    const baseResult = await git(workDir, ["rev-parse", remoteTracking], context.signal)
    if (!baseResult.success) {
      outputs.push(baseResult.combinedOutput)
      return fetchFailure({
        source,
        target,
        remote,
        strategy,
        push,
        landingSha,
        output: combinedGitOutput(outputs),
        detail: `Could not resolve ${remoteTracking} after fetch: ${baseResult.combinedOutput}`,
      })
    }
    baseSha = baseResult.stdout.trim()

    const rebaseResult = await rebaseSourceOnto(workDir, source, remoteTracking, context)
    outputs.push(rebaseResult.output)
    // `rebaseSourceOnto` reports `success: false` when the source branch
    // could not be checked out (a guard failure), but a failed rebase that
    // produced conflicts still reports `success: false` with a non-empty
    // `conflicts` array. Only the no-conflict failure path is a checkout
    // error that should bubble up as a fetch failure.
    if (!rebaseResult.success && rebaseResult.conflicts.length === 0) {
      return fetchFailure({
        source,
        target,
        remote,
        strategy,
        push,
        landingSha,
        baseSha,
        output: combinedGitOutput(outputs),
        detail: rebaseResult.output || `Could not check out or rebase source branch '${source}'.`,
      })
    }
    if (rebaseResult.conflicts.length > 0) {
      const resolved = await resolveRebaseConflicts(context, {
        workDir,
        source,
        target,
        remoteTracking,
        baseSha,
        conflicts: rebaseResult.conflicts,
        conflictResolver,
        maxConflictRetries,
        initialOutput: rebaseResult.output,
      })
      outputs.push(resolved.output)
      resolveAttemptsTotal += resolved.attempts
      if (resolved.ok) {
        resolvedConflicts = Array.from(new Set([...resolvedConflicts, ...resolved.resolvedConflicts]))
      }
      if (!resolved.ok) {
        return rebaseConflictFailure({
          source,
          target,
          remote,
          strategy,
          push,
          baseSha,
          landingSha,
          outputs,
          message: `Rebase conflicts could not be resolved after ${resolved.attempts} attempt(s).`,
          conflicts: resolved.unresolvedConflicts,
          resolveAttempts: resolved.attempts,
        })
      }
    }

    const rebasedHead = await git(workDir, ["rev-parse", "HEAD"], context.signal)
    if (!rebasedHead.success) {
      outputs.push(rebasedHead.combinedOutput)
      return rebaseConflictFailure({
        source,
        target,
        remote,
        strategy,
        push,
        baseSha,
        landingSha,
        outputs,
        message: `Could not resolve HEAD after rebase: ${rebasedHead.combinedOutput}`,
        resolveAttempts: resolveAttemptsTotal,
      })
    }
    rebasedSha = rebasedHead.stdout.trim()

    const postRebaseClean = await collectDirtyWorktree(workDir, context.signal)
    outputs.push(postRebaseClean ? `Status after rebase: ${postRebaseClean.staged.length} staged, ${postRebaseClean.unstaged.length} unstaged, ${postRebaseClean.untracked.length} untracked` : "Status after rebase: ok")
    if (postRebaseClean && !isWorktreeClean(postRebaseClean)) {
      return rebaseConflictFailure({
        source,
        target,
        remote,
        strategy,
        push,
        baseSha,
        rebasedSha,
        landingSha,
        outputs,
        message: `Source worktree is dirty after rebase:\n${describeDirty(postRebaseClean)}`,
        resolveAttempts: resolveAttemptsTotal,
        dirty: postRebaseClean,
      })
    }

    const landing = await createSquashLanding(workDir, baseSha, source, strategy, message, context)
    outputs.push(landing.output)
    if (!landing.landingSha) {
      return landingValidationFailure({
        source,
        target,
        remote,
        strategy,
        push,
        baseSha,
        rebasedSha,
        landingSha: null,
        outputs,
        message: landing.message ?? "Squash landing commit could not be created",
        resolveAttempts: resolveAttemptsTotal,
      })
    }
    landingSha = landing.landingSha

    const validation = await validateLanding(workDir, baseSha, landingSha, context.signal)
    outputs.push(validation.output)
    if (!validation.ok) {
      return landingValidationFailure({
        source,
        target,
        remote,
        strategy,
        push,
        baseSha,
        rebasedSha,
        landingSha,
        outputs,
        message: validation.message ?? "Landing validation failed",
        resolveAttempts: resolveAttemptsTotal,
      })
    }

    if (!push) {
      return mergeSuccess({
        source,
        target,
        remote,
        strategy,
        push: false,
        pushEnabled: false,
        baseSha,
        rebasedSha,
        landingSha,
        remoteRef: null,
        pushRetryAttempts: 0,
        lastRemoteSha: null,
        resolveAttempts: resolveAttemptsTotal,
        conflicts: resolvedConflicts,
        output: combinedGitOutput(outputs),
      })
    }

    pushAttemptsConsumed = attempt + 1
    lastRemoteSha = baseSha

    const pushResult = await pushLandingCommit(workDir, remote, target, landingSha, context.signal)
    outputs.push(pushResult.combinedOutput)
    if (pushResult.success) {
      const remoteRef = await verifyRemoteRef(workDir, remote, target, context.signal)
      outputs.push(remoteRef.combinedOutput)
      if (!remoteRef.success) {
        return pushFailure({
          source,
          target,
          remote,
          strategy,
          baseSha,
          rebasedSha,
          landingSha,
          remoteRef: null,
          pushRetryAttempts: pushAttemptsConsumed,
          lastRemoteSha,
          outputs,
          message: `Remote ref verification failed: ${remoteRef.combinedOutput}`,
          resolveAttempts: resolveAttemptsTotal,
        })
      }
      if (remoteRef.remoteSha && remoteRef.remoteSha !== landingSha) {
        return pushFailure({
          source,
          target,
          remote,
          strategy,
          baseSha,
          rebasedSha,
          landingSha,
          remoteRef: remoteRef.remoteSha,
          pushRetryAttempts: pushAttemptsConsumed,
          lastRemoteSha,
          outputs,
          message: `Remote ref points at '${remoteRef.remoteSha}' but expected landing commit '${landingSha}'.`,
          resolveAttempts: resolveAttemptsTotal,
        })
      }
      return mergeSuccess({
        source,
        target,
        remote,
        strategy,
        push: true,
        pushEnabled: true,
        baseSha,
        rebasedSha,
        landingSha,
        remoteRef: remoteRef.remoteSha,
        pushRetryAttempts: pushAttemptsConsumed,
        lastRemoteSha,
        resolveAttempts: resolveAttemptsTotal,
        conflicts: resolvedConflicts,
        output: combinedGitOutput(outputs),
      })
    }

    if (isRemoteAdvancedRejection(pushResult.combinedOutput)) {
      const newRemote = await git(workDir, ["ls-remote", remote, `refs/heads/${target}`], context.signal)
      if (newRemote.success) {
        const parsed = parseLsRemoteSha(newRemote.stdout)
        if (parsed) lastRemoteSha = parsed
      }
      if (attempt < maxPushRetry) continue
    }

    return pushFailure({
      source,
      target,
      remote,
      strategy,
      baseSha,
      rebasedSha,
      landingSha,
      remoteRef: null,
      pushRetryAttempts: pushAttemptsConsumed,
      lastRemoteSha,
      outputs,
      message: `Fast-forward push to '${remote}/${target}' failed: ${pushResult.combinedOutput}`,
      resolveAttempts: resolveAttemptsTotal,
    })
  }

  return pushFailure({
    source,
    target,
    remote,
    strategy,
    baseSha,
    rebasedSha,
    landingSha,
    remoteRef: null,
    pushRetryAttempts: pushAttemptsConsumed,
    lastRemoteSha,
    outputs,
    message: `Remote-advanced push retry exhausted after ${pushAttemptsConsumed} attempt(s).`,
    resolveAttempts: resolveAttemptsTotal,
  })
}

export async function pushAction(context: ActionContext): Promise<ActionResult> {
  const remote = stringInput(context.with, "remote") ?? "origin"
  const target = stringInput(context.with, "target") ?? stringAt(context.variables, ["repository", "baseBranch"])
  if (!target) return { status: "failure", message: "Push action requires target or repository.baseBranch" }

  const result = await git(context.workDir, ["push", remote, target], context.signal)
  if (!result.success && isNonFastForward(result.combinedOutput)) {
    return await recoverNonFastForwardPush(context, remote, target, result.combinedOutput)
  }

  const output = JSON.stringify({ kind: "push", remote, target, status: result.success ? "pushed" : "failed", output: result.combinedOutput })
  return result.success
    ? { status: "success", message: "Push completed", output, exitCode: result.exitCode }
    : { status: "failure", message: result.combinedOutput, output, exitCode: result.exitCode }
}

interface FetchFailureInput {
  source: string
  target: string
  remote: string
  strategy: string
  push: boolean
  landingSha: string | null
  baseSha?: string | null
  output: string
  detail: string
}

function fetchFailure(input: FetchFailureInput): ActionResult {
  return mergeFailure({
    source: input.source,
    target: input.target,
    remote: input.remote,
    strategy: input.strategy,
    push: input.push,
    pushEnabled: input.push,
    pushRemote: input.push ? input.remote : undefined,
    baseSha: input.baseSha ?? null,
    landingSha: input.landingSha,
    phase: "fetch",
    message: input.detail,
    output: input.output,
  })
}

interface RebaseConflictFailureInput {
  source: string
  target: string
  remote: string
  strategy: string
  push: boolean
  baseSha: string | null
  rebasedSha?: string | null
  landingSha: string | null
  outputs: string[]
  message: string
  conflicts?: string[]
  resolveAttempts: number
  dirty?: DirtyWorktree
}

function rebaseConflictFailure(input: RebaseConflictFailureInput): ActionResult {
  return mergeFailure({
    source: input.source,
    target: input.target,
    remote: input.remote,
    strategy: input.strategy,
    push: input.push,
    pushEnabled: input.push,
    pushRemote: input.push ? input.remote : undefined,
    baseSha: input.baseSha,
    rebasedSha: input.rebasedSha ?? null,
    landingSha: input.landingSha,
    phase: "rebase-conflict",
    message: input.message,
    output: combinedGitOutput(input.outputs),
    conflicts: input.conflicts,
    resolveAttempts: input.resolveAttempts,
    dirty: input.dirty,
  })
}

interface LandingValidationFailureInput {
  source: string
  target: string
  remote: string
  strategy: string
  push: boolean
  baseSha: string | null
  rebasedSha: string | null
  landingSha: string | null
  outputs: string[]
  message: string
  resolveAttempts: number
}

function landingValidationFailure(input: LandingValidationFailureInput): ActionResult {
  return mergeFailure({
    source: input.source,
    target: input.target,
    remote: input.remote,
    strategy: input.strategy,
    push: input.push,
    pushEnabled: input.push,
    pushRemote: input.push ? input.remote : undefined,
    baseSha: input.baseSha,
    rebasedSha: input.rebasedSha,
    landingSha: input.landingSha,
    phase: "landing-validation",
    message: input.message,
    output: combinedGitOutput(input.outputs),
    resolveAttempts: input.resolveAttempts,
  })
}

interface PushFailureInput {
  source: string
  target: string
  remote: string
  strategy: string
  baseSha: string | null
  rebasedSha: string | null
  landingSha: string | null
  remoteRef: string | null
  pushRetryAttempts: number
  lastRemoteSha: string | null
  outputs: string[]
  message: string
  resolveAttempts: number
}

function pushFailure(input: PushFailureInput): ActionResult {
  return mergeFailure({
    source: input.source,
    target: input.target,
    remote: input.remote,
    strategy: input.strategy,
    push: true,
    pushEnabled: true,
    pushRemote: input.remote,
    baseSha: input.baseSha,
    rebasedSha: input.rebasedSha,
    landingSha: input.landingSha,
    remoteRef: input.remoteRef,
    pushRetryAttempts: input.pushRetryAttempts,
    lastRemoteSha: input.lastRemoteSha,
    phase: "push",
    message: input.message,
    output: combinedGitOutput(input.outputs),
    resolveAttempts: input.resolveAttempts,
  })
}

async function fetchRemote(workDir: string, remote: string, target: string, signal: AbortSignal) {
  return await git(workDir, ["fetch", remote, target], signal)
}

async function fetchTargetRemote(workDir: string, remote: string, target: string, signal: AbortSignal): Promise<GitResult> {
  return await git(workDir, ["fetch", remote, target], signal)
}

interface RebaseResult {
  output: string
  conflicts: string[]
  success: boolean
}

async function rebaseSourceOnto(workDir: string, source: string, remoteTracking: string, context: ActionContext): Promise<RebaseResult> {
  const outputs: string[] = []
  // Defensive: `git rebase <upstream>` rebases the currently checked-out
  // branch, not the named `source` branch. The runner worktree is normally
  // already on `source` (set by WorkspaceManager.ensureIssueWorktree), but
  // if a previous step left the worktree on a different ref, the rebase
  // would silently target the wrong branch. Explicitly check out the
  // source branch first so the rebased range always matches the configured
  // source, even after worktree state drift.
  const checkout = await git(workDir, ["checkout", source], context.signal)
  outputs.push(checkout.combinedOutput)
  if (!checkout.success) {
    return { output: combinedGitOutput(outputs), conflicts: [], success: false }
  }
  const result = await git(workDir, ["rebase", remoteTracking], context.signal)
  outputs.push(result.combinedOutput)
  if (result.success) {
    return { output: combinedGitOutput(outputs), conflicts: [], success: true }
  }
  const conflicts = await mergeConflictFiles(workDir, context.signal)
  return { output: combinedGitOutput(outputs), conflicts, success: false }
}

interface ResolveRebaseConflictsInput {
  workDir: string
  source: string
  target: string
  remoteTracking: string
  baseSha: string
  conflicts: string[]
  conflictResolver: JsonObject
  maxConflictRetries: number
  initialOutput: string
}

interface ResolveRebaseConflictsResult {
  ok: boolean
  attempts: number
  output: string
  unresolvedConflicts: string[]
  resolvedConflicts: string[]
}

async function resolveRebaseConflicts(context: ActionContext, input: ResolveRebaseConflictsInput): Promise<ResolveRebaseConflictsResult> {
  const outputs: string[] = [input.initialOutput]
  const allConflicts: string[][] = [input.conflicts]
  let attempts = 0
  let conflicts = input.conflicts

  while (attempts < input.maxConflictRetries) {
    attempts += 1
    const agentResult = await runRebaseMergeConflictResolver(context, input.conflictResolver, input.workDir, input.source, input.target, conflicts, attempts, "rebase")
    if (agentResult.output) outputs.push(agentResult.output)
    if (agentResult.status !== "success") {
      await git(input.workDir, ["rebase", "--abort"], context.signal).catch(() => undefined)
      return {
        ok: false,
        attempts,
        output: combinedGitOutput(outputs),
        unresolvedConflicts: Array.from(new Set(allConflicts.flat())),
        resolvedConflicts: [],
      }
    }

    const remaining = await mergeConflictFiles(input.workDir, context.signal)
    if (remaining.length > 0) {
      allConflicts.push(remaining)
      conflicts = remaining
      continue
    }

    const rebaseInProgress = await isRebaseInProgress(input.workDir, context.signal)
    if (rebaseInProgress) {
      const continueResult = await git(input.workDir, ["rebase", "--continue"], context.signal)
      outputs.push(continueResult.combinedOutput)
      if (!continueResult.success) {
        const stillUnresolved = await mergeConflictFiles(input.workDir, context.signal)
        if (stillUnresolved.length > 0) {
          allConflicts.push(stillUnresolved)
          conflicts = stillUnresolved
          continue
        }
      }
    }

    const stillInProgress = await isRebaseInProgress(input.workDir, context.signal)
    const afterConflicts = await mergeConflictFiles(input.workDir, context.signal)
    if (stillInProgress || afterConflicts.length > 0) {
      if (afterConflicts.length > 0) {
        allConflicts.push(afterConflicts)
        conflicts = afterConflicts
      }
      continue
    }

    const head = await git(input.workDir, ["rev-parse", "HEAD"], context.signal)
    outputs.push(head.combinedOutput)
    return {
      ok: true,
      attempts,
      output: combinedGitOutput(outputs),
      unresolvedConflicts: [],
      resolvedConflicts: Array.from(new Set(allConflicts.flat())),
    }
  }

  await git(input.workDir, ["rebase", "--abort"], context.signal).catch(() => undefined)
  return {
    ok: false,
    attempts,
    output: combinedGitOutput(outputs),
    unresolvedConflicts: Array.from(new Set(allConflicts.flat())),
    resolvedConflicts: [],
  }
}

async function runRebaseMergeConflictResolver(
  context: ActionContext,
  conflictResolver: JsonObject,
  workDir: string,
  source: string,
  target: string,
  conflicts: string[],
  attempt: number,
  phase: "rebase" | "merge",
): Promise<ActionResult> {
  const resolverWith: JsonObject = {
    prompt: buildRebaseConflictPrompt(conflicts, source, target, attempt, phase),
    ...objectInput(conflictResolver, "with"),
  }
  applyWorkflowAgentDefault(resolverWith, context.variables)

  return mergeConflictResolverRunner({
    ...context,
    workDir,
    workId: `${context.workId}-conflict-resolve-${attempt}`,
    workType: "task",
    title: stringInput(conflictResolver, "title") ?? (phase === "rebase" ? "Resolve rebase conflicts" : "Resolve merge conflicts"),
    with: resolverWith,
  })
}

function buildRebaseConflictPrompt(conflicts: string[], source: string, target: string, attempt: number, phase: "rebase" | "merge") {
  const fileList = conflicts.map((f) => `- ${f}`).join("\n")
  const verb = phase === "rebase" ? "rebase" : "merge"
  const targetLabel = phase === "rebase" ? `\`${target}\`` : `\`${source}\` into \`${target}\``
  return [
    `## Complete Git ${verb[0]!.toUpperCase()}${verb.slice(1)} Conflict Resolution (attempt ${attempt})`,
    "",
    `A ${verb} of ${targetLabel} produced conflicts.`,
    "",
    "Current conflict files:",
    fileList,
    "",
    "Resolution rules:",
    "1. Preserve both sides. Never drop or overwrite either side's intentional changes.",
    "2. Resolve every conflict marker. Search for `<<<<<<<`, `=======`, and `>>>>>>>`; no markers may remain.",
    "3. Stage resolved files with `git add`.",
    phase === "rebase"
      ? "4. Continue the rebase yourself with `GIT_EDITOR=true git rebase --continue` until the rebase is complete and no rebase is in progress."
      : "4. Do not create the merge commit. The runner will commit after verifying the conflict is resolved.",
    "5. If verification fails because of your resolution, fix it before finishing.",
  ].join("\n")
}

async function fastForwardCheckedOutTargetToRemote(workDir: string, remote: string, target: string, fetchOutput: string, signal: AbortSignal): Promise<RemoteAlignResult> {
  const remoteRef = `${remote}/${target}`
  const remoteHead = await git(workDir, ["rev-parse", "--verify", remoteRef], signal)
  if (!remoteHead.success) {
    return { success: true, stdout: "", stderr: "", exitCode: 0, combinedOutput: combinedGitOutput([fetchOutput, remoteHead.combinedOutput]), aligned: false }
  }

  const ff = await git(workDir, ["merge", "--ff-only", remoteRef], signal)
  return {
    ...ff,
    combinedOutput: combinedGitOutput([fetchOutput, ff.combinedOutput]),
    aligned: ff.success,
  }
}

async function recoverNonFastForwardPush(context: ActionContext, remote: string, target: string, initialOutput: string): Promise<ActionResult> {
  const maxRetries = numberInput(context.with, "maxConflictRetries") ?? 3
  const conflictResolver = objectInput(context.with, "conflictResolver") ?? {}
  const fetch = await git(context.workDir, ["fetch", remote, target], context.signal)
  if (!fetch.success) {
    const output = JSON.stringify({ kind: "push", remote, target, status: "remote_advanced_fetch_failed", output: combinedGitOutput([initialOutput, fetch.combinedOutput]) })
    return { status: "failure", message: fetch.combinedOutput, output, exitCode: fetch.exitCode }
  }

  const checkout = await git(context.workDir, ["checkout", target], context.signal)
  if (!checkout.success) {
    const output = JSON.stringify({ kind: "push", remote, target, status: "remote_advanced_checkout_failed", output: combinedGitOutput([initialOutput, fetch.combinedOutput, checkout.combinedOutput]) })
    return { status: "failure", message: checkout.combinedOutput, output, exitCode: checkout.exitCode }
  }

  const remoteRef = `${remote}/${target}`
  const rebase = await git(context.workDir, ["rebase", remoteRef], context.signal)
  if (!rebase.success) {
    const conflictFiles = await mergeConflictFiles(context.workDir, context.signal)
    if (conflictFiles.length > 0) {
      return await resolvePushRebaseConflict(context, {
        remote,
        target,
        remoteRef,
        initialOutput,
        fetchOutput: fetch.combinedOutput,
        rebaseOutput: rebase.combinedOutput,
        conflictFiles,
        conflictResolver,
        maxRetries,
      })
    }
    const output = JSON.stringify({
      kind: "push",
      remote,
      target,
      status: conflictFiles.length > 0 ? "remote_advanced_rebase_conflict" : "remote_advanced_rebase_failed",
      conflictFiles,
      output: combinedGitOutput([initialOutput, fetch.combinedOutput, rebase.combinedOutput]),
    })
    return { status: "failure", message: `Remote branch advanced and rebase failed: ${rebase.combinedOutput}`, output, exitCode: rebase.exitCode }
  }

  const verify = await git(context.workDir, ["diff", "--check"], context.signal)
  if (!verify.success) {
    const output = JSON.stringify({ kind: "push", remote, target, status: "remote_advanced_verify_failed", output: combinedGitOutput([initialOutput, fetch.combinedOutput, rebase.combinedOutput, verify.combinedOutput]) })
    return { status: "failure", message: verify.combinedOutput, output, exitCode: verify.exitCode }
  }

  const retry = await git(context.workDir, ["push", remote, target], context.signal)
  if (!retry.success) {
    const output = JSON.stringify({ kind: "push", remote, target, status: isNonFastForward(retry.combinedOutput) ? "remote_advanced_retry_rejected" : "retry_failed", output: combinedGitOutput([initialOutput, fetch.combinedOutput, rebase.combinedOutput, verify.combinedOutput, retry.combinedOutput]) })
    return { status: "failure", message: retry.combinedOutput, output, exitCode: retry.exitCode }
  }

  const output = JSON.stringify({
    kind: "push",
    remote,
    target,
    status: "remote_advanced_rebased_and_pushed",
    output: combinedGitOutput([initialOutput, fetch.combinedOutput, rebase.combinedOutput, verify.combinedOutput, retry.combinedOutput]),
  })
  return { status: "success", message: "Push completed after rebasing onto remote", output, exitCode: retry.exitCode }
}

async function resolvePushRebaseConflict(
  context: ActionContext,
  input: {
    remote: string
    target: string
    remoteRef: string
    initialOutput: string
    fetchOutput: string
    rebaseOutput: string
    conflictFiles: string[]
    conflictResolver: JsonObject
    maxRetries: number
  },
): Promise<ActionResult> {
  const outputs = [input.initialOutput, input.fetchOutput, input.rebaseOutput]
  const allConflicts: string[][] = [input.conflictFiles]
  let conflicts = input.conflictFiles
  let attempts = 0

  while (attempts < input.maxRetries) {
    attempts++
    const agentResult = await runPushRebaseConflictResolver(context, input.conflictResolver, input.remoteRef, conflicts, attempts)
    if (agentResult.output) outputs.push(agentResult.output)
    if (agentResult.status !== "success") {
      return pushRebaseConflictFailure(input, allConflicts.flat(), attempts, outputs, agentResult.exitCode ?? 1)
    }

    const verified = await verifyPushRebaseComplete(context, input.remoteRef)
    outputs.push(verified.output)
    if (verified.ok) {
      const verify = await git(context.workDir, ["diff", "--check"], context.signal)
      outputs.push(verify.combinedOutput)
      if (!verify.success) {
        const output = JSON.stringify({ kind: "push", remote: input.remote, target: input.target, status: "remote_advanced_verify_failed", conflictFiles: allConflicts.flat(), resolveAttempts: attempts, output: combinedGitOutput(outputs) })
        return { status: "failure", message: verify.combinedOutput, output, exitCode: verify.exitCode }
      }

      const retry = await git(context.workDir, ["push", input.remote, input.target], context.signal)
      outputs.push(retry.combinedOutput)
      if (!retry.success) {
        const output = JSON.stringify({ kind: "push", remote: input.remote, target: input.target, status: isNonFastForward(retry.combinedOutput) ? "remote_advanced_retry_rejected" : "retry_failed", conflictFiles: allConflicts.flat(), resolveAttempts: attempts, output: combinedGitOutput(outputs) })
        return { status: "failure", message: retry.combinedOutput, output, exitCode: retry.exitCode }
      }

      const output = JSON.stringify({ kind: "push", remote: input.remote, target: input.target, status: "remote_advanced_rebased_and_pushed", conflictFiles: allConflicts.flat(), resolveAttempts: attempts, output: combinedGitOutput(outputs) })
      return { status: "success", message: "Push completed after resolving remote rebase conflicts", output, exitCode: retry.exitCode }
    }

    conflicts = await mergeConflictFiles(context.workDir, context.signal)
    if (conflicts.length > 0) allConflicts.push(conflicts)
  }

  return pushRebaseConflictFailure(input, allConflicts.flat(), attempts, outputs, 1)
}

async function runPushRebaseConflictResolver(
  context: ActionContext,
  conflictResolver: JsonObject,
  remoteRef: string,
  conflicts: string[],
  attempt: number,
): Promise<ActionResult> {
  const resolverWith: JsonObject = {
    prompt: buildPushRebaseConflictPrompt(remoteRef, conflicts, attempt),
    ...objectInput(conflictResolver, "with"),
  }
  applyWorkflowAgentDefault(resolverWith, context.variables)

  return mergeConflictResolverRunner({
    ...context,
    workId: `${context.workId}-push-rebase-resolve-${attempt}`,
    workType: "task",
    title: stringInput(conflictResolver, "title") ?? "Resolve push rebase conflicts",
    with: resolverWith,
  })
}

async function verifyPushRebaseComplete(context: ActionContext, remoteRef: string) {
  const conflicts = await mergeConflictFiles(context.workDir, context.signal)
  const rebaseInProgress = await isGitSequencerInProgress(context)
  const output = [
    conflicts.length > 0 ? `Conflicts remain:\n${conflicts.join("\n")}` : "",
    rebaseInProgress ? `Rebase onto ${remoteRef} is still in progress.` : "",
  ].filter(Boolean).join("\n")
  return { ok: conflicts.length === 0 && !rebaseInProgress, output }
}

async function isGitSequencerInProgress(context: ActionContext) {
  const merge = await git(context.workDir, ["rev-parse", "--git-path", "rebase-merge"], context.signal)
  if (merge.success && exists(resolveGitPath(context.workDir, merge.stdout.trim()))) return true
  const apply = await git(context.workDir, ["rev-parse", "--git-path", "rebase-apply"], context.signal)
  return apply.success && exists(resolveGitPath(context.workDir, apply.stdout.trim()))
}

function buildPushRebaseConflictPrompt(remoteRef: string, conflicts: string[], attempt: number) {
  const fileList = conflicts.map((f) => `- ${f}`).join("\n")
  return [
    `## Complete Push Rebase Conflict Resolution (attempt ${attempt})`,
    "",
    `A \`git rebase ${remoteRef}\` was started because the remote base branch advanced before push.`,
    "",
    "Current conflict files:",
    fileList,
    "",
    "Resolution rules:",
    "1. Preserve both the integrated issue changes and the newer remote base changes.",
    "2. Resolve every conflict marker. Search for `<<<<<<<`, `=======`, and `>>>>>>>` across the repository; no markers may remain.",
    "3. Stage resolved files with `git add`.",
    "4. Run `GIT_EDITOR=true git rebase --continue`.",
    "5. If more conflicts appear, keep resolving and continuing until the rebase fully completes.",
    "6. Do not push and do not force push. The runner will verify and push after the rebase is complete.",
  ].join("\n")
}

function pushRebaseConflictFailure(
  input: { remote: string; target: string },
  conflicts: string[],
  attempts: number,
  outputs: string[],
  exitCode: number,
): ActionResult {
  const output = JSON.stringify({
    kind: "push",
    remote: input.remote,
    target: input.target,
    status: "remote_advanced_rebase_conflict",
    conflictFiles: conflicts,
    resolveAttempts: attempts,
    output: combinedGitOutput(outputs),
  })
  return { status: "failure", message: combinedGitOutput(outputs), output, exitCode }
}

function resolveGitPath(workDir: string, path: string) {
  return path.match(/^[A-Za-z]:[\\/]|^\//) ? path : join(workDir, path)
}

function isNonFastForward(output: string) {
  return /non-fast-forward|fetch first|tip is behind|Updates were rejected/i.test(output)
}

async function createSquashLanding(
  workDir: string,
  baseSha: string,
  source: string,
  strategy: string,
  message: string,
  context: ActionContext,
): Promise<{ landingSha: string | null; output: string; message?: string }> {
  const outputs: string[] = []

  const checkout = await git(workDir, ["checkout", "--detach", baseSha], context.signal)
  outputs.push(checkout.combinedOutput)
  if (!checkout.success) {
    return { landingSha: null, output: combinedGitOutput(outputs), message: `Could not checkout detached HEAD at ${baseSha}: ${checkout.combinedOutput}` }
  }

  if (strategy === "squash") {
    const merge = await git(workDir, ["merge", "--squash", source], context.signal)
    outputs.push(merge.combinedOutput)
    if (!merge.success) {
      await git(workDir, ["reset", "--hard", "HEAD"], context.signal).catch(() => undefined)
      return { landingSha: null, output: combinedGitOutput(outputs), message: `Squash merge of '${source}' failed: ${merge.combinedOutput}` }
    }
    const commitResult = await finishLandingCommit(workDir, message, context)
    outputs.push(commitResult.combinedOutput)
    if (!commitResult.success) {
      await git(workDir, ["reset", "--hard", "HEAD"], context.signal).catch(() => undefined)
      return { landingSha: null, output: combinedGitOutput(outputs), message: `Landing commit failed: ${commitResult.combinedOutput}` }
    }
  } else {
    const merge = await git(workDir, ["merge", source], context.signal)
    outputs.push(merge.combinedOutput)
    if (!merge.success) {
      await git(workDir, ["merge", "--abort"], context.signal).catch(() => undefined)
      return { landingSha: null, output: combinedGitOutput(outputs), message: `Merge of '${source}' failed: ${merge.combinedOutput}` }
    }
  }

  const head = await git(workDir, ["rev-parse", "HEAD"], context.signal)
  outputs.push(head.combinedOutput)
  if (!head.success) {
    return { landingSha: null, output: combinedGitOutput(outputs), message: `Could not resolve landing HEAD: ${head.combinedOutput}` }
  }
  return { landingSha: head.stdout.trim(), output: combinedGitOutput(outputs) }
}

async function finishLandingCommit(workDir: string, message: string, context: ActionContext) {
  const title = stringAt(context.variables, ["issue", "title"]) ?? message
  const numberStr = typeof context.issueNumber === "number" && context.issueNumber > 0
    ? String(context.issueNumber)
    : numberAtString(context.variables, ["issue", "number"])
  const header = numberStr ? `${title} (#${numberStr})` : title

  const source = stringInput(context.with, "source") ?? "HEAD"
  const target = stringInput(context.with, "target") ?? ""
  const logResult = await git(workDir, ["log", "--format=* %s", `${target}..${source}`], context.signal)
  const body = capCommitBody(logResult.success ? logResult.stdout.trim() : "")

  return body
    ? await git(workDir, ["commit", "-m", header, "-m", body], context.signal)
    : await git(workDir, ["commit", "-m", header], context.signal)
}

const MAX_LANDING_BODY_LINES = 50
const MAX_LANDING_BODY_CHARS = 20_000

function capCommitBody(body: string): string {
  if (!body) return ""
  const lines = body.split(/\r?\n/)
  if (lines.length <= MAX_LANDING_BODY_LINES && body.length <= MAX_LANDING_BODY_CHARS) return body
  const kept = lines.slice(0, MAX_LANDING_BODY_LINES).join("\n")
  const remaining = lines.length - MAX_LANDING_BODY_LINES
  return `${kept}\n\n... and ${remaining} more commit(s) ...`
}

interface LandingValidation {
  ok: boolean
  message?: string
  output: string
}

async function validateLanding(workDir: string, baseSha: string, landingSha: string, signal: AbortSignal): Promise<LandingValidation> {
  const outputs: string[] = []
  const status = await git(workDir, ["status", "--porcelain"], signal)
  outputs.push(status.combinedOutput)
  if (!status.success) {
    return { ok: false, output: combinedGitOutput(outputs), message: `Could not read worktree status: ${status.combinedOutput}` }
  }
  if (status.stdout.trim()) {
    return { ok: false, output: combinedGitOutput(outputs), message: `Landing worktree is dirty:\n${status.stdout.trim()}` }
  }

  if (await isRebaseInProgress(workDir, signal)) {
    return { ok: false, output: combinedGitOutput(outputs), message: "Rebase is still in progress after landing commit" }
  }
  const mergeHead = await git(workDir, ["rev-parse", "--git-path", "MERGE_HEAD"], signal)
  if (mergeHead.success) {
    const path = mergeHead.stdout.trim()
    if (path && exists(resolveGitPath(workDir, path))) {
      return { ok: false, output: combinedGitOutput(outputs), message: "Merge is still in progress after landing commit" }
    }
  }

  const parents = await git(workDir, ["log", "-1", "--format=%P", landingSha], signal)
  outputs.push(parents.combinedOutput)
  if (!parents.success) {
    return { ok: false, output: combinedGitOutput(outputs), message: `Could not read landing commit parents: ${parents.combinedOutput}` }
  }
  const parentList = parents.stdout.trim().split(/\s+/).filter(Boolean)
  if (parentList.length !== 1 || parentList[0] !== baseSha) {
    return {
      ok: false,
      output: combinedGitOutput(outputs),
      message: `Landing commit parent mismatch. expected=${baseSha} actual=${parentList.join(",") || "<none>"}`,
    }
  }

  return { ok: true, output: combinedGitOutput(outputs) }
}

async function pushLandingCommit(workDir: string, remote: string, target: string, landingSha: string, signal: AbortSignal) {
  return await git(workDir, ["push", remote, `${landingSha}:refs/heads/${target}`], signal)
}

async function verifyRemoteRef(workDir: string, remote: string, target: string, signal: AbortSignal) {
  const result = await git(workDir, ["ls-remote", remote, `refs/heads/${target}`], signal)
  if (!result.success) return { ...result, remoteSha: null }
  const sha = parseLsRemoteSha(result.stdout)
  if (!sha) {
    return {
      success: false,
      stdout: result.stdout,
      stderr: result.stderr,
      exitCode: result.exitCode,
      combinedOutput: result.combinedOutput,
      remoteSha: null,
    }
  }
  return { ...result, remoteSha: sha }
}

function parseLsRemoteSha(stdout: string) {
  const firstLine = stdout.split("\n").map((line) => line.trim()).find(Boolean)
  if (!firstLine) return null
  const [sha] = firstLine.split(/\s+/)
  return sha || null
}

function isRemoteAdvancedRejection(message: string) {
  const lower = message.toLowerCase()
  // Git's standard remote-advanced message reads:
  //   ! [rejected] <ref> -> <ref> (non-fast-forward)
  // or, in newer git: "Updates were rejected because the tip of your current branch is behind".
  // Match those specific phrasings; do not match generic "rejected" / "fetch first"
  // because they also appear in authentication / refspec / permission failures
  // and we want the merge action to fail with a specific phase rather than
  // burning retry attempts on a non-race failure.
  return lower.includes("non-fast-forward") || lower.includes("updates were rejected")
}

interface DirtyWorktree {
  staged: string[]
  unstaged: string[]
  untracked: string[]
}

async function collectDirtyWorktree(workDir: string, signal: AbortSignal): Promise<DirtyWorktree | null> {
  const status = await git(workDir, ["status", "--porcelain"], signal)
  if (!status.success) return null
  if (!status.stdout.trim()) return { staged: [], unstaged: [], untracked: [] }
  const staged: string[] = []
  const unstaged: string[] = []
  const untracked: string[] = []
  for (const line of status.stdout.split("\n")) {
    if (!line) continue
    const code = line.slice(0, 2)
    const file = line.slice(3).trim()
    if (code === "??") {
      untracked.push(file)
    } else {
      if (code[0] !== " ") staged.push(file)
      if (code[1] !== " ") unstaged.push(file)
    }
  }
  return { staged, unstaged, untracked }
}

function isWorktreeClean(dirty: DirtyWorktree) {
  return dirty.staged.length === 0 && dirty.unstaged.length === 0 && dirty.untracked.length === 0
}

function describeDirty(dirty: DirtyWorktree) {
  const lines: string[] = []
  if (dirty.staged.length > 0) lines.push(`staged: ${dirty.staged.join(", ")}`)
  if (dirty.unstaged.length > 0) lines.push(`unstaged: ${dirty.unstaged.join(", ")}`)
  if (dirty.untracked.length > 0) lines.push(`untracked: ${dirty.untracked.join(", ")}`)
  return lines.join("\n")
}

async function isRebaseInProgress(workDir: string, signal: AbortSignal) {
  const merge = await git(workDir, ["rev-parse", "--git-path", "rebase-merge"], signal)
  if (merge.success) {
    const path = merge.stdout.trim()
    if (path && exists(resolveGitPath(workDir, path))) return true
  }
  const apply = await git(workDir, ["rev-parse", "--git-path", "rebase-apply"], signal)
  if (!apply.success) return false
  const path = apply.stdout.trim()
  if (path && exists(resolveGitPath(workDir, path))) return true
  return false
}

async function mergeConflictFiles(workDir: string, signal: AbortSignal) {
  const status = await git(workDir, ["diff", "--name-only", "--diff-filter=U"], signal)
  if (!status.success || !status.stdout.trim()) return []
  return [...new Set(status.stdout.split("\n").map((line) => line.trim()).filter(Boolean))]
}

function mergeReadyResult(canMerge: boolean, targetBranch: string, baseSha: string | null, candidateHeadSha: string | null, mergeBaseSha: string | null, error: string | null, exitCode: number | null, conflictFiles: string[], checkedAt: string): ActionResult {
  const output = JSON.stringify({ kind: "merge-ready", targetBranch, strategy: "squash", baseSha: baseSha ?? "", candidateHeadSha: candidateHeadSha ?? "", mergeBaseSha: mergeBaseSha ?? "", canMerge, conflictFiles, checkedAt, error })
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

async function commitPendingSourceChanges(workDir: string, message: string, signal: AbortSignal) {
  const status = await git(workDir, ["status", "--porcelain"], signal)
  if (!status.success || !status.stdout.trim()) return status.success ? { ...status, combinedOutput: "" } : status
  const add = await git(workDir, ["add", "."], signal)
  if (!add.success) return add
  return await git(workDir, ["commit", "-m", `${message} integration`], signal)
}

async function squashMerge(workDir: string, source: string, target: string, message: string, context: ActionContext) {
  const checkout = await git(workDir, ["checkout", target], context.signal)
  if (!checkout.success) return checkout
  const merge = await git(workDir, ["merge", "--squash", source], context.signal)
  if (!merge.success) return merge
  return await finishSquashMerge(workDir, source, target, message, context)
}

async function finishSquashMerge(workDir: string, source: string, target: string, message: string, context: ActionContext) {
  const title = stringAt(context.variables, ["issue", "title"]) ?? message
  const numberStr = typeof context.issueNumber === "number" && context.issueNumber > 0
    ? String(context.issueNumber)
    : numberAtString(context.variables, ["issue", "number"])
  const header = numberStr ? `${title} (#${numberStr})` : title

  const logResult = await git(workDir, ["log", "--format=* %s", `${target}..${source}`], context.signal)
  const body = logResult.success ? logResult.stdout.trim() : ""

  return body
    ? await git(workDir, ["commit", "-m", header, "-m", trim(body)], context.signal)
    : await git(workDir, ["commit", "-m", header], context.signal)
}

async function finishRegularMerge(workDir: string, message: string, signal: AbortSignal) {
  return await git(workDir, ["commit", "-m", message], signal)
}

function mergeConflictFailure(
  input: { source: string; target?: string; strategy: string },
  conflicts: string[],
  attempts: number,
  outputs: string[],
  diagnostics: MergeDiagnostics,
  exitCode: number,
): ActionResult {
  const output = JSON.stringify({
    kind: "merge",
    source: input.source,
    target: input.target,
    strategy: input.strategy,
    targetBranch: diagnostics.targetBranch,
    baseSha: diagnostics.baseSha,
    candidateHeadSha: diagnostics.candidateHeadSha,
    mergeBaseSha: diagnostics.mergeBaseSha,
    commit: null,
    conflicts,
    resolveAttempts: attempts,
    output: combinedGitOutput(outputs),
  })
  return { status: "failure", message: combinedGitOutput(outputs), output, exitCode }
}

function mergeFailure(input: Omit<MergeEvidence, "kind"> & { message: string; output: string }): ActionResult {
  const { message, output, ...rest } = input
  const evidence: MergeEvidence = { kind: "merge", message, output, ...rest }
  return { status: "failure", message, output: JSON.stringify(evidence), exitCode: 1 }
}

function mergeSuccess(input: Omit<MergeEvidence, "kind" | "message" | "phase"> & { output: string }): ActionResult {
  const { output, ...rest } = input
  const evidence: MergeEvidence = { kind: "merge", message: "Merge completed", output, ...rest }
  return { status: "success", message: "Merge completed", output: JSON.stringify(evidence) }
}

type MergeDiagnostics = { targetBranch: string; baseSha: string | null; candidateHeadSha: string | null; mergeBaseSha: string | null; conflictFiles: string[] }

async function collectMergeDiagnostics(workDir: string, target: string | undefined, source: string, signal: AbortSignal): Promise<MergeDiagnostics> {
  const targetBranch = target ?? "HEAD"
  const base = await git(workDir, ["rev-parse", targetBranch], signal)
  const head = await git(workDir, ["rev-parse", source], signal)
  const mergeBase = base.success && head.success ? await git(workDir, ["merge-base", targetBranch, source], signal) : null
  const conflictFiles = await mergeConflictFiles(workDir, signal)
  return {
    targetBranch,
    baseSha: base.success ? base.stdout.trim() : null,
    candidateHeadSha: head.success ? head.stdout.trim() : null,
    mergeBaseSha: mergeBase?.success ? mergeBase.stdout.trim() : null,
    conflictFiles,
  }
}

function buildMergeConflictPrompt(conflicts: string[], source: string, target: string | undefined, attempt: number) {
  const fileList = conflicts.map((f) => `- ${f}`).join("\n")
  const targetLabel = target ? `\`${source}\` into \`${target}\`` : `\`${source}\``
  return [
    `## Complete Git Merge Conflict Resolution (attempt ${attempt})`,
    "",
    `A merge of ${targetLabel} produced conflicts.`,
    "",
    "Current conflict files:",
    fileList,
    "",
    "Resolution rules:",
    "1. Preserve both sides. Never drop or overwrite either side's intentional changes.",
    "2. Resolve every conflict marker. Search for `<<<<<<<`, `=======`, and `>>>>>>>`; no markers may remain.",
    "3. Stage resolved files with `git add`.",
    "4. Do not create the merge commit. The runner will commit after verifying the conflict is resolved.",
    "5. If verification fails because of your resolution, fix it before finishing.",
  ].join("\n")
}

function boolInput(input: JsonObject | null | undefined, key: string): boolean | undefined {
  const value = input?.[key]
  if (value === undefined || value === null) return undefined
  if (typeof value === "boolean") return value
  if (typeof value === "string") {
    const lower = value.trim().toLowerCase()
    if (lower === "true") return true
    if (lower === "false") return false
  }
  return undefined
}

function firstLine(value: string) {
  return value.replace(/\r\n/g, "\n").trim().split("\n")[0]
}

function trim(value: string) {
  return value.length <= 20_000 ? value : value.slice(0, 20_000)
}

function combinedGitOutput(outputs: string[]) {
  return outputs.map((output) => output.trim()).filter(Boolean).join("\n\n")
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
