import type { ActionContext, ActionResult, JsonObject } from "../core/types.js"
import { numberInput } from "../core/json.js"
import { stringAt } from "../core/json-path.js"
import type { CommandLineOptions } from "../system/process.js"
import { NETWORK_COMMAND_TIMEOUT_MS } from "./git.js"
import {
  getGitHubPrChecksPollIntervalMs,
  waitForGitHubPrChecks,
} from "./github-pr-checks-wait.js"
import { timeoutStepMetadata } from "./github-pr-types.js"
import { getGitHubPrGh, runGhPrecheck } from "./github-pr-runtime.js"
import { resolveGitHubRepository } from "./delivery-context.js"
import { fail, succeed } from "./action-result.js"

const ACTION_SOURCE = "action:github-pr-checks"

export interface GitHubPrChecksStep {
  name: string
  command: string
  exitCode: number
  output: string
  status?: "timeout"
  timeoutMs?: number
}

export interface GitHubPrChecksOutput {
  kind: "github-pr-checks"
  status: "verified" | "failed"
  prNumber: number
  pollIntervalMs: number
  message: string | null
  output: string
  steps: GitHubPrChecksStep[]
}

export async function githubPrChecksAction(context: ActionContext): Promise<ActionResult> {
  const prNumber = numberInput(context.with, "prNumber") ?? numberFromVariables(context.variables)
  if (prNumber === null || !Number.isFinite(prNumber)) {
    return fail("invalid-input", "GitHub PR checks requires 'prNumber' (or vars.github.pr.number)")
  }
  const workDir = stringAt(context.variables, ["workspace", "path"]) ?? context.workDir

  const gh = getGitHubPrGh()
  const ghOpts = ghLineOptions(context)

  const steps: GitHubPrChecksStep[] = []
  const record = (name: string, command: string, exitCode: number, output: string, metadata?: Pick<GitHubPrChecksStep, "status" | "timeoutMs">) => {
    steps.push({ name, command, exitCode, output, ...metadata })
  }

  const githubRepository = resolveGitHubRepository(context)
  if (githubRepository === null) {
    return fail("config-error", "github-pr-checks requires an authoritative GitHub repository URL")
  }

  const ghPrecheck = await runGhPrecheck(gh, workDir, context.signal, ghOpts)
  record("gh-precheck", "gh --version && gh auth status", ghPrecheck.ok ? 0 : ghPrecheck.exitCode, ghPrecheck.output, ghPrecheck.ok ? undefined : timeoutStepMetadata(ghPrecheck))
  if (!ghPrecheck.ok) {
    return fail("config-error", ghPrecheck.message, { exitCode: 1 })
  }

  const wait = await waitForGitHubPrChecks(
    gh,
    workDir,
    prNumber,
    context.signal,
    record,
    ghOpts,
    githubRepository,
  )

  if (wait.kind === "ok") {
    const output: GitHubPrChecksOutput = {
      kind: "github-pr-checks",
      status: "verified",
      prNumber,
      pollIntervalMs: getGitHubPrChecksPollIntervalMs(),
      message: `PR #${prNumber} checks passed`,
      output: `PR #${prNumber} checks passed`,
      steps,
    }
    return succeed(toJsonOutput(output))
  }
  if (wait.kind === "failed") {
    return fail("pr-checks-failed", `PR #${prNumber} checks failed: ${wait.message}`, {
      exitCode: 1,
    })
  }
  if (wait.kind === "unavailable") {
    return fail("pr-checks-unavailable", `PR #${prNumber} checks status unavailable: ${wait.message}`, {
      exitCode: 1,
    })
  }
  return fail("aborted", `Cancelled while waiting for PR #${prNumber} checks: ${wait.message}`, { exitCode: 1 })
}

function toJsonOutput(output: GitHubPrChecksOutput): JsonObject {
  return {
    kind: output.kind,
    status: output.status,
    prNumber: output.prNumber,
    pollIntervalMs: output.pollIntervalMs,
    message: output.message,
    output: output.output,
    steps: output.steps as unknown as JsonObject,
  }
}

function numberFromVariables(variables: unknown): number | null {
  if (!variables || typeof variables !== "object") return null
  const root = variables as Record<string, unknown>
  const github = root["github"]
  if (!github || typeof github !== "object") return null
  const pr = (github as Record<string, unknown>)["pr"]
  if (!pr || typeof pr !== "object") return null
  const number = (pr as Record<string, unknown>)["number"]
  if (typeof number === "number" && Number.isFinite(number)) return number
  if (typeof number === "string" && number.trim()) {
    const parsed = Number(number)
    return Number.isFinite(parsed) ? parsed : null
  }
  return null
}

function ghLineOptions(context: ActionContext): CommandLineOptions | undefined {
  if (!context.log) return { timeoutMs: NETWORK_COMMAND_TIMEOUT_MS }
  return { onLine: (line) => context.log!.write(ACTION_SOURCE, line), timeoutMs: NETWORK_COMMAND_TIMEOUT_MS }
}
