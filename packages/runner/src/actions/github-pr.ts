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

// How long to poll mergeStateStatus after checks pass before giving up.
// GitHub's merge eligibility can lag behind PR check rollup by a few seconds;
// a BLOCKED/UNSTABLE state right after checks settle is usually transient.
const PR_MERGE_STATUS_POLL_TIMEOUT_MS = 120_000

let prChecksPollIntervalMs = PR_CHECKS_POLL_INTERVAL_MS_DEFAULT
let prChecksNoChecksGraceMs = PR_CHECKS_NO_CHECKS_GRACE_MS_DEFAULT

export function setGitHubPrChecksTimingForTest(timing: { pollIntervalMs?: number; noChecksGraceMs?: number } | null) {
  if (timing === null) {
    prChecksPollIntervalMs = PR_CHECKS_POLL_INTERVAL_MS_DEFAULT
    prChecksNoChecksGraceMs = PR_CHECKS_NO_CHECKS_GRACE_MS_DEFAULT
    return
  }
  if (timing.pollIntervalMs !== undefined) prChecksPollIntervalMs = timing.pollIntervalMs
  if (timing.noChecksGraceMs !== undefined) prChecksNoChecksGraceMs = timing.noChecksGraceMs
}

export type GitHubPrErrorCode =
  | "base-moved"
  | "retry-safe"
  | "config-error"
  | "protection-conflict"
  | "pr-state-conflict"
  | "pr-checks-failed"

export interface GitHubPrStep {
  name: string
  command: string
  exitCode: number
  output: string
}

export interface CreateGitHubPrOutput {
  kind: "create-github-pr"
  status: "completed" | "failed"
  source: string
  targetBranch: string
  branch: string | null
  prNumber: number | null
  prUrl: string | null
  operation: "created" | "updated" | "reused" | null
  baseSha: string | null
  pushed: boolean
  draft: boolean
  errorCode: GitHubPrErrorCode | null
  message: string | null
  output: string
  steps: GitHubPrStep[]
}

export interface MergeGitHubPrOutput {
  kind: "merge-github-pr"
  status: "completed" | "failed"
  prNumber: number | null
  prUrl: string | null
  mergeCommitSha: string | null
  method: "squash"
  errorCode: GitHubPrErrorCode | null
  message: string | null
  output: string
  steps: GitHubPrStep[]
}

export interface MarkGitHubPrReadyOutput {
  kind: "mark-github-pr-ready"
  status: "completed" | "failed"
  prNumber: number | null
  prUrl: string | null
  state: "READY" | "DRAFT" | null
  previousState: "READY" | "DRAFT" | null
  transitioned: boolean
  errorCode: GitHubPrErrorCode | null
  message: string | null
  output: string
  steps: GitHubPrStep[]
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
  const viewResult = await gh("gh", ["pr", "view", String(prNumber), "--json", "state,mergeCommit,url,number,mergeStateStatus"], workDir, signal)
  const viewOutput = combinedGhOutput(viewResult)
  record("gh-pr-view", `pr view ${prNumber} --json state,mergeCommit,url,number,mergeStateStatus`, viewResult.exitCode, viewOutput)
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

  const checksWait = await waitForPrChecks(gh, workDir, prNumber, signal, record)
  if (checksWait.kind === "failure") {
    return {
      kind: "failure",
      errorCode: "pr-checks-failed",
      message: `PR #${prNumber} checks failed: ${checksWait.message}`,
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
  // Poll it for up to PR_MERGE_STATUS_POLL_TIMEOUT_MS — BLOCKED/UNSTABLE right
  // after checks settle is usually transient (checks hadn't fully registered).
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
    const mergeStatusResult = await gh("gh", ["pr", "view", String(prNumber), "--json", "mergeStateStatus"], workDir, signal)
    const mergeStatusOutput = combinedGhOutput(mergeStatusResult)
    record("gh-pr-merge-ready", `pr view ${prNumber} --json mergeStateStatus`, mergeStatusResult.exitCode, mergeStatusOutput)
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
    if (mergeState === "CLEAN" || mergeState === "HAS_HOOKS" || mergeState === "UNKNOWN") {
      break
    }
    if (mergeState === "DIRTY" || mergeState === "BEHIND") {
      return {
        kind: "failure",
        errorCode: "base-moved",
        message: `PR #${prNumber} is ${mergeState}; rebase required.`,
        prUrl: view.url ?? null,
        output: mergeStatusOutput,
      }
    }
    if (mergeState === "DRAFT") {
      return {
        kind: "failure",
        errorCode: "pr-state-conflict",
        message: `PR #${prNumber} is still a draft.`,
        prUrl: view.url ?? null,
        output: mergeStatusOutput,
      }
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

  const recheck = await gh("gh", ["pr", "view", String(prNumber), "--json", "state,mergeCommit,url"], workDir, signal)
  const recheckOutput = combinedGhOutput(recheck)
  record("gh-pr-view-confirm", `pr view ${prNumber} --json state,mergeCommit,url`, recheck.exitCode, recheckOutput)
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

type PrChecksWaitResult =
  | { kind: "ok" }
  | { kind: "failure"; message: string; output: string }
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
  for (;;) {
    if (signal.aborted) {
      return {
        kind: "cancelled",
        message: `Cancelled before polling checks: ${signal.reason instanceof Error ? signal.reason.message : String(signal.reason ?? "aborted")}`,
        output: "cancelled before next poll",
      }
    }
    const checksResult = await gh(
      "gh",
      ["pr", "view", String(prNumber), "--json", "statusCheckRollup"],
      workDir,
      signal,
    )
    const checksOutput = combinedGhOutput(checksResult)
    record("gh-pr-checks", `pr view ${prNumber} --json statusCheckRollup`, checksResult.exitCode, checksOutput)
    if (checksResult.exitCode !== 0) {
      // Right after a push / force-push, GitHub can briefly report no check
      // runs before the workflow registers. Poll that state for a bounded
      // grace window, then allow repos with no CI to merge.
      if (looksLikeNoChecksReported(checksOutput)) {
        if (noChecksSince === null) noChecksSince = Date.now()
        if (Date.now() - noChecksSince >= prChecksNoChecksGraceMs) {
          return { kind: "ok" }
        }
      } else {
        return {
          kind: "failure",
          message: checksOutput,
          output: checksOutput,
        }
      }
    } else {
      const checks = parsePrStatusCheckRollup(checksResult.stdout)
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
          message: classification.message,
          output: classification.message,
        }
      }
      if (classification.kind === "passed") {
        return { kind: "ok" }
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

export interface PrCheckEntry {
  name: string
  bucket: string
  state: string
}

export function parsePrStatusCheckRollup(stdout: string): PrCheckEntry[] {
  const trimmed = stdout.trim()
  if (!trimmed) return []
  let parsed: unknown
  try {
    parsed = JSON.parse(trimmed)
  } catch {
    return []
  }
  if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) return []
  const rollup = (parsed as Record<string, unknown>)["statusCheckRollup"]
  if (!Array.isArray(rollup)) return []
  const out: PrCheckEntry[] = []
  for (const item of rollup) {
    if (!item || typeof item !== "object" || Array.isArray(item)) continue
    const obj = item as Record<string, unknown>
    const name = typeof obj["name"] === "string"
      ? (obj["name"] as string)
      : typeof obj["context"] === "string"
        ? (obj["context"] as string)
        : ""
    const status = typeof obj["status"] === "string" ? (obj["status"] as string) : ""
    const rawState = typeof obj["state"] === "string" ? (obj["state"] as string) : ""
    const conclusion = typeof obj["conclusion"] === "string" ? (obj["conclusion"] as string) : ""
    const state = conclusion || rawState || status
    const bucket = classifyRollupBucket(status || rawState, conclusion)
    out.push({ name, bucket, state })
  }
  return out
}

function classifyRollupBucket(status: string, conclusion: string): string {
  const normalizedStatus = status.toUpperCase()
  const normalizedConclusion = conclusion.toUpperCase()
  if (normalizedConclusion === "SUCCESS") return "pass"
  if (normalizedConclusion === "SKIPPED" || normalizedConclusion === "NEUTRAL") return "skip"
  if (normalizedConclusion === "FAILURE" || normalizedConclusion === "ERROR" || normalizedConclusion === "CANCELLED" || normalizedConclusion === "ACTION_REQUIRED") return "fail"
  if (normalizedStatus === "SUCCESS") return "pass"
  if (normalizedStatus === "SKIPPED" || normalizedStatus === "NEUTRAL") return "skip"
  if (normalizedStatus === "FAILURE" || normalizedStatus === "ERROR" || normalizedStatus === "CANCELLED" || normalizedStatus === "ACTION_REQUIRED") return "fail"
  return "pending"
}

type PrChecksClassification =
  | { kind: "pending" }
  | { kind: "passed" }
  | { kind: "failed"; message: string }

export function classifyPrChecks(entries: PrCheckEntry[]): PrChecksClassification {
  if (entries.length === 0) return { kind: "passed" }
  const failed: string[] = []
  for (const entry of entries) {
    const bucket = (entry.bucket ?? "").toLowerCase()
    if (bucket === "pending" || bucket === "") {
      return { kind: "pending" }
    }
    if (bucket === "fail") {
      failed.push(formatFailedCheck(entry))
    }
  }
  if (failed.length > 0) {
    return { kind: "failed", message: failed.join("; ") }
  }
  return { kind: "passed" }
}

function formatFailedCheck(entry: PrCheckEntry): string {
  const label = entry.name || "unknown check"
  const bucket = entry.bucket || "FAIL"
  const state = entry.state && entry.state !== bucket ? ` (state=${entry.state})` : ""
  return `${label} [bucket=${bucket}]${state}`
}

// GitHub can return this before checks register, or permanently for repos with no CI.
export function looksLikeNoChecksReported(output: string): boolean {
  return /no checks reported/i.test(output)
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

export function parsePrListWithDraft(stdout: string): { number: number; url: string; isDraft: boolean }[] {
  const trimmed = stdout.trim()
  if (!trimmed) return []
  let parsed: unknown
  try {
    parsed = JSON.parse(trimmed)
  } catch {
    return []
  }
  if (!Array.isArray(parsed)) return []
  const out: { number: number; url: string; isDraft: boolean }[] = []
  for (const item of parsed) {
    if (!item || typeof item !== "object" || Array.isArray(item)) continue
    const obj = item as Record<string, unknown>
    const number = obj["number"]
    const url = obj["url"]
    const draft = obj["isDraft"]
    if (typeof number === "number" && typeof url === "string") {
      out.push({ number, url, isDraft: draft === true })
    }
  }
  return out
}

interface PrViewState {
  state?: string
  url?: string
  mergeCommit?: { oid?: string } | null
  isDraft?: boolean
  mergeStateStatus?: string
}

export function parsePrView(stdout: string): PrViewState | null {
  return parsePrViewInternal(stdout, false)
}

export function parsePrViewWithDraft(stdout: string): PrViewState | null {
  return parsePrViewInternal(stdout, true)
}

function parsePrViewInternal(stdout: string, includeDraft: boolean): PrViewState | null {
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
  const result: PrViewState = { state, url, mergeCommit, mergeStateStatus: typeof obj["mergeStateStatus"] === "string" ? (obj["mergeStateStatus"] as string) : undefined }
  if (includeDraft) result.isDraft = obj["isDraft"] === true
  return result
}

export function extractPrNumberFromUrl(url: string): number | null {
  const match = url.match(/\/pull\/(\d+)/)
  if (!match || !match[1]) return null
  const n = Number(match[1])
  return Number.isFinite(n) ? n : null
}

export function combinedGhOutput(result: { stdout: string; stderr: string }): string {
  return [result.stdout.trim(), result.stderr.trim()].filter(Boolean).join("\n")
}

export function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error)
}
