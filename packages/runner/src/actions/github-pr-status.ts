import type { ActionResult, JsonObject } from "../core/types.js"
import type { ActionInvocationContext } from "./context.js"
import { numberInput, stringInput } from "../core/json.js"
import { runCommand, type CommandLineOptions } from "../system/process.js"
import { NETWORK_COMMAND_TIMEOUT_MS } from "./git.js"
import { timeoutStepMetadata } from "./github-pr-types.js"
import { parseGitHubRepository } from "./github-pr-repository.js"
import { fail, succeed } from "./action-result.js"

type GhRunner = typeof runCommand
const ACTION_SOURCE = "action:github-pr-status"

let gh: GhRunner = runCommand

export function setGitHubPrStatusGhRunnerForTest(runner: GhRunner | null) {
  gh = runner ?? runCommand
}

export type GitHubPrStatusExpectation =
  | "open"
  | "ready"
  | "merged"

export interface GitHubPrStatusStep {
  name: string
  command: string
  exitCode: number
  output: string
  status?: "timeout"
  timeoutMs?: number
}

export interface GitHubPrStatusOutput {
  kind: "github-pr-status"
  status: "verified" | "failed"
  prNumber: number | null
  prUrl: string | null
  prState: string | null
  isDraft: boolean | null
  expectations: GitHubPrStatusExpectation[]
  missing: GitHubPrStatusExpectation[]
  message: string | null
  output: string
  steps: GitHubPrStatusStep[]
}

const VALID_EXPECTATIONS: ReadonlySet<GitHubPrStatusExpectation> = new Set([
  "open",
  "ready",
  "merged",
])

const DEFAULT_EXPECTATIONS: GitHubPrStatusExpectation[] = ["open", "ready"]

export function parseGitHubPrStatusExpectation(value: string | null | undefined): GitHubPrStatusExpectation[] {
  if (!value) return [...DEFAULT_EXPECTATIONS]
  const items = value
    .split(",")
    .map((entry) => entry.trim().toLowerCase())
    .filter(Boolean)
  const seen = new Set<GitHubPrStatusExpectation>()
  const result: GitHubPrStatusExpectation[] = []
  for (const item of items) {
    if (VALID_EXPECTATIONS.has(item as GitHubPrStatusExpectation) && !seen.has(item as GitHubPrStatusExpectation)) {
      seen.add(item as GitHubPrStatusExpectation)
      result.push(item as GitHubPrStatusExpectation)
    }
  }
  return result
}

interface PrViewFull {
  url?: string
  state?: string
  isDraft?: boolean
}

export function parsePrViewFull(stdout: string): PrViewFull | null {
  const trimmed = stdout.trim()
  if (!trimmed) return null
  let parsed: unknown
  try {
    parsed = JSON.parse(trimmed)
  } catch {
    return null
  }
  if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) return null
  return parsed as PrViewFull
}

function combinedGhOutput(result: { stdout: string; stderr: string }): string {
  return [result.stdout.trim(), result.stderr.trim()].filter(Boolean).join("\n")
}

function buildOutput(payload: GitHubPrStatusOutput, failureCode = "pr-status-failed"): ActionResult {
  if (payload.status === "verified") {
    const { message: _message, steps, ...rest } = payload
    const success: JsonObject = { ...rest, steps: steps as unknown as JsonObject }
    return succeed(success)
  }
  return fail(failureCode, payload.message ?? `PR #${payload.prNumber ?? "?"} status check failed`)
}

function emptyStatusOutput(message: string): GitHubPrStatusOutput {
  return {
    kind: "github-pr-status",
    status: "failed",
    prNumber: null,
    prUrl: null,
    prState: null,
    isDraft: null,
    expectations: [],
    missing: [],
    message,
    output: message,
    steps: [],
  }
}

export async function githubPrStatusAction(context: ActionInvocationContext): Promise<ActionResult> {
  const requestedPrNumber = numberInput(context.with, "prNumber")
  if (requestedPrNumber === undefined || !Number.isFinite(requestedPrNumber)) {
    return fail("invalid-input", "GitHub PR status check requires 'prNumber'")
  }
  const prNumber = requestedPrNumber
  const repositoryUrl = typeof context.with?.["repositoryUrl"] === "string" ? context.with["repositoryUrl"] : undefined
  if (!repositoryUrl) return fail("invalid-input", "GitHub PR status check requires 'repositoryUrl'")
  const githubRepository = parseGitHubRepository(repositoryUrl)
  if (!githubRepository) return fail("config-error", "github-pr-status requires a valid GitHub repository URL")

  const expect = parseGitHubPrStatusExpectation(stringInput(context.with, "expect"))
  const workDir = context.workDir

  const steps: GitHubPrStatusStep[] = []
  const ghOpts = ghLineOptions(context)
  const recordStep = (name: string, command: string, exitCode: number, output: string, metadata?: Pick<GitHubPrStatusStep, "status" | "timeoutMs">) => {
    steps.push({ name, command, exitCode, output, ...metadata })
  }
  const prViewFields = buildPrViewFields(expect)
  const viewResult = await gh(
    "gh",
    withGitHubRepository(["pr", "view", String(prNumber), "--json", prViewFields.join(",")], githubRepository),
    workDir,
    context.signal,
    undefined,
    ghOpts,
  )
  const viewOutput = combinedGhOutput(viewResult)
  recordStep("gh-pr-view", `pr view ${prNumber} --json ${prViewFields.join(",")}`, viewResult.exitCode, viewOutput, timeoutStepMetadata(viewResult))

  if (viewResult.exitCode !== 0) {
    return buildOutput({
      kind: "github-pr-status",
      status: "failed",
      prNumber,
      prUrl: null,
      prState: null,
      isDraft: null,
      expectations: expect,
      missing: expect,
      message: `gh pr view ${prNumber} failed: ${viewOutput}`,
      output: viewOutput,
      steps,
    }, viewResult.status === "timeout" ? "timeout" : undefined)
  }

  const view = parsePrViewFull(viewResult.stdout)
  if (!view) {
    return buildOutput({
      kind: "github-pr-status",
      status: "failed",
      prNumber,
      prUrl: null,
      prState: null,
      isDraft: null,
      expectations: expect,
      missing: expect,
      message: `gh pr view ${prNumber} returned unparseable JSON: ${viewOutput}`,
      output: viewOutput,
      steps,
    })
  }

  const prUrl = view.url ?? null
  const prState = view.state ?? null
  const isDraft = view.isDraft ?? null

  const missing: GitHubPrStatusExpectation[] = []
  for (const expectation of expect) {
    const satisfied = evaluateExpectation(expectation, {
      prState,
      isDraft,
    })
    if (!satisfied) missing.push(expectation)
  }

  const verified = missing.length === 0
  const summary = verified
    ? `PR #${prNumber} status verified (${expect.join(", ") || "default ready+open"})`
    : `PR #${prNumber} status check failed: missing ${missing.join(", ")}`

  return buildOutput({
    kind: "github-pr-status",
    status: verified ? "verified" : "failed",
    prNumber,
    prUrl,
    prState,
    isDraft,
    expectations: expect,
    missing,
    message: summary,
    output: viewOutput,
    steps,
  })
}

interface ExpectationContext {
  prState: string | null
  isDraft: boolean | null
}

function evaluateExpectation(expectation: GitHubPrStatusExpectation, ctx: ExpectationContext): boolean {
  switch (expectation) {
    case "open":
      return ctx.prState === "OPEN"
    case "ready":
      return ctx.prState === "OPEN" && ctx.isDraft === false
    case "merged":
      return ctx.prState === "MERGED"
    default:
      return false
  }
}

function ghLineOptions(context: ActionInvocationContext): CommandLineOptions | undefined {
  if (!context.log) return { timeoutMs: NETWORK_COMMAND_TIMEOUT_MS }
  return { onLine: (line) => context.log!.write(ACTION_SOURCE, line), timeoutMs: NETWORK_COMMAND_TIMEOUT_MS }
}

function buildPrViewFields(expect: GitHubPrStatusExpectation[]): string[] {
  const fields = ["url", "state"]
  if (expect.includes("ready")) fields.push("isDraft")
  return fields
}


export const __testing = {
  DEFAULT_EXPECTATIONS,
  VALID_EXPECTATIONS,
  buildPrViewFields,
  evaluateExpectation,
  parseGitHubPrStatusExpectation,
}

function withGitHubRepository(args: string[], githubRepository?: string): string[] {
  return githubRepository ? [...args, "--repo", githubRepository] : args
}
