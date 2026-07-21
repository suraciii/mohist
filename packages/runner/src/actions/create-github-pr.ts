import type { ActionResult, JsonObject } from "../core/types.js"
import type { ActionHost } from "./host.js"
import { runCommand, type CommandLineOptions } from "../system/process.js"
import { NETWORK_COMMAND_TIMEOUT_MS } from "./git.js"
import { resolveCreatePrText } from "./github-pr-issue-fields.js"
import { combinedGhOutput, extractPrNumberFromUrl, parsePrListWithDraft } from "./github-pr-parse.js"
import { classifyGhFailure } from "./github-pr-classify.js"
import { getGitHubPrGh, runGhPrecheck } from "./github-pr-runtime.js"
import { timeoutStepMetadata, type CreateGitHubPrOutput, type GitHubPrErrorCode, type GitHubPrStep, type GitHubPrStepMetadata } from "./github-pr-types.js"
import { parseGitHubRepository } from "./github-pr-repository.js"
import { fail as actionFail, succeed } from "./action-result.js"
type GhRunner = typeof runCommand

const ACTION_SOURCE = "action:create-github-pr"

function networkGhOptions(host: ActionHost): CommandLineOptions | undefined {
  if (!host.log) return { timeoutMs: NETWORK_COMMAND_TIMEOUT_MS }
  return { onLine: (line) => host.log!.write(ACTION_SOURCE, line), timeoutMs: NETWORK_COMMAND_TIMEOUT_MS }
}

export async function createGitHubPrAction(inputs: JsonObject, host: ActionHost): Promise<ActionResult> {
  const repositoryUrl = typeof inputs["repositoryUrl"] === "string" ? inputs["repositoryUrl"] : undefined
  const source = typeof inputs["source"] === "string" ? inputs["source"] : undefined
  const target = typeof inputs["target"] === "string" ? inputs["target"] : undefined
  if (!repositoryUrl || !source || !target) return actionFail("invalid-input", "create-github-pr requires 'repositoryUrl', 'source', and 'target'")
  const githubRepository = parseGitHubRepository(repositoryUrl)
  if (!githubRepository) return actionFail("config-error", "create-github-pr requires a valid GitHub repository URL")
  const draft = inputs["draft"] !== false
  const workDir = host.workDir

  const gh = getGitHubPrGh()

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
    draft,
    errorCode,
    message,
    output: payload.output ?? message,
    steps,
  })
  const ghOpts = networkGhOptions(host)
  const ghPrecheck = await runGhPrecheck(gh, workDir, host.signal, ghOpts)
  record("gh-precheck", "gh --version && gh auth status", ghPrecheck.ok ? 0 : ghPrecheck.exitCode, ghPrecheck.output, ghPrecheck.ok ? undefined : timeoutStepMetadata(ghPrecheck))
  if (!ghPrecheck.ok) {
    return fail("config-error", ghPrecheck.message, { output: ghPrecheck.output })
  }

  const text = await resolveCreatePrText(inputs, host)
  if (text.kind === "failure") {
    return fail("config-error", text.message, { output: text.message })
  }

  const opened = await openOrReusePr(gh, workDir, source, target, text.title, text.body, draft, host.signal, record, ghOpts, githubRepository)
  if (opened.kind === "failure") {
    return fail(opened.errorCode, opened.message, {
      output: opened.output,
      branch: source,
    })
  }

  return buildCreateGitHubPrOutput({
    kind: "create-github-pr",
    status: "completed",
    source,
    targetBranch: target,
    branch: source,
    prNumber: opened.prNumber,
    prUrl: opened.prUrl,
    operation: opened.operation,
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
  githubRepository?: string,
): Promise<
  | { kind: "ok"; prNumber: number; prUrl: string; operation: "created" | "updated" | "reused"; output: string }
  | { kind: "failure"; errorCode: GitHubPrErrorCode; message: string; output: string }
> {
  const listResult = await gh("gh", withGitHubRepository(["pr", "list", "--head", head, "--base", base, "--state", "open", "--json", "number,url,isDraft"], githubRepository), workDir, signal, undefined, options)
  const listOutput = combinedGhOutput(listResult)
  record("gh-pr-list", `pr list --head ${head} --base ${base} --state open --json number,url,isDraft`, listResult.exitCode, listOutput, timeoutStepMetadata(listResult))
  if (listResult.exitCode !== 0) {
    return {
      kind: "failure",
      errorCode: classifyGhFailure(listResult.stdout, listResult.stderr, listResult.status),
      message: `gh pr list failed: ${listOutput}`,
      output: listOutput,
    }
  }

  const existing = parsePrListWithDraft(listResult.stdout)
  if (existing.length > 0) {
    const pr = existing[0]!
    const editArgs = ["pr", "edit", String(pr.number), "--title", title, "--body", body]
    const editResult = await gh("gh", withGitHubRepository(editArgs, githubRepository), workDir, signal, undefined, options)
    const editOutput = combinedGhOutput(editResult)
    record("gh-pr-edit", `pr edit ${pr.number} --title "${title}" --body "${body}"`, editResult.exitCode, editOutput, timeoutStepMetadata(editResult))
    if (editResult.exitCode !== 0) {
      return {
        kind: "failure",
        errorCode: classifyGhFailure(editResult.stdout, editResult.stderr, editResult.status),
        message: `gh pr edit ${pr.number} failed: ${editOutput}`,
        output: editOutput,
      }
    }
    return { kind: "ok", prNumber: pr.number, prUrl: pr.url, operation: "reused", output: editOutput || `Reused PR #${pr.number}` }
  }

  const createArgs = ["pr", "create", "--head", head, "--base", base, "--title", title, "--body", body]
  if (draft) createArgs.push("--draft")
  const createResult = await gh("gh", withGitHubRepository(createArgs, githubRepository), workDir, signal, undefined, options)
  const createOutput = combinedGhOutput(createResult)
  record("gh-pr-create", `pr create --head ${head} --base ${base} --title "${title}"${draft ? " --draft" : ""}`, createResult.exitCode, createOutput, timeoutStepMetadata(createResult))
  if (createResult.exitCode !== 0) {
    return {
      kind: "failure",
      errorCode: classifyGhFailure(createResult.stdout, createResult.stderr, createResult.status),
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

export function buildCreateGitHubPrOutput(output: CreateGitHubPrOutput): ActionResult {
  if (output.status === "completed") {
    const { errorCode: _errorCode, message: _message, steps, ...rest } = output
    const success: JsonObject = { ...rest, steps: steps as unknown as JsonObject }
    return succeed(success)
  }
  return actionFail(output.errorCode ?? "create-pr-failed", output.message ?? output.output, { exitCode: 1 })
}

function withGitHubRepository(args: string[], githubRepository?: string): string[] {
  return githubRepository ? [...args, "--repo", githubRepository] : args
}
