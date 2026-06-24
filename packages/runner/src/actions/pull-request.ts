import type { ActionContext, ActionResult } from "../core/types.js"
import { numberInput, stringInput } from "../core/json.js"
import { stringAt } from "../core/json-path.js"
import { runCommand } from "../system/process.js"
import { git as defaultGit } from "./git.js"
import { isIssueFieldSource, resolveIssueFields, type IssueFields } from "./issue-fields.js"
import {
  classifyGhFailure,
  classifyPushFailure,
  combinedGhOutput,
  errorMessage,
  extractPrNumberFromUrl,
  mergeOrConfirmPr,
  parsePrList,
  resolveBaseSha,
  resolveCurrentBranch,
  runGhPrecheck,
  type PublishViaPrFailureKind,
} from "./publish-via-pr.js"

type GitRunner = typeof defaultGit
type GhRunner = typeof runCommand

let git: GitRunner = defaultGit
let gh: GhRunner = runCommand

export function setPullRequestGitRunnerForTest(runner: GitRunner | null) {
  git = runner ?? defaultGit
}

export function setPullRequestGhRunnerForTest(runner: GhRunner | null) {
  gh = runner ?? runCommand
}

export type PullRequestErrorCode =
  PublishViaPrFailureKind

export interface PullRequestStep {
  name: string
  command: string
  exitCode: number
  output: string
}

export interface CreatePullRequestOutput {
  kind: "create-pull-request"
  status: "completed" | "failed"
  source: string
  targetBranch: string
  branch: string | null
  prNumber: number | null
  prUrl: string | null
  operation: "created" | "updated" | "reused" | null
  baseSha: string | null
  pushed: boolean
  errorCode: PullRequestErrorCode | null
  message: string | null
  output: string
  steps: PullRequestStep[]
}

export interface MergePullRequestOutput {
  kind: "merge-pull-request"
  status: "completed" | "failed"
  prNumber: number | null
  prUrl: string | null
  mergeCommitSha: string | null
  method: "squash"
  errorCode: PullRequestErrorCode | null
  message: string | null
  output: string
  steps: PullRequestStep[]
}

export async function createPullRequestAction(context: ActionContext): Promise<ActionResult> {
  const source = stringInput(context.with, "source") ?? stringAt(context.variables, ["workspace", "branch"]) ?? "HEAD"
  const target = stringInput(context.with, "target") ?? stringAt(context.variables, ["repository", "baseBranch"]) ?? stringAt(context.variables, ["project", "defaultBranch"]) ?? stringAt(context.variables, ["project", "baseBranch"]) ?? "main"
  const remote = stringInput(context.with, "remote") ?? "origin"
  const workDir = stringAt(context.variables, ["workspace", "path"]) ?? context.workDir

  const steps: PullRequestStep[] = []
  const record = createRecorder(steps)

  const fail = (
    errorCode: PullRequestErrorCode,
    message: string,
    payload: Partial<CreatePullRequestOutput> = {},
  ): ActionResult => buildCreatePullRequestOutput({
    kind: "create-pull-request",
    status: "failed",
    source,
    targetBranch: target,
    branch: payload.branch ?? null,
    prNumber: payload.prNumber ?? null,
    prUrl: payload.prUrl ?? null,
    operation: payload.operation ?? null,
    baseSha: payload.baseSha ?? null,
    pushed: payload.pushed ?? false,
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
      classifyPushFailure(pushResult.stdout, pushResult.stderr),
      `git push --force-with-lease ${remote} ${branchProbe.name} failed: ${pushResult.combinedOutput}`,
      { output: pushResult.combinedOutput, branch: branchProbe.name, baseSha: baseSha.sha },
    )
  }

  const opened = await openOrUpdatePr(workDir, branchProbe.name, target, text.title, text.body, context.signal, record)
  if (opened.kind === "failure") {
    return fail(opened.errorCode, opened.message, {
      output: opened.output,
      branch: branchProbe.name,
      baseSha: baseSha.sha,
      pushed: true,
    })
  }

  return buildCreatePullRequestOutput({
    kind: "create-pull-request",
    status: "completed",
    source,
    targetBranch: target,
    branch: branchProbe.name,
    prNumber: opened.prNumber,
    prUrl: opened.prUrl,
    operation: opened.operation,
    baseSha: baseSha.sha,
    pushed: true,
    errorCode: null,
    message: null,
    output: opened.output,
    steps,
  })
}

export async function mergePullRequestAction(context: ActionContext): Promise<ActionResult> {
  const method = stringInput(context.with, "method") ?? "squash"
  const workDir = stringAt(context.variables, ["workspace", "path"]) ?? context.workDir

  const steps: PullRequestStep[] = []
  const record = createRecorder(steps)

  const fail = (
    errorCode: PullRequestErrorCode,
    message: string,
    payload: Partial<MergePullRequestOutput> = {},
  ): ActionResult => buildMergePullRequestOutput({
    kind: "merge-pull-request",
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

  const merged = await mergeOrConfirmPr(gh, workDir, resolvedPr.prNumber, subject.subject, context.signal, record)
  if (merged.kind === "failure") {
    return fail(merged.failureKind, merged.message, {
      output: merged.output,
      prNumber: resolvedPr.prNumber,
      prUrl: merged.prUrl ?? resolvedPr.prUrl,
    })
  }

  return buildMergePullRequestOutput({
    kind: "merge-pull-request",
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

function createRecorder(steps: PullRequestStep[]) {
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

async function openOrUpdatePr(
  workDir: string,
  head: string,
  base: string,
  title: string,
  body: string,
  signal: AbortSignal,
  record: (name: string, command: string, exitCode: number, output: string) => void,
): Promise<
  | { kind: "ok"; prNumber: number; prUrl: string; operation: "created" | "updated"; output: string }
  | { kind: "failure"; errorCode: PullRequestErrorCode; message: string; output: string }
> {
  const listResult = await gh("gh", ["pr", "list", "--head", head, "--base", base, "--state", "open", "--json", "number,url"], workDir, signal)
  const listOutput = combinedGhOutput(listResult)
  record("gh-pr-list", `pr list --head ${head} --base ${base} --state open --json number,url`, listResult.exitCode, listOutput)
  if (listResult.exitCode !== 0) {
    return {
      kind: "failure",
      errorCode: classifyGhFailure(listResult.stdout, listResult.stderr),
      message: `gh pr list failed: ${listOutput}`,
      output: listOutput,
    }
  }

  const existing = parsePrList(listResult.stdout)
  if (existing.length > 0) {
    const pr = existing[0]!
    const editResult = await gh("gh", ["pr", "edit", String(pr.number), "--title", title, "--body", body], workDir, signal)
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
    return { kind: "ok", prNumber: pr.number, prUrl: pr.url, operation: "updated", output: editOutput || `Updated PR #${pr.number}` }
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
  | { kind: "failure"; errorCode: PullRequestErrorCode; message: string; output: string }
> {
  const explicit = numberInput(context.with, "prNumber")
  if (explicit !== undefined) return { kind: "ok", prNumber: explicit, prUrl: null }

  const source = stringInput(context.with, "source") ?? stringAt(context.variables, ["workspace", "branch"])
  const target = stringInput(context.with, "target") ?? stringAt(context.variables, ["repository", "baseBranch"]) ?? stringAt(context.variables, ["project", "defaultBranch"]) ?? stringAt(context.variables, ["project", "baseBranch"]) ?? "main"
  if (!source) {
    return {
      kind: "failure",
      errorCode: "config-error",
      message: "merge-pull-request requires prNumber or source branch.",
      output: "merge-pull-request requires prNumber or source branch.",
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

function buildCreatePullRequestOutput(output: CreatePullRequestOutput): ActionResult {
  const json = JSON.stringify(output)
  if (output.status === "completed") {
    return { status: "success", message: "Pull request created or updated", output: json }
  }
  return {
    status: "failure",
    message: `Create pull request failed (${output.errorCode ?? "unknown"}): ${output.message ?? output.output}`,
    output: json,
    exitCode: 1,
  }
}

function buildMergePullRequestOutput(output: MergePullRequestOutput): ActionResult {
  const json = JSON.stringify(output)
  if (output.status === "completed") {
    return { status: "success", message: "Pull request merged", output: json }
  }
  return {
    status: "failure",
    message: `Merge pull request failed (${output.errorCode ?? "unknown"}): ${output.message ?? output.output}`,
    output: json,
    exitCode: 1,
  }
}
