import { runCommand, type CommandLineOptions } from "../system/process.js"
import { classifyGhFailure } from "./github-pr-classify.js"
import {
  delayWithSignal,
  getGitHubPrChecksPollIntervalMs,
  runGhReadWithRetry,
  waitForGitHubPrChecks,
  withGitHubRepository,
} from "./github-pr-checks-wait.js"
import { combinedGhOutput, errorMessage, parsePrView } from "./github-pr-parse.js"
import { timeoutStepMetadata, type GitHubPrErrorCode, type GitHubPrStepMetadata } from "./github-pr-types.js"

type GhRunner = typeof runCommand

// How long to poll mergeStateStatus after checks pass before giving up.
// GitHub's merge eligibility can lag behind PR check rollup by a few seconds;
// a BLOCKED/UNSTABLE state right after checks settle is usually transient.
const PR_MERGE_STATUS_POLL_TIMEOUT_MS = 120_000

export interface WaitChecksAndMergeOk {
  kind: "ok"
  mergeCommitSha: string | null
  prUrl: string | null
  output: string
}

export interface WaitChecksAndMergeFailure {
  kind: "failure"
  errorCode: GitHubPrErrorCode
  message: string
  prUrl: string | null
  output: string
}

export async function waitChecksAndMergePr(
  gh: GhRunner,
  workDir: string,
  prNumber: number,
  subject: string,
  signal: AbortSignal,
  record: (name: string, command: string, exitCode: number, output: string, metadata?: GitHubPrStepMetadata) => void,
  options?: CommandLineOptions,
  githubRepository?: string,
): Promise<WaitChecksAndMergeOk | WaitChecksAndMergeFailure> {
  const viewResult = await runGhReadWithRetry(
    gh,
    withGitHubRepository(["pr", "view", String(prNumber), "--json", "state,mergeCommit,url,number,mergeStateStatus"], githubRepository),
    workDir,
    signal,
    record,
    "gh-pr-view",
    `pr view ${prNumber} --json state,mergeCommit,url,number,mergeStateStatus`,
    options,
  )
  const viewOutput = combinedGhOutput(viewResult)
  if (viewResult.exitCode !== 0) {
    return {
      kind: "failure",
      errorCode: classifyGhFailure(viewResult.stdout, viewResult.stderr, viewResult.status),
      message: `gh pr view ${prNumber} failed: ${viewOutput}`,
      prUrl: null,
      output: viewOutput,
    }
  }

  const view = parsePrView(viewResult.stdout)
  if (!view) {
    return {
      kind: "failure",
      errorCode: "retry-safe",
      message: `gh pr view ${prNumber} returned unparseable JSON: ${viewOutput}`,
      prUrl: null,
      output: viewOutput,
    }
  }

  if (view.state === "MERGED") {
    return {
      kind: "ok",
      mergeCommitSha: view.mergeCommit?.oid ?? null,
      prUrl: view.url ?? null,
      output: `PR #${prNumber} already merged at ${view.mergeCommit?.oid ?? "unknown sha"}`,
    }
  }

  if (view.state === "CLOSED") {
    return {
      kind: "failure",
      errorCode: "pr-state-conflict",
      message: `PR #${prNumber} is closed; refusing to recreate. Re-open the PR or run workflow integrate retry from prepare.`,
      prUrl: view.url ?? null,
      output: viewOutput,
    }
  }

  const initialMergeStateFailure = mergeStateStatusFailure(prNumber, view.mergeStateStatus, view.url ?? null, viewOutput)
  if (initialMergeStateFailure) {
    return initialMergeStateFailure
  }

  const checksWait = await waitForGitHubPrChecks(gh, workDir, prNumber, signal, record, options, githubRepository)
  if (checksWait.kind === "failed") {
    return {
      kind: "failure",
      errorCode: "pr-checks-failed",
      message: `PR #${prNumber} checks failed: ${checksWait.message}`,
      prUrl: view.url ?? null,
      output: checksWait.output,
    }
  }
  if (checksWait.kind === "unavailable") {
    return {
      kind: "failure",
      errorCode: "pr-checks-unavailable",
      message: `PR #${prNumber} checks status unavailable: ${checksWait.message}`,
      prUrl: view.url ?? null,
      output: checksWait.output,
    }
  }
  if (checksWait.kind === "cancelled") {
    return {
      kind: "failure",
      errorCode: "retry-safe",
      message: `Cancelled while waiting for PR #${prNumber} checks to settle: ${checksWait.message}`,
      prUrl: view.url ?? null,
      output: checksWait.output,
    }
  }

  // waitForGitHubPrChecks tracks PR check rollup state, but branch protection may
  // also gate on reviews or check suites that aren't reported as check runs.
  // The PR's mergeStateStatus is the authoritative final signal.
  // Poll it for up to PR_MERGE_STATUS_POLL_TIMEOUT_MS — BLOCKED/UNSTABLE/UNKNOWN
  // right after checks settle is usually transient (checks hadn't fully registered).
  const mergeStatusPollStart = Date.now()
  for (;;) {
    if (signal.aborted) {
      return {
        kind: "failure",
        errorCode: "retry-safe",
        message: `Cancelled while waiting for merge eligibility: ${signal.reason instanceof Error ? signal.reason.message : String(signal.reason ?? "aborted")}`,
        prUrl: view.url ?? null,
        output: "cancelled before merge status settled",
      }
    }
    const mergeStatusResult = await runGhReadWithRetry(
      gh,
      withGitHubRepository(["pr", "view", String(prNumber), "--json", "mergeStateStatus"], githubRepository),
      workDir,
      signal,
      record,
      "gh-pr-merge-ready",
      `pr view ${prNumber} --json mergeStateStatus`,
      options,
    )
    const mergeStatusOutput = combinedGhOutput(mergeStatusResult)
    if (mergeStatusResult.exitCode !== 0) {
      return {
        kind: "failure",
        errorCode: classifyGhFailure(mergeStatusResult.stdout, mergeStatusResult.stderr, mergeStatusResult.status),
        message: `gh pr view ${prNumber} mergeStateStatus failed: ${mergeStatusOutput}`,
        prUrl: view.url ?? null,
        output: mergeStatusOutput,
      }
    }
    const mergeStatusView = parsePrView(mergeStatusResult.stdout)
    const mergeState = mergeStatusView?.mergeStateStatus
    if (mergeState === "CLEAN" || mergeState === "HAS_HOOKS") {
      break
    }
    const mergeStateFailure = mergeStateStatusFailure(prNumber, mergeState, view.url ?? null, mergeStatusOutput)
    if (mergeStateFailure) {
      return mergeStateFailure
    }
    if (Date.now() - mergeStatusPollStart >= PR_MERGE_STATUS_POLL_TIMEOUT_MS) {
      return {
        kind: "failure",
        errorCode: "protection-conflict",
        message: `PR #${prNumber} merge blocked by branch protection (state=${mergeState}); timeout after ${PR_MERGE_STATUS_POLL_TIMEOUT_MS / 1000}s`,
        prUrl: view.url ?? null,
        output: mergeStatusOutput,
      }
    }
    try {
      await delayWithSignal(getGitHubPrChecksPollIntervalMs(), signal)
    } catch (err) {
      return {
        kind: "failure",
        errorCode: "retry-safe",
        message: `Cancelled while waiting for merge eligibility: ${errorMessage(err)}`,
        prUrl: view.url ?? null,
        output: "cancelled during merge status poll",
      }
    }
  }

  const mergeArgs = withGitHubRepository(["pr", "merge", String(prNumber), "--squash", "--subject", subject, "--body", ""], githubRepository)
  const mergeResult = await gh("gh", mergeArgs, workDir, signal, undefined, options)
  const mergeOutput = combinedGhOutput(mergeResult)
  record("gh-pr-merge", `pr merge ${prNumber} --squash --subject "${subject}"`, mergeResult.exitCode, mergeOutput, timeoutStepMetadata(mergeResult))
  if (mergeResult.exitCode !== 0) {
    return {
      kind: "failure",
      errorCode: classifyGhFailure(mergeResult.stdout, mergeResult.stderr, mergeResult.status),
      message: `gh pr merge ${prNumber} --squash failed: ${mergeOutput}`,
      prUrl: view.url ?? null,
      output: mergeOutput,
    }
  }

  const recheck = await runGhReadWithRetry(
    gh,
    withGitHubRepository(["pr", "view", String(prNumber), "--json", "state,mergeCommit,url"], githubRepository),
    workDir,
    signal,
    record,
    "gh-pr-view-confirm",
    `pr view ${prNumber} --json state,mergeCommit,url`,
    options,
  )
  const recheckOutput = combinedGhOutput(recheck)
  if (recheck.exitCode !== 0) {
    return {
      kind: "failure",
      errorCode: classifyGhFailure(recheck.stdout, recheck.stderr, recheck.status),
      message: `gh pr view ${prNumber} (post-merge confirm) failed: ${recheckOutput}`,
      prUrl: view.url ?? null,
      output: recheckOutput,
    }
  }

  const confirmed = parsePrView(recheck.stdout)
  if (!confirmed || confirmed.state !== "MERGED") {
    return {
      kind: "failure",
      errorCode: confirmed ? "pr-state-conflict" : "retry-safe",
      message: confirmed
        ? `PR #${prNumber} is in state ${confirmed.state} after merge; expected MERGED.`
        : `gh pr view ${prNumber} returned unparseable JSON after merge: ${recheckOutput}`,
      prUrl: confirmed?.url ?? view.url ?? null,
      output: recheckOutput,
    }
  }

  return {
    kind: "ok",
    mergeCommitSha: confirmed.mergeCommit?.oid ?? null,
    prUrl: confirmed.url ?? null,
    output: `Merged PR #${prNumber} via squash with subject "${subject}"`,
  }
}

function mergeStateStatusFailure(
  prNumber: number,
  mergeStateStatus: string | undefined,
  prUrl: string | null,
  output: string,
): WaitChecksAndMergeFailure | null {
  if (mergeStateStatus === "DIRTY" || mergeStateStatus === "BEHIND") {
    return {
      kind: "failure",
      errorCode: "base-moved",
      message: `PR #${prNumber} is ${mergeStateStatus}; rebase required.`,
      prUrl,
      output,
    }
  }
  if (mergeStateStatus === "DRAFT") {
    return {
      kind: "failure",
      errorCode: "pr-state-conflict",
      message: `PR #${prNumber} is still a draft.`,
      prUrl,
      output,
    }
  }
  return null
}
