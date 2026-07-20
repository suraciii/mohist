import { numberInput, stringInput } from "../core/json.js"
import { stringAt } from "../core/json-path.js"
import type { ActionContext, ActionResult } from "../core/types.js"
import { runCommand, type CommandLineOptions } from "../system/process.js"
import { NETWORK_COMMAND_TIMEOUT_MS } from "./git.js"
import { resolveMergeSubject } from "./github-pr-issue-fields.js"
import { combinedGhOutput, parsePrList } from "./github-pr-parse.js"
import { classifyGhFailure } from "./github-pr-classify.js"
import { waitChecksAndMergePr } from "./github-pr-merge.js"
import { getGitHubPrGh, runGhPrecheck } from "./github-pr-runtime.js"
import { timeoutStepMetadata, type GitHubPrErrorCode, type GitHubPrStep, type GitHubPrStepMetadata, type MergeGitHubPrOutput } from "./github-pr-types.js"
import { resolveDeliveryBaseBranch, resolveDeliverySource, resolveGitHubRepository } from "./delivery-context.js"
import { fail as actionFail, succeed } from "./action-result.js"

type GhRunner = typeof runCommand
const ACTION_SOURCE = "action:merge-github-pr"

export async function mergeGitHubPrAction(context: ActionContext): Promise<ActionResult> {
  const method = stringInput(context.with, "method") ?? "squash"
  const workDir = stringAt(context.variables, ["workspace", "path"]) ?? context.workDir

  const gh = getGitHubPrGh()
  const ghOpts = ghLineOptions(context)

  const steps: GitHubPrStep[] = []
  const record = createRecorder(steps)

  const fail = (
    errorCode: GitHubPrErrorCode,
    message: string,
    payload: Partial<MergeGitHubPrOutput> = {},
  ): ActionResult => {
    void payload
    return actionFail(errorCode, message, { exitCode: 1 })
  }
  const githubRepository = resolveGitHubRepository(context)
  if (githubRepository === null) return fail("config-error", "merge-github-pr requires an authoritative GitHub repository URL")

  if (method !== "squash") {
    return fail("config-error", `Unsupported merge method '${method}'. Supported method: squash.`)
  }

  const ghPrecheck = await runGhPrecheck(gh, workDir, context.signal, ghOpts)
  record("gh-precheck", "gh --version && gh auth status", ghPrecheck.ok ? 0 : ghPrecheck.exitCode, ghPrecheck.output, ghPrecheck.ok ? undefined : timeoutStepMetadata(ghPrecheck))
  if (!ghPrecheck.ok) {
    return fail("config-error", ghPrecheck.message, { output: ghPrecheck.output })
  }

  const subject = await resolveMergeSubject(context)
  if (subject.kind === "failure") {
    return fail("config-error", subject.message, { output: subject.message })
  }

  const resolvedPr = await resolvePrNumberForMerge(gh, context, workDir, context.signal, record, ghOpts, githubRepository)
  if (resolvedPr.kind === "failure") {
    return fail(resolvedPr.errorCode, resolvedPr.message, { output: resolvedPr.output })
  }

  const merged = await waitChecksAndMergePr(gh, workDir, resolvedPr.prNumber, subject.subject, context.signal, record, ghOpts, githubRepository)
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
  return (name: string, command: string, exitCode: number, output: string, metadata?: GitHubPrStepMetadata) => {
    steps.push({ name, command, exitCode, output, ...metadata })
  }
}

function ghLineOptions(context: ActionContext): CommandLineOptions | undefined {
  if (!context.log) return { timeoutMs: NETWORK_COMMAND_TIMEOUT_MS }
  return { onLine: (line) => context.log!.write(ACTION_SOURCE, line), timeoutMs: NETWORK_COMMAND_TIMEOUT_MS }
}

async function resolvePrNumberForMerge(
  gh: GhRunner,
  context: ActionContext,
  workDir: string,
  signal: AbortSignal,
  record: (name: string, command: string, exitCode: number, output: string, metadata?: GitHubPrStepMetadata) => void,
  options?: CommandLineOptions,
  githubRepository?: string,
): Promise<
  | { kind: "ok"; prNumber: number; prUrl: string | null }
  | { kind: "failure"; errorCode: GitHubPrErrorCode; message: string; output: string }
> {
  const explicit = numberInput(context.with, "prNumber")
  if (explicit !== undefined) return { kind: "ok", prNumber: explicit, prUrl: null }

  const source = resolveDeliverySource(context)
  const target = resolveDeliveryBaseBranch(context)
  if (!target) {
    return {
      kind: "failure",
      errorCode: "config-error",
      message: "merge-github-pr requires the authoritative repository base branch.",
      output: "merge-github-pr requires the authoritative repository base branch.",
    }
  }
  if (!source) {
    return {
      kind: "failure",
      errorCode: "config-error",
      message: "merge-github-pr requires prNumber or source branch.",
      output: "merge-github-pr requires prNumber or source branch.",
    }
  }

  const listResult = await gh("gh", withGitHubRepository(["pr", "list", "--head", source, "--base", target, "--state", "open", "--json", "number,url"], githubRepository), workDir, signal, undefined, options)
  const listOutput = combinedGhOutput(listResult)
  record("gh-pr-list", `pr list --head ${source} --base ${target} --state open --json number,url`, listResult.exitCode, listOutput, timeoutStepMetadata(listResult))
  if (listResult.exitCode !== 0) {
    return {
      kind: "failure",
      errorCode: classifyGhFailure(listResult.stdout, listResult.stderr, listResult.status),
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
  if (output.status === "completed") {
    const { errorCode: _errorCode, message: _message, ...success } = output
    return succeed(JSON.stringify(success))
  }
  return actionFail(output.errorCode ?? "merge-failed", output.message ?? output.output, { exitCode: 1 })
}

function withGitHubRepository(args: string[], githubRepository?: string): string[] {
  return githubRepository ? [...args, "--repo", githubRepository] : args
}
