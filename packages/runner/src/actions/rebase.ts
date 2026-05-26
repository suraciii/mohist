import { join } from "node:path"
import { exists } from "../system/process.js"
import type { ActionContext, ActionResult, JsonObject } from "../core/types.js"
import { objectInput, stringInput } from "../core/json.js"
import { git } from "./git.js"

export async function rebaseAction(context: ActionContext): Promise<ActionResult> {
  const baseBranch = stringInput(context.with, "baseBranch") ?? "main"
  const conflictResolver = objectInput(context.with, "conflictResolver")
  const before = await git(context.workDir, ["rev-parse", "HEAD"], context.signal)
  const result = await git(context.workDir, ["rebase", baseBranch], context.signal)
  const after = result.success ? await git(context.workDir, ["rev-parse", "HEAD"], context.signal) : null
  const conflicts = result.success ? [] : await conflictFiles(context)

  if (!result.success && conflicts.length > 0 && conflictResolver) {
    const output = JSON.stringify({
      kind: "rebase",
      status: "conflict",
      baseBranch,
      beforeHeadSha: before.success ? before.stdout.trim() : null,
      conflicts,
      output: result.combinedOutput,
      requestedTask: buildRequestedTask(conflictResolver, conflicts, baseBranch),
    })
    return { status: "failure", message: "Rebase conflict; conflict resolver task requested", output, exitCode: result.exitCode }
  }

  const output = JSON.stringify({
    kind: "rebase",
    status: result.success ? "completed" : conflicts.length > 0 ? "conflict" : "failed",
    baseBranch,
    beforeHeadSha: before.success ? before.stdout.trim() : null,
    afterHeadSha: after?.success ? after.stdout.trim() : null,
    rebased: result.success,
    conflicts,
    output: result.combinedOutput,
  })
  return result.success ? { status: "success", message: "Rebase completed", output, exitCode: result.exitCode } : { status: "failure", message: result.combinedOutput, output, exitCode: result.exitCode }
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

function buildRequestedTask(conflictResolver: JsonObject, conflicts: string[], baseBranch: string) {
  const withInput = objectInput(conflictResolver, "with") ?? {}
  const withResolved: JsonObject = {
    stage: "maintenance",
    task: "resolve-rebase-conflicts",
    conflicts,
    description: "Resolve git rebase conflicts, stage resolved files, and continue the rebase until it completes.",
    ...withInput,
  }
  return {
    id: stringInput(conflictResolver, "id") ?? "resolve-rebase-conflicts",
    title: stringInput(conflictResolver, "title") ?? "Resolve rebase conflicts",
    uses: stringInput(conflictResolver, "uses") ?? "mohist/acp-agent",
    with: withResolved,
    then: { id: "verify-rebase", title: "Verify rebase completed", uses: "mohist/rebase-status", with: { baseBranch } },
  }
}
