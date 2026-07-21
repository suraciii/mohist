import { git as defaultGit, type GitOptions } from "./git.js"
import type { ActionContext, ActionResult, JsonObject } from "../core/types.js"
import { stringAt } from "../core/json-path.js"
import { resolveDeliveryBaseBranch, resolveDeliveryRemote, resolveDeliverySource } from "./delivery-context.js"
import { fail, succeed } from "./action-result.js"

export type ActionHandler = (context: ActionContext) => Promise<ActionResult>
type GitRunner = (workDir: string, args: string[], signal: AbortSignal, options?: GitOptions) => Promise<{
  success: boolean
  stdout: string
  stderr: string
  exitCode: number
  combinedOutput: string
}>

let git: GitRunner = defaultGit

export function setDeliveryGitRunnerForTest(runner: GitRunner | null) {
  git = runner ?? defaultGit
}

export async function mergeReadyAction(context: ActionContext): Promise<ActionResult> {
  const baseBranch = resolveDeliveryBaseBranch(context, "baseBranch")
  if (!baseBranch) return fail("invalid-input", "Merge readiness requires the authoritative repository base branch")
  const remote = resolveDeliveryRemote(context)
  if (!remote) return fail("invalid-input", "Merge readiness requires the authoritative repository origin")
  const baseRef = `${remote}/${baseBranch}`
  const source = resolveDeliverySource(context)
  if (!source) return fail("invalid-input", "Merge readiness requires the authoritative workspace branch")
  const workDir = stringAt(context.variables, ["workspace", "path"]) ?? context.workDir
  const checkedAt = new Date().toISOString()
  const opts: GitOptions | undefined = context.log ? { sink: { log: context.log, source: "action:merge-ready" } } : undefined

  const base = await git(workDir, ["rev-parse", baseRef], context.signal, opts)
  if (!base.success) return mergeReadyResult(false, baseBranch, null, null, null, `Could not resolve base branch '${baseRef}'`, base.exitCode, [], checkedAt)

  const head = await git(workDir, ["rev-parse", source], context.signal, opts)
  if (!head.success) return mergeReadyResult(false, baseBranch, base.stdout.trim(), null, null, "Could not resolve source", head.exitCode, [], checkedAt)

  const mergeBase = await git(workDir, ["merge-base", baseRef, source], context.signal, opts)
  const ancestorCheck = await git(workDir, ["merge-base", "--is-ancestor", baseRef, source], context.signal, opts)
  const mergeBaseSha = mergeBase.success ? mergeBase.stdout.trim() : null

  if (!ancestorCheck.success) {
    return mergeReadyResult(
      false,
      baseBranch,
      base.stdout.trim(),
      head.stdout.trim(),
      mergeBaseSha,
      `Merge candidate '${source}' does not contain the latest '${baseRef}' tip; rebase is required.`,
      ancestorCheck.exitCode,
      [],
      checkedAt,
    )
  }

  return mergeReadyResult(true, baseBranch, base.stdout.trim(), head.stdout.trim(), mergeBaseSha, null, 0, [], checkedAt)
}

function mergeReadyResult(canMerge: boolean, baseBranch: string, baseSha: string | null, headSha: string | null, mergeBaseSha: string | null, error: string | null, exitCode: number | null, conflictFiles: string[], checkedAt: string): ActionResult {
  if (!canMerge) return fail("merge-not-ready", error ?? "Merge is not ready", { exitCode })
  const output: JsonObject = { kind: "merge-ready", targetBranch: baseBranch, strategy: "squash", baseSha: baseSha ?? "", candidateHeadSha: headSha ?? "", mergeBaseSha: mergeBaseSha ?? "", canMerge, conflictFiles, checkedAt }
  return succeed(output, { exitCode })
}
