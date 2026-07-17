import { join } from "node:path"
import { randomUUID } from "node:crypto"
import type { ActionContext, ActionResult } from "../core/types.js"
import { arrayInput, numberInput, stringInput } from "../core/json.js"
import { stringAt } from "../core/json-path.js"
import { resolveDeliveryBaseBranch } from "./delivery-context.js"
import { deleteFile, exists, readText, runCommand, writeText, type CommandLineOptions } from "../system/process.js"
import { timeoutSignal } from "../system/timeout-signal.js"
import { acpAgentAction } from "./acp-agent.js"
import { opencodeAction } from "./opencode.js"
import { resolveActionPath } from "./expectations.js"
import {
  createGitHubPrAction,
  markGitHubPrReadyAction,
  mergeGitHubPrAction,
} from "./github-pr.js"
import { githubPrStatusAction } from "./github-pr-status.js"
import { archiveChangeAction, openspecArtifactsAction, openspecTasksAction } from "./openspec.js"
import { rebaseAction, rebaseStatusAction } from "./rebase.js"
import { git as defaultGit, type GitOptions } from "./git.js"
import { pushAction } from "./push.js"
import { workspacePrepareAction } from "./workspace-prepare.js"

export type ActionHandler = (context: ActionContext) => Promise<ActionResult>
type GitRunner = (workDir: string, args: string[], signal: AbortSignal, options?: GitOptions) => Promise<{
  success: boolean
  stdout: string
  stderr: string
  exitCode: number
  combinedOutput: string
}>

let git: GitRunner = defaultGit

export function setDeliveryGitRunnerForTest(runner: GitRunner | null) {
  git = runner ?? defaultGit
}

export class ActionRegistry {
  private readonly actions = new Map<string, ActionHandler>()

  register(uses: string, handler: ActionHandler) {
    this.actions.set(uses.toLowerCase(), handler)
  }

  resolve(uses?: string | null) {
    if (!uses) return undefined
    return this.actions.get(uses.toLowerCase())
  }
}

export function createDefaultRegistry() {
  const registry = new ActionRegistry()
  registry.register("core/process", processAction)
  registry.register("core/script", scriptAction)
  registry.register("core/artifact-exists", artifactExistsAction)
  registry.register("core/marker", markerAction)
  registry.register("mohist/acp-agent", acpAgentAction)
  registry.register("mohist/opencode", opencodeAction)
  registry.register("mohist/openspec-tasks", openspecTasksAction)
  registry.register("mohist/openspec-artifacts", openspecArtifactsAction)
  registry.register("mohist/archive-change", archiveChangeAction)
  registry.register("mohist/rebase", rebaseAction)
  registry.register("mohist/rebase-status", rebaseStatusAction)
  registry.register("mohist/merge-ready", mergeReadyAction)
  registry.register("mohist/push", pushAction)
  registry.register("mohist/create-github-pr", createGitHubPrAction)
  registry.register("mohist/mark-github-pr-ready", markGitHubPrReadyAction)
  registry.register("mohist/merge-github-pr", mergeGitHubPrAction)
  registry.register("mohist/github-pr-status", githubPrStatusAction)
  registry.register("mohist/workspace-prepare", workspacePrepareAction)
  return registry
}

async function processAction(context: ActionContext): Promise<ActionResult> {
  const command = context.uses === "core/process" ? stringInput(context.with, "command") : context.uses
  if (!command) return { status: "failure", message: "Process action requires command" }
  const result = await runCommand(command, arrayInput(context.with, "args").map(String), context.workDir, context.signal, undefined, logLineOptions(context, "action:process"))
  return result.exitCode === 0
    ? { status: "success", message: "Process completed", output: result.stdout.trim(), exitCode: result.exitCode }
    : { status: "failure", message: result.stderr.trim() || `Process exited with code ${result.exitCode}`, output: result.stdout.trim(), exitCode: result.exitCode }
}

async function scriptAction(context: ActionContext): Promise<ActionResult> {
  const run = stringInput(context.with, "run")
  if (!run?.trim()) return { status: "failure", message: "Script action requires 'run'" }
  const shell = stringInput(context.with, "shell") || (process.platform === "win32" ? "pwsh" : "bash")
  const file = join(context.workDir, `_${randomUUID().replace(/-/g, "")}${process.platform === "win32" ? ".ps1" : ".sh"}`)
  await writeText(file, run)
  try {
    const timeoutMs = numberInput(context.with, "timeout")
    const signal = timeoutMs ? timeoutSignal(context.signal, timeoutMs) : context.signal
    const result = await runCommand(shell, [file], context.workDir, signal, undefined, logLineOptions(context, "action:script"))
    return {
      status: result.exitCode === 0 ? "success" : "failure",
      message: result.exitCode === 0 ? "Script completed" : `Script failed: ${firstLine(run)}`,
      output: JSON.stringify({
        kind: "script",
        status: result.exitCode === 0 ? "success" : "failure",
        errorCode: result.exitCode === 0 ? null : "script-failed",
        run,
        shell,
        exitCode: result.exitCode,
        stdout: trim(result.stdout),
        stderr: trim(result.stderr),
      }),
      exitCode: result.exitCode,
    }
  } finally {
    await deleteFile(file)
  }
}

async function artifactExistsAction(context: ActionContext): Promise<ActionResult> {
  const path = resolveActionPath(context.workDir, stringInput(context.with, "path"))
  if (!path) return { status: "failure", message: "Artifact check requires 'path'" }
  const found = exists(path)
  const output = JSON.stringify({ kind: "artifact-exists", path, exists: found })
  return found ? { status: "success", message: `Artifact exists: ${path}`, output } : { status: "failure", message: `Artifact missing: ${path}`, output }
}

async function markerAction(context: ActionContext): Promise<ActionResult> {
  const path = resolveActionPath(context.workDir, stringInput(context.with, "path"))
  const expect = stringInput(context.with, "expect") ?? stringInput(context.with, "contains")
  if (!path || !expect) return { status: "failure", message: "Marker check requires 'path' and 'expect'" }
  if (!exists(path)) return { status: "failure", message: `Marker file missing: ${path}` }
  const content = await readText(path)
  const found = matchesMarker(content, expect)
  const output = JSON.stringify({ kind: "marker", path, marker: expect, found })
  return found ? { status: "success", message: `Marker found in ${path}`, output } : { status: "failure", message: `Marker missing in ${path}`, output }
}

function matchesMarker(content: string, expect: string) {
  if (isPromiseVerdict(expect)) {
    const verdicts = [...content.matchAll(/<promise>\s*(PASS|FAIL)\s*<\/promise>/g)].map((match) => `<promise>${match[1]}</promise>`)
    return verdicts.length === 1 && verdicts[0] === expect
  }

  return content.includes(expect)
}

function isPromiseVerdict(value: string) {
  return /^<promise>\s*(PASS|FAIL)\s*<\/promise>$/.test(value)
}

export async function mergeReadyAction(context: ActionContext): Promise<ActionResult> {
  const baseBranch = resolveDeliveryBaseBranch(context, "baseBranch")
  if (!baseBranch) return { status: "failure", message: "Merge readiness requires the authoritative repository base branch" }
  const remote = stringInput(context.with, "remote") ?? "origin"
  const baseRef = `${remote}/${baseBranch}`
  const source = stringInput(context.with, "source") ?? stringAt(context.variables, ["workspace", "branch"]) ?? "HEAD"
  const workDir = stringAt(context.variables, ["workspace", "path"]) ?? context.workDir
  const checkedAt = new Date().toISOString()
  const opts: GitOptions | undefined = context.log ? { sink: { log: context.log, source: "action:merge-ready" } } : undefined

  // Ref-only preflight: the workflow workspace never has its branch
  // switched. The branch-stable contract requires this action to never
  // run `checkout`, `merge --squash`, `fetch`, or any clone — only
  // `rev-parse` and `merge-base` against the workflow workspace refs.
  const base = await git(workDir, ["rev-parse", baseRef], context.signal, opts)
  if (!base.success) return mergeReadyResult(false, baseBranch, null, null, null, `Could not resolve base branch '${baseRef}'`, base.exitCode, [], checkedAt)

  const head = await git(workDir, ["rev-parse", source], context.signal, opts)
  if (!head.success) return mergeReadyResult(false, baseBranch, base.stdout.trim(), null, null, "Could not resolve source", head.exitCode, [], checkedAt)

  const mergeBase = await git(workDir, ["merge-base", baseRef, source], context.signal, opts)
  const ancestorCheck = await git(workDir, ["merge-base", "--is-ancestor", baseRef, source], context.signal, opts)
  const mergeBaseSha = mergeBase.success ? mergeBase.stdout.trim() : null

  if (!ancestorCheck.success) {
    return mergeReadyResult(
      false,
      baseBranch,
      base.stdout.trim(),
      head.stdout.trim(),
      mergeBaseSha,
      `Merge candidate '${source}' does not contain the latest '${baseRef}' tip; rebase is required.`,
      ancestorCheck.exitCode,
      [],
      checkedAt,
    )
  }

  return mergeReadyResult(true, baseBranch, base.stdout.trim(), head.stdout.trim(), mergeBaseSha, null, 0, [], checkedAt)
}

function mergeReadyResult(canMerge: boolean, baseBranch: string, baseSha: string | null, headSha: string | null, mergeBaseSha: string | null, error: string | null, exitCode: number | null, conflictFiles: string[], checkedAt: string): ActionResult {
  const output = JSON.stringify({ kind: "merge-ready", targetBranch: baseBranch, strategy: "squash", baseSha: baseSha ?? "", candidateHeadSha: headSha ?? "", mergeBaseSha: mergeBaseSha ?? "", canMerge, conflictFiles, checkedAt, error })
  return canMerge ? { status: "success", message: "Merge ready", output, exitCode } : { status: "failure", message: error ?? "Merge is not ready", output, exitCode }
}

function firstLine(value: string) {
  return value.replace(/\r\n/g, "\n").trim().split("\n")[0]
}

function trim(value: string) {
  return value.length <= 20_000 ? value : value.slice(0, 20_000)
}

function logLineOptions(context: ActionContext, source: string): CommandLineOptions | undefined {
  return context.log ? { onLine: (line) => context.log!.write(source, line) } : undefined
}
