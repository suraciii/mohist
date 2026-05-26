import { join } from "node:path"
import { randomUUID } from "node:crypto"
import type { ActionContext, ActionResult, JsonObject, JsonValue } from "../core/types.js"
import { arrayInput, isObject, objectInput, stringInput } from "../core/json.js"
import { deleteFile, exists, readText, runCommand, writeText } from "../system/process.js"

export type ActionHandler = (context: ActionContext) => Promise<ActionResult>

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
  registry.register("mohist/coder-agent", coderAgentAction)
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
    const result = await runCommand(shell, [file], context.workDir, context.signal)
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
  return exists(path) ? { status: "success", message: `Artifact exists: ${path}` } : { status: "failure", message: `Artifact missing: ${path}` }
}

async function markerAction(context: ActionContext): Promise<ActionResult> {
  const path = resolveActionPath(context, stringInput(context.with, "path"))
  const expect = stringInput(context.with, "expect") ?? stringInput(context.with, "contains")
  if (!path || !expect) return { status: "failure", message: "Marker check requires 'path' and 'expect'" }
  if (!exists(path)) return { status: "failure", message: `Marker file missing: ${path}` }
  return (await readText(path)).includes(expect) ? { status: "success", message: `Marker found in ${path}` } : { status: "failure", message: `Marker missing in ${path}` }
}

async function coderAgentAction(context: ActionContext): Promise<ActionResult> {
  const prompt = stringInput(context.with, "prompt")
  if (!prompt?.trim()) return { status: "failure", message: "Coder agent requires 'prompt'" }

  // ACP belongs in the TypeScript runner. The current implementation keeps the boundary here;
  // next step is replacing this CLI fallback with an ACP ClientSideConnection session.
  const command = process.env.MOHIST_AGENT_COMMAND ?? "opencode"
  const args = ["agent", "--local", "--message"]
  const model = stringInput(context.with, "model")
  const variant = stringInput(context.with, "variant")
  if (model) args.push("--model", model)
  if (variant) args.push("--variant", variant)
  args.push(prompt)

  if (context.session) {
    await emitSessionStarted(context, context.session.externalSessionId ?? context.session.id)
    await emitSessionEvent(context, "mohist_prompt", { text: prompt, sentAt: new Date().toISOString(), kind: "task", issueId: String(context.session.issueNumber), acpSessionId: context.session.externalSessionId ?? context.session.id })
  }

  const result = await runCommand(command, args, context.workDir, context.signal)
  const verification = await verifyExpectations(context)
  const ok = result.exitCode === 0 && verification.satisfied
  if (context.session) await emitSessionCompleted(context, ok ? "completed" : "failed", ok ? "Agent completed" : verification.message, result.exitCode)
  return {
    status: ok ? "success" : "failure",
    message: ok ? "Coder agent task completed" : verification.message,
    output: JSON.stringify({ kind: "coder-agent", status: ok ? "success" : "failure", exitCode: result.exitCode, model, stdout: result.stdout, stderr: result.stderr, expectation: verification }),
    exitCode: result.exitCode,
  }
}

async function mergeReadyAction(context: ActionContext): Promise<ActionResult> {
  const result = await runCommand("git", ["diff", "--check"], context.workDir, context.signal)
  return result.exitCode === 0
    ? { status: "success", message: "Merge checks passed", output: result.stdout }
    : { status: "failure", message: result.stderr || result.stdout || "Merge checks failed", output: result.stdout, exitCode: result.exitCode }
}

async function mergeAction(context: ActionContext): Promise<ActionResult> {
  const base = stringInput(context.with, "base") ?? "main"
  const squash = stringInput(context.with, "squash") !== "false"
  const message = stringInput(context.with, "message") ?? "Integrate Mohist issue"
  const checkout = await runCommand("git", ["checkout", base], context.workDir, context.signal)
  if (checkout.exitCode !== 0) return { status: "failure", message: checkout.stderr || checkout.stdout, exitCode: checkout.exitCode }
  const merge = await runCommand("git", squash ? ["merge", "--squash", "HEAD@{1}"] : ["merge", "HEAD@{1}"], context.workDir, context.signal)
  if (merge.exitCode !== 0) return { status: "failure", message: merge.stderr || merge.stdout, exitCode: merge.exitCode }
  const commit = await runCommand("git", ["commit", "-m", message], context.workDir, context.signal)
  return commit.exitCode === 0 ? { status: "success", message: "Merged", output: commit.stdout, exitCode: commit.exitCode } : { status: "failure", message: commit.stderr || commit.stdout, exitCode: commit.exitCode }
}

async function verifyExpectations(context: ActionContext) {
  const expect = objectInput(context.with, "expect")
  const files = arrayInput(expect, "files").filter(isObject)
  const markers = arrayInput(expect, "markers").filter(isObject)
  const missingFiles = files.map((file) => resolveActionPath(context, stringValue(file.path))).filter((path): path is string => !!path && !exists(path)).map((path) => ({ path }))
  const missingMarkers = (await Promise.all(markers.map(async (marker) => {
    const path = resolveActionPath(context, stringValue(marker.path))
    const contains = stringValue(marker.contains)
    if (!path || !contains || !exists(path)) return path && contains ? { path, contains } : null
    return (await readText(path)).includes(contains) ? null : { path, contains }
  }))).filter((marker): marker is { path: string; contains: string } => marker !== null)
  return {
    satisfied: missingFiles.length === 0 && missingMarkers.length === 0,
    missingFiles,
    missingMarkers,
    message: missingFiles.length === 0 && missingMarkers.length === 0 ? "Agent completion requirements satisfied" : `Agent completion requirements were not satisfied: ${[...missingFiles.map((file) => `missing file: ${file.path}`), ...missingMarkers.map((marker) => `missing marker in ${marker.path}: ${marker.contains}`)].join("; ")}`,
  }
}

async function emitSessionStarted(context: ActionContext, externalSessionId: string) {
  if (context.telemetry && context.session) await context.telemetry.started(context.session.id, { externalSessionId, workDir: context.workDir, changeDir: null, processPid: null }, context.signal)
}

async function emitSessionEvent(context: ActionContext, type: string, payload: JsonObject) {
  if (context.telemetry && context.session) await context.telemetry.events(context.session.id, [{ type, payload }], context.signal)
}

async function emitSessionCompleted(context: ActionContext, status: string, message: string, exitCode: number) {
  if (context.telemetry && context.session) await context.telemetry.completed(context.session.id, { status, failureReason: message, exitCode }, context.signal)
}

function resolveActionPath(context: ActionContext, value?: string) {
  if (!value) return undefined
  return value.match(/^[A-Za-z]:[\\/]|^\//) ? value : join(context.workDir, value)
}

function stringValue(value: JsonValue | undefined) {
  return typeof value === "string" ? value : undefined
}

function firstLine(value: string) {
  return value.replace(/\r\n/g, "\n").trim().split("\n")[0]
}

function trim(value: string) {
  return value.length <= 20_000 ? value : value.slice(0, 20_000)
}
