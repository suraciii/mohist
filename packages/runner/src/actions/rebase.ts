import { join } from "node:path"
import { exists } from "../system/process.js"
import type { ActionContext, ActionResult, JsonObject } from "../core/types.js"
import { stringInput } from "../core/json.js"
import { git as defaultGit, NETWORK_COMMAND_TIMEOUT_MS, type GitOptions } from "./git.js"
import { isIssueFieldSource, resolveIssueField } from "./issue-fields.js"
import { fail, succeed } from "./action-result.js"

type GitRunner = (workDir: string, args: string[], signal: AbortSignal, options?: GitOptions) => Promise<{
  success: boolean
  stdout: string
  stderr: string
  exitCode: number
  combinedOutput: string
  status?: "timeout"
  timeoutMs?: number
}>
type ExistsChecker = typeof exists
type GitResult = Awaited<ReturnType<GitRunner>>
interface RebaseStep {
  name: string
  command: string
  exitCode: number
  output: string
  status?: "timeout"
  timeoutMs?: number
}
let git: GitRunner = defaultGit
let pathExists: ExistsChecker = exists

/**
 * `source` tag recorded against every captured `mohist/rebase` action
 * body line. Phase-distinguished from `branch-check` and `workspace-prep`
 * so the web viewer can tell which ops phase produced which line.
 */
const ACTION_SOURCE = "action:rebase"

function sinkOptions(context: ActionContext): GitOptions | undefined {
  return context.log ? { sink: { log: context.log, source: ACTION_SOURCE } } : undefined
}

function networkOptions(context: ActionContext): GitOptions | undefined {
  if (!context.log) return { timeoutMs: NETWORK_COMMAND_TIMEOUT_MS }
  return { sink: { log: context.log, source: ACTION_SOURCE }, timeoutMs: NETWORK_COMMAND_TIMEOUT_MS }
}

export type RebaseGitResult = GitResult

export function setRebaseGitRunnerForTest(runner: GitRunner | null) {
  git = runner ?? defaultGit
}

export function setRebaseExistsCheckerForTest(checker: ExistsChecker | null) {
  pathExists = checker ?? exists
}

export async function rebaseAction(context: ActionContext): Promise<ActionResult> {
  const baseBranch = stringInput(context.with, "baseBranch")
  if (!baseBranch) return fail("invalid-input", "Rebase requires input 'baseBranch'")
  const remote = stringInput(context.with, "remote") ?? null
  const squash = booleanInput(context.with, "squash") === true
  const baseRef = remote ? `${remote}/${baseBranch}` : baseBranch
  const opts = sinkOptions(context)
  const abortResult = await abortRebaseIfInProgress(context, opts)
  if (!abortResult.success) {
    return rebaseOutput(false, baseBranch, remote, baseRef, null, null, null, null, false, [], abortResult.combinedOutput, "abort-failed", abortResult.exitCode)
  }
  const squashMessageResult = squash ? await resolveSquashMessage(context) : { kind: "ok" as const, message: undefined }
  const squashMessage = squashMessageResult.kind === "ok" ? squashMessageResult.message : undefined
  if (squashMessageResult.kind === "failure") {
    return rebaseOutput(false, baseBranch, remote, baseRef, null, null, null, null, false, [], squashMessageResult.message, "invalid-input", 1)
  }
  if (squash && !squashMessage) {
    return fail("invalid-input", "Rebase with squash requires a non-empty commit 'message' or 'messageFrom'")
  }
  if (remote) {
    const fetch = await git(context.workDir, ["fetch", remote, baseBranch], context.signal, networkOptions(context))
    if (!fetch.success) {
      const steps = [rebaseStep("git-fetch-base", `fetch ${remote} ${baseBranch}`, fetch)]
      return rebaseOutput(false, baseBranch, remote, baseRef, null, null, null, null, false, [], fetch.combinedOutput, fetch.status === "timeout" ? "timeout" : "fetch-failed", fetch.exitCode, false, steps)
    }
  }
  const baseShaResult = await git(context.workDir, ["rev-parse", baseRef], context.signal, opts)
  if (!baseShaResult.success) {
    return rebaseOutput(false, baseBranch, remote, baseRef, null, null, null, null, false, [], baseShaResult.combinedOutput, "base-resolve-failed", baseShaResult.exitCode)
  }
  const baseSha = baseShaResult.stdout.trim()
  const sourceCommit = await commitPendingChanges(context.workDir, `Prepare rebase onto ${baseBranch}`, context.signal, opts)
  if (!sourceCommit.success) {
    return rebaseOutput(false, baseBranch, remote, baseRef, baseSha, null, null, null, false, [], sourceCommit.combinedOutput, "prepare-failed", sourceCommit.exitCode)
  }
  const before = await git(context.workDir, ["rev-parse", "HEAD"], context.signal, opts)
  const beforeSha = before.success ? before.stdout.trim() : null

  const result = await git(context.workDir, ["rebase", baseRef], context.signal, opts)
  if (result.success) {
    const after = await git(context.workDir, ["rev-parse", "HEAD"], context.signal, opts)
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
      rebaseOutput: result.combinedOutput,
      squash,
      squashMessage,
    })
  }

  let conflicts = await conflictFiles(context, opts)
  if (conflicts.length === 0) {
    return rebaseOutput(false, baseBranch, remote, baseRef, baseSha, beforeSha, null, null, false, [], result.combinedOutput, result.status === "timeout" ? "timeout" : "rebase-failed", result.exitCode)
  }

  return rebaseOutput(false, baseBranch, remote, baseRef, baseSha, beforeSha, null, null, false, conflicts, result.combinedOutput, "conflict", 1, true)
}

async function resolveSquashMessage(context: ActionContext): Promise<{ kind: "ok"; message: string | undefined } | { kind: "failure"; message: string }> {
  const literal = stringInput(context.with, "message")
  if (literal !== undefined) return { kind: "ok", message: literal }
  const source = stringInput(context.with, "messageFrom")
  if (source === undefined) return { kind: "ok", message: undefined }
  if (!isIssueFieldSource(source)) {
    return { kind: "failure", message: `Unsupported messageFrom source '${source}'. Supported sources: issue.title, issue.body.` }
  }
  try {
    return { kind: "ok", message: await resolveIssueField(context, source) }
  } catch (error) {
    return { kind: "failure", message: errorMessage(error) }
  }
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
      req.rebaseOutput,
      null,
      null,
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
      req.rebaseOutput,
      "invalid-input",
      1,
    )
  }
  const softReset = await git(req.context.workDir, ["reset", "--soft", req.baseSha], req.context.signal, sinkOptions(req.context))
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
      [req.rebaseOutput, softReset.combinedOutput].filter(Boolean).join("\n\n"),
      softReset.status === "timeout" ? "timeout" : "squash-failed",
      softReset.exitCode,
    )
  }
  const commit = await git(req.context.workDir, ["commit", "-m", req.squashMessage], req.context.signal, sinkOptions(req.context))
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
      [req.rebaseOutput, softReset.combinedOutput, commit.combinedOutput].filter(Boolean).join("\n\n"),
      commit.status === "timeout" ? "timeout" : "squash-failed",
      commit.exitCode,
    )
  }
  const squashedHead = await git(req.context.workDir, ["rev-parse", "HEAD"], req.context.signal, sinkOptions(req.context))
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
    squashOutput,
    null,
    null,
  )
}

type RebaseFailureCode =
  | "abort-failed"
  | "invalid-input"
  | "fetch-failed"
  | "base-resolve-failed"
  | "prepare-failed"
  | "rebase-failed"
  | "conflict"
  | "squash-failed"
  | "timeout"
  | null

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
  gitOutput: string,
  failureCode: RebaseFailureCode = null,
  exitCode: number | null = null,
  rebaseLeftInProgress: boolean = false,
  steps: RebaseStep[] = [],
): ActionResult {
  if (!rebased) {
    return fail(failureCode ?? "rebase-failed", rebaseFailureMessage(failureCode, baseRef, conflicts, gitOutput), { exitCode: exitCode ?? 1 })
  }
  const output: JsonObject = {
    kind: "rebase",
    status: "completed",
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
    rebaseLeftInProgress: false,
    output: gitOutput,
    steps: steps as unknown as JsonObject,
  }
  return succeed(output, { exitCode: exitCode ?? 0 })
}

function rebaseFailureMessage(code: RebaseFailureCode, baseRef: string, conflicts: string[], output: string): string {
  const detail = output.trim() || "unknown error"
  if (code === "conflict") {
    const files = conflicts.length > 0 ? ` Conflicts: ${conflicts.join(", ")}.` : ""
    return `Rebase onto ${baseRef} has unresolved conflicts.${files}`
  }
  if (code === "fetch-failed") return `Failed to fetch ${baseRef}: ${detail}. Rebase was not started.`
  if (code === "timeout") return `Rebase operation timed out while preparing ${baseRef}.`
  if (code === "invalid-input") return detail
  return `Rebase onto ${baseRef} failed: ${detail}`
}

function rebaseStep(name: string, command: string, result: GitResult): RebaseStep {
  return { name, command, exitCode: result.exitCode, output: result.combinedOutput, ...timeoutMetadata(result) }
}

function timeoutMetadata(result: GitResult): Pick<RebaseStep, "status" | "timeoutMs"> | undefined {
  if (result.status !== "timeout") return undefined
  return { status: "timeout", timeoutMs: result.timeoutMs }
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

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error)
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

export function combinedRebaseGitOutput(outputs: string[]) {
  return combinedGitOutput(outputs)
}

export async function rebaseStatusAction(context: ActionContext): Promise<ActionResult> {
  const baseBranch = stringInput(context.with, "baseBranch")
  if (!baseBranch) return fail("invalid-input", "Rebase status requires input 'baseBranch'")
  const remote = stringInput(context.with, "remote") ?? null
  const baseRef = remote ? `${remote}/${baseBranch}` : baseBranch
  const opts = sinkOptions(context)
  const conflicts = await conflictFiles(context, opts)
  const rebaseInProgress = await isRebaseInProgress(context, opts)
  const head = await git(context.workDir, ["rev-parse", "HEAD"], context.signal, opts)
  const base = await git(context.workDir, ["rev-parse", baseRef], context.signal, opts)
  const mergeBase = base.success ? await git(context.workDir, ["merge-base", baseRef, "HEAD"], context.signal, opts) : null
  const verified = !rebaseInProgress && conflicts.length === 0 && head.success && base.success && mergeBase?.success === true && mergeBase.stdout.trim() === base.stdout.trim()
  const output: JsonObject = {
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
  }
  return verified ? succeed(output) : fail("rebase-incomplete", "Rebase is not complete or not clean")
}

async function conflictFiles(context: ActionContext, opts?: GitOptions) {
  const status = await git(context.workDir, ["diff", "--name-only", "--diff-filter=U"], context.signal, opts)
  if (!status.success || !status.stdout.trim()) return []
  return [...new Set(status.stdout.split("\n").map((line) => line.trim()).filter(Boolean))]
}

async function verifyRebaseComplete(context: ActionContext, baseBranch: string) {
  const opts = sinkOptions(context)
  const rebaseInProgress = await isRebaseInProgress(context, opts)
  const conflicts = await conflictFiles(context, opts)
  const head = await git(context.workDir, ["rev-parse", "HEAD"], context.signal, opts)
  const base = await git(context.workDir, ["rev-parse", baseBranch], context.signal, opts)
  const mergeBase = base.success ? await git(context.workDir, ["merge-base", baseBranch, "HEAD"], context.signal, opts) : null
  const branch = await git(context.workDir, ["branch", "--show-current"], context.signal, opts)
  const statusPorcelain = await git(context.workDir, ["status", "--porcelain"], context.signal, opts)

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

async function commitPendingChanges(workDir: string, message: string, signal: AbortSignal, opts?: GitOptions) {
  const status = await git(workDir, ["status", "--porcelain"], signal, opts)
  if (!status.success || !status.stdout.trim()) return status.success ? { ...status, combinedOutput: "" } : status

  const add = await git(workDir, ["add", "."], signal, opts)
  if (!add.success) return add

  return await git(workDir, ["commit", "-m", message], signal, opts)
}

async function abortRebaseIfInProgress(context: ActionContext, opts?: GitOptions) {
  const inProgress = await isRebaseInProgress(context, opts)
  if (!inProgress) return okGitResult()
  return await git(context.workDir, ["rebase", "--abort"], context.signal, opts)
}

async function isRebaseInProgress(context: ActionContext, opts?: GitOptions) {
  const merge = await git(context.workDir, ["rev-parse", "--git-path", "rebase-merge"], context.signal, opts)
  if (merge.success && pathExists(resolveGitPath(context.workDir, merge.stdout.trim()))) return true
  const apply = await git(context.workDir, ["rev-parse", "--git-path", "rebase-apply"], context.signal, opts)
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
