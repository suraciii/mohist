import { git as defaultGit, type GitOptions } from "./git.js"
import type { ActionResult, JsonObject } from "../core/types.js"
import type { ActionHost } from "./host.js"
import { stringInput } from "../core/json.js"
import { fail, succeed } from "./action-result.js"
import { currentRunnerResources, type RunnerGitRunner } from "../system/filesystem.js"

export type ActionHandler = (inputs: JsonObject, host: ActionHost) => Promise<ActionResult>
type GitRunner = RunnerGitRunner

export async function mergeReadyAction(inputs: JsonObject, host: ActionHost): Promise<ActionResult> {
  const baseBranch = stringInput(inputs, "baseBranch")
  if (!baseBranch) return fail("invalid-input", "Merge readiness requires input 'baseBranch'")
  const remote = stringInput(inputs, "remote")
  if (!remote) return fail("invalid-input", "Merge readiness requires input 'remote'")
  const baseRef = `${remote}/${baseBranch}`
  const source = stringInput(inputs, "source")
  if (!source) return fail("invalid-input", "Merge readiness requires input 'source'")
  const workDir = host.workDir
  const checkedAt = new Date().toISOString()
  const opts: GitOptions | undefined = host.log ? { sink: { log: host.log, source: "action:merge-ready" } } : undefined

  const git = currentRunnerResources()?.deliveryGitRunner ?? currentRunnerResources()?.gitRunner ?? defaultGit
  const base = await git(workDir, ["rev-parse", baseRef], host.signal, opts)
  if (!base.success) return mergeReadyResult(false, baseBranch, null, null, null, `Could not resolve base branch '${baseRef}'`, base.exitCode, [], checkedAt)

  const head = await git(workDir, ["rev-parse", source], host.signal, opts)
  if (!head.success) return mergeReadyResult(false, baseBranch, base.stdout.trim(), null, null, "Could not resolve source", head.exitCode, [], checkedAt)

  const mergeBase = await git(workDir, ["merge-base", baseRef, source], host.signal, opts)
  const ancestorCheck = await git(workDir, ["merge-base", "--is-ancestor", baseRef, source], host.signal, opts)
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
