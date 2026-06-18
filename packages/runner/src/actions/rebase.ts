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
  const maxRetries = numberInput(context.with, "maxConflictRetries") ?? DEFAULT_MAX_CONFLICT_RETRIES
  const conflictResolver = objectInput(context.with, "conflictResolver")
  const abortResult = await abortRebaseIfInProgress(context)
  if (!abortResult.success) {
    return rebaseOutput(false, baseBranch, null, null, [], 0, abortResult.combinedOutput, abortResult.exitCode)
  }
  const sourceCommit = await commitPendingChanges(context.workDir, `Prepare rebase onto ${baseBranch}`, context.signal)
  if (!sourceCommit.success) {
    return rebaseOutput(false, baseBranch, null, null, [], 0, sourceCommit.combinedOutput, sourceCommit.exitCode)
  }
  const before = await git(context.workDir, ["rev-parse", "HEAD"], context.signal)
  const beforeSha = before.success ? before.stdout.trim() : null

  const result = await git(context.workDir, ["rebase", baseBranch], context.signal)
  if (result.success) {
    const after = await git(context.workDir, ["rev-parse", "HEAD"], context.signal)
    return rebaseOutput(true, baseBranch, beforeSha, after.success ? after.stdout.trim() : null, [], 0, result.combinedOutput)
  }

  let conflicts = await conflictFiles(context)
  if (conflicts.length === 0) {
    return rebaseOutput(false, baseBranch, beforeSha, null, [], 0, result.combinedOutput, result.exitCode)
  }

  if (!conflictResolver) {
    await git(context.workDir, ["rebase", "--abort"], context.signal)
    return rebaseOutput(false, baseBranch, beforeSha, null, conflicts, 0, result.combinedOutput, result.exitCode)
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
      return rebaseOutput(false, baseBranch, beforeSha, null, conflicts, attempts, combinedGitOutput(gitOutputs), 1)
    }

    const verified = await verifyRebaseComplete(context, baseBranch)
    gitOutputs.push(verified.output)
    if (verified.ok) {
      const after = await git(context.workDir, ["rev-parse", "HEAD"], context.signal)
      return rebaseOutput(true, baseBranch, beforeSha, after.success ? after.stdout.trim() : null, conflicts.flat(), attempts, combinedGitOutput(gitOutputs))
    }

    conflicts = await conflictFiles(context)
    if (conflicts.length > 0) allConflicts.push(conflicts)
  }

  await git(context.workDir, ["rebase", "--abort"], context.signal)
  return rebaseOutput(false, baseBranch, beforeSha, null, allConflicts.flat(), attempts, combinedGitOutput(gitOutputs), 1)
}

function rebaseOutput(
  rebased: boolean,
  baseBranch: string,
  beforeSha: string | null,
  afterSha: string | null,
  conflicts: string[],
  resolveAttempts: number,
  gitOutput: string,
  exitCode: number | null = null,
): ActionResult {
  const output = JSON.stringify({
    kind: "rebase",
    status: rebased ? "completed" : "failed",
    baseBranch,
    beforeHeadSha: beforeSha,
    afterHeadSha: afterSha,
    rebased,
    conflicts,
    resolveAttempts,
    output: gitOutput,
  })
  return rebased
    ? { status: "success", message: "Rebase completed", output }
    : { status: "failure", message: `Rebase failed after ${resolveAttempts} conflict resolution attempts`, output, exitCode: exitCode ?? 1 }
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
    `## Complete Git Rebase Conflict Resolution (attempt ${attempt})`,
    "",
    `A \`git rebase ${baseBranch}\` produced merge conflicts. You are now inside an in-progress rebase.`,
    "",
    "Current conflict files:",
    fileList,
    "",
    "Resolution rules:",
    "1. Preserve both sides. Never drop or overwrite either side's intentional changes.",
    "2. Resolve every conflict marker. Search for `<<<<<<<`, `=======`, and `>>>>>>>` across the repository; no markers may remain.",
    "3. Stage resolved files and continue the rebase yourself.",
    "4. The rebase may have conflicts in multiple commits. Keep looping until the rebase is fully complete.",
    "5. If verification fails because of your resolution, fix it before finishing.",
    "6. After the rebase fully completes, run focused verification relevant to the conflicted files. Avoid broad evidence-generation tests or unrelated full-suite commands unless the conflict requires them.",
    "",
    "Steps - loop until complete:",
    "1. Read each conflict file.",
    "2. For each block, understand what the base branch changed and what the issue branch changed.",
    "3. Merge both changes intelligently and remove all conflict markers.",
    "4. Run `git add` for resolved files.",
    "5. Run `GIT_EDITOR=true git rebase --continue`.",
    "6. If more conflicts appear, go back to step 1. Do not stop after only one commit.",
    "7. When rebase completes, verify there is no rebase in progress and no conflict markers remain.",
    "8. Run focused verification relevant to the conflicted files. If anything fails, fix it before finishing.",
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

export function combinedRebaseGitOutput(outputs: string[]) {
  return combinedGitOutput(outputs)
}

export async function rebaseStatusAction(context: ActionContext): Promise<ActionResult> {
  const baseBranch = stringInput(context.with, "baseBranch") ?? "main"
  const conflicts = await conflictFiles(context)
  const rebaseInProgress = await isRebaseInProgress(context)
  const head = await git(context.workDir, ["rev-parse", "HEAD"], context.signal)
  const base = await git(context.workDir, ["rev-parse", baseBranch], context.signal)
  const mergeBase = base.success ? await git(context.workDir, ["merge-base", baseBranch, "HEAD"], context.signal) : null
  const verified = !rebaseInProgress && conflicts.length === 0 && head.success && base.success && mergeBase?.success === true && mergeBase.stdout.trim() === base.stdout.trim()
  const output = JSON.stringify({
    kind: "rebase-status",
    status: verified ? "verified" : "failed",
    baseBranch,
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
  const ok =
    !rebaseInProgress &&
    conflicts.length === 0 &&
    head.success &&
    base.success &&
    mergeBase?.success === true &&
    mergeBase.stdout.trim() === base.stdout.trim()
  const output = [
    rebaseInProgress ? "Rebase is still in progress." : "",
    conflicts.length > 0 ? `Conflicts remain:\n${conflicts.join("\n")}` : "",
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
