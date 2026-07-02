import type { ActionContext, ActionResult } from "../core/types.js"
import { numberInput, stringInput } from "../core/json.js"
import { stringAt } from "../core/json-path.js"
import { runCommand, type CommandResult } from "../system/process.js"
import { git as defaultGit } from "./git.js"
import { isIssueFieldSource, resolveIssueFields, type IssueFields } from "./issue-fields.js"
import {
  classifyPrChecks,
  parsePrStatusCheckRollupResult,
} from "./github-pr-checks.js"
import {
  combinedGhOutput,
  errorMessage,
  extractPrNumberFromUrl,
  parsePrList,
  parsePrListWithDraft,
  parsePrView,
  parsePrViewWithDraft,
} from "./github-pr-parse.js"
import type {
  CreateGitHubPrOutput,
  GitHubPrErrorCode,
  GitHubPrStep,
  MarkGitHubPrReadyOutput,
  MergeGitHubPrOutput,
} from "./github-pr-types.js"

export {
  classifyPrChecks,
  parsePrStatusCheckRollup,
  type PrCheckEntry,
} from "./github-pr-checks.js"
export {
  combinedGhOutput,
  errorMessage,
  extractPrNumberFromUrl,
  parsePrList,
  parsePrListWithDraft,
  parsePrView,
  parsePrViewWithDraft,
} from "./github-pr-parse.js"
export type {
  CreateGitHubPrOutput,
  GitHubPrErrorCode,
  GitHubPrStep,
  MarkGitHubPrReadyOutput,
  MergeGitHubPrOutput,
} from "./github-pr-types.js"

type GitRunner = typeof defaultGit
type GhRunner = typeof runCommand

let git: GitRunner = defaultGit
let gh: GhRunner = runCommand

export function setGitHubPrGitRunnerForTest(runner: GitRunner | null) {
  git = runner ?? defaultGit
}

export function setGitHubPrGhRunnerForTest(runner: GhRunner | null) {
  gh = runner ?? runCommand
}

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

export async function createGitHubPrAction(context: ActionContext): Promise<ActionResult> {
  const source = stringInput(context.with, "source") ?? stringAt(context.variables, ["workspace", "branch"]) ?? "HEAD"
  const target = stringInput(context.with, "target") ?? stringAt(context.variables, ["repository", "baseBranch"]) ?? stringAt(context.variables, ["project", "defaultBranch"]) ?? stringAt(context.variables, ["project", "baseBranch"]) ?? "main"
  const remote = stringInput(context.with, "remote") ?? "origin"
  const draft = context.with?.["draft"] !== false
  const workDir = stringAt(context.variables, ["workspace", "path"]) ?? context.workDir

  const steps: GitHubPrStep[] = []
  const record = createRecorder(steps)

  const fail = (
    errorCode: GitHubPrErrorCode,
    message: string,
    payload: Partial<CreateGitHubPrOutput> = {},
  ): ActionResult => buildCreateGitHubPrOutput({
    kind: "create-github-pr",
    status: "failed",
    source,
    targetBranch: target,
    branch: payload.branch ?? null,
    prNumber: payload.prNumber ?? null,
    prUrl: payload.prUrl ?? null,
    operation: payload.operation ?? null,
    baseSha: payload.baseSha ?? null,
    pushed: payload.pushed ?? false,
    draft,
    errorCode,
    message,
    output: payload.output ?? message,
    steps,
  })

  const ghPrecheck = await runGhPrecheck(gh, workDir, context.signal)
  record("gh-precheck", "gh --version && gh auth status", ghPrecheck.ok ? 0 : ghPrecheck.exitCode, ghPrecheck.output)
  if (!ghPrecheck.ok) {
    return fail("config-error", ghPrecheck.message, { output: ghPrecheck.output })
  }

  const text = await resolveCreatePrText(context)
  if (text.kind === "failure") {
    return fail("config-error", text.message, { output: text.message })
  }

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

  const baseSha = await resolveBaseSha(git, workDir, remote, target, context.signal, record)
  if (baseSha.kind === "failure") {
    return fail(baseSha.failureKind, baseSha.message, { output: baseSha.output, branch: branchProbe.name })
  }

  const pushResult = await git(workDir, ["push", "--force-with-lease", remote, branchProbe.name], context.signal)
  record("git-push", `push --force-with-lease ${remote} ${branchProbe.name}`, pushResult.exitCode, pushResult.combinedOutput)
  if (!pushResult.success) {
    return fail(
      classifyGhFailure(pushResult.stdout, pushResult.stderr),
      `git push --force-with-lease ${remote} ${branchProbe.name} failed: ${pushResult.combinedOutput}`,
      { output: pushResult.combinedOutput, branch: branchProbe.name, baseSha: baseSha.sha },
    )
  }

  const opened = await openOrReusePr(gh, workDir, branchProbe.name, target, text.title, text.body, draft, context.signal, record)
  if (opened.kind === "failure") {
    return fail(opened.errorCode, opened.message, {
      output: opened.output,
      branch: branchProbe.name,
      baseSha: baseSha.sha,
      pushed: true,
    })
  }

  return buildCreateGitHubPrOutput({
    kind: "create-github-pr",
    status: "completed",
    source,
    targetBranch: target,
    branch: branchProbe.name,
    prNumber: opened.prNumber,
    prUrl: opened.prUrl,
    operation: opened.operation,
    baseSha: baseSha.sha,
    pushed: true,
    draft,
    errorCode: null,
    message: null,
    output: opened.output,
    steps,
  })
}

export async function mergeGitHubPrAction(context: ActionContext): Promise<ActionResult> {
  const method = stringInput(context.with, "method") ?? "squash"
  const workDir = stringAt(context.variables, ["workspace", "path"]) ?? context.workDir

  const steps: GitHubPrStep[] = []
  const record = createRecorder(steps)

  const fail = (
    errorCode: GitHubPrErrorCode,
    message: string,
    payload: Partial<MergeGitHubPrOutput> = {},
  ): ActionResult => buildMergeGitHubPrOutput({
    kind: "merge-github-pr",
    status: "failed",
    prNumber: payload.prNumber ?? null,
    prUrl: payload.prUrl ?? null,
    mergeCommitSha: payload.mergeCommitSha ?? null,
    method: "squash",
    errorCode,
    message,
    output: payload.output ?? message,
    steps,
  })

  if (method !== "squash") {
    return fail("config-error", `Unsupported merge method '${method}'. Supported method: squash.`)
  }

  const ghPrecheck = await runGhPrecheck(gh, workDir, context.signal)
  record("gh-precheck", "gh --version && gh auth status", ghPrecheck.ok ? 0 : ghPrecheck.exitCode, ghPrecheck.output)
  if (!ghPrecheck.ok) {
    return fail("config-error", ghPrecheck.message, { output: ghPrecheck.output })
  }

  const subject = await resolveMergeSubject(context)
  if (subject.kind === "failure") {
    return fail("config-error", subject.message, { output: subject.message })
  }

  const resolvedPr = await resolvePrNumberForMerge(context, workDir, context.signal, record)
  if (resolvedPr.kind === "failure") {
    return fail(resolvedPr.errorCode, resolvedPr.message, { output: resolvedPr.output })
  }

  const merged = await waitChecksAndMergePr(gh, workDir, resolvedPr.prNumber, subject.subject, context.signal, record)
  if (merged.kind === "failure") {
    return fail(merged.errorCode, merged.message, {
      output: merged.output,
      prNumber: resolvedPr.prNumber,
      prUrl: merged.prUrl ?? resolvedPr.prUrl,
    })
  }

  return buildMergeGitHubPrOutput({
    kind: "merge-github-pr",
    status: "completed",
    prNumber: resolvedPr.prNumber,
    prUrl: merged.prUrl ?? resolvedPr.prUrl,
    mergeCommitSha: merged.mergeCommitSha,
    method: "squash",
    errorCode: null,
    message: null,
    output: merged.output,
    steps,
  })
}

export async function markGitHubPrReadyAction(context: ActionContext): Promise<ActionResult> {
  const prNumber = numberInput(context.with, "prNumber")
  if (prNumber === undefined) {
    return markReadyOutput({
      kind: "mark-github-pr-ready",
      status: "failed",
      prNumber: null,
      prUrl: null,
      state: null,
      previousState: null,
      transitioned: false,
      errorCode: "config-error",
      message: "mark-github-pr-ready requires prNumber",
      output: "mark-github-pr-ready requires prNumber",
      steps: [],
    })
  }

  const workDir = stringAt(context.variables, ["workspace", "path"]) ?? context.workDir
  const steps: GitHubPrStep[] = []
  const record = createRecorder(steps)

  const fail = (
    errorCode: GitHubPrErrorCode,
    message: string,
    payload: Partial<MarkGitHubPrReadyOutput> = {},
  ): ActionResult => markReadyOutput({
    kind: "mark-github-pr-ready",
    status: "failed",
    prNumber,
    prUrl: payload.prUrl ?? null,
    state: payload.state ?? null,
    previousState: payload.previousState ?? null,
    transitioned: payload.transitioned ?? false,
    errorCode,
    message,
    output: payload.output ?? message,
    steps,
  })

  const ghPrecheck = await runGhPrecheck(gh, workDir, context.signal)
  record("gh-precheck", "gh --version && gh auth status", ghPrecheck.ok ? 0 : ghPrecheck.exitCode, ghPrecheck.output)
  if (!ghPrecheck.ok) {
    return fail("config-error", ghPrecheck.message, { output: ghPrecheck.output })
  }

  const viewResult = await gh("gh", ["pr", "view", String(prNumber), "--json", "state,isDraft,url"], workDir, context.signal)
  const viewOutput = combinedGhOutput(viewResult)
  record("gh-pr-view", `pr view ${prNumber} --json state,isDraft,url`, viewResult.exitCode, viewOutput)
  if (viewResult.exitCode !== 0) {
    return fail(classifyGhFailure(viewResult.stdout, viewResult.stderr), `gh pr view ${prNumber} failed: ${viewOutput}`, { output: viewOutput })
  }

  const view = parsePrViewWithDraft(viewResult.stdout)
  if (!view) {
    return fail("retry-safe", `gh pr view ${prNumber} returned unparseable JSON: ${viewOutput}`, { output: viewOutput })
  }

  if (view.state === "CLOSED" || view.state === "MERGED") {
    return fail("pr-state-conflict", `PR #${prNumber} is in state ${view.state}; refusing to mark ready.`, {
      output: viewOutput,
      prUrl: view.url ?? null,
    })
  }

  const prUrl = view.url ?? null
  const previousState: "READY" | "DRAFT" = view.isDraft ? "DRAFT" : "READY"
  if (!view.isDraft) {
    return markReadyOutput({
      kind: "mark-github-pr-ready",
      status: "completed",
      prNumber,
      prUrl,
      state: "READY",
      previousState: "READY",
      transitioned: false,
      errorCode: null,
      message: `PR #${prNumber} is already ready for review`,
      output: `PR #${prNumber} already READY; no mutation performed`,
      steps,
    })
  }

  const readyResult = await gh("gh", ["pr", "ready", String(prNumber)], workDir, context.signal)
  const readyOutput = combinedGhOutput(readyResult)
  record("gh-pr-ready", `pr ready ${prNumber}`, readyResult.exitCode, readyOutput)
  if (readyResult.exitCode !== 0) {
    return fail(classifyGhFailure(readyResult.stdout, readyResult.stderr), `gh pr ready ${prNumber} failed: ${readyOutput}`, {
      output: readyOutput,
      prUrl,
      previousState,
    })
  }

  return markReadyOutput({
    kind: "mark-github-pr-ready",
    status: "completed",
    prNumber,
    prUrl,
    state: "READY",
    previousState,
    transitioned: true,
    errorCode: null,
    message: `Marked PR #${prNumber} as ready for review`,
    output: readyOutput || `Marked PR #${prNumber} as ready for review`,
    steps,
  })
}

function createRecorder(steps: GitHubPrStep[]) {
  return (name: string, command: string, exitCode: number, output: string) => {
    steps.push({ name, command, exitCode, output })
  }
}

async function resolveCreatePrText(context: ActionContext): Promise<
  | { kind: "ok"; title: string; body: string }
  | { kind: "failure"; message: string }
> {
  const titleLiteral = stringInput(context.with, "title") ?? stringInput(context.with, "message")
  const bodyLiteral = stringInput(context.with, "body")
  const titleSource = titleLiteral === undefined ? stringInput(context.with, "titleFrom") ?? "issue.title" : undefined
  const bodySource = bodyLiteral === undefined ? stringInput(context.with, "bodyFrom") ?? "issue.body" : undefined

  const sourceError = validateIssueFieldSource("titleFrom", titleSource) ?? validateIssueFieldSource("bodyFrom", bodySource)
  if (sourceError) return { kind: "failure", message: sourceError }

  let issueFields: IssueFields | null = null
  if (titleSource || bodySource) {
    const loaded = await loadIssueFields(context)
    if (loaded.kind === "failure") return loaded
    issueFields = loaded.issueFields
  }

  return {
    kind: "ok",
    title: titleLiteral ?? resolveIssueFieldValue(requiredIssueFields(issueFields), titleSource),
    body: bodyLiteral ?? resolveIssueFieldValue(requiredIssueFields(issueFields), bodySource),
  }
}

async function resolveMergeSubject(context: ActionContext): Promise<
  | { kind: "ok"; subject: string }
  | { kind: "failure"; message: string }
> {
  const literal = stringInput(context.with, "subject")
  if (literal !== undefined) return { kind: "ok", subject: literal }

  const source = stringInput(context.with, "subjectFrom") ?? "issue.title"
  const sourceError = validateIssueFieldSource("subjectFrom", source)
  if (sourceError) return { kind: "failure", message: sourceError }

  const issueFields = await loadIssueFields(context)
  if (issueFields.kind === "failure") return issueFields
  return { kind: "ok", subject: resolveIssueFieldValue(issueFields.issueFields, source) }
}

function validateIssueFieldSource(name: string, source: string | undefined): string | null {
  if (source === undefined || isIssueFieldSource(source)) return null
  return `Unsupported ${name} source '${source}'. Supported sources: issue.title, issue.body.`
}

async function loadIssueFields(context: ActionContext): Promise<
  | { kind: "ok"; issueFields: IssueFields }
  | { kind: "failure"; message: string }
> {
  try {
    return { kind: "ok", issueFields: await resolveIssueFields(context) }
  } catch (error) {
    return { kind: "failure", message: errorMessage(error) }
  }
}

function resolveIssueFieldValue(issueFields: IssueFields, source: string | undefined): string {
  if (source === "issue.body") return issueFields.body
  return issueFields.title
}

function requiredIssueFields(issueFields: IssueFields | null): IssueFields {
  if (issueFields) return issueFields
  throw new Error("issue fields were not loaded")
}

async function openOrReusePr(
  gh: GhRunner,
  workDir: string,
  head: string,
  base: string,
  title: string,
  body: string,
  draft: boolean,
  signal: AbortSignal,
  record: (name: string, command: string, exitCode: number, output: string) => void,
): Promise<
  | { kind: "ok"; prNumber: number; prUrl: string; operation: "created" | "updated" | "reused"; output: string }
  | { kind: "failure"; errorCode: GitHubPrErrorCode; message: string; output: string }
> {
  const listResult = await gh("gh", ["pr", "list", "--head", head, "--base", base, "--state", "open", "--json", "number,url,isDraft"], workDir, signal)
  const listOutput = combinedGhOutput(listResult)
  record("gh-pr-list", `pr list --head ${head} --base ${base} --state open --json number,url,isDraft`, listResult.exitCode, listOutput)
  if (listResult.exitCode !== 0) {
    return {
      kind: "failure",
      errorCode: classifyGhFailure(listResult.stdout, listResult.stderr),
      message: `gh pr list failed: ${listOutput}`,
      output: listOutput,
    }
  }

  const existing = parsePrListWithDraft(listResult.stdout)
  if (existing.length > 0) {
    const pr = existing[0]!
    const editArgs = ["pr", "edit", String(pr.number), "--title", title, "--body", body]
    const editResult = await gh("gh", editArgs, workDir, signal)
    const editOutput = combinedGhOutput(editResult)
    record("gh-pr-edit", `pr edit ${pr.number} --title "${title}" --body "${body}"`, editResult.exitCode, editOutput)
    if (editResult.exitCode !== 0) {
      return {
        kind: "failure",
        errorCode: classifyGhFailure(editResult.stdout, editResult.stderr),
        message: `gh pr edit ${pr.number} failed: ${editOutput}`,
        output: editOutput,
      }
    }
    return { kind: "ok", prNumber: pr.number, prUrl: pr.url, operation: "reused", output: editOutput || `Reused PR #${pr.number}` }
  }

  const createArgs = ["pr", "create", "--head", head, "--base", base, "--title", title, "--body", body]
  if (draft) createArgs.push("--draft")
  const createResult = await gh("gh", createArgs, workDir, signal)
  const createOutput = combinedGhOutput(createResult)
  record("gh-pr-create", `pr create --head ${head} --base ${base} --title "${title}"${draft ? " --draft" : ""}`, createResult.exitCode, createOutput)
  if (createResult.exitCode !== 0) {
    return {
      kind: "failure",
      errorCode: classifyGhFailure(createResult.stdout, createResult.stderr),
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
      errorCode: "retry-safe",
      message: `gh pr create did not return a PR URL: ${createOutput}`,
      output: createOutput,
    }
  }

  return { kind: "ok", prNumber, prUrl: url, operation: "created", output: createOutput }
}

async function resolvePrNumberForMerge(
  context: ActionContext,
  workDir: string,
  signal: AbortSignal,
  record: (name: string, command: string, exitCode: number, output: string) => void,
): Promise<
  | { kind: "ok"; prNumber: number; prUrl: string | null }
  | { kind: "failure"; errorCode: GitHubPrErrorCode; message: string; output: string }
> {
  const explicit = numberInput(context.with, "prNumber")
  if (explicit !== undefined) return { kind: "ok", prNumber: explicit, prUrl: null }

  const source = stringInput(context.with, "source") ?? stringAt(context.variables, ["workspace", "branch"])
  const target = stringInput(context.with, "target") ?? stringAt(context.variables, ["repository", "baseBranch"]) ?? stringAt(context.variables, ["project", "defaultBranch"]) ?? stringAt(context.variables, ["project", "baseBranch"]) ?? "main"
  if (!source) {
    return {
      kind: "failure",
      errorCode: "config-error",
      message: "merge-github-pr requires prNumber or source branch.",
      output: "merge-github-pr requires prNumber or source branch.",
    }
  }

  const listResult = await gh("gh", ["pr", "list", "--head", source, "--base", target, "--state", "open", "--json", "number,url"], workDir, signal)
  const listOutput = combinedGhOutput(listResult)
  record("gh-pr-list", `pr list --head ${source} --base ${target} --state open --json number,url`, listResult.exitCode, listOutput)
  if (listResult.exitCode !== 0) {
    return {
      kind: "failure",
      errorCode: classifyGhFailure(listResult.stdout, listResult.stderr),
      message: `gh pr list failed: ${listOutput}`,
      output: listOutput,
    }
  }

  const existing = parsePrList(listResult.stdout)
  if (existing.length === 0) {
    return {
      kind: "failure",
      errorCode: "pr-state-conflict",
      message: `No open PR found for ${source} -> ${target}.`,
      output: listOutput,
    }
  }
  return { kind: "ok", prNumber: existing[0]!.number, prUrl: existing[0]!.url }
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
  record: (name: string, command: string, exitCode: number, output: string) => void,
): Promise<WaitChecksAndMergeOk | WaitChecksAndMergeFailure> {
  const viewResult = await runGhReadWithRetry(
    gh,
    ["pr", "view", String(prNumber), "--json", "state,mergeCommit,url,number,mergeStateStatus"],
    workDir,
    signal,
    record,
    "gh-pr-view",
    `pr view ${prNumber} --json state,mergeCommit,url,number,mergeStateStatus`,
  )
  const viewOutput = combinedGhOutput(viewResult)
  if (viewResult.exitCode !== 0) {
    return {
      kind: "failure",
      errorCode: classifyGhFailure(viewResult.stdout, viewResult.stderr),
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

  const checksWait = await waitForPrChecks(gh, workDir, prNumber, signal, record)
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
      ["pr", "view", String(prNumber), "--json", "mergeStateStatus"],
      workDir,
      signal,
      record,
      "gh-pr-merge-ready",
      `pr view ${prNumber} --json mergeStateStatus`,
    )
    const mergeStatusOutput = combinedGhOutput(mergeStatusResult)
    if (mergeStatusResult.exitCode !== 0) {
      return {
        kind: "failure",
        errorCode: classifyGhFailure(mergeStatusResult.stdout, mergeStatusResult.stderr),
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

  const mergeArgs = ["pr", "merge", String(prNumber), "--squash", "--subject", subject, "--body", ""]
  const mergeResult = await gh("gh", mergeArgs, workDir, signal)
  const mergeOutput = combinedGhOutput(mergeResult)
  record("gh-pr-merge", `pr merge ${prNumber} --squash --subject "${subject}"`, mergeResult.exitCode, mergeOutput)
  if (mergeResult.exitCode !== 0) {
    return {
      kind: "failure",
      errorCode: classifyGhFailure(mergeResult.stdout, mergeResult.stderr),
      message: `gh pr merge ${prNumber} --squash failed: ${mergeOutput}`,
      prUrl: view.url ?? null,
      output: mergeOutput,
    }
  }

  const recheck = await runGhReadWithRetry(
    gh,
    ["pr", "view", String(prNumber), "--json", "state,mergeCommit,url"],
    workDir,
    signal,
    record,
    "gh-pr-view-confirm",
    `pr view ${prNumber} --json state,mergeCommit,url`,
  )
  const recheckOutput = combinedGhOutput(recheck)
  if (recheck.exitCode !== 0) {
    return {
      kind: "failure",
      errorCode: classifyGhFailure(recheck.stdout, recheck.stderr),
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
  record: (name: string, command: string, exitCode: number, output: string) => void,
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
      ["pr", "view", String(prNumber), "--json", "statusCheckRollup"],
      workDir,
      signal,
      record,
      "gh-pr-checks",
      `pr view ${prNumber} --json statusCheckRollup`,
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
  record: (name: string, command: string, exitCode: number, output: string) => void,
  recordName: string,
  recordCommand: string,
): Promise<CommandResult> {
  let attempt = 0
  for (;;) {
    const result = await gh("gh", args, workDir, signal)
    const transient = result.exitCode !== 0
      && attempt < ghTransientRetryLimit
      && looksLikeRetrySafe(`${result.stdout}\n${result.stderr}`)
    if (!transient) {
      record(recordName, recordCommand, result.exitCode, combinedGhOutput(result))
      return result
    }
    attempt++
    record(recordName, `${recordCommand} (transient retry ${attempt}/${ghTransientRetryLimit})`, result.exitCode, combinedGhOutput(result))
    try {
      await delayWithSignal(ghTransientRetryBackoffMs, signal)
    } catch (error) {
      record(recordName, recordCommand, result.exitCode, `aborted during retry backoff: ${errorMessage(error)}`)
      return result
    }
    if (signal.aborted) return result
  }
}


export async function runGhPrecheck(gh: GhRunner, workDir: string, signal: AbortSignal): Promise<{ ok: true; output: string } | { ok: false; exitCode: number; output: string; message: string }> {
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

export async function resolveCurrentBranch(git: GitRunner, workDir: string, signal: AbortSignal): Promise<{ success: true; name: string } | { success: false; exitCode: number; combinedOutput: string }> {
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

export interface BaseShaOk {
  kind: "ok"
  sha: string
}

export interface BaseShaFailure {
  kind: "failure"
  failureKind: GitHubPrErrorCode
  message: string
  output: string
}

export async function resolveBaseSha(
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

function buildCreateGitHubPrOutput(output: CreateGitHubPrOutput): ActionResult {
  const json = JSON.stringify(output)
  if (output.status === "completed") {
    return { status: "success", message: "GitHub pull request created or reused", output: json }
  }
  return {
    status: "failure",
    message: `Create GitHub PR failed (${output.errorCode ?? "unknown"}): ${output.message ?? output.output}`,
    output: json,
    exitCode: 1,
  }
}

function buildMergeGitHubPrOutput(output: MergeGitHubPrOutput): ActionResult {
  const json = JSON.stringify(output)
  if (output.status === "completed") {
    return { status: "success", message: "GitHub pull request merged", output: json }
  }
  return {
    status: "failure",
    message: `Merge GitHub PR failed (${output.errorCode ?? "unknown"}): ${output.message ?? output.output}`,
    output: json,
    exitCode: 1,
  }
}

function markReadyOutput(output: MarkGitHubPrReadyOutput): ActionResult {
  const json = JSON.stringify(output)
  if (output.status === "completed") {
    return { status: "success", message: output.message ?? "Mark GitHub PR ready", output: json }
  }
  return {
    status: "failure",
    message: `Mark GitHub PR ready failed (${output.errorCode ?? "unknown"}): ${output.message ?? output.output}`,
    output: json,
    exitCode: 1,
  }
}

export function classifyGhFailure(stdout: string, stderr: string): GitHubPrErrorCode {
  const text = `${stdout}\n${stderr}`.toLowerCase()
  if (!text.trim()) return "retry-safe"
  if (looksLikeAuthFailure(text)) return "config-error"
  if (looksLikeProtectionConflict(text)) return "protection-conflict"
  if (looksLikeBaseMoved(text)) return "base-moved"
  if (looksLikePrStateConflict(text)) return "pr-state-conflict"
  if (looksLikeRetrySafe(text)) return "retry-safe"
  return "retry-safe"
}

export function classifyPushFailure(stdout: string, stderr: string): GitHubPrErrorCode {
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
  if (lower.includes("branch protection") || lower.includes("branch policy")) return true
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
  // Go net/http transport errors emitted by gh (e.g. when the TLS stream to
  // api.github.com is cut mid-response through an unstable proxy path).
  if (
    lower.includes("unexpected eof") ||
    lower.includes("connection refused") ||
    lower.includes("broken pipe") ||
    lower.includes("dial tcp") ||
    lower.includes("no such host") ||
    lower.includes("tls handshake") ||
    lower.includes("context deadline exceeded") ||
    lower.includes("i/o timeout")
  ) {
    return true
  }
  return false
}

