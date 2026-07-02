import { numberInput, stringInput } from "../core/json.js"
import { stringAt } from "../core/json-path.js"
import type { ActionContext, ActionResult } from "../core/types.js"
import { runCommand } from "../system/process.js"
import { resolveMergeSubject } from "./github-pr-issue-fields.js"
import { combinedGhOutput, parsePrList } from "./github-pr-parse.js"
import { classifyGhFailure } from "./github-pr-classify.js"
import { waitChecksAndMergePr } from "./github-pr-merge.js"
import { getGitHubPrGh, runGhPrecheck } from "./github-pr-runtime.js"
import type { GitHubPrErrorCode, GitHubPrStep, MergeGitHubPrOutput } from "./github-pr-types.js"

type GhRunner = typeof runCommand

export async function mergeGitHubPrAction(context: ActionContext): Promise<ActionResult> {
  const method = stringInput(context.with, "method") ?? "squash"
  const workDir = stringAt(context.variables, ["workspace", "path"]) ?? context.workDir

  const gh = getGitHubPrGh()

  const steps: GitHubPrStep[] = []
  const record = createRecorder(steps)

  const fail = (
    errorCode: GitHubPrErrorCode,
    message: string,
    payload: Partial<MergeGitHubPrOutput> = {},
  ): ActionResult => buildMergeGitHubPrOutput({
    kind: "merge-github-pr",
    status: "failed",
    prNumber: payload.prNumber ?? null,
    prUrl: payload.prUrl ?? null,
    mergeCommitSha: payload.mergeCommitSha ?? null,
    method: "squash",
    errorCode,
    message,
    output: payload.output ?? message,
    steps,
  })

  if (method !== "squash") {
    return fail("config-error", `Unsupported merge method '${method}'. Supported method: squash.`)
  }

  const ghPrecheck = await runGhPrecheck(gh, workDir, context.signal)
  record("gh-precheck", "gh --version && gh auth status", ghPrecheck.ok ? 0 : ghPrecheck.exitCode, ghPrecheck.output)
  if (!ghPrecheck.ok) {
    return fail("config-error", ghPrecheck.message, { output: ghPrecheck.output })
  }

  const subject = await resolveMergeSubject(context)
  if (subject.kind === "failure") {
    return fail("config-error", subject.message, { output: subject.message })
  }

  const resolvedPr = await resolvePrNumberForMerge(gh, context, workDir, context.signal, record)
  if (resolvedPr.kind === "failure") {
    return fail(resolvedPr.errorCode, resolvedPr.message, { output: resolvedPr.output })
  }

  const merged = await waitChecksAndMergePr(gh, workDir, resolvedPr.prNumber, subject.subject, context.signal, record)
  if (merged.kind === "failure") {
    return fail(merged.errorCode, merged.message, {
      output: merged.output,
      prNumber: resolvedPr.prNumber,
      prUrl: merged.prUrl ?? resolvedPr.prUrl,
    })
  }

  return buildMergeGitHubPrOutput({
    kind: "merge-github-pr",
    status: "completed",
    prNumber: resolvedPr.prNumber,
    prUrl: merged.prUrl ?? resolvedPr.prUrl,
    mergeCommitSha: merged.mergeCommitSha,
    method: "squash",
    errorCode: null,
    message: null,
    output: merged.output,
    steps,
  })
}

function createRecorder(steps: GitHubPrStep[]) {
  return (name: string, command: string, exitCode: number, output: string) => {
    steps.push({ name, command, exitCode, output })
  }
}

async function resolvePrNumberForMerge(
  gh: GhRunner,
  context: ActionContext,
  workDir: string,
  signal: AbortSignal,
  record: (name: string, command: string, exitCode: number, output: string) => void,
): Promise<
  | { kind: "ok"; prNumber: number; prUrl: string | null }
  | { kind: "failure"; errorCode: GitHubPrErrorCode; message: string; output: string }
> {
  const explicit = numberInput(context.with, "prNumber")
  if (explicit !== undefined) return { kind: "ok", prNumber: explicit, prUrl: null }

  const source = stringInput(context.with, "source") ?? stringAt(context.variables, ["workspace", "branch"])
  const target = stringInput(context.with, "target") ?? stringAt(context.variables, ["repository", "baseBranch"]) ?? stringAt(context.variables, ["project", "defaultBranch"]) ?? stringAt(context.variables, ["project", "baseBranch"]) ?? "main"
  if (!source) {
    return {
      kind: "failure",
      errorCode: "config-error",
      message: "merge-github-pr requires prNumber or source branch.",
      output: "merge-github-pr requires prNumber or source branch.",
    }
  }

  const listResult = await gh("gh", ["pr", "list", "--head", source, "--base", target, "--state", "open", "--json", "number,url"], workDir, signal)
  const listOutput = combinedGhOutput(listResult)
  record("gh-pr-list", `pr list --head ${source} --base ${target} --state open --json number,url`, listResult.exitCode, listOutput)
  if (listResult.exitCode !== 0) {
    return {
      kind: "failure",
      errorCode: classifyGhFailure(listResult.stdout, listResult.stderr),
      message: `gh pr list failed: ${listOutput}`,
      output: listOutput,
    }
  }

  const existing = parsePrList(listResult.stdout)
  if (existing.length === 0) {
    return {
      kind: "failure",
      errorCode: "pr-state-conflict",
      message: `No open PR found for ${source} -> ${target}.`,
      output: listOutput,
    }
  }
  return { kind: "ok", prNumber: existing[0]!.number, prUrl: existing[0]!.url }
}

export function buildMergeGitHubPrOutput(output: MergeGitHubPrOutput): ActionResult {
  const json = JSON.stringify(output)
  if (output.status === "completed") {
    return { status: "success", message: "GitHub pull request merged", output: json }
  }
  return {
    status: "failure",
    message: `Merge GitHub PR failed (${output.errorCode ?? "unknown"}): ${output.message ?? output.output}`,
    output: json,
    exitCode: 1,
  }
}
