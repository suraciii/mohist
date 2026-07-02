import { numberInput } from "../core/json.js"
import { stringAt } from "../core/json-path.js"
import type { ActionContext, ActionResult } from "../core/types.js"
import { runCommand } from "../system/process.js"
import { combinedGhOutput, parsePrViewWithDraft } from "./github-pr-parse.js"
import { classifyGhFailure } from "./github-pr-classify.js"
import { getGitHubPrGh, runGhPrecheck } from "./github-pr-runtime.js"
import type { GitHubPrErrorCode, GitHubPrStep, MarkGitHubPrReadyOutput } from "./github-pr-types.js"

type GhRunner = typeof runCommand

export async function markGitHubPrReadyAction(context: ActionContext): Promise<ActionResult> {
  const prNumber = numberInput(context.with, "prNumber")
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

  const workDir = stringAt(context.variables, ["workspace", "path"]) ?? context.workDir
  const gh = getGitHubPrGh()
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

  const ghPrecheck = await runGhPrecheck(gh, workDir, context.signal)
  record("gh-precheck", "gh --version && gh auth status", ghPrecheck.ok ? 0 : ghPrecheck.exitCode, ghPrecheck.output)
  if (!ghPrecheck.ok) {
    return fail("config-error", ghPrecheck.message, { output: ghPrecheck.output })
  }

  const viewResult = await gh("gh", ["pr", "view", String(prNumber), "--json", "state,isDraft,url"], workDir, context.signal)
  const viewOutput = combinedGhOutput(viewResult)
  record("gh-pr-view", `pr view ${prNumber} --json state,isDraft,url`, viewResult.exitCode, viewOutput)
  if (viewResult.exitCode !== 0) {
    return fail(classifyGhFailure(viewResult.stdout, viewResult.stderr), `gh pr view ${prNumber} failed: ${viewOutput}`, { output: viewOutput })
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

  const readyResult = await gh("gh", ["pr", "ready", String(prNumber)], workDir, context.signal)
  const readyOutput = combinedGhOutput(readyResult)
  record("gh-pr-ready", `pr ready ${prNumber}`, readyResult.exitCode, readyOutput)
  if (readyResult.exitCode !== 0) {
    return fail(classifyGhFailure(readyResult.stdout, readyResult.stderr), `gh pr ready ${prNumber} failed: ${readyOutput}`, {
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
  return (name: string, command: string, exitCode: number, output: string) => {
    steps.push({ name, command, exitCode, output })
  }
}

export function markReadyOutput(output: MarkGitHubPrReadyOutput): ActionResult {
  const json = JSON.stringify(output)
  if (output.status === "completed") {
    return { status: "success", message: output.message ?? "Mark GitHub PR ready", output: json }
  }
  return {
    status: "failure",
    message: `Mark GitHub PR ready failed (${output.errorCode ?? "unknown"}): ${output.message ?? output.output}`,
    output: json,
    exitCode: 1,
  }
}
