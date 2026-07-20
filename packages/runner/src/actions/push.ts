import type { ActionContext, ActionResult, JsonObject } from "../core/types.js"
import { booleanInput, stringInput } from "../core/json.js"
import { git as defaultGit, NETWORK_COMMAND_TIMEOUT_MS, type GitOptions } from "./git.js"
import { timeoutStepMetadata, type GitHubPrStep } from "./github-pr-types.js"
import { resolveDeliveryRemote, resolvePushSource, resolvePushTarget } from "./delivery-context.js"
import { fail, succeed } from "./action-result.js"

type GitRunner = (workDir: string, args: string[], signal: AbortSignal, options?: GitOptions) => Promise<{
  success: boolean
  stdout: string
  stderr: string
  exitCode: number
  combinedOutput: string
  status?: "timeout"
  timeoutMs?: number
}>
type GitResult = Awaited<ReturnType<GitRunner>>
let git: GitRunner = defaultGit

export type PushGitResult = GitResult

export function setPushGitRunnerForTest(runner: GitRunner | null) {
  git = runner ?? defaultGit
}

/**
 * `source` tag recorded against every captured `mohist/push` action
 * body line. Phase-distinguished from `branch-check` and `cleanup`
 * so the web viewer can tell which ops phase produced which line.
 */
const ACTION_SOURCE = "action:push"

function sinkOptions(context: ActionContext): GitOptions | undefined {
  return context.log ? { sink: { log: context.log, source: ACTION_SOURCE } } : undefined
}

function networkOptions(context: ActionContext): GitOptions | undefined {
  if (!context.log) return { timeoutMs: NETWORK_COMMAND_TIMEOUT_MS }
  return { sink: { log: context.log, source: ACTION_SOURCE }, timeoutMs: NETWORK_COMMAND_TIMEOUT_MS }
}

export async function pushAction(context: ActionContext): Promise<ActionResult> {
  const target = resolvePushTarget(context)
  const source = target ? resolvePushSource(context, target) : null
  const remote = resolveDeliveryRemote(context)
  if (!target) return fail("invalid-input", "Push requires the authoritative repository base branch or workflow branch")
  if (!source || !remote) return fail("invalid-input", "Push requires the authoritative workspace branch and repository origin")
  const force = booleanInput(context.with, "force") === true
  const forceWithLease = !force && booleanInput(context.with, "forceWithLease") === true
  const refspec = `${source}:${target}`
  const workDir = context.workDir
  const opts = sinkOptions(context)
  const networkOpts = networkOptions(context)
  const steps: GitHubPrStep[] = []

  const sourceResolve = await git(workDir, ["rev-parse", source], context.signal, opts)
  if (!sourceResolve.success) {
    return pushOutput(
      source,
      target,
      remote,
      workDir,
      null,
      false,
      force,
      forceWithLease,
      sourceResolve.combinedOutput,
      sourceResolve.status === "timeout" ? "timeout" : "push-failed",
      sourceResolve.exitCode,
    )
  }
  const landedCommit = sourceResolve.stdout.trim()

  const pushArgs = ["push"]
  if (force) {
    // Dynamic workflow branches (e.g. mohist/run-<runId>) are single-owner and
    // carry no configured remote-tracking ref, so `--force-with-lease` fails
    // with "(stale info)" and is misclassified as base-moved. `--force` is
    // safe for these branches and bypasses the tracking-ref dependency.
    pushArgs.push("--force")
  } else if (forceWithLease) {
    // Regular force-with-lease path: resolve the remote tip and use the
    // explicit lease form, which git trusts regardless of tracking-ref state.
    // If the probe itself fails, fall back to the bare form (best-effort).
    const remoteTip = await resolveRemoteTip(workDir, remote, target, context.signal, networkOpts)
    if (remoteTip.kind === "timeout") {
      steps.push({ name: "git-ls-remote", command: remoteTip.command, exitCode: remoteTip.result.exitCode, output: remoteTip.result.combinedOutput, ...timeoutStepMetadata(remoteTip.result) })
      return pushOutput(source, target, remote, workDir, landedCommit, false, force, forceWithLease, remoteTip.result.combinedOutput, "timeout", remoteTip.result.exitCode, steps)
    }
    if (remoteTip.kind === "failed") {
      pushArgs.push("--force-with-lease")
    } else if (remoteTip.tip) {
      pushArgs.push(`--force-with-lease=${target}:${remoteTip.tip}`)
    }
    // remoteTip.tip === "" → branch absent on remote; a plain push creates it, no force needed.
  }
  pushArgs.push(remote, refspec)
  const push = await git(workDir, pushArgs, context.signal, networkOpts)
  steps.push({ name: "git-push", command: pushArgs.join(" "), exitCode: push.exitCode, output: push.combinedOutput, ...timeoutStepMetadata(push) })
  if (!push.success) {
    const failureCode = looksLikeNonFastForward(push.combinedOutput) ? "base-moved" : push.status === "timeout" ? "timeout" : "push-failed"
    return pushOutput(source, target, remote, workDir, landedCommit, false, force, forceWithLease, push.combinedOutput, failureCode, push.exitCode, steps)
  }

  return pushOutput(source, target, remote, workDir, landedCommit, true, force, forceWithLease, push.combinedOutput, null, push.exitCode, steps)
}

type PushFailureCode = "base-moved" | "push-failed" | "timeout" | null

function pushOutput(
  source: string,
  target: string,
  remote: string,
  workDir: string,
  landedCommit: string | null,
  pushed: boolean,
  force: boolean,
  forceWithLease: boolean,
  gitOutput: string,
  failureCode: PushFailureCode,
  exitCode: number | null,
  steps: GitHubPrStep[] = [],
): ActionResult {
  if (!pushed) {
    const message = failureCode === "base-moved"
      ? "Push failed because the target branch moved (non-fast-forward). Rebase and try again."
      : failureCode === "timeout"
        ? "Push timed out."
        : `Push failed: ${gitOutput || "unknown error"}`
    return fail(failureCode ?? "push-failed", message, { exitCode: exitCode ?? 1 })
  }
  const output = JSON.stringify({
    kind: "push",
    status: "completed",
    source,
    target,
    remote,
    refspec: `${source}:${target}`,
    workDir,
    landedCommit,
    pushed,
    force,
    forceWithLease,
    output: gitOutput,
    steps,
  })
  return succeed(output, { exitCode: exitCode ?? 0 })
}

function looksLikeNonFastForward(text: string) {
  // Match git's actual push-rejection shapes so transient network/auth errors
  // do not get mis-classified as base-moved. Real non-fast-forward messages
  // contain either `! [rejected]` followed by a hint in parens, or an
  // explicit "non-fast-forward" / "fetch first" hint.
  return /non[-\s]?fast-forward|fetch first/i.test(text)
    || /!\s*\[rejected\][^\n]*\((stale info|stale|fetch first|non[-\s]?fast-forward|behind[^\)]*)\)/i.test(text)
}

/**
 * Resolves the current tip of `target` on `remote` via `ls-remote`.
 *   - tip sha when the branch exists on the remote,
 *   - "" when the branch is absent (a plain push creates it, no force needed),
 *   - failed when the probe itself failed (caller falls back to bare --force-with-lease),
 *   - timeout when the probe hung and must be surfaced instead of disappearing.
 */
async function resolveRemoteTip(workDir: string, remote: string, target: string, signal: AbortSignal, opts?: GitOptions): Promise<
  | { kind: "resolved"; tip: string }
  | { kind: "failed" }
  | { kind: "timeout"; command: string; result: GitResult }
> {
  const args = ["ls-remote", remote, `refs/heads/${target}`]
  const probe = await git(workDir, args, signal, opts)
  if (!probe.success) {
    if (probe.status === "timeout") return { kind: "timeout", command: args.join(" "), result: probe }
    return { kind: "failed" }
  }
  const firstLine = probe.stdout.split(/\r?\n/)[0] ?? ""
  return { kind: "resolved", tip: firstLine.trim().split(/\s+/)[0] ?? "" }
}
