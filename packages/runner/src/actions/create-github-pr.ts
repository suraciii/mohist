import { stringInput } from "../core/json.js"
import { stringAt } from "../core/json-path.js"
import type { ActionContext, ActionResult } from "../core/types.js"
import { runCommand, type CommandLineOptions } from "../system/process.js"
import { git as defaultGit, NETWORK_COMMAND_TIMEOUT_MS, type GitOptions } from "./git.js"
import { resolveCreatePrText } from "./github-pr-issue-fields.js"
import { combinedGhOutput, extractPrNumberFromUrl, parsePrListWithDraft } from "./github-pr-parse.js"
import { classifyGhFailure } from "./github-pr-classify.js"
import { getGitHubPrGh, getGitHubPrGit, runGhPrecheck } from "./github-pr-runtime.js"
import { timeoutStepMetadata, type CreateGitHubPrOutput, type GitHubPrErrorCode, type GitHubPrStep, type GitHubPrStepMetadata } from "./github-pr-types.js"
import { resolveDeliveryBaseBranch } from "./delivery-context.js"

type GitRunner = (workDir: string, args: string[], signal: AbortSignal, options?: GitOptions) => Promise<{
  success: boolean
  stdout: string
  stderr: string
  exitCode: number
  combinedOutput: string
  status?: "timeout"
  timeoutMs?: number
}>
type GhRunner = typeof runCommand

/**
 * `source` tag recorded against every captured `mohist/create-github-pr`
 * action body line. Phase-distinguished so the web viewer can tell
 * which ops phase produced which line.
 */
const ACTION_SOURCE = "action:create-github-pr"

function sinkOptions(context: ActionContext): GitOptions | undefined {
  return context.log ? { sink: { log: context.log, source: ACTION_SOURCE } } : undefined
}

function networkGitOptions(context: ActionContext): GitOptions | undefined {
  if (!context.log) return { timeoutMs: NETWORK_COMMAND_TIMEOUT_MS }
  return { sink: { log: context.log, source: ACTION_SOURCE }, timeoutMs: NETWORK_COMMAND_TIMEOUT_MS }
}

function ghLineOptions(context: ActionContext): CommandLineOptions | undefined {
  return context.log ? { onLine: (line) => context.log!.write(ACTION_SOURCE, line) } : undefined
}

function networkGhOptions(context: ActionContext): CommandLineOptions | undefined {
  if (!context.log) return { timeoutMs: NETWORK_COMMAND_TIMEOUT_MS }
  return { onLine: (line) => context.log!.write(ACTION_SOURCE, line), timeoutMs: NETWORK_COMMAND_TIMEOUT_MS }
}

export async function createGitHubPrAction(context: ActionContext): Promise<ActionResult> {
  const source = stringInput(context.with, "source") ?? stringAt(context.variables, ["workspace", "branch"]) ?? "HEAD"
  const target = resolveDeliveryBaseBranch(context)
  if (!target) return { status: "failure", message: "create-github-pr requires the authoritative repository base branch" }
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

  const ghOpts = networkGhOptions(context)
  const ghPrecheck = await runGhPrecheck(gh, workDir, context.signal, ghOpts)
  record("gh-precheck", "gh --version && gh auth status", ghPrecheck.ok ? 0 : ghPrecheck.exitCode, ghPrecheck.output, ghPrecheck.ok ? undefined : timeoutStepMetadata(ghPrecheck))
  if (!ghPrecheck.ok) {
    return fail("config-error", ghPrecheck.message, { output: ghPrecheck.output })
  }

  const text = await resolveCreatePrText(context)
  if (text.kind === "failure") {
    return fail("config-error", text.message, { output: text.message })
  }

  const explicitSource = source !== "HEAD"
  const gitOpts = sinkOptions(context)
  const branchProbe = explicitSource
    ? { success: true as const, name: source }
    : await resolveCurrentBranch(git, workDir, context.signal, gitOpts)
  if (explicitSource) {
    record("git-source-anchor", `use source ${source}`, 0, source)
  } else {
    record("rev-parse-HEAD", "rev-parse --abbrev-ref HEAD", branchProbe.success ? 0 : branchProbe.exitCode, branchProbe.success ? branchProbe.name : branchProbe.combinedOutput)
  }
  if (!branchProbe.success) {
    return fail("retry-safe", `Could not resolve current branch: ${branchProbe.combinedOutput}`, { output: branchProbe.combinedOutput })
  }

  const baseSha = await resolveBaseSha(git, workDir, remote, target, context.signal, record, gitOpts)
  if (baseSha.kind === "failure") {
    return fail(baseSha.failureKind, baseSha.message, { output: baseSha.output, branch: branchProbe.name })
  }

  const pushResult = await git(workDir, ["push", "--force-with-lease", remote, branchProbe.name], context.signal, networkGitOptions(context))
  record("git-push", `push --force-with-lease ${remote} ${branchProbe.name}`, pushResult.exitCode, pushResult.combinedOutput, timeoutStepMetadata(pushResult))
  if (!pushResult.success) {
    return fail(
      classifyGhFailure(pushResult.stdout, pushResult.stderr),
      `git push --force-with-lease ${remote} ${branchProbe.name} failed: ${pushResult.combinedOutput}`,
      { output: pushResult.combinedOutput, branch: branchProbe.name, baseSha: baseSha.sha },
    )
  }

  const opened = await openOrReusePr(gh, workDir, branchProbe.name, target, text.title, text.body, draft, context.signal, record, ghOpts)
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

function createRecorder(steps: GitHubPrStep[]) {
  return (name: string, command: string, exitCode: number, output: string, metadata?: GitHubPrStepMetadata) => {
    steps.push({ name, command, exitCode, output, ...metadata })
  }
}

export async function openOrReusePr(
  gh: GhRunner,
  workDir: string,
  head: string,
  base: string,
  title: string,
  body: string,
  draft: boolean,
  signal: AbortSignal,
  record: (name: string, command: string, exitCode: number, output: string, metadata?: GitHubPrStepMetadata) => void,
  options?: CommandLineOptions,
): Promise<
  | { kind: "ok"; prNumber: number; prUrl: string; operation: "created" | "updated" | "reused"; output: string }
  | { kind: "failure"; errorCode: GitHubPrErrorCode; message: string; output: string }
> {
  const listResult = await gh("gh", ["pr", "list", "--head", head, "--base", base, "--state", "open", "--json", "number,url,isDraft"], workDir, signal, undefined, options)
  const listOutput = combinedGhOutput(listResult)
  record("gh-pr-list", `pr list --head ${head} --base ${base} --state open --json number,url,isDraft`, listResult.exitCode, listOutput, timeoutStepMetadata(listResult))
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
    const editResult = await gh("gh", editArgs, workDir, signal, undefined, options)
    const editOutput = combinedGhOutput(editResult)
    record("gh-pr-edit", `pr edit ${pr.number} --title "${title}" --body "${body}"`, editResult.exitCode, editOutput, timeoutStepMetadata(editResult))
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
  const createResult = await gh("gh", createArgs, workDir, signal, undefined, options)
  const createOutput = combinedGhOutput(createResult)
  record("gh-pr-create", `pr create --head ${head} --base ${base} --title "${title}"${draft ? " --draft" : ""}`, createResult.exitCode, createOutput, timeoutStepMetadata(createResult))
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

export interface ResolveCurrentBranchOk { success: true; name: string }
export interface ResolveCurrentBranchFailure { success: false; exitCode: number; combinedOutput: string }

export async function resolveCurrentBranch(git: GitRunner, workDir: string, signal: AbortSignal, opts?: GitOptions): Promise<ResolveCurrentBranchOk | ResolveCurrentBranchFailure> {
  const result = await git(workDir, ["rev-parse", "--abbrev-ref", "HEAD"], signal, opts)
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
  record: (name: string, command: string, exitCode: number, output: string, metadata?: GitHubPrStepMetadata) => void,
  opts?: GitOptions,
): Promise<BaseShaOk | BaseShaFailure> {
  const networkOpts: GitOptions | undefined = opts ? { ...opts, timeoutMs: NETWORK_COMMAND_TIMEOUT_MS } : { timeoutMs: NETWORK_COMMAND_TIMEOUT_MS }
  const fetch = await git(workDir, ["fetch", remote, target], signal, networkOpts)
  record("git-fetch-base", `fetch ${remote} ${target}`, fetch.exitCode, fetch.combinedOutput, timeoutStepMetadata(fetch))
  if (!fetch.success) {
    return { kind: "failure", failureKind: "retry-safe", message: `git fetch ${remote} ${target} failed: ${fetch.combinedOutput}`, output: fetch.combinedOutput }
  }
  const resolve = await git(workDir, ["rev-parse", `${remote}/${target}`], signal, opts)
  record("git-rev-parse-base", `rev-parse ${remote}/${target}`, resolve.exitCode, resolve.combinedOutput)
  if (!resolve.success) {
    return { kind: "failure", failureKind: "retry-safe", message: `git rev-parse ${remote}/${target} failed: ${resolve.combinedOutput}`, output: resolve.combinedOutput }
  }
  return { kind: "ok", sha: resolve.stdout.trim() }
}

export function buildCreateGitHubPrOutput(output: CreateGitHubPrOutput): ActionResult {
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
