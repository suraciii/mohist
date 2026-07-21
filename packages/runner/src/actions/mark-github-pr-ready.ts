import { numberInput } from "../core/json.js"
import type { ActionContext, ActionResult, JsonObject } from "../core/types.js"
import { runCommand, type CommandLineOptions } from "../system/process.js"
import { NETWORK_COMMAND_TIMEOUT_MS } from "./git.js"
import { combinedGhOutput, parsePrViewWithDraft } from "./github-pr-parse.js"
import { classifyGhFailure } from "./github-pr-classify.js"
import { getGitHubPrGh, runGhPrecheck } from "./github-pr-runtime.js"
import { parseGitHubRepository } from "./github-pr-repository.js"
import { timeoutStepMetadata, type GitHubPrErrorCode, type GitHubPrStep, type GitHubPrStepMetadata, type MarkGitHubPrReadyOutput } from "./github-pr-types.js"
import { fail, succeed } from "./action-result.js"

type GhRunner = typeof runCommand
const ACTION_SOURCE = "action:mark-github-pr-ready"

export async function markGitHubPrReadyAction(context: ActionContext): Promise<ActionResult> {
  const prNumber = numberInput(context.with, "prNumber")
  const repositoryUrl = typeof context.with?.["repositoryUrl"] === "string" ? context.with["repositoryUrl"] : undefined
  if (prNumber === undefined) {
    return markReadyOutput({
      kind: "mark-github-pr-ready",
      status: "failed",
      prNumber: null,
      prUrl: null,
      state: null,
      previousState: null,
      transitioned: false,
      errorCode: "config-error",
      message: "mark-github-pr-ready requires prNumber",
      output: "mark-github-pr-ready requires prNumber",
      steps: [],
    })
  }
  const steps: GitHubPrStep[] = []
  const record = createRecorder(steps)

  const fail = (
    errorCode: GitHubPrErrorCode,
    message: string,
    payload: Partial<MarkGitHubPrReadyOutput> = {},
  ): ActionResult => markReadyOutput({
    kind: "mark-github-pr-ready",
    status: "failed",
    prNumber,
    prUrl: payload.prUrl ?? null,
    state: payload.state ?? null,
    previousState: payload.previousState ?? null,
    transitioned: payload.transitioned ?? false,
    errorCode,
    message,
    output: payload.output ?? message,
    steps,
  })
  if (!repositoryUrl) return fail("invalid-input", "mark-github-pr-ready requires 'repositoryUrl'")
  const githubRepository = parseGitHubRepository(repositoryUrl)
  if (!githubRepository) return fail("config-error", "mark-github-pr-ready requires a valid GitHub repository URL")

  const workDir = context.workDir
  const gh = getGitHubPrGh()
  const ghOpts = ghLineOptions(context)
  const ghPrecheck = await runGhPrecheck(gh, workDir, context.signal, ghOpts)
  record("gh-precheck", "gh --version && gh auth status", ghPrecheck.ok ? 0 : ghPrecheck.exitCode, ghPrecheck.output, ghPrecheck.ok ? undefined : timeoutStepMetadata(ghPrecheck))
  if (!ghPrecheck.ok) {
    return fail("config-error", ghPrecheck.message, { output: ghPrecheck.output })
  }

  const viewResult = await gh("gh", withGitHubRepository(["pr", "view", String(prNumber), "--json", "state,isDraft,url"], githubRepository), workDir, context.signal, undefined, ghOpts)
  const viewOutput = combinedGhOutput(viewResult)
  record("gh-pr-view", `pr view ${prNumber} --json state,isDraft,url`, viewResult.exitCode, viewOutput, timeoutStepMetadata(viewResult))
  if (viewResult.exitCode !== 0) {
    return fail(classifyGhFailure(viewResult.stdout, viewResult.stderr, viewResult.status), `gh pr view ${prNumber} failed: ${viewOutput}`, { output: viewOutput })
  }

  const view = parsePrViewWithDraft(viewResult.stdout)
  if (!view) {
    return fail("retry-safe", `gh pr view ${prNumber} returned unparseable JSON: ${viewOutput}`, { output: viewOutput })
  }

  if (view.state === "CLOSED" || view.state === "MERGED") {
    return fail("pr-state-conflict", `PR #${prNumber} is in state ${view.state}; refusing to mark ready.`, {
      output: viewOutput,
      prUrl: view.url ?? null,
    })
  }

  const prUrl = view.url ?? null
  const previousState: "READY" | "DRAFT" = view.isDraft ? "DRAFT" : "READY"
  if (!view.isDraft) {
    return markReadyOutput({
      kind: "mark-github-pr-ready",
      status: "completed",
      prNumber,
      prUrl,
      state: "READY",
      previousState: "READY",
      transitioned: false,
      errorCode: null,
      message: `PR #${prNumber} is already ready for review`,
      output: `PR #${prNumber} already READY; no mutation performed`,
      steps,
    })
  }

  const readyResult = await gh("gh", withGitHubRepository(["pr", "ready", String(prNumber)], githubRepository), workDir, context.signal, undefined, ghOpts)
  const readyOutput = combinedGhOutput(readyResult)
  record("gh-pr-ready", `pr ready ${prNumber}`, readyResult.exitCode, readyOutput, timeoutStepMetadata(readyResult))
  if (readyResult.exitCode !== 0) {
    return fail(classifyGhFailure(readyResult.stdout, readyResult.stderr, readyResult.status), `gh pr ready ${prNumber} failed: ${readyOutput}`, {
      output: readyOutput,
      prUrl,
      previousState,
    })
  }

  return markReadyOutput({
    kind: "mark-github-pr-ready",
    status: "completed",
    prNumber,
    prUrl,
    state: "READY",
    previousState,
    transitioned: true,
    errorCode: null,
    message: `Marked PR #${prNumber} as ready for review`,
    output: readyOutput || `Marked PR #${prNumber} as ready for review`,
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

export function markReadyOutput(output: MarkGitHubPrReadyOutput): ActionResult {
  if (output.status === "completed") {
    const { errorCode: _errorCode, message: _message, steps, ...rest } = output
    const success: JsonObject = { ...rest, steps: steps as unknown as JsonObject }
    return succeed(success)
  }
  return fail(output.errorCode ?? "mark-ready-failed", output.message ?? output.output, { exitCode: 1 })
}

function withGitHubRepository(args: string[], githubRepository?: string): string[] {
  return githubRepository ? [...args, "--repo", githubRepository] : args
}
