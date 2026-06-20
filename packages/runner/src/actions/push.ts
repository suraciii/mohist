import type { ActionContext, ActionResult, JsonObject } from "../core/types.js"
import { stringInput } from "../core/json.js"
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
      sourceResolve.combinedOutput,
      "retry-safe",
      sourceResolve.exitCode,
    )
  }
  const landedCommit = sourceResolve.stdout.trim()

  const push = await git(workDir, ["push", remote, refspec], context.signal)
  if (!push.success) {
    const failureKind = looksLikeNonFastForward(push.combinedOutput) ? "base-moved" : "retry-safe"
    return pushOutput(source, target, remote, workDir, landedCommit, false, push.combinedOutput, failureKind, push.exitCode)
  }

  return pushOutput(source, target, remote, workDir, landedCommit, true, push.combinedOutput, null, push.exitCode)
}

type PushFailureKind = "base-moved" | "retry-safe" | null

function pushOutput(
  source: string,
  target: string,
  remote: string,
  workDir: string,
  landedCommit: string | null,
  pushed: boolean,
  gitOutput: string,
  failureKind: PushFailureKind,
  exitCode: number | null,
): ActionResult {
  // Schema convention: `failureKind` is always present (null on success).
  // Downstream renderers (CLI DeliveryFailureGuidance, web delivery-failure.ts)
  // detect the kind from the JSON `failureKind` field first. Push reports
  // `base-moved` (non-fast-forward; rebase and try again) and `retry-safe`
  // (transient/network/auth).
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

function stringAt(value: unknown, path: string[]): string | undefined {
  const found = path.reduce<unknown>((current, part) => {
    if (typeof current !== "object" || current === null || Array.isArray(current)) return undefined
    return (current as Record<string, unknown>)[part]
  }, value)
  return typeof found === "string" ? found : undefined
}
