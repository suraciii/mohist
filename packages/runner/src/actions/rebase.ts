import { join } from "node:path"
import { exists } from "../system/process.js"
import type { ActionContext, ActionResult, JsonObject } from "../core/types.js"
import { numberInput, objectInput, stringInput } from "../core/json.js"
import { acpAgentAction } from "./acp-agent.js"
import { git as defaultGit } from "./git.js"

const DEFAULT_MAX_CONFLICT_RETRIES = 3

type GitRunner = typeof defaultGit
type ExistsChecker = typeof exists
type ConflictResolverRunner = typeof acpAgentAction
type GitResult = Awaited<ReturnType<GitRunner>>
let git: GitRunner = defaultGit
let pathExists: ExistsChecker = exists
let conflictResolverRunner: ConflictResolverRunner = acpAgentAction

export type RebaseGitResult = GitResult

export function setRebaseGitRunnerForTest(runner: GitRunner | null) {
  git = runner ?? defaultGit
}

export function setRebaseExistsCheckerForTest(checker: ExistsChecker | null) {
  pathExists = checker ?? exists
}

export function setRebaseConflictResolverForTest(runner: ConflictResolverRunner | null) {
  conflictResolverRunner = runner ?? acpAgentAction
}

export async function rebaseAction(context: ActionContext): Promise<ActionResult> {
  const baseBranch = stringInput(context.with, "baseBranch") ?? "main"
  const remote = stringInput(context.with, "remote") ?? null
  const squash = booleanInput(context.with, "squash") === true
  const squashMessage = stringInput(context.with, "message")
  const maxRetries = numberInput(context.with, "maxConflictRetries") ?? DEFAULT_MAX_CONFLICT_RETRIES
  const conflictResolver = objectInput(context.with, "conflictResolver")
  const baseRef = remote ? `${remote}/${baseBranch}` : baseBranch
  const requiresSquashMessage = squash && !squashMessage
  const abortResult = await abortRebaseIfInProgress(context)
  if (!abortResult.success) {
    return rebaseOutput(false, baseBranch, remote, baseRef, null, null, null, null, false, [], 0, abortResult.combinedOutput, requiresSquashMessage ? "squash-message-missing" : "retry-safe", abortResult.exitCode)
  }
  if (remote) {
    const fetch = await git(context.workDir, ["fetch", remote, baseBranch], context.signal)
    if (!fetch.success) {
      return rebaseOutput(false, baseBranch, remote, baseRef, null, null, null, null, false, [], 0, fetch.combinedOutput, "retry-safe", fetch.exitCode)
    }
  }
  const baseShaResult = await git(context.workDir, ["rev-parse", baseRef], context.signal)
  if (!baseShaResult.success) {
    return rebaseOutput(false, baseBranch, remote, baseRef, null, null, null, null, false, [], 0, baseShaResult.combinedOutput, "retry-safe", baseShaResult.exitCode)
  }
  const baseSha = baseShaResult.stdout.trim()
  const sourceCommit = await commitPendingChanges(context.workDir, `Prepare rebase onto ${baseBranch}`, context.signal)
  if (!sourceCommit.success) {
    return rebaseOutput(false, baseBranch, remote, baseRef, baseSha, null, null, null, false, [], 0, sourceCommit.combinedOutput, "retry-safe", sourceCommit.exitCode)
  }
  const before = await git(context.workDir, ["rev-parse", "HEAD"], context.signal)
  const beforeSha = before.success ? before.stdout.trim() : null

  const result = await git(context.workDir, ["rebase", baseRef], context.signal)
  if (result.success) {
    const after = await git(context.workDir, ["rev-parse", "HEAD"], context.signal)
    const afterSha = after.success ? after.stdout.trim() : null
    return await runSquashIfRequested({
      context,
      baseBranch,
      remote,
      baseRef,
      baseSha,
      beforeSha,
      rebasedHeadSha: afterSha,
      rebaseSucceeded: true,
      conflicts: [],
      resolveAttempts: 0,
      rebaseOutput: result.combinedOutput,
      squash,
      squashMessage,
    })
  }

  let conflicts = await conflictFiles(context)
  if (conflicts.length === 0) {
    return rebaseOutput(false, baseBranch, remote, baseRef, baseSha, beforeSha, null, null, false, [], 0, result.combinedOutput, "retry-safe", result.exitCode)
  }

  if (!conflictResolver) {
    await git(context.workDir, ["rebase", "--abort"], context.signal)
    return rebaseOutput(false, baseBranch, remote, baseRef, baseSha, beforeSha, null, null, false, conflicts, 0, result.combinedOutput, "conflict", 1)
  }

  const allConflicts: string[][] = [conflicts]
  const gitOutputs: string[] = [result.combinedOutput]
  let attempts = 0

  while (attempts < maxRetries) {
    attempts++
    const agentResult = await runConflictResolver(context, conflictResolver, conflicts, baseBranch, attempts)
    if (agentResult.output) gitOutputs.push(agentResult.output)
    if (agentResult.status !== "success") {
      await git(context.workDir, ["rebase", "--abort"], context.signal)
      return rebaseOutput(false, baseBranch, remote, baseRef, baseSha, beforeSha, null, null, false, conflicts, attempts, combinedGitOutput(gitOutputs), "conflict", 1)
    }

    const verified = await verifyRebaseComplete(context, baseRef)
    gitOutputs.push(verified.output)
    if (verified.ok) {
      const after = await git(context.workDir, ["rev-parse", "HEAD"], context.signal)
      const afterSha = after.success ? after.stdout.trim() : null
      return await runSquashIfRequested({
        context,
        baseBranch,
        remote,
        baseRef,
        baseSha,
        beforeSha,
        rebasedHeadSha: afterSha,
        rebaseSucceeded: true,
        conflicts: allConflicts.flat(),
        resolveAttempts: attempts,
        rebaseOutput: combinedGitOutput(gitOutputs),
        squash,
        squashMessage,
      })
    }

    conflicts = await conflictFiles(context)
    if (conflicts.length > 0) allConflicts.push(conflicts)
  }

  await git(context.workDir, ["rebase", "--abort"], context.signal)
  return rebaseOutput(false, baseBranch, remote, baseRef, baseSha, beforeSha, null, null, false, allConflicts.flat(), attempts, combinedGitOutput(gitOutputs), "conflict", 1)
}

interface SquashRequest {
  context: ActionContext
  baseBranch: string
  remote: string | null
  baseRef: string
  baseSha: string
  beforeSha: string | null
  rebasedHeadSha: string | null
  rebaseSucceeded: boolean
  conflicts: string[]
  resolveAttempts: number
  rebaseOutput: string
  squash: boolean
  squashMessage: string | undefined
}

async function runSquashIfRequested(req: SquashRequest): Promise<ActionResult> {
  if (!req.squash) {
    return rebaseOutput(
      req.rebaseSucceeded,
      req.baseBranch,
      req.remote,
      req.baseRef,
      req.baseSha,
      req.beforeSha,
      req.rebasedHeadSha,
      null,
      false,
      req.conflicts,
      req.resolveAttempts,
      req.rebaseOutput,
    )
  }
  if (!req.squashMessage) {
    return rebaseOutput(
      false,
      req.baseBranch,
      req.remote,
      req.baseRef,
      req.baseSha,
      req.beforeSha,
      req.rebasedHeadSha,
      null,
      false,
      req.conflicts,
      req.resolveAttempts,
      req.rebaseOutput,
      "squash-message-missing",
      1,
    )
  }
  const softReset = await git(req.context.workDir, ["reset", "--soft", req.baseSha], req.context.signal)
  if (!softReset.success) {
    return rebaseOutput(
      false,
      req.baseBranch,
      req.remote,
      req.baseRef,
      req.baseSha,
      req.beforeSha,
      req.rebasedHeadSha,
      null,
      false,
      req.conflicts,
      req.resolveAttempts,
      [req.rebaseOutput, softReset.combinedOutput].filter(Boolean).join("\n\n"),
      "retry-safe",
      softReset.exitCode,
    )
  }
  const commit = await git(req.context.workDir, ["commit", "-m", req.squashMessage], req.context.signal)
  if (!commit.success) {
    return rebaseOutput(
      false,
      req.baseBranch,
      req.remote,
      req.baseRef,
      req.baseSha,
      req.beforeSha,
      req.rebasedHeadSha,
      null,
      false,
      req.conflicts,
      req.resolveAttempts,
      [req.rebaseOutput, softReset.combinedOutput, commit.combinedOutput].filter(Boolean).join("\n\n"),
      "retry-safe",
      commit.exitCode,
    )
  }
  const squashedHead = await git(req.context.workDir, ["rev-parse", "HEAD"], req.context.signal)
  const squashedHeadSha = squashedHead.success ? squashedHead.stdout.trim() : null
  const squashOutput = [req.rebaseOutput, softReset.combinedOutput, commit.combinedOutput].filter(Boolean).join("\n\n")
  return rebaseOutput(
    true,
    req.baseBranch,
    req.remote,
    req.baseRef,
    req.baseSha,
    req.beforeSha,
    req.rebasedHeadSha,
    squashedHeadSha,
    true,
    req.conflicts,
    req.resolveAttempts,
    squashOutput,
  )
}

type RebaseFailureKind = "retry-safe" | "conflict" | "squash-message-missing" | null

function rebaseOutput(
  rebased: boolean,
  baseBranch: string,
  remote: string | null,
  baseRef: string,
  baseSha: string | null,
  beforeSha: string | null,
  afterSha: string | null,
  squashedHeadSha: string | null,
  squashed: boolean,
  conflicts: string[],
  resolveAttempts: number,
  gitOutput: string,
  failureKind: RebaseFailureKind = null,
  exitCode: number | null = null,
): ActionResult {
  const output = JSON.stringify({
    kind: "rebase",
    status: rebased ? "completed" : "failed",
    baseBranch,
    remote,
    baseRef,
    rebasedOntoSha: baseSha,
    beforeHeadSha: beforeSha,
    afterHeadSha: afterSha,
    squashed,
    squashedHeadSha,
    rebased,
    conflicts,
    resolveAttempts,
    failureKind,
    output: gitOutput,
  })
  if (rebased) {
    return { status: "success", message: squashed ? "Rebase and squash completed" : "Rebase completed", output }
  }
  const label = failureKind === "conflict"
    ? "Rebase failed: conflict could not be resolved"
    : failureKind === "squash-message-missing"
      ? "Rebase squashed: a commit 'message' is required when 'squash' is true"
      : `Rebase failed after ${resolveAttempts} conflict resolution attempts`
  return { status: "failure", message: label, output, exitCode: exitCode ?? 1 }
}

function booleanInput(input: JsonObject | null | undefined, key: string) {
  const value = input?.[key]
  if (typeof value === "boolean") return value
  if (typeof value === "string") {
    if (/^(true|1|yes|on)$/i.test(value)) return true
    if (/^(false|0|no|off)$/i.test(value)) return false
  }
  return undefined
}

async function runConflictResolver(
  context: ActionContext,
  conflictResolver: JsonObject,
  conflicts: string[],
  baseBranch: string,
  attempt: number,
): Promise<ActionResult> {
  const resolverWith: JsonObject = {
    prompt: buildConflictPrompt(conflicts, baseBranch, attempt),
    ...objectInput(conflictResolver, "with"),
  }
  applyWorkflowAgentDefault(resolverWith, context.variables)

  const resolverContext: ActionContext = {
    ...context,
    workId: `${context.workId}-conflict-resolve-${attempt}`,
    workType: "task",
    title: stringInput(conflictResolver, "title") ?? "Resolve rebase conflicts",
    with: resolverWith,
  }

  return conflictResolverRunner(resolverContext)
}

export function applyWorkflowAgentDefault(with_: JsonObject, variables: JsonObject) {
  if (objectInput(with_, "agent")) return

  const vars = objectInput(variables, "vars")
  const agent = objectInput(vars, "agent") ?? objectInput(variables, "agent")
  if (agent) with_.agent = agent
}

function buildConflictPrompt(conflicts: string[], baseBranch: string, attempt: number): string {
  const fileList = conflicts.map((f) => `- ${f}`).join("\n")
  return [
    `## Rebase Conflict Resolution (attempt ${attempt})`,
    "",
    `A \`git rebase ${baseBranch}\` is in progress and has merge conflicts.`,
    "",
    "<task>",
    "Resolve every conflict and bring the rebase to full completion.",
    "The rebase may have conflicts in multiple commits — keep going until it finishes.",
    "",
    "Conflicted files:",
    fileList,
    "</task>",
    "",
    "<rules>",
    "- Preserve both sides. Never drop or overwrite either side's intentional changes.",
    "- If the resolution introduces compilation or test failures, fix them as part of this work.",
    "</rules>",
    "",
    "<verify>",
    "When you finish, all of the following must be true:",
    "- The rebase is fully complete (no `.git/rebase-merge` or `.git/rebase-apply`).",
    "- No conflict markers remain anywhere in the repository.",
    "- The worktree is clean — no staged, unstaged, or untracked changes. Commit everything you change.",
    "- You are on a named branch, not a detached HEAD.",
    "</verify>",
  ].join("\n")
}

export async function commitRebasePendingChanges(workDir: string, message: string, signal: AbortSignal) {
  return await commitPendingChanges(workDir, message, signal)
}

export async function abortRebaseIfInProgressAction(context: ActionContext) {
  return await abortRebaseIfInProgress(context)
}

export async function rebaseConflictFiles(context: ActionContext) {
  return await conflictFiles(context)
}

export async function verifyRebaseCompleteAction(context: ActionContext, baseBranch: string) {
  return await verifyRebaseComplete(context, baseBranch)
}

export async function runRebaseConflictResolver(
  context: ActionContext,
  conflictResolver: JsonObject,
  conflicts: string[],
  baseBranch: string,
  attempt: number,
): Promise<ActionResult> {
  return await runConflictResolver(context, conflictResolver, conflicts, baseBranch, attempt)
}

export async function runRebaseResolverFollowup(
  context: ActionContext,
  title: string,
  with_: JsonObject,
  workIdSuffix: string,
): Promise<ActionResult> {
  const resolverContext: ActionContext = {
    ...context,
    workId: `${context.workId}-${workIdSuffix}`,
    workType: "task",
    title,
    with: with_,
  }

  return conflictResolverRunner(resolverContext)
}

export function combinedRebaseGitOutput(outputs: string[]) {
  return combinedGitOutput(outputs)
}

export async function rebaseStatusAction(context: ActionContext): Promise<ActionResult> {
  const baseBranch = stringInput(context.with, "baseBranch") ?? "main"
  const remote = stringInput(context.with, "remote")
  const baseRef = remote ? `${remote}/${baseBranch}` : baseBranch
  const conflicts = await conflictFiles(context)
  const rebaseInProgress = await isRebaseInProgress(context)
  const head = await git(context.workDir, ["rev-parse", "HEAD"], context.signal)
  const base = await git(context.workDir, ["rev-parse", baseRef], context.signal)
  const mergeBase = base.success ? await git(context.workDir, ["merge-base", baseRef, "HEAD"], context.signal) : null
  const verified = !rebaseInProgress && conflicts.length === 0 && head.success && base.success && mergeBase?.success === true && mergeBase.stdout.trim() === base.stdout.trim()
  const output = JSON.stringify({
    kind: "rebase-status",
    status: verified ? "verified" : "failed",
    baseBranch,
    remote,
    baseRef,
    rebaseInProgress,
    conflicts,
    baseSha: base.success ? base.stdout.trim() : null,
    headSha: head.success ? head.stdout.trim() : null,
    mergeBaseSha: mergeBase?.success ? mergeBase.stdout.trim() : null,
    output: [base.combinedOutput, mergeBase?.combinedOutput].filter(Boolean).join("\n"),
  })
  return verified ? { status: "success", message: "Rebase verified", output } : { status: "failure", message: "Rebase is not complete or not clean", output }
}

async function conflictFiles(context: ActionContext) {
  const status = await git(context.workDir, ["diff", "--name-only", "--diff-filter=U"], context.signal)
  if (!status.success || !status.stdout.trim()) return []
  return [...new Set(status.stdout.split("\n").map((line) => line.trim()).filter(Boolean))]
}

async function verifyRebaseComplete(context: ActionContext, baseBranch: string) {
  const rebaseInProgress = await isRebaseInProgress(context)
  const conflicts = await conflictFiles(context)
  const head = await git(context.workDir, ["rev-parse", "HEAD"], context.signal)
  const base = await git(context.workDir, ["rev-parse", baseBranch], context.signal)
  const mergeBase = base.success ? await git(context.workDir, ["merge-base", baseBranch, "HEAD"], context.signal) : null
  const branch = await git(context.workDir, ["branch", "--show-current"], context.signal)
  const statusPorcelain = await git(context.workDir, ["status", "--porcelain"], context.signal)

  const detached = branch.exitCode !== 0 || !branch.stdout.trim() || branch.stdout.trim() === "HEAD"
  const dirty = statusPorcelain.success && statusPorcelain.stdout.trim().length > 0

  const ok =
    !rebaseInProgress &&
    conflicts.length === 0 &&
    !detached &&
    !dirty &&
    head.success &&
    base.success &&
    mergeBase?.success === true &&
    mergeBase.stdout.trim() === base.stdout.trim()
  const output = [
    rebaseInProgress ? "Rebase is still in progress." : "",
    conflicts.length > 0 ? `Conflicts remain:\n${conflicts.join("\n")}` : "",
    detached ? `HEAD is detached (branch: ${branch.stdout.trim()})` : "",
    dirty ? `Worktree is not clean:\n${statusPorcelain.stdout.trim()}` : "",
    head.combinedOutput,
    base.combinedOutput,
    mergeBase?.combinedOutput ?? "",
  ].filter(Boolean).join("\n")
  return { ok, output }
}

async function commitPendingChanges(workDir: string, message: string, signal: AbortSignal) {
  const status = await git(workDir, ["status", "--porcelain"], signal)
  if (!status.success || !status.stdout.trim()) return status.success ? { ...status, combinedOutput: "" } : status

  const add = await git(workDir, ["add", "."], signal)
  if (!add.success) return add

  return await git(workDir, ["commit", "-m", message], signal)
}

async function abortRebaseIfInProgress(context: ActionContext) {
  const inProgress = await isRebaseInProgress(context)
  if (!inProgress) return okGitResult()
  return await git(context.workDir, ["rebase", "--abort"], context.signal)
}

async function isRebaseInProgress(context: ActionContext) {
  const merge = await git(context.workDir, ["rev-parse", "--git-path", "rebase-merge"], context.signal)
  if (merge.success && pathExists(resolveGitPath(context.workDir, merge.stdout.trim()))) return true
  const apply = await git(context.workDir, ["rev-parse", "--git-path", "rebase-apply"], context.signal)
  return apply.success && pathExists(resolveGitPath(context.workDir, apply.stdout.trim()))
}

function resolveGitPath(workDir: string, path: string) {
  return path.match(/^[A-Za-z]:[\\/]|^\//) ? path : join(workDir, path)
}

function okGitResult() {
  return { success: true, stdout: "", stderr: "", exitCode: 0, combinedOutput: "" }
}

function combinedGitOutput(outputs: string[]) {
  return outputs.map((output) => output.trim()).filter(Boolean).join("\n\n")
}
