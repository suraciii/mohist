import { join } from "node:path"
import { exists } from "../system/process.js"
import type { ActionContext, ActionResult, JsonObject } from "../core/types.js"
import { numberInput, objectInput, stringInput } from "../core/json.js"
import { acpAgentAction } from "./acp-agent.js"
import { git } from "./git.js"

const DEFAULT_MAX_CONFLICT_RETRIES = 3

export async function rebaseAction(context: ActionContext): Promise<ActionResult> {
  const baseBranch = stringInput(context.with, "baseBranch") ?? "main"
  const maxRetries = numberInput(context.with, "maxConflictRetries") ?? DEFAULT_MAX_CONFLICT_RETRIES
  const conflictResolver = objectInput(context.with, "conflictResolver")
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
  let attempts = 0

  while (attempts < maxRetries) {
    attempts++
    const agentResult = await runConflictResolver(context, conflictResolver, conflicts, baseBranch, attempts)
    if (agentResult.status !== "success") {
      await git(context.workDir, ["rebase", "--abort"], context.signal)
      return rebaseOutput(false, baseBranch, beforeSha, null, conflicts, attempts, result.combinedOutput, 1)
    }

    await git(context.workDir, ["add", "."], context.signal)
    const continued = await git(context.workDir, ["-c", "core.editor=true", "rebase", "--continue"], context.signal)
    if (continued.success) {
      const after = await git(context.workDir, ["rev-parse", "HEAD"], context.signal)
      return rebaseOutput(true, baseBranch, beforeSha, after.success ? after.stdout.trim() : null, conflicts.flat(), attempts, result.combinedOutput)
    }

    conflicts = await conflictFiles(context)
    if (conflicts.length === 0) {
      const after = await git(context.workDir, ["rev-parse", "HEAD"], context.signal)
      return rebaseOutput(true, baseBranch, beforeSha, after.success ? after.stdout.trim() : null, allConflicts.flat(), attempts, result.combinedOutput)
    }
    allConflicts.push(conflicts)
  }

  await git(context.workDir, ["rebase", "--abort"], context.signal)
  return rebaseOutput(false, baseBranch, beforeSha, null, allConflicts.flat(), attempts, result.combinedOutput, 1)
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

  const resolverContext: ActionContext = {
    ...context,
    workId: `${context.workId}-conflict-resolve-${attempt}`,
    workType: "task",
    title: stringInput(conflictResolver, "title") ?? "Resolve rebase conflicts",
    with: resolverWith,
  }

  return acpAgentAction(resolverContext)
}

function buildConflictPrompt(conflicts: string[], baseBranch: string, attempt: number): string {
  const fileList = conflicts.map((f) => `- ${f}`).join("\n")
  return [
    `## Git Rebase Conflict Resolution (attempt ${attempt})`,
    "",
    `A \`git rebase ${baseBranch}\` produced merge conflicts in the following files:`,
    "",
    fileList,
    "",
    "Resolve every conflict marker in each file listed above. For each file:",
    "1. Open the file and find all `<<<<<<<`, `=======`, and `>>>>>>>` markers.",
    "2. Choose the correct resolution (typically keep incoming changes from the branch being rebased onto, unless the current branch has newer intentional changes).",
    "3. Remove all conflict markers and leave clean, correct code.",
    "4. Do NOT run `git add` or `git rebase --continue` — those will be handled automatically.",
    "",
    "After resolving all conflicts, verify the project still builds/tests pass if possible.",
  ].join("\n")
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

async function isRebaseInProgress(context: ActionContext) {
  const merge = await git(context.workDir, ["rev-parse", "--git-path", "rebase-merge"], context.signal)
  if (merge.success && exists(resolveGitPath(context.workDir, merge.stdout.trim()))) return true
  const apply = await git(context.workDir, ["rev-parse", "--git-path", "rebase-apply"], context.signal)
  return apply.success && exists(resolveGitPath(context.workDir, apply.stdout.trim()))
}

function resolveGitPath(workDir: string, path: string) {
  return path.match(/^[A-Za-z]:[\\/]|^\//) ? path : join(workDir, path)
}
