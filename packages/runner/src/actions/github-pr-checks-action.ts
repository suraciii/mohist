import type { ActionResult, JsonObject } from "../core/types.js"
import type { ActionHost } from "./host.js"
import { numberInput } from "../core/json.js"
import type { CommandLineOptions } from "../system/process.js"
import { NETWORK_COMMAND_TIMEOUT_MS } from "./git.js"
import {
  getGitHubPrChecksPollIntervalMs,
  waitForGitHubPrChecks,
} from "./github-pr-checks-wait.js"
import { timeoutStepMetadata } from "./github-pr-types.js"
import { getGitHubPrGh, runGhPrecheck } from "./github-pr-runtime.js"
import { parseGitHubRepository } from "./github-pr-repository.js"
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

export async function githubPrChecksAction(inputs: JsonObject, host: ActionHost): Promise<ActionResult> {
  const prNumber = numberInput(inputs, "prNumber")
  if (prNumber === undefined || !Number.isFinite(prNumber)) {
    return fail("invalid-input", "GitHub PR checks requires 'prNumber'")
  }
  const repositoryUrl = typeof inputs["repositoryUrl"] === "string" ? inputs["repositoryUrl"] : undefined
  if (!repositoryUrl) return fail("invalid-input", "GitHub PR checks requires 'repositoryUrl'")
  const githubRepository = parseGitHubRepository(repositoryUrl)
  if (!githubRepository) return fail("config-error", "github-pr-checks requires a valid GitHub repository URL")
  const workDir = host.workDir

  const gh = getGitHubPrGh()
  const ghOpts = ghLineOptions(host)

  const steps: GitHubPrChecksStep[] = []
  const record = (name: string, command: string, exitCode: number, output: string, metadata?: Pick<GitHubPrChecksStep, "status" | "timeoutMs">) => {
    steps.push({ name, command, exitCode, output, ...metadata })
  }

  const ghPrecheck = await runGhPrecheck(gh, workDir, host.signal, ghOpts)
  record("gh-precheck", "gh --version && gh auth status", ghPrecheck.ok ? 0 : ghPrecheck.exitCode, ghPrecheck.output, ghPrecheck.ok ? undefined : timeoutStepMetadata(ghPrecheck))
  if (!ghPrecheck.ok) {
    return fail("config-error", ghPrecheck.message, { exitCode: 1 })
  }

  const wait = await waitForGitHubPrChecks(
    gh,
    workDir,
    prNumber,
    host.signal,
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

function ghLineOptions(host: ActionHost): CommandLineOptions | undefined {
  if (!host.log) return { timeoutMs: NETWORK_COMMAND_TIMEOUT_MS }
  return { onLine: (line) => host.log!.write(ACTION_SOURCE, line), timeoutMs: NETWORK_COMMAND_TIMEOUT_MS }
}
