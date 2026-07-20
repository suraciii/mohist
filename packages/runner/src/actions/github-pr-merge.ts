import { runCommand, type CommandLineOptions, type CommandResult } from "../system/process.js"
import { classifyGhFailure, looksLikeRetrySafe } from "./github-pr-classify.js"
import {
  classifyPrChecks,
  parsePrStatusCheckRollupResult,
} from "./github-pr-checks.js"
import { combinedGhOutput, errorMessage, parsePrView } from "./github-pr-parse.js"
import { timeoutStepMetadata, type GitHubPrErrorCode, type GitHubPrStepMetadata } from "./github-pr-types.js"

type GhRunner = typeof runCommand

const PR_CHECKS_POLL_INTERVAL_MS_DEFAULT = 15_000
// How long to keep polling after GitHub reports no checks before concluding
// the branch genuinely has no CI and proceeding to merge.
// Long enough to ride out the registration window right after a push / force
// push (GitHub hasn't turned the workflow run into a check run yet), short
// enough that repos without CI don't wait forever.
const PR_CHECKS_NO_CHECKS_GRACE_MS_DEFAULT = 120_000
const PR_CHECKS_UNAVAILABLE_RETRY_LIMIT_DEFAULT = 3

// How long to poll mergeStateStatus after checks pass before giving up.
// GitHub's merge eligibility can lag behind PR check rollup by a few seconds;
// a BLOCKED/UNSTABLE state right after checks settle is usually transient.
const PR_MERGE_STATUS_POLL_TIMEOUT_MS = 120_000

let prChecksPollIntervalMs = PR_CHECKS_POLL_INTERVAL_MS_DEFAULT
let prChecksNoChecksGraceMs = PR_CHECKS_NO_CHECKS_GRACE_MS_DEFAULT
let prChecksUnavailableRetryLimit = PR_CHECKS_UNAVAILABLE_RETRY_LIMIT_DEFAULT

export function setGitHubPrChecksTimingForTest(timing: { pollIntervalMs?: number; noChecksGraceMs?: number; unavailableRetryLimit?: number } | null) {
  if (timing === null) {
    prChecksPollIntervalMs = PR_CHECKS_POLL_INTERVAL_MS_DEFAULT
    prChecksNoChecksGraceMs = PR_CHECKS_NO_CHECKS_GRACE_MS_DEFAULT
    prChecksUnavailableRetryLimit = PR_CHECKS_UNAVAILABLE_RETRY_LIMIT_DEFAULT
    return
  }
  if (timing.pollIntervalMs !== undefined) prChecksPollIntervalMs = timing.pollIntervalMs
  if (timing.noChecksGraceMs !== undefined) prChecksNoChecksGraceMs = timing.noChecksGraceMs
  if (timing.unavailableRetryLimit !== undefined) prChecksUnavailableRetryLimit = Math.max(0, Math.floor(timing.unavailableRetryLimit))
}

// Bounded in-action retry for transient network failures on read-only gh calls
// (gh pr view / gh pr list). Network jitter to api.github.com (e.g. "unexpected
// EOF", connection reset) is common through a flaky proxy path and should not
// surface as an action failure. Writes (gh pr create/merge/...) are intentionally
// NOT retried here: they are not all idempotent.
const GH_TRANSIENT_RETRY_LIMIT_DEFAULT = 3
const GH_TRANSIENT_RETRY_BACKOFF_MS_DEFAULT = 2_000
let ghTransientRetryLimit = GH_TRANSIENT_RETRY_LIMIT_DEFAULT
let ghTransientRetryBackoffMs = GH_TRANSIENT_RETRY_BACKOFF_MS_DEFAULT

export function setGitHubPrTransientRetryForTest(opts: { limit?: number; backoffMs?: number } | null) {
  if (opts === null) {
    ghTransientRetryLimit = GH_TRANSIENT_RETRY_LIMIT_DEFAULT
    ghTransientRetryBackoffMs = GH_TRANSIENT_RETRY_BACKOFF_MS_DEFAULT
    return
  }
  if (opts.limit !== undefined) ghTransientRetryLimit = Math.max(0, Math.floor(opts.limit))
  if (opts.backoffMs !== undefined) ghTransientRetryBackoffMs = Math.max(0, Math.floor(opts.backoffMs))
}

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

  const checksWait = await waitForPrChecks(gh, workDir, prNumber, signal, record, options, githubRepository)
  if (checksWait.kind === "failure") {
    const prefix = checksWait.errorCode === "pr-checks-unavailable"
      ? "checks status unavailable"
      : "checks failed"
    return {
      kind: "failure",
      errorCode: checksWait.errorCode,
      message: `PR #${prNumber} ${prefix}: ${checksWait.message}`,
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

  // waitForPrChecks tracks PR check rollup state, but branch protection may
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
      await delayWithSignal(prChecksPollIntervalMs, signal)
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

type PrChecksWaitResult =
  | { kind: "ok" }
  | { kind: "failure"; errorCode: "pr-checks-failed" | "pr-checks-unavailable"; message: string; output: string }
  | { kind: "cancelled"; message: string; output: string }

async function waitForPrChecks(
  gh: GhRunner,
  workDir: string,
  prNumber: number,
  signal: AbortSignal,
  record: (name: string, command: string, exitCode: number, output: string, metadata?: GitHubPrStepMetadata) => void,
  options?: CommandLineOptions,
  githubRepository?: string,
): Promise<PrChecksWaitResult> {
  // Timestamp of the first poll that saw zero check runs, or null once checks
  // have appeared. Used to bound how long we wait before treating the branch
  // as genuinely check-less.
  let noChecksSince: number | null = null
  let unavailableRetries = 0
  for (;;) {
    if (signal.aborted) {
      return {
        kind: "cancelled",
        message: `Cancelled before polling checks: ${signal.reason instanceof Error ? signal.reason.message : String(signal.reason ?? "aborted")}`,
        output: "cancelled before next poll",
      }
    }
    const checksResult = await runGhReadWithRetry(
      gh,
      withGitHubRepository(["pr", "view", String(prNumber), "--json", "statusCheckRollup"], githubRepository),
      workDir,
      signal,
      record,
      "gh-pr-checks",
      `pr view ${prNumber} --json statusCheckRollup`,
      options,
    )
    const checksOutput = combinedGhOutput(checksResult)
    let unavailable: { message: string; output: string } | null = null
    if (checksResult.exitCode !== 0) {
      unavailable = { message: checksOutput, output: checksOutput }
    } else {
      const parsed = parsePrStatusCheckRollupResult(checksResult.stdout)
      if (parsed.kind === "invalid") {
        unavailable = { message: parsed.message, output: checksResult.stdout }
      } else {
        unavailableRetries = 0
        const checks = parsed.checks
        if (checks.length === 0) {
          if (noChecksSince === null) noChecksSince = Date.now()
          if (Date.now() - noChecksSince < prChecksNoChecksGraceMs) {
            try {
              await delayWithSignal(prChecksPollIntervalMs, signal)
            } catch (error) {
              return {
                kind: "cancelled",
                message: errorMessage(error),
                output: `cancelled during wait: ${errorMessage(error)}`,
              }
            }
            continue
          }
        } else {
          noChecksSince = null
        }
        const classification = classifyPrChecks(checks)
        if (classification.kind === "failed") {
          return {
            kind: "failure",
            errorCode: "pr-checks-failed",
            message: classification.message,
            output: classification.message,
          }
        }
        if (classification.kind === "passed") {
          return { kind: "ok" }
        }
      }
    }
    if (unavailable) {
      if (unavailableRetries >= prChecksUnavailableRetryLimit) {
        return {
          kind: "failure",
          errorCode: "pr-checks-unavailable",
          message: `check status unavailable after ${unavailableRetries + 1} attempts: ${unavailable.message}`,
          output: unavailable.output,
        }
      }
      unavailableRetries += 1
    }
    try {
      await delayWithSignal(prChecksPollIntervalMs, signal)
    } catch (error) {
      return {
        kind: "cancelled",
        message: errorMessage(error),
        output: `cancelled during wait: ${errorMessage(error)}`,
      }
    }
  }
}

function delayWithSignal(ms: number, signal: AbortSignal): Promise<void> {
  return new Promise<void>((resolve, reject) => {
    if (signal.aborted) {
      reject(signal.reason ?? new Error("aborted"))
      return
    }
    const timer = setTimeout(() => {
      signal.removeEventListener("abort", onAbort)
      resolve()
    }, ms)
    const onAbort = () => {
      clearTimeout(timer)
      reject(signal.reason ?? new Error("aborted"))
    }
    signal.addEventListener("abort", onAbort, { once: true })
  })
}

// Runs a read-only gh command, retrying transient network failures (network
// jitter, rate limits, 5xx) up to ghTransientRetryLimit times with backoff.
// Only reads are safe to retry; writes must bypass this. Each retried attempt
// is recorded with a "(transient retry N/M)" marker; the final outcome is
// recorded under the canonical command so existing step assertions hold.
async function runGhReadWithRetry(
  gh: GhRunner,
  args: string[],
  workDir: string,
  signal: AbortSignal,
  record: (name: string, command: string, exitCode: number, output: string, metadata?: GitHubPrStepMetadata) => void,
  recordName: string,
  recordCommand: string,
  options?: CommandLineOptions,
): Promise<CommandResult> {
  let attempt = 0
  for (;;) {
    const result = await gh("gh", args, workDir, signal, undefined, options)
    const transient = result.exitCode !== 0
      && result.status !== "timeout"
      && attempt < ghTransientRetryLimit
      && looksLikeRetrySafe(`${result.stdout}\n${result.stderr}`)
    if (!transient) {
      record(recordName, recordCommand, result.exitCode, combinedGhOutput(result), timeoutStepMetadata(result))
      return result
    }
    attempt++
    record(recordName, `${recordCommand} (transient retry ${attempt}/${ghTransientRetryLimit})`, result.exitCode, combinedGhOutput(result), timeoutStepMetadata(result))
    try {
      await delayWithSignal(ghTransientRetryBackoffMs, signal)
    } catch (error) {
      record(recordName, recordCommand, result.exitCode, `aborted during retry backoff: ${errorMessage(error)}`)
      return result
    }
    if (signal.aborted) return result
  }
}

function withGitHubRepository(args: string[], githubRepository?: string): string[] {
  return githubRepository ? [...args, "--repo", githubRepository] : args
}
