import { numberInput, stringInput } from "../core/json.js"
import type { ActionResult, JsonObject } from "../core/types.js"
import type { ActionHost } from "./host.js"
import { runCommand, type CommandLineOptions } from "../system/process.js"
import { NETWORK_COMMAND_TIMEOUT_MS } from "./git.js"
import { resolveMergeSubject } from "./github-pr-issue-fields.js"
import { combinedGhOutput } from "./github-pr-parse.js"
import { classifyGhFailure } from "./github-pr-classify.js"
import { waitChecksAndMergePr } from "./github-pr-merge.js"
import { getGitHubPrGh, runGhPrecheck } from "./github-pr-runtime.js"
import { timeoutStepMetadata, type GitHubPrErrorCode, type GitHubPrStep, type GitHubPrStepMetadata, type MergeGitHubPrOutput } from "./github-pr-types.js"
import { parseGitHubRepository } from "./github-pr-repository.js"
import { fail as actionFail, succeed } from "./action-result.js"

type GhRunner = typeof runCommand
const ACTION_SOURCE = "action:merge-github-pr"

export async function mergeGitHubPrAction(inputs: JsonObject, host: ActionHost): Promise<ActionResult> {
  const method = stringInput(inputs, "method") ?? "squash"
  const prNumber = numberInput(inputs, "prNumber")
  const repositoryUrl = typeof inputs["repositoryUrl"] === "string" ? inputs["repositoryUrl"] : undefined
  if (prNumber === undefined || !repositoryUrl) return actionFail("invalid-input", "merge-github-pr requires 'repositoryUrl' and 'prNumber'")
  const githubRepository = parseGitHubRepository(repositoryUrl)
  if (!githubRepository) return actionFail("config-error", "merge-github-pr requires a valid GitHub repository URL")
  const workDir = host.workDir

  const gh = getGitHubPrGh()
  const ghOpts = ghLineOptions(host)

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
  if (method !== "squash") {
    return fail("config-error", `Unsupported merge method '${method}'. Supported method: squash.`)
  }

  const ghPrecheck = await runGhPrecheck(gh, workDir, host.signal, ghOpts)
  record("gh-precheck", "gh --version && gh auth status", ghPrecheck.ok ? 0 : ghPrecheck.exitCode, ghPrecheck.output, ghPrecheck.ok ? undefined : timeoutStepMetadata(ghPrecheck))
  if (!ghPrecheck.ok) {
    return fail("config-error", ghPrecheck.message, { output: ghPrecheck.output })
  }

  const subject = await resolveMergeSubject(inputs, host)
  if (subject.kind === "failure") {
    return fail("config-error", subject.message, { output: subject.message })
  }

  const merged = await waitChecksAndMergePr(gh, workDir, prNumber, subject.subject, host.signal, record, ghOpts, githubRepository)
  if (merged.kind === "failure") {
    return fail(merged.errorCode, merged.message, {
      output: merged.output,
      prNumber,
      prUrl: merged.prUrl,
    })
  }

  return buildMergeGitHubPrOutput({
    kind: "merge-github-pr",
    status: "completed",
    prNumber,
    prUrl: merged.prUrl,
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

function ghLineOptions(host: ActionHost): CommandLineOptions | undefined {
  if (!host.log) return { timeoutMs: NETWORK_COMMAND_TIMEOUT_MS }
  return { onLine: (line) => host.log!.write(ACTION_SOURCE, line), timeoutMs: NETWORK_COMMAND_TIMEOUT_MS }
}

export function buildMergeGitHubPrOutput(output: MergeGitHubPrOutput): ActionResult {
  if (output.status === "completed") {
    const { errorCode: _errorCode, message: _message, steps, ...rest } = output
    const success: JsonObject = { ...rest, steps: steps as unknown as JsonObject }
    return succeed(success)
  }
  return actionFail(output.errorCode ?? "merge-failed", output.message ?? output.output, { exitCode: 1 })
}

function withGitHubRepository(args: string[], githubRepository?: string): string[] {
  return githubRepository ? [...args, "--repo", githubRepository] : args
}
