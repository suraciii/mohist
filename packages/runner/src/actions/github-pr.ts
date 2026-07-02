import type { ActionContext, ActionResult } from "../core/types.js"
import { numberInput, stringInput } from "../core/json.js"
import { stringAt } from "../core/json-path.js"
import { runCommand } from "../system/process.js"
import { git as defaultGit } from "./git.js"
import { isIssueFieldSource, resolveIssueFields, type IssueFields } from "./issue-fields.js"
import {
  classifyGhFailure,
  classifyPushFailure,
  looksLikeAuthFailure,
  looksLikeBaseMoved,
  looksLikePrStateConflict,
  looksLikeProtectionConflict,
  looksLikeRetrySafe,
} from "./github-pr-classify.js"
import {
  combinedGhOutput,
  errorMessage,
  extractPrNumberFromUrl,
  parsePrList,
  parsePrListWithDraft,
  parsePrViewWithDraft,
} from "./github-pr-parse.js"
import {
  getGitHubPrGh,
  getGitHubPrGit,
  runGhPrecheck,
  setGitHubPrGhRunnerForTest,
  setGitHubPrGitRunnerForTest,
} from "./github-pr-runtime.js"
import {
  setGitHubPrChecksTimingForTest,
  setGitHubPrTransientRetryForTest,
  waitChecksAndMergePr,
} from "./github-pr-merge.js"
import type {
  CreateGitHubPrOutput,
  GitHubPrErrorCode,
  GitHubPrStep,
  MarkGitHubPrReadyOutput,
  MergeGitHubPrOutput,
} from "./github-pr-types.js"

export {
  classifyGhFailure,
  classifyPushFailure,
  looksLikeAuthFailure,
  looksLikeBaseMoved,
  looksLikePrStateConflict,
  looksLikeProtectionConflict,
  looksLikeRetrySafe,
} from "./github-pr-classify.js"
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
export {
  setGitHubPrGhRunnerForTest,
  setGitHubPrGitRunnerForTest,
} from "./github-pr-runtime.js"
export {
  setGitHubPrChecksTimingForTest,
  setGitHubPrTransientRetryForTest,
  waitChecksAndMergePr,
} from "./github-pr-merge.js"
export type {
  CreateGitHubPrOutput,
  GitHubPrErrorCode,
  GitHubPrStep,
  MarkGitHubPrReadyOutput,
  MergeGitHubPrOutput,
} from "./github-pr-types.js"

type GitRunner = typeof defaultGit
type GhRunner = typeof runCommand

export async function createGitHubPrAction(context: ActionContext): Promise<ActionResult> {
  const source = stringInput(context.with, "source") ?? stringAt(context.variables, ["workspace", "branch"]) ?? "HEAD"
  const target = stringInput(context.with, "target") ?? stringAt(context.variables, ["repository", "baseBranch"]) ?? stringAt(context.variables, ["project", "defaultBranch"]) ?? stringAt(context.variables, ["project", "baseBranch"]) ?? "main"
  const remote = stringInput(context.with, "remote") ?? "origin"
  const draft = context.with?.["draft"] !== false
  const workDir = stringAt(context.variables, ["workspace", "path"]) ?? context.workDir

  const gh = getGitHubPrGh()
  const git = getGitHubPrGit()

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

  const gh = getGitHubPrGh()

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

  const resolvedPr = await resolvePrNumberForMerge(gh, context, workDir, context.signal, record)
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
  const gh = getGitHubPrGh()
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
  gh: GhRunner,
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

