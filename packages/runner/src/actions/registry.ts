import { join } from "node:path"
import { randomUUID } from "node:crypto"
import type { ActionContext, ActionResult, JsonObject } from "../core/types.js"
import { arrayInput, numberInput, objectInput, stringInput } from "../core/json.js"
import { deleteFile, exists, readText, runCommand, writeText } from "../system/process.js"
import { acpAgentAction } from "./acp-agent.js"
import { resolveActionPath } from "./expectations.js"
import { archiveChangeAction, openspecSyncAction, openspecTasksAction } from "./openspec.js"
import { applyWorkflowAgentDefault, rebaseAction, rebaseStatusAction } from "./rebase.js"
import { git as defaultGit } from "./git.js"

export type ActionHandler = (context: ActionContext) => Promise<ActionResult>
type GitRunner = typeof defaultGit
type ConflictResolverRunner = typeof acpAgentAction

let git: GitRunner = defaultGit
let mergeConflictResolverRunner: ConflictResolverRunner = acpAgentAction

export function setMergeGitRunnerForTest(runner: GitRunner | null) {
  git = runner ?? defaultGit
}

export function setMergeConflictResolverForTest(runner: ConflictResolverRunner | null) {
  mergeConflictResolverRunner = runner ?? acpAgentAction
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
  registry.register("mohist/openspec-tasks", openspecTasksAction)
  registry.register("mohist/openspec-sync", openspecSyncAction)
  registry.register("mohist/archive-change", archiveChangeAction)
  registry.register("mohist/rebase", rebaseAction)
  registry.register("mohist/rebase-status", rebaseStatusAction)
  registry.register("mohist/merge-ready", mergeReadyAction)
  registry.register("mohist/merge", mergeAction)
  return registry
}

async function processAction(context: ActionContext): Promise<ActionResult> {
  const command = context.uses === "core/process" ? stringInput(context.with, "command") : context.uses
  if (!command) return { status: "failure", message: "Process action requires command" }
  const result = await runCommand(command, arrayInput(context.with, "args").map(String), context.workDir, context.signal)
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
    const result = await runCommand(shell, [file], context.workDir, signal)
    return {
      status: result.exitCode === 0 ? "success" : "failure",
      message: result.exitCode === 0 ? "Script completed" : `Script failed: ${firstLine(run)}`,
      output: JSON.stringify({ kind: "script", run, shell, exitCode: result.exitCode, stdout: trim(result.stdout), stderr: trim(result.stderr) }),
      exitCode: result.exitCode,
    }
  } finally {
    await deleteFile(file)
  }
}

async function artifactExistsAction(context: ActionContext): Promise<ActionResult> {
  const path = resolveActionPath(context, stringInput(context.with, "path"))
  if (!path) return { status: "failure", message: "Artifact check requires 'path'" }
  const found = exists(path)
  const output = JSON.stringify({ kind: "artifact-exists", path, exists: found })
  return found ? { status: "success", message: `Artifact exists: ${path}`, output } : { status: "failure", message: `Artifact missing: ${path}`, output }
}

async function markerAction(context: ActionContext): Promise<ActionResult> {
  const path = resolveActionPath(context, stringInput(context.with, "path"))
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

async function mergeReadyAction(context: ActionContext): Promise<ActionResult> {
  const baseBranch = stringInput(context.with, "baseBranch") ?? stringAt(context.variables, ["project", "defaultBranch"]) ?? stringAt(context.variables, ["project", "baseBranch"]) ?? "main"
  const base = await git(context.workDir, ["rev-parse", baseBranch], context.signal)
  if (!base.success) return mergeReadyResult(false, baseBranch, null, null, null, `Could not resolve base branch '${baseBranch}'`, base.exitCode)
  const head = await git(context.workDir, ["rev-parse", "HEAD"], context.signal)
  if (!head.success) return mergeReadyResult(false, baseBranch, base.stdout.trim(), null, null, "Could not resolve HEAD", head.exitCode)
  const mergeBase = await git(context.workDir, ["merge-base", baseBranch, "HEAD"], context.signal)
  const mergeTree = await git(context.workDir, ["merge-tree", "--write-tree", baseBranch, "HEAD"], context.signal)
  return mergeTree.success
    ? mergeReadyResult(true, baseBranch, base.stdout.trim(), head.stdout.trim(), mergeBase.success ? mergeBase.stdout.trim() : null, null, mergeTree.exitCode)
    : mergeReadyResult(false, baseBranch, base.stdout.trim(), head.stdout.trim(), mergeBase.success ? mergeBase.stdout.trim() : null, mergeTree.combinedOutput, mergeTree.exitCode)
}

export async function mergeAction(context: ActionContext): Promise<ActionResult> {
  const source = stringInput(context.with, "source") ?? "HEAD"
  const target = stringInput(context.with, "target")
  const strategy = stringInput(context.with, "strategy") ?? "squash"
  const message = stringInput(context.with, "message") ?? "Mohist merge"
  const maxRetries = numberInput(context.with, "maxConflictRetries") ?? 3
  const conflictResolver = objectInput(context.with, "conflictResolver") ?? {}
  const mergeWorkDir = stringAt(context.variables, ["project", "path"]) ?? context.workDir

  const sourceCommit = await commitPendingSourceChanges(context.workDir, message, context.signal)
  if (!sourceCommit.success) return mergeFailure(source, target, strategy, sourceCommit.combinedOutput, sourceCommit.exitCode)

  const existingConflicts = await mergeConflictFiles(mergeWorkDir, context.signal)
  if (existingConflicts.length > 0) {
    return resolveMergeConflict(context, {
      source,
      target,
      strategy,
      message,
      mergeWorkDir,
      initialOutput: `Existing merge conflicts detected:\n${existingConflicts.join("\n")}`,
      initialExitCode: 1,
      conflictResolver,
      maxRetries,
    })
  }

  if (target?.trim()) {
    const checkout = await git(mergeWorkDir, ["checkout", target], context.signal)
    if (!checkout.success) {
      const checkoutConflicts = await mergeConflictFiles(mergeWorkDir, context.signal)
      if (checkoutConflicts.length > 0) {
        return resolveMergeConflict(context, {
          source,
          target,
          strategy,
          message,
          mergeWorkDir,
          initialOutput: checkout.combinedOutput,
          initialExitCode: checkout.exitCode,
          conflictResolver,
          maxRetries,
        })
      }

      return mergeFailure(source, target, strategy, checkout.combinedOutput, checkout.exitCode)
    }
  }

  const result = strategy.toLowerCase() === "squash"
    ? await squashMerge(mergeWorkDir, source, target ?? "", message, context)
    : await git(mergeWorkDir, ["merge", source], context.signal)
  if (!result.success && conflictResolver) {
    return resolveMergeConflict(context, {
      source,
      target,
      strategy,
      message,
      mergeWorkDir,
      initialOutput: result.combinedOutput,
      initialExitCode: result.exitCode,
      conflictResolver,
      maxRetries,
    })
  }

  const head = result.success ? await git(mergeWorkDir, ["rev-parse", "HEAD"], context.signal) : null
  const output = JSON.stringify({ kind: "merge", source, target, strategy, workDir: mergeWorkDir, sourceCommitted: sourceCommit.combinedOutput, commit: head?.success ? head.stdout.trim() : null, output: result.combinedOutput })
  return result.success ? { status: "success", message: "Merge completed", output, exitCode: result.exitCode } : { status: "failure", message: result.combinedOutput, output, exitCode: result.exitCode }
}

async function resolveMergeConflict(
  context: ActionContext,
  input: {
    source: string
    target?: string
    strategy: string
    message: string
    mergeWorkDir: string
    initialOutput: string
    initialExitCode: number
    conflictResolver: JsonObject
    maxRetries: number
  },
): Promise<ActionResult> {
  let conflicts = await mergeConflictFiles(input.mergeWorkDir, context.signal)
  if (conflicts.length === 0) {
    return mergeFailure(input.source, input.target, input.strategy, input.initialOutput, input.initialExitCode)
  }

  const allConflicts: string[][] = [conflicts]
  const outputs: string[] = [input.initialOutput]
  let attempts = 0

  while (attempts < input.maxRetries) {
    attempts++
    const agentResult = await runMergeConflictResolver(context, input.conflictResolver, input.mergeWorkDir, input.source, input.target, conflicts, attempts)
    if (agentResult.output) outputs.push(agentResult.output)
    if (agentResult.status !== "success") {
      return mergeConflictFailure(input, conflicts, attempts, outputs, agentResult.exitCode ?? 1)
    }

    conflicts = await mergeConflictFiles(input.mergeWorkDir, context.signal)
    if (conflicts.length > 0) {
      allConflicts.push(conflicts)
      continue
    }

    const finish = input.strategy.toLowerCase() === "squash"
      ? await finishSquashMerge(input.mergeWorkDir, input.source, input.target ?? "", input.message, context)
      : await finishRegularMerge(input.mergeWorkDir, input.message, context.signal)
    outputs.push(finish.combinedOutput)
    const head = finish.success ? await git(input.mergeWorkDir, ["rev-parse", "HEAD"], context.signal) : null
    if (head?.combinedOutput) outputs.push(head.combinedOutput)
    const output = JSON.stringify({
      kind: "merge",
      source: input.source,
      target: input.target,
      strategy: input.strategy,
      workDir: input.mergeWorkDir,
      commit: head?.success ? head.stdout.trim() : null,
      conflicts: allConflicts.flat(),
      resolveAttempts: attempts,
      output: combinedGitOutput(outputs),
    })
    return finish.success
      ? { status: "success", message: "Merge completed", output, exitCode: finish.exitCode }
      : { status: "failure", message: finish.combinedOutput, output, exitCode: finish.exitCode }
  }

  return mergeConflictFailure(input, allConflicts.flat(), attempts, outputs, 1)
}

async function runMergeConflictResolver(
  context: ActionContext,
  conflictResolver: JsonObject,
  mergeWorkDir: string,
  source: string,
  target: string | undefined,
  conflicts: string[],
  attempt: number,
): Promise<ActionResult> {
  const resolverWith: JsonObject = {
    prompt: buildMergeConflictPrompt(conflicts, source, target, attempt),
    ...objectInput(conflictResolver, "with"),
  }
  applyWorkflowAgentDefault(resolverWith, context.variables)

  return mergeConflictResolverRunner({
    ...context,
    workDir: mergeWorkDir,
    workId: `${context.workId}-conflict-resolve-${attempt}`,
    workType: "task",
    title: stringInput(conflictResolver, "title") ?? "Resolve merge conflicts",
    with: resolverWith,
  })
}

function buildMergeConflictPrompt(conflicts: string[], source: string, target: string | undefined, attempt: number) {
  const fileList = conflicts.map((f) => `- ${f}`).join("\n")
  return [
    `## Complete Git Merge Conflict Resolution (attempt ${attempt})`,
    "",
    `A merge from \`${source}\`${target ? ` into \`${target}\`` : ""} produced conflicts.`,
    "",
    "Current conflict files:",
    fileList,
    "",
    "Resolution rules:",
    "1. Preserve both sides. Never drop or overwrite either side's intentional changes.",
    "2. Resolve every conflict marker. Search for `<<<<<<<`, `=======`, and `>>>>>>>`; no markers may remain.",
    "3. Stage resolved files with `git add`.",
    "4. Do not create the merge commit. The runner will commit after verifying the conflict is resolved.",
    "5. If verification fails because of your resolution, fix it before finishing.",
  ].join("\n")
}

async function mergeConflictFiles(workDir: string, signal: AbortSignal) {
  const status = await git(workDir, ["diff", "--name-only", "--diff-filter=U"], signal)
  if (!status.success || !status.stdout.trim()) return []
  return [...new Set(status.stdout.split("\n").map((line) => line.trim()).filter(Boolean))]
}

function mergeConflictFailure(
  input: { source: string; target?: string; strategy: string; mergeWorkDir: string },
  conflicts: string[],
  attempts: number,
  outputs: string[],
  exitCode: number,
): ActionResult {
  const output = JSON.stringify({
    kind: "merge",
    source: input.source,
    target: input.target,
    strategy: input.strategy,
    workDir: input.mergeWorkDir,
    commit: null,
    conflicts,
    resolveAttempts: attempts,
    output: combinedGitOutput(outputs),
  })
  return { status: "failure", message: combinedGitOutput(outputs), output, exitCode }
}

function mergeReadyResult(canMerge: boolean, baseBranch: string, baseSha: string | null, headSha: string | null, mergeBaseSha: string | null, error: string | null, exitCode: number | null): ActionResult {
  const output = JSON.stringify({ kind: "merge-ready", targetBranch: baseBranch, baseSha: baseSha ?? "", candidateHeadSha: headSha ?? "", mergeBaseSha: mergeBaseSha ?? "", canMerge, conflictFiles: [], checkedAt: new Date().toISOString(), error })
  return canMerge ? { status: "success", message: "Merge ready", output, exitCode } : { status: "failure", message: error ?? "Merge is not ready", output, exitCode }
}

async function commitPendingSourceChanges(workDir: string, message: string, signal: AbortSignal) {
  const status = await git(workDir, ["status", "--porcelain"], signal)
  if (!status.success || !status.stdout.trim()) return status.success ? { ...status, combinedOutput: "" } : status
  const add = await git(workDir, ["add", "."], signal)
  if (!add.success) return add
  return await git(workDir, ["commit", "-m", `${message} integration`], signal)
}

async function squashMerge(workDir: string, source: string, target: string, message: string, context: ActionContext) {
  const merge = await git(workDir, ["merge", "--squash", source], context.signal)
  if (!merge.success) return merge
  return finishSquashMerge(workDir, source, target, message, context)
}

async function finishSquashMerge(workDir: string, source: string, target: string, message: string, context: ActionContext) {
  const title = stringAt(context.variables, ["issue", "title"]) ?? message
  const numberStr = typeof context.issueNumber === "number" && context.issueNumber > 0
    ? String(context.issueNumber)
    : numberAtString(context.variables, ["issue", "number"])
  const header = numberStr ? `${title} (#${numberStr})` : title

  const logResult = await git(workDir, ["log", "--format=* %s", `${target}..${source}`], context.signal)
  const body = logResult.success ? logResult.stdout.trim() : ""

  return body
    ? await git(workDir, ["commit", "-m", header, "-m", trim(body)], context.signal)
    : await git(workDir, ["commit", "-m", header], context.signal)
}

async function finishRegularMerge(workDir: string, message: string, signal: AbortSignal) {
  return await git(workDir, ["commit", "-m", message], signal)
}

function mergeFailure(source: string, target: string | undefined, strategy: string, outputText: string, exitCode: number): ActionResult {
  return { status: "failure", message: outputText, output: JSON.stringify({ kind: "merge", source, target, strategy, commit: null, output: outputText }), exitCode }
}

function firstLine(value: string) {
  return value.replace(/\r\n/g, "\n").trim().split("\n")[0]
}

function trim(value: string) {
  return value.length <= 20_000 ? value : value.slice(0, 20_000)
}

function combinedGitOutput(outputs: string[]) {
  return outputs.map((output) => output.trim()).filter(Boolean).join("\n\n")
}

function timeoutSignal(parent: AbortSignal, timeoutMs: number) {
  const controller = new AbortController()
  const abort = () => controller.abort(parent.reason)
  if (parent.aborted) {
    abort()
  } else {
    const onAbort = () => {
      clearTimeout(timer)
      abort()
    }
    const timer = setTimeout(() => {
      controller.abort(new Error(`Timed out after ${timeoutMs / 1000}s`))
      parent.removeEventListener("abort", onAbort)
    }, timeoutMs)
    parent.addEventListener("abort", onAbort, { once: true })
  }
  return controller.signal
}

function stringAt(value: unknown, path: string[]) {
  const found = path.reduce<unknown>((current, part) => {
    if (typeof current !== "object" || current === null || Array.isArray(current)) return undefined
    return (current as Record<string, unknown>)[part]
  }, value)
  return typeof found === "string" ? found : undefined
}

function numberAtString(value: unknown, path: string[]) {
  const found = path.reduce<unknown>((current, part) => {
    if (typeof current !== "object" || current === null || Array.isArray(current)) return undefined
    return (current as Record<string, unknown>)[part]
  }, value)
  return typeof found === "number" ? String(found) : undefined
}
