import type { ActionContext, ActionResult, JsonObject } from "../core/types.js"
import { booleanInput, stringInput } from "../core/json.js"
import { stringAt } from "../core/json-path.js"
import { git as defaultGit } from "./git.js"

type GitRunner = typeof defaultGit
type GitResult = Awaited<ReturnType<GitRunner>>
let git: GitRunner = defaultGit

export type PushGitResult = GitResult

export function setPushGitRunnerForTest(runner: GitRunner | null) {
  git = runner ?? defaultGit
}

export async function pushAction(context: ActionContext): Promise<ActionResult> {
  const source = stringInput(context.with, "source") ?? stringAt(context.variables, ["workspace", "branch"]) ?? "HEAD"
  const target = stringInput(context.with, "target")
    ?? stringAt(context.variables, ["repository", "baseBranch"])
    ?? stringAt(context.variables, ["project", "defaultBranch"])
    ?? stringAt(context.variables, ["project", "baseBranch"])
    ?? "main"
  const remote = stringInput(context.with, "remote") ?? "origin"
  const forceWithLease = booleanInput(context.with, "forceWithLease") === true
  const refspec = `${source}:${target}`
  const workDir = stringAt(context.variables, ["workspace", "path"]) ?? context.workDir

  const sourceResolve = await git(workDir, ["rev-parse", source], context.signal)
  if (!sourceResolve.success) {
    return pushOutput(
      source,
      target,
      remote,
      workDir,
      null,
      false,
      forceWithLease,
      sourceResolve.combinedOutput,
      "retry-safe",
      sourceResolve.exitCode,
    )
  }
  const landedCommit = sourceResolve.stdout.trim()

  const pushArgs = ["push"]
  if (forceWithLease) {
    // Dynamic working branches (e.g. mohist/run-<runId>) carry no configured
    // remote-tracking ref, so the bare `--force-with-lease` form always fails
    // with "(stale info)" — even on a trivial fast-forward. Resolve the remote
    // tip and use the explicit lease form, which git trusts regardless of
    // tracking-ref state. If the probe itself fails, fall back to the bare
    // form (best-effort, prior behavior).
    const remoteTip = await resolveRemoteTip(workDir, remote, target, context.signal)
    if (remoteTip === null) {
      pushArgs.push("--force-with-lease")
    } else if (remoteTip) {
      pushArgs.push(`--force-with-lease=${target}:${remoteTip}`)
    }
    // remoteTip === "" → branch absent on remote; a plain push creates it, no force needed.
  }
  pushArgs.push(remote, refspec)
  const push = await git(workDir, pushArgs, context.signal)
  if (!push.success) {
    const failureKind = looksLikeNonFastForward(push.combinedOutput) ? "base-moved" : "retry-safe"
    return pushOutput(source, target, remote, workDir, landedCommit, false, forceWithLease, push.combinedOutput, failureKind, push.exitCode)
  }

  return pushOutput(source, target, remote, workDir, landedCommit, true, forceWithLease, push.combinedOutput, null, push.exitCode)
}

type PushFailureKind = "base-moved" | "retry-safe" | null

function pushOutput(
  source: string,
  target: string,
  remote: string,
  workDir: string,
  landedCommit: string | null,
  pushed: boolean,
  forceWithLease: boolean,
  gitOutput: string,
  failureKind: PushFailureKind,
  exitCode: number | null,
): ActionResult {
  // Schema convention: `failureKind` is always present (null on success).
  // Downstream renderers (CLI DeliveryFailureGuidance, web delivery-failure.ts)
  // detect the kind from the JSON `failureKind` field first. Push reports
  // `base-moved` (non-fast-forward; rebase and try again) and `retry-safe`
  // (transient/network/auth). `forceWithLease` is recorded so the integrate
  // stage's rebase-then-push recovery path can be audited in the task output.
  const output = JSON.stringify({
    kind: "push",
    status: pushed ? "completed" : "failed",
    source,
    target,
    remote,
    refspec: `${source}:${target}`,
    workDir,
    landedCommit,
    pushed,
    forceWithLease,
    failureKind,
    output: gitOutput,
  })
  if (pushed) {
    return { status: "success", message: "Push completed", output, exitCode: exitCode ?? 0 }
  }
  const label = failureKind === "base-moved"
    ? `Push failed: base branch moved (non-fast-forward). Rebase and try again.`
    : `Push failed (retry-safe): ${gitOutput || "unknown error"}`
  return { status: "failure", message: label, output, exitCode: exitCode ?? 1 }
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
 *   - null when the probe itself failed (caller falls back to bare --force-with-lease).
 */
async function resolveRemoteTip(workDir: string, remote: string, target: string, signal: AbortSignal): Promise<string | null> {
  const probe = await git(workDir, ["ls-remote", remote, `refs/heads/${target}`], signal)
  if (!probe.success) return null
  const firstLine = probe.stdout.split(/\r?\n/)[0] ?? ""
  return firstLine.trim().split(/\s+/)[0] ?? ""
}
