import { runCommand, type CommandLineOptions, type CommandResult } from "../system/process.js"
import { classifyGhFailure, looksLikeRetrySafe } from "./github-pr-classify.js"
import {
  classifyPrChecks,
  parsePrStatusCheckRollupResult,
} from "./github-pr-checks.js"
import { combinedGhOutput, errorMessage } from "./github-pr-parse.js"
import { timeoutStepMetadata, type GitHubPrErrorCode, type GitHubPrStepMetadata } from "./github-pr-types.js"

type GhRunner = typeof runCommand

const PR_CHECKS_POLL_INTERVAL_MS_DEFAULT = 15_000
// How long to keep polling after GitHub reports no checks before concluding
// that check status is unavailable.
// Long enough to ride out the registration window right after a push / force
// push (GitHub hasn't turned the workflow run into a check run yet), short
// enough that repos without CI don't wait forever.
const PR_CHECKS_NO_CHECKS_GRACE_MS_DEFAULT = 120_000
const PR_CHECKS_UNAVAILABLE_RETRY_LIMIT_DEFAULT = 3

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

// Read accessor so callers that share the checks-poll cadence (e.g. the merge
// action's mergeStateStatus poll) follow test-injected timing without owning
// the mutable state.
export function getGitHubPrChecksPollIntervalMs(): number {
  return prChecksPollIntervalMs
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

export type GitHubPrCheckRecorder = (
  name: string,
  command: string,
  exitCode: number,
  output: string,
  metadata?: GitHubPrStepMetadata,
) => void

export type GitHubPrChecksWaitResult =
  | { kind: "ok" }
  | { kind: "failed"; message: string; output: string }
  | { kind: "unavailable"; message: string; output: string }
  | { kind: "cancelled"; message: string; output: string }

export async function waitForGitHubPrChecks(
  gh: GhRunner,
  workDir: string,
  prNumber: number,
  signal: AbortSignal,
  record: GitHubPrCheckRecorder,
  options?: CommandLineOptions,
  githubRepository?: string,
): Promise<GitHubPrChecksWaitResult> {
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
          if (Date.now() - noChecksSince >= prChecksNoChecksGraceMs) {
            return {
              kind: "unavailable",
              message: `no PR checks were reported during the ${prChecksNoChecksGraceMs / 1000}s bounded wait`,
              output: checksOutput,
            }
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
          continue
        } else {
          noChecksSince = null
        }
        const classification = classifyPrChecks(checks)
        if (classification.kind === "failed") {
          return {
            kind: "failed",
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
          kind: "unavailable",
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

export function delayWithSignal(ms: number, signal: AbortSignal): Promise<void> {
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
export async function runGhReadWithRetry(
  gh: GhRunner,
  args: string[],
  workDir: string,
  signal: AbortSignal,
  record: GitHubPrCheckRecorder,
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

export function withGitHubRepository(args: string[], githubRepository?: string): string[] {
  return githubRepository ? [...args, "--repo", githubRepository] : args
}

// Re-exported so github-pr-merge.ts can classify gh failures without a second
// import site; the canonical classifier lives in github-pr-classify.ts.
export { classifyGhFailure, type GitHubPrErrorCode }
