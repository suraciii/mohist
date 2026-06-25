import type { ActionContext, ActionResult } from "../core/types.js"
import { numberInput, stringInput } from "../core/json.js"
import { stringAt } from "../core/json-path.js"
import { runCommand } from "../system/process.js"
import { git as defaultGit } from "./git.js"

type GitRunner = typeof defaultGit
type GhRunner = typeof runCommand

let git: GitRunner = defaultGit
let gh: GhRunner = runCommand

export function setGitHubPrStatusGitRunnerForTest(runner: GitRunner | null) {
  git = runner ?? defaultGit
}

export function setGitHubPrStatusGhRunnerForTest(runner: GhRunner | null) {
  gh = runner ?? runCommand
}

export type GitHubPrStatusExpectation =
  | "open"
  | "ready"
  | "merged"
  | "head-matches"
  | "base-matches"
  | "in-sync"

export interface GitHubPrStatusStep {
  name: string
  command: string
  exitCode: number
  output: string
}

export interface GitHubPrStatusOutput {
  kind: "github-pr-status"
  status: "verified" | "failed"
  prNumber: number | null
  prUrl: string | null
  prState: string | null
  isDraft: boolean | null
  baseRefName: string | null
  baseSha: string | null
  headSha: string | null
  localHeadSha: string | null
  expectations: GitHubPrStatusExpectation[]
  missing: GitHubPrStatusExpectation[]
  source: string | null
  target: string | null
  remote: string | null
  message: string | null
  output: string
  steps: GitHubPrStatusStep[]
}

const VALID_EXPECTATIONS: ReadonlySet<GitHubPrStatusExpectation> = new Set([
  "open",
  "ready",
  "merged",
  "head-matches",
  "base-matches",
  "in-sync",
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
  number?: number
  url?: string
  state?: string
  isDraft?: boolean
  baseRefName?: string
  baseRef?: { name?: string }
  baseRepository?: { nameWithOwner?: string }
  baseRefOid?: string
  headRefOid?: string
  headRefName?: string
  headRepository?: { nameWithOwner?: string }
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

function buildOutput(payload: GitHubPrStatusOutput): ActionResult {
  const json = JSON.stringify(payload)
  if (payload.status === "verified") {
    return { status: "success", message: payload.message ?? `PR #${payload.prNumber ?? "?"} status verified`, output: json }
  }
  return {
    status: "failure",
    message: payload.message ?? `PR #${payload.prNumber ?? "?"} status check failed`,
    output: json,
  }
}

function emptyStatusOutput(message: string): GitHubPrStatusOutput {
  return {
    kind: "github-pr-status",
    status: "failed",
    prNumber: null,
    prUrl: null,
    prState: null,
    isDraft: null,
    baseRefName: null,
    baseSha: null,
    headSha: null,
    localHeadSha: null,
    expectations: [],
    missing: [],
    source: null,
    target: null,
    remote: null,
    message,
    output: message,
    steps: [],
  }
}

function classifyGhFailure(_stdout: string, stderr: string): string {
  const text = stderr.toLowerCase()
  if (text.includes("not found") || text.includes("could not resolve")) return "pr-state-conflict"
  if (text.includes("auth") || text.includes("login")) return "config-error"
  return "retry-safe"
}

export async function githubPrStatusAction(context: ActionContext): Promise<ActionResult> {
  const prNumber = numberInput(context.with, "prNumber") ?? numberFromVariables(context.variables)
  if (prNumber === null || !Number.isFinite(prNumber)) {
    return {
      status: "failure",
      message: "GitHub PR status check requires 'prNumber' (or vars.github.pr.number)",
      output: JSON.stringify(emptyStatusOutput("missing prNumber")),
    }
  }

  const source = stringInput(context.with, "source") ?? stringAt(context.variables, ["workspace", "branch"]) ?? "HEAD"
  const target = stringInput(context.with, "target")
    ?? stringAt(context.variables, ["repository", "baseBranch"])
    ?? stringAt(context.variables, ["project", "defaultBranch"])
    ?? stringAt(context.variables, ["project", "baseBranch"])
    ?? "main"
  const remote = stringInput(context.with, "remote") ?? "origin"
  const expect = parseGitHubPrStatusExpectation(stringInput(context.with, "expect"))
  const workDir = stringAt(context.variables, ["workspace", "path"]) ?? context.workDir

  const steps: GitHubPrStatusStep[] = []
  const recordStep = (name: string, command: string, exitCode: number, output: string) => {
    steps.push({ name, command, exitCode, output })
  }

  const viewResult = await gh(
    "gh",
    [
      "pr",
      "view",
      String(prNumber),
      "--json",
      "number,url,state,isDraft,baseRefName,baseRefOid,headRefOid,headRefName",
    ],
    workDir,
    context.signal,
  )
  const viewOutput = combinedGhOutput(viewResult)
  recordStep("gh-pr-view", `pr view ${prNumber} --json number,url,state,...`, viewResult.exitCode, viewOutput)

  if (viewResult.exitCode !== 0) {
    const errorCode = classifyGhFailure(viewResult.stdout, viewResult.stderr)
    return buildOutput({
      kind: "github-pr-status",
      status: "failed",
      prNumber,
      prUrl: null,
      prState: null,
      isDraft: null,
      baseRefName: null,
      baseSha: null,
      headSha: null,
      localHeadSha: null,
      expectations: expect,
      missing: expect,
      source,
      target,
      remote,
      message: `gh pr view ${prNumber} failed: ${viewOutput}`,
      output: viewOutput,
      steps,
    })
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
      baseRefName: null,
      baseSha: null,
      headSha: null,
      localHeadSha: null,
      expectations: expect,
      missing: expect,
      source,
      target,
      remote,
      message: `gh pr view ${prNumber} returned unparseable JSON: ${viewOutput}`,
      output: viewOutput,
      steps,
    })
  }

  const prUrl = view.url ?? null
  const prState = view.state ?? null
  const isDraft = view.isDraft ?? null
  const baseRefName = view.baseRefName ?? view.baseRef?.name ?? null
  const baseSha = view.baseRefOid ?? null
  const headSha = view.headRefOid ?? null

  let localHeadSha: string | null = null
  if (expect.includes("head-matches") || expect.includes("in-sync")) {
    const local = await git(workDir, ["rev-parse", source], context.signal)
    const localOutput = combinedGhOutput(local)
    recordStep("git-rev-parse", `rev-parse ${source}`, local.exitCode, localOutput)
    if (local.success) {
      localHeadSha = local.stdout.trim() || null
    } else {
      return buildOutput({
        kind: "github-pr-status",
        status: "failed",
        prNumber,
        prUrl,
        prState,
        isDraft,
        baseRefName,
        baseSha,
        headSha,
        localHeadSha: null,
        expectations: expect,
        missing: expect.filter((entry) => entry === "head-matches" || entry === "in-sync"),
        source,
        target,
        remote,
        message: `git rev-parse ${source} failed: ${localOutput}`,
        output: localOutput,
        steps,
      })
    }
  }

  const missing: GitHubPrStatusExpectation[] = []
  for (const expectation of expect) {
    const satisfied = evaluateExpectation(expectation, {
      prState,
      isDraft,
      baseRefName,
      baseSha,
      headSha,
      localHeadSha,
      target,
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
    baseRefName,
    baseSha,
    headSha,
    localHeadSha,
    expectations: expect,
    missing,
    source,
    target,
    remote,
    message: summary,
    output: viewOutput,
    steps,
  })
}

interface ExpectationContext {
  prState: string | null
  isDraft: boolean | null
  baseRefName: string | null
  baseSha: string | null
  headSha: string | null
  localHeadSha: string | null
  target: string
}

function evaluateExpectation(expectation: GitHubPrStatusExpectation, ctx: ExpectationContext): boolean {
  switch (expectation) {
    case "open":
      return ctx.prState === "OPEN"
    case "ready":
      return ctx.prState === "OPEN" && ctx.isDraft === false
    case "merged":
      return ctx.prState === "MERGED"
    case "head-matches":
      return ctx.headSha !== null && ctx.localHeadSha !== null && ctx.headSha === ctx.localHeadSha
    case "base-matches":
      return ctx.baseRefName !== null && ctx.baseRefName === ctx.target
    case "in-sync":
      return ctx.headSha !== null && ctx.localHeadSha !== null && ctx.headSha === ctx.localHeadSha && ctx.baseRefName === ctx.target
    default:
      return false
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

export function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error)
}

export const __testing = {
  DEFAULT_EXPECTATIONS,
  VALID_EXPECTATIONS,
  evaluateExpectation,
  parseGitHubPrStatusExpectation,
}
