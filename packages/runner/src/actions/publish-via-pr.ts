import type { ActionContext, ActionResult } from "../core/types.js"
import { numberInput, stringInput } from "../core/json.js"
import { stringAt } from "../core/json-path.js"
import { runCommand } from "../system/process.js"
import { git as defaultGit } from "./git.js"
import { isIssueFieldSource, resolveIssueFields, type IssueFields } from "./issue-fields.js"

type GitRunner = typeof defaultGit
type GhRunner = typeof runCommand

let git: GitRunner = defaultGit
let gh: GhRunner = runCommand

export function setPublishViaPrGitRunnerForTest(runner: GitRunner | null) {
  git = runner ?? defaultGit
}

export function setPublishViaPrGhRunnerForTest(runner: GhRunner | null) {
  gh = runner ?? runCommand
}

export type PublishViaPrFailureKind =
  | "base-moved"
  | "retry-safe"
  | "config-error"
  | "protection-conflict"
  | "pr-state-conflict"

export interface PublishViaPrStep {
  name: string
  command: string
  exitCode: number
  output: string
}

export interface PublishViaPrOutput {
  kind: "publish-via-pr"
  status: "completed" | "failed"
  source: string
  targetBranch: string
  prNumber: number | null
  prUrl: string | null
  mergeCommitSha: string | null
  baseSha: string | null
  pushed: boolean
  failureKind: PublishViaPrFailureKind | null
  failureMessage: string | null
  output: string
  steps: PublishViaPrStep[]
}

export async function publishViaPrAction(context: ActionContext): Promise<ActionResult> {
  const source = stringInput(context.with, "source") ?? stringAt(context.variables, ["workspace", "branch"]) ?? "HEAD"
  const target = stringInput(context.with, "target") ?? stringAt(context.variables, ["repository", "baseBranch"]) ?? stringAt(context.variables, ["project", "defaultBranch"]) ?? stringAt(context.variables, ["project", "baseBranch"]) ?? "main"
  const remote = stringInput(context.with, "remote") ?? "origin"
  const issueNumber = resolveIssueNumber(context)
  const workDir = stringAt(context.variables, ["project", "path"]) ?? context.workDir

  const steps: PublishViaPrStep[] = []
  const record = (name: string, command: string, exitCode: number, output: string) => {
    steps.push({ name, command, exitCode, output })
  }

  const fail = (kind: PublishViaPrFailureKind, failureMessage: string, payload: Partial<PublishViaPrOutput> = {}): ActionResult => {
    return buildPublishViaPrOutput({
      kind: "publish-via-pr",
      status: "failed",
      source,
      targetBranch: target,
      prNumber: payload.prNumber ?? null,
      prUrl: payload.prUrl ?? null,
      mergeCommitSha: payload.mergeCommitSha ?? null,
      baseSha: payload.baseSha ?? null,
      pushed: payload.pushed ?? false,
      failureKind: kind,
      failureMessage,
      output: payload.output ?? failureMessage,
      steps,
    })
  }

  const succeed = (payload: Partial<PublishViaPrOutput> & { output?: string }): ActionResult => {
    return buildPublishViaPrOutput({
      kind: "publish-via-pr",
      status: "completed",
      source,
      targetBranch: target,
      prNumber: payload.prNumber ?? null,
      prUrl: payload.prUrl ?? null,
      mergeCommitSha: payload.mergeCommitSha ?? null,
      baseSha: payload.baseSha ?? null,
      pushed: payload.pushed ?? true,
      failureKind: null,
      failureMessage: null,
      output: payload.output ?? "PR published via GitHub",
      steps,
    })
  }

  // Phase 1: gh CLI precheck. Operator hosts MUST have `gh` installed and
  // `gh auth login` completed; missing/unauthenticated gh fails fast with
  // `config-error` and the action performs no remote mutation.
  const ghPrecheck = await runGhPrecheck(gh, workDir, context.signal)
  const precheckExitCode = ghPrecheck.ok ? 0 : ghPrecheck.exitCode
  record("gh-precheck", "gh --version && gh auth status", precheckExitCode, ghPrecheck.output)
  if (!ghPrecheck.ok) {
    return fail("config-error", ghPrecheck.message, { output: ghPrecheck.output })
  }

  const sourceValidationError = validateIssueFieldSources(context)
  if (sourceValidationError) {
    return fail("config-error", sourceValidationError, { output: sourceValidationError })
  }
  const issueFieldsResult = await loadIssueFieldsIfNeeded(context)
  if (issueFieldsResult.kind === "failure") {
    return fail("config-error", issueFieldsResult.message, { output: issueFieldsResult.message })
  }
  const issueFields = issueFieldsResult.issueFields
  const title = resolveTitle(context, issueNumber, issueFields)
  const body = resolveBody(context, issueNumber, issueFields)

  // Phase 2: resolve the head branch from the workflow workspace. The
  // workflow workspace stays on `workspace.branch` for the entire action
  // — we never `git checkout` the base branch here. The action accepts
  // an explicit `source` (e.g. `${{ workspace.branch }}`) so the run
  // branch is known up front and we only fall back to `rev-parse` when
  // the source resolves to literal `HEAD`.
  const explicitSource = source !== "HEAD"
  const branchProbe = explicitSource
    ? { success: true as const, name: source }
    : await resolveCurrentBranch(git, workDir, context.signal)
  if (explicitSource) {
    record("git-source-anchor", `use source ${source}`, 0, source)
  } else {
    record("rev-parse-HEAD", "rev-parse --abbrev-ref HEAD", branchProbe.success ? 0 : branchProbe.exitCode, branchProbe.success ? branchProbe.name : branchProbe.combinedOutput)
  }
  if (!branchProbe.success) {
    return fail("retry-safe", `Could not resolve current branch: ${branchProbe.combinedOutput}`, { output: branchProbe.combinedOutput })
  }

  // Phase 3: resolve the base SHA the branch was prepared against. PR
  // delivery records `baseSha` on the task result alongside the landed
  // commit and target branch, mirroring the direct delivery shape.
  const baseSha = await resolveBaseSha(git, workDir, remote, target, context.signal, record)
  if (baseSha.kind === "failure") {
    return fail(baseSha.failureKind, baseSha.message, { output: baseSha.output })
  }

  // Phase 4: force-with-lease push. The action never checks out the base
  // branch inside the workflow workspace; the workspace stays on
  // `workspace.branch` and that branch is what we push.
  const pushResult = await git(workDir, ["push", "--force-with-lease", remote, branchProbe.name], context.signal)
  record("git-push", `push --force-with-lease ${remote} ${branchProbe.name}`, pushResult.exitCode, pushResult.combinedOutput)
  if (!pushResult.success) {
    return fail(
      classifyPushFailure(pushResult.stdout, pushResult.stderr),
      `git push --force-with-lease ${remote} ${branchProbe.name} failed: ${pushResult.combinedOutput}`,
      { output: pushResult.combinedOutput, baseSha: baseSha.sha },
    )
  }

  // Phase 5: open or reuse the PR for `head:base`.
  const prOpen = await openOrReusePr(gh, workDir, branchProbe.name, target, title, body, context.signal, record)
  if (prOpen.kind === "failure") {
    return fail(prOpen.failureKind, prOpen.message, { output: prOpen.output, baseSha: baseSha.sha })
  }
  const { prNumber, prUrl } = prOpen

  // Phase 6: merge or confirm.
  const merge = await mergeOrConfirmPr(gh, workDir, prNumber, title, context.signal, record)
  if (merge.kind === "failure") {
    return fail(merge.failureKind, merge.message, {
      output: merge.output,
      prNumber,
      prUrl,
      baseSha: baseSha.sha,
    })
  }

  return succeed({
    prNumber,
    prUrl: merge.prUrl ?? prUrl,
    mergeCommitSha: merge.mergeCommitSha,
    baseSha: baseSha.sha,
    output: merge.output,
  })
}

function resolveTitle(context: ActionContext, issueNumber: number | null, issueFields: IssueFields | null): string {
  const literal = stringInput(context.with, "title") ?? stringInput(context.with, "message")
  if (literal !== undefined) return literal
  const source = stringInput(context.with, "titleFrom")
  if (source === "issue.title" && issueFields) return issueFields.title
  if (source === "issue.body" && issueFields) return issueFields.body
  return `Complete issue #${issueNumber ?? ""}`.trim()
}

function resolveBody(context: ActionContext, issueNumber: number | null, issueFields: IssueFields | null): string {
  const literal = stringInput(context.with, "body")
  if (literal !== undefined) return literal
  const source = stringInput(context.with, "bodyFrom")
  if (source === "issue.title" && issueFields) return issueFields.title
  if (source === "issue.body" && issueFields) return issueFields.body
  return `Mohist issue #${issueNumber ?? ""}`.trim()
}

function needsIssueFields(context: ActionContext): boolean {
  const titleLiteral = stringInput(context.with, "title") ?? stringInput(context.with, "message")
  const bodyLiteral = stringInput(context.with, "body")
  return (titleLiteral === undefined && isIssueFieldSource(stringInput(context.with, "titleFrom"))) ||
    (bodyLiteral === undefined && isIssueFieldSource(stringInput(context.with, "bodyFrom")))
}

function validateIssueFieldSources(context: ActionContext): string | null {
  const titleLiteral = stringInput(context.with, "title") ?? stringInput(context.with, "message")
  const bodyLiteral = stringInput(context.with, "body")
  const titleFrom = stringInput(context.with, "titleFrom")
  if (titleLiteral === undefined && titleFrom !== undefined && !isIssueFieldSource(titleFrom)) {
    return `Unsupported titleFrom source '${titleFrom}'. Supported sources: issue.title, issue.body.`
  }
  const bodyFrom = stringInput(context.with, "bodyFrom")
  if (bodyLiteral === undefined && bodyFrom !== undefined && !isIssueFieldSource(bodyFrom)) {
    return `Unsupported bodyFrom source '${bodyFrom}'. Supported sources: issue.title, issue.body.`
  }
  return null
}

async function loadIssueFieldsIfNeeded(context: ActionContext): Promise<
  | { kind: "ok"; issueFields: IssueFields | null }
  | { kind: "failure"; message: string }
> {
  if (!needsIssueFields(context)) return { kind: "ok", issueFields: null }
  try {
    return { kind: "ok", issueFields: await resolveIssueFields(context) }
  } catch (error) {
    return { kind: "failure", message: errorMessage(error) }
  }
}

interface OpenOrReusePrOk {
  kind: "ok"
  prNumber: number
  prUrl: string
}

interface OpenOrReusePrFailure {
  kind: "failure"
  failureKind: PublishViaPrFailureKind
  message: string
  output: string
}

async function openOrReusePr(
  gh: GhRunner,
  workDir: string,
  head: string,
  base: string,
  title: string,
  body: string,
  signal: AbortSignal,
  record: (name: string, command: string, exitCode: number, output: string) => void,
): Promise<OpenOrReusePrOk | OpenOrReusePrFailure> {
  const listResult = await gh("gh", ["pr", "list", "--head", head, "--base", base, "--state", "open", "--json", "number,url"], workDir, signal)
  const listOutput = combinedGhOutput(listResult)
  record("gh-pr-list", `pr list --head ${head} --base ${base} --state open --json number,url`, listResult.exitCode, listOutput)
  if (listResult.exitCode !== 0) {
    return {
      kind: "failure",
      failureKind: classifyGhFailure(listResult.stdout, listResult.stderr),
      message: `gh pr list failed: ${listOutput}`,
      output: listOutput,
    }
  }

  const existing = parsePrList(listResult.stdout)
  if (existing.length > 0) {
    return { kind: "ok", prNumber: existing[0]!.number, prUrl: existing[0]!.url }
  }

  const createResult = await gh(
    "gh",
    ["pr", "create", "--head", head, "--base", base, "--title", title, "--body", body],
    workDir,
    signal,
  )
  const createOutput = combinedGhOutput(createResult)
  record("gh-pr-create", `pr create --head ${head} --base ${base} --title "${title}" --body "${body}"`, createResult.exitCode, createOutput)
  if (createResult.exitCode !== 0) {
    return {
      kind: "failure",
      failureKind: classifyGhFailure(createResult.stdout, createResult.stderr),
      message: `gh pr create failed: ${createOutput}`,
      output: createOutput,
    }
  }

  const urlMatch = createResult.stdout.match(/https?:\/\/\S+/)
  const url = urlMatch ? urlMatch[0] : ""
  const prNumber = extractPrNumberFromUrl(url)
  if (!prNumber) {
    return {
      kind: "failure",
      failureKind: "retry-safe",
      message: `gh pr create did not return a PR URL: ${createOutput}`,
      output: createOutput,
    }
  }

  return { kind: "ok", prNumber, prUrl: url }
}

interface MergeOrConfirmPrOk {
  kind: "ok"
  mergeCommitSha: string | null
  prUrl: string | null
  output: string
}

interface MergeOrConfirmPrFailure {
  kind: "failure"
  failureKind: PublishViaPrFailureKind
  message: string
  output: string
}

async function mergeOrConfirmPr(
  gh: GhRunner,
  workDir: string,
  prNumber: number,
  subject: string,
  signal: AbortSignal,
  record: (name: string, command: string, exitCode: number, output: string) => void,
): Promise<MergeOrConfirmPrOk | MergeOrConfirmPrFailure> {
  const viewResult = await gh("gh", ["pr", "view", String(prNumber), "--json", "state,mergeCommit,url,number"], workDir, signal)
  const viewOutput = combinedGhOutput(viewResult)
  record("gh-pr-view", `pr view ${prNumber} --json state,mergeCommit,url,number`, viewResult.exitCode, viewOutput)
  if (viewResult.exitCode !== 0) {
    return {
      kind: "failure",
      failureKind: classifyGhFailure(viewResult.stdout, viewResult.stderr),
      message: `gh pr view ${prNumber} failed: ${viewOutput}`,
      output: viewOutput,
    }
  }

  const view = parsePrView(viewResult.stdout)
  if (!view) {
    return {
      kind: "failure",
      failureKind: "retry-safe",
      message: `gh pr view ${prNumber} returned unparseable JSON: ${viewOutput}`,
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
      failureKind: "pr-state-conflict",
      message: `PR #${prNumber} is closed; refusing to recreate. Re-open the PR or run workflow integrate retry from prepare.`,
      output: viewOutput,
    }
  }

  const mergeArgs = ["pr", "merge", String(prNumber), "--squash", "--subject", subject, "--body", ""]
  const mergeResult = await gh("gh", mergeArgs, workDir, signal)
  const mergeOutput = combinedGhOutput(mergeResult)
  record("gh-pr-merge", `pr merge ${prNumber} --squash --subject "${subject}"`, mergeResult.exitCode, mergeOutput)
  if (mergeResult.exitCode !== 0) {
    return {
      kind: "failure",
      failureKind: classifyGhFailure(mergeResult.stdout, mergeResult.stderr),
      message: `gh pr merge ${prNumber} --squash failed: ${mergeOutput}`,
      output: mergeOutput,
    }
  }

  const recheck = await gh("gh", ["pr", "view", String(prNumber), "--json", "state,mergeCommit,url"], workDir, signal)
  const recheckOutput = combinedGhOutput(recheck)
  record("gh-pr-view-confirm", `pr view ${prNumber} --json state,mergeCommit,url`, recheck.exitCode, recheckOutput)
  if (recheck.exitCode !== 0) {
    return {
      kind: "failure",
      failureKind: classifyGhFailure(recheck.stdout, recheck.stderr),
      message: `gh pr view ${prNumber} (post-merge confirm) failed: ${recheckOutput}`,
      output: recheckOutput,
    }
  }

  const confirmed = parsePrView(recheck.stdout)
  if (!confirmed || confirmed.state !== "MERGED") {
    return {
      kind: "failure",
      failureKind: confirmed ? "pr-state-conflict" : "retry-safe",
      message: confirmed
        ? `PR #${prNumber} is in state ${confirmed.state} after merge; expected MERGED.`
        : `gh pr view ${prNumber} returned unparseable JSON after merge: ${recheckOutput}`,
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

async function runGhPrecheck(gh: GhRunner, workDir: string, signal: AbortSignal): Promise<{ ok: true; output: string } | { ok: false; exitCode: number; output: string; message: string }> {
  const version = await gh("gh", ["--version"], workDir, signal)
  if (version.exitCode !== 0) {
    const output = combinedGhOutput(version)
    return {
      ok: false,
      exitCode: version.exitCode,
      output,
      message: "gh CLI is not installed or not on PATH. Install GitHub CLI and run `gh auth login` on the runner host before re-running this issue.",
    }
  }

  const auth = await gh("gh", ["auth", "status"], workDir, signal)
  const authOutput = combinedGhOutput(auth)
  if (auth.exitCode !== 0) {
    return {
      ok: false,
      exitCode: auth.exitCode,
      output: authOutput,
      message: "gh CLI is installed but `gh auth status` did not return a logged-in account. Run `gh auth login` on the runner host before re-running this issue.",
    }
  }

  return { ok: true, output: `${version.stdout.trim()}\n${authOutput}` }
}

async function resolveCurrentBranch(git: GitRunner, workDir: string, signal: AbortSignal): Promise<{ success: true; name: string } | { success: false; exitCode: number; combinedOutput: string }> {
  const result = await git(workDir, ["rev-parse", "--abbrev-ref", "HEAD"], signal)
  if (!result.success) {
    return { success: false, exitCode: result.exitCode, combinedOutput: result.combinedOutput }
  }
  const name = result.stdout.trim()
  if (!name || name === "HEAD") {
    return { success: false, exitCode: result.exitCode, combinedOutput: result.combinedOutput || "detached HEAD" }
  }
  return { success: true, name }
}

interface BaseShaOk {
  kind: "ok"
  sha: string
}

interface BaseShaFailure {
  kind: "failure"
  failureKind: PublishViaPrFailureKind
  message: string
  output: string
}

async function resolveBaseSha(
  git: GitRunner,
  workDir: string,
  remote: string,
  target: string,
  signal: AbortSignal,
  record: (name: string, command: string, exitCode: number, output: string) => void,
): Promise<BaseShaOk | BaseShaFailure> {
  const fetch = await git(workDir, ["fetch", remote, target], signal)
  record("git-fetch-base", `fetch ${remote} ${target}`, fetch.exitCode, fetch.combinedOutput)
  if (!fetch.success) {
    return { kind: "failure", failureKind: "retry-safe", message: `git fetch ${remote} ${target} failed: ${fetch.combinedOutput}`, output: fetch.combinedOutput }
  }
  const resolve = await git(workDir, ["rev-parse", `${remote}/${target}`], signal)
  record("git-rev-parse-base", `rev-parse ${remote}/${target}`, resolve.exitCode, resolve.combinedOutput)
  if (!resolve.success) {
    return { kind: "failure", failureKind: "retry-safe", message: `git rev-parse ${remote}/${target} failed: ${resolve.combinedOutput}`, output: resolve.combinedOutput }
  }
  return { kind: "ok", sha: resolve.stdout.trim() }
}

function buildPublishViaPrOutput(output: PublishViaPrOutput): ActionResult {
  const json = JSON.stringify(output)
  if (output.status === "completed") {
    return { status: "success", message: "Publish via PR completed", output: json }
  }
  return {
    status: "failure",
    message: `Publish via PR failed (${output.failureKind ?? "unknown"}): ${output.failureMessage ?? output.output}`,
    output: json,
    exitCode: 1,
  }
}

export function classifyGhFailure(stdout: string, stderr: string): PublishViaPrFailureKind {
  const text = `${stdout}\n${stderr}`.toLowerCase()
  if (!text.trim()) return "retry-safe"
  if (looksLikeAuthFailure(text)) return "config-error"
  if (looksLikeProtectionConflict(text)) return "protection-conflict"
  if (looksLikeBaseMoved(text)) return "base-moved"
  if (looksLikePrStateConflict(text)) return "pr-state-conflict"
  if (looksLikeRetrySafe(text)) return "retry-safe"
  return "retry-safe"
}

export function classifyPushFailure(stdout: string, stderr: string): PublishViaPrFailureKind {
  return classifyGhFailure(stdout, stderr)
}

export function looksLikeBaseMoved(text: string): boolean {
  const lower = text.toLowerCase()
  if (lower.includes("merge conflict") || lower.includes("not mergeable") || lower.includes("can't be merged") || lower.includes("can not be merged")) {
    return true
  }
  if (lower.includes("base branch head") || lower.includes("base branch has been updated") || lower.includes("branch is out-of-date") || lower.includes("is out of date") || lower.includes("diverged") || lower.includes("non-fast-forward") || lower.includes("stale info")) {
    return true
  }
  return false
}

export function looksLikeProtectionConflict(text: string): boolean {
  const lower = text.toLowerCase()
  if (lower.includes("protected branch")) return true
  if (lower.includes("required status check") || lower.includes("status check")) return true
  if (lower.includes("required review") || lower.includes("review required") || lower.includes("approving review")) return true
  if (lower.includes("branch protection")) return true
  return false
}

export function looksLikePrStateConflict(text: string): boolean {
  const lower = text.toLowerCase()
  if (lower.includes("pull request is in a closed state") || lower.includes("closed pull request")) return true
  if (lower.includes("pull request is in a merged state") || lower.includes("already merged")) return true
  if (lower.includes("state was changed") || lower.includes("state has changed")) return true
  return false
}

export function looksLikeAuthFailure(text: string): boolean {
  const lower = text.toLowerCase()
  if (lower.includes("not logged into") || lower.includes("not logged in")) return true
  if (lower.includes("authentication required") || lower.includes("bad credentials")) return true
  if (lower.includes("github token") || lower.includes("gh: authentication")) return true
  if (lower.includes("login required") || lower.includes("must be logged in")) return true
  return false
}

export function looksLikeRetrySafe(text: string): boolean {
  const lower = text.toLowerCase()
  if (lower.includes("rate limit") || lower.includes("api rate limit")) return true
  if (lower.includes("could not resolve host") || lower.includes("network") || lower.includes("timeout") || lower.includes("timed out")) return true
  if (lower.includes("connection reset") || lower.includes("temporarily unavailable") || lower.includes("try again")) return true
  if (lower.includes("502") || lower.includes("503") || lower.includes("504")) return true
  return false
}

export function parsePrList(stdout: string): { number: number; url: string }[] {
  const trimmed = stdout.trim()
  if (!trimmed) return []
  let parsed: unknown
  try {
    parsed = JSON.parse(trimmed)
  } catch {
    return []
  }
  if (!Array.isArray(parsed)) return []
  const out: { number: number; url: string }[] = []
  for (const item of parsed) {
    if (!item || typeof item !== "object" || Array.isArray(item)) continue
    const number = (item as Record<string, unknown>)["number"]
    const url = (item as Record<string, unknown>)["url"]
    if (typeof number === "number" && typeof url === "string") {
      out.push({ number, url })
    }
  }
  return out
}

interface PrViewState {
  state?: string
  url?: string
  mergeCommit?: { oid?: string } | null
}

export function parsePrView(stdout: string): PrViewState | null {
  const trimmed = stdout.trim()
  if (!trimmed) return null
  let parsed: unknown
  try {
    parsed = JSON.parse(trimmed)
  } catch {
    return null
  }
  if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) return null
  const obj = parsed as Record<string, unknown>
  const state = typeof obj["state"] === "string" ? (obj["state"] as string) : undefined
  const url = typeof obj["url"] === "string" ? (obj["url"] as string) : undefined
  const rawMergeCommit = obj["mergeCommit"]
  const mergeCommit = rawMergeCommit && typeof rawMergeCommit === "object" && !Array.isArray(rawMergeCommit)
    ? { oid: typeof (rawMergeCommit as Record<string, unknown>)["oid"] === "string" ? ((rawMergeCommit as Record<string, unknown>)["oid"] as string) : undefined }
    : null
  return { state, url, mergeCommit }
}

export function extractPrNumberFromUrl(url: string): number | null {
  const match = url.match(/\/pull\/(\d+)/)
  if (!match || !match[1]) return null
  const n = Number(match[1])
  return Number.isFinite(n) ? n : null
}

export function extractIssueNumberFromMessage(message: string): string | null {
  const match = message.match(/(\d+)/)
  return match && match[1] ? match[1] : null
}

function combinedGhOutput(result: { stdout: string; stderr: string }): string {
  return [result.stdout.trim(), result.stderr.trim()].filter(Boolean).join("\n")
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error)
}

function resolveIssueNumber(context: ActionContext): number | null {
  if (typeof context.issueNumber === "number" && context.issueNumber > 0) {
    return context.issueNumber
  }
  const fromVars = numberInput(context.variables, "issueNumber")
  if (typeof fromVars === "number" && fromVars > 0) return fromVars
  return null
}
