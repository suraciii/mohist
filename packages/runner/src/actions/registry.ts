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
  registry.register("mohist/push", pushAction)
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

export async function mergeReadyAction(context: ActionContext): Promise<ActionResult> {
  const baseBranch = stringInput(context.with, "baseBranch") ?? stringAt(context.variables, ["repository", "baseBranch"]) ?? "main"
  const source = stringInput(context.with, "source") ?? "HEAD"
  const targetBranch = baseBranch

  const base = await git(context.workDir, ["rev-parse", targetBranch], context.signal)
  if (!base.success) return mergeReadyResult(false, targetBranch, null, null, null, `Could not resolve base branch '${targetBranch}'`, base.exitCode, [], new Date().toISOString())

  const head = await git(context.workDir, ["rev-parse", source], context.signal)
  if (!head.success) return mergeReadyResult(false, targetBranch, base.stdout.trim(), null, null, "Could not resolve source", head.exitCode, [], new Date().toISOString())

  const mergeBase = await git(context.workDir, ["merge-base", targetBranch, source], context.signal)
  const mergeBaseSha = mergeBase.success ? mergeBase.stdout.trim() : null
  const checkedAt = new Date().toISOString()

  const preflight = await runSquashMergePreflight(context.workDir, targetBranch, source, context.signal)

  return mergeReadyResult(
    preflight.canMerge,
    targetBranch,
    base.stdout.trim(),
    head.stdout.trim(),
    mergeBaseSha,
    preflight.error,
    preflight.exitCode,
    preflight.conflictFiles,
    checkedAt,
  )
}

export async function mergeAction(context: ActionContext): Promise<ActionResult> {
  const source = stringInput(context.with, "source") ?? "HEAD"
  const target = stringInput(context.with, "target")
  const strategy = stringInput(context.with, "strategy") ?? "squash"
  const message = stringInput(context.with, "message") ?? "Mohist merge"
  const maxRetries = numberInput(context.with, "maxConflictRetries") ?? 3
  const conflictResolver = objectInput(context.with, "conflictResolver") ?? {}
  const workDir = context.workDir

  const sourceCommit = await commitPendingSourceChanges(workDir, message, context.signal)
  if (!sourceCommit.success) {
    const diagnostics = await collectMergeDiagnostics(workDir, target, source, context.signal)
    return mergeFailure(source, target, strategy, sourceCommit.combinedOutput, diagnostics, sourceCommit.exitCode)
  }

  const existingConflicts = await mergeConflictFiles(workDir, context.signal)
  if (existingConflicts.length > 0) {
    return resolveMergeConflict(context, {
      source,
      target,
      strategy,
      message,
      initialOutput: `Existing merge conflicts detected:\n${existingConflicts.join("\n")}`,
      initialExitCode: 1,
      conflictResolver,
      maxRetries,
    })
  }

  if (target?.trim()) {
    const checkout = await git(workDir, ["checkout", target], context.signal)
    if (!checkout.success) {
      const checkoutConflicts = await mergeConflictFiles(workDir, context.signal)
      if (checkoutConflicts.length > 0) {
        return resolveMergeConflict(context, {
          source,
          target,
          strategy,
          message,
          initialOutput: checkout.combinedOutput,
          initialExitCode: checkout.exitCode,
          conflictResolver,
          maxRetries,
        })
      }

      const diagnostics = await collectMergeDiagnostics(workDir, target, source, context.signal)
      return mergeFailure(source, target, strategy, checkout.combinedOutput, diagnostics, checkout.exitCode)
    }
  }

  const result = strategy.toLowerCase() === "squash"
    ? await squashMerge(workDir, source, target ?? "", message, context)
    : await git(workDir, ["merge", source], context.signal)
  if (!result.success && conflictResolver) {
    return resolveMergeConflict(context, {
      source,
      target,
      strategy,
      message,
      initialOutput: result.combinedOutput,
      initialExitCode: result.exitCode,
      conflictResolver,
      maxRetries,
    })
  }

  const head = result.success ? await git(workDir, ["rev-parse", "HEAD"], context.signal) : null
  const output = JSON.stringify({ kind: "merge", source, target, strategy, sourceCommitted: sourceCommit.combinedOutput, commit: head?.success ? head.stdout.trim() : null, output: result.combinedOutput })
  if (result.success) {
    return { status: "success", message: "Merge completed", output, exitCode: result.exitCode }
  }
  const diagnostics = await collectMergeDiagnostics(workDir, target, source, context.signal)
  return mergeFailure(source, target, strategy, result.combinedOutput, diagnostics, result.exitCode)
}

export async function pushAction(context: ActionContext): Promise<ActionResult> {
  const remote = stringInput(context.with, "remote") ?? "origin"
  const target = stringInput(context.with, "target") ?? await resolveCurrentBranch(context.workDir, context.signal)
  if (!target) return { status: "failure", message: "Push action could not resolve current branch" }

  const result = await git(context.workDir, ["push", remote, target], context.signal)
  const output = JSON.stringify({ kind: "push", remote, target, output: result.combinedOutput })
  return result.success
    ? { status: "success", message: "Push completed", output, exitCode: result.exitCode }
    : { status: "failure", message: result.combinedOutput, output, exitCode: result.exitCode }
}

async function resolveCurrentBranch(workDir: string, signal: AbortSignal) {
  const result = await git(workDir, ["rev-parse", "--abbrev-ref", "HEAD"], signal)
  return result.success ? result.stdout.trim() : null
}

async function resolveMergeConflict(
  context: ActionContext,
  input: {
    source: string
    target?: string
    strategy: string
    message: string
    initialOutput: string
    initialExitCode: number
    conflictResolver: JsonObject
    maxRetries: number
  },
): Promise<ActionResult> {
  const workDir = context.workDir
  let conflicts = await mergeConflictFiles(workDir, context.signal)
  if (conflicts.length === 0) {
    const diagnostics = await collectMergeDiagnostics(workDir, input.target, input.source, context.signal)
    return mergeFailure(input.source, input.target, input.strategy, input.initialOutput, diagnostics, input.initialExitCode)
  }

  const allConflicts: string[][] = [conflicts]
  const outputs: string[] = [input.initialOutput]
  let attempts = 0

  while (attempts < input.maxRetries) {
    attempts++
    const agentResult = await runMergeConflictResolver(context, input.conflictResolver, input.source, input.target, conflicts, attempts)
    if (agentResult.output) outputs.push(agentResult.output)
    if (agentResult.status !== "success") {
      const diagnostics = await collectMergeDiagnostics(workDir, input.target, input.source, context.signal)
      return mergeConflictFailure(input, allConflicts.flat(), attempts, outputs, diagnostics, agentResult.exitCode ?? 1)
    }

    conflicts = await mergeConflictFiles(workDir, context.signal)
    if (conflicts.length > 0) {
      allConflicts.push(conflicts)
      continue
    }

    const finish = input.strategy.toLowerCase() === "squash"
      ? await finishSquashMerge(workDir, input.source, input.target ?? "", input.message, context)
      : await finishRegularMerge(workDir, input.message, context.signal)
    outputs.push(finish.combinedOutput)
    const head = finish.success ? await git(workDir, ["rev-parse", "HEAD"], context.signal) : null
    if (head?.combinedOutput) outputs.push(head.combinedOutput)
    const output = JSON.stringify({
      kind: "merge",
      source: input.source,
      target: input.target,
      strategy: input.strategy,
      commit: head?.success ? head.stdout.trim() : null,
      conflicts: allConflicts.flat(),
      resolveAttempts: attempts,
      output: combinedGitOutput(outputs),
    })
    return finish.success
      ? { status: "success", message: "Merge completed", output, exitCode: finish.exitCode }
      : { status: "failure", message: finish.combinedOutput, output, exitCode: finish.exitCode }
  }

  const diagnostics = await collectMergeDiagnostics(workDir, input.target, input.source, context.signal)
  return mergeConflictFailure(input, allConflicts.flat(), attempts, outputs, diagnostics, 1)
}

async function runMergeConflictResolver(
  context: ActionContext,
  conflictResolver: JsonObject,
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
    workDir: context.workDir,
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
  input: { source: string; target?: string; strategy: string },
  conflicts: string[],
  attempts: number,
  outputs: string[],
  diagnostics: MergeDiagnostics,
  exitCode: number,
): ActionResult {
  const output = JSON.stringify({
    kind: "merge",
    source: input.source,
    target: input.target,
    strategy: input.strategy,
    targetBranch: diagnostics.targetBranch,
    baseSha: diagnostics.baseSha,
    candidateHeadSha: diagnostics.candidateHeadSha,
    mergeBaseSha: diagnostics.mergeBaseSha,
    commit: null,
    conflicts,
    resolveAttempts: attempts,
    output: combinedGitOutput(outputs),
  })
  return { status: "failure", message: combinedGitOutput(outputs), output, exitCode }
}

function mergeReadyResult(canMerge: boolean, targetBranch: string, baseSha: string | null, candidateHeadSha: string | null, mergeBaseSha: string | null, error: string | null, exitCode: number | null, conflictFiles: string[], checkedAt: string): ActionResult {
  const output = JSON.stringify({ kind: "merge-ready", targetBranch, strategy: "squash", baseSha: baseSha ?? "", candidateHeadSha: candidateHeadSha ?? "", mergeBaseSha: mergeBaseSha ?? "", canMerge, conflictFiles, checkedAt, error })
  return canMerge ? { status: "success", message: "Merge ready", output, exitCode } : { status: "failure", message: error ?? "Merge is not ready", output, exitCode }
}

async function runSquashMergePreflight(workDir: string, target: string, source: string, signal: AbortSignal): Promise<{ canMerge: boolean; conflictFiles: string[]; error: string | null; exitCode: number | null }> {
  const originalRef = await git(workDir, ["rev-parse", "--abbrev-ref", "HEAD"], signal)
  const originalSha = await git(workDir, ["rev-parse", "HEAD"], signal)

  const checkout = await git(workDir, ["checkout", target], signal)
  if (!checkout.success) {
    return { canMerge: false, conflictFiles: [], error: checkout.combinedOutput, exitCode: checkout.exitCode }
  }

  const merge = await git(workDir, ["merge", "--squash", "--no-commit", source], signal)

  let conflictFiles: string[] = []
  if (!merge.success) {
    const status = await git(workDir, ["diff", "--name-only", "--diff-filter=U"], signal)
    if (status.success && status.stdout.trim()) {
      conflictFiles = [...new Set(status.stdout.split("\n").map((line) => line.trim()).filter(Boolean))]
    }
  }

  await git(workDir, ["reset", "--hard"], signal)
  if (originalRef.success && originalRef.stdout.trim() && originalRef.stdout.trim() !== "HEAD") {
    await git(workDir, ["checkout", originalRef.stdout.trim()], signal)
  } else if (originalSha.success) {
    await git(workDir, ["checkout", originalSha.stdout.trim()], signal)
  }

  return {
    canMerge: merge.success,
    conflictFiles,
    error: merge.success ? null : merge.combinedOutput,
    exitCode: merge.exitCode,
  }
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

function mergeFailure(source: string, target: string | undefined, strategy: string, outputText: string, diagnostics: MergeDiagnostics, exitCode: number): ActionResult {
  return { status: "failure", message: outputText, output: JSON.stringify({ kind: "merge", source, target, strategy, targetBranch: diagnostics.targetBranch, baseSha: diagnostics.baseSha, candidateHeadSha: diagnostics.candidateHeadSha, mergeBaseSha: diagnostics.mergeBaseSha, conflictFiles: diagnostics.conflictFiles, commit: null, output: outputText }), exitCode }
}

type MergeDiagnostics = { targetBranch: string; baseSha: string | null; candidateHeadSha: string | null; mergeBaseSha: string | null; conflictFiles: string[] }

async function collectMergeDiagnostics(workDir: string, target: string | undefined, source: string, signal: AbortSignal): Promise<MergeDiagnostics> {
  const targetBranch = target ?? "HEAD"
  const base = await git(workDir, ["rev-parse", targetBranch], signal)
  const head = await git(workDir, ["rev-parse", source], signal)
  const mergeBase = base.success && head.success ? await git(workDir, ["merge-base", targetBranch, source], signal) : null
  const conflictFiles = await mergeConflictFiles(workDir, signal)
  return {
    targetBranch,
    baseSha: base.success ? base.stdout.trim() : null,
    candidateHeadSha: head.success ? head.stdout.trim() : null,
    mergeBaseSha: mergeBase?.success ? mergeBase.stdout.trim() : null,
    conflictFiles,
  }
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
