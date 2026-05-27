import { dirname, join, resolve } from "node:path"
import { mkdir, readFile, rename } from "node:fs/promises"
import { exists, copyDirectory } from "../system/process.js"
import type { ActionContext, ActionResult, JsonObject } from "../core/types.js"
import { isObject, objectInput, stringInput } from "../core/json.js"
import { resolveActionPath } from "./expectations.js"

export async function openspecTasksAction(context: ActionContext): Promise<ActionResult> {
  const path = resolveActionPath(context, stringInput(context.with, "path"))
  if (!path) return { status: "failure", message: "OpenSpec task loader requires 'path'" }
  if (!exists(path)) return { status: "failure", message: `tasks.json not found: ${path}` }

  const root = JSON.parse(await readFile(path, "utf8")) as JsonObject
  const sourceTasks = Array.isArray(root.tasks) ? root.tasks.filter(isObject) : []
  if (!Array.isArray(root.tasks)) return { status: "failure", message: "tasks.json must contain a tasks array" }

  const taskDefaults = objectInput(context.with, "task")
  const defaultUses = stringInput(taskDefaults, "uses") ?? "mohist/acp-agent"
  const defaultWith = objectInput(taskDefaults, "with")
  const tasks = sourceTasks.flatMap((task) => {
    const id = stringInput(task, "id") ?? stringInput(task, "taskId")
    if (!id?.trim()) return []
    return [{ id, title: stringInput(task, "title") ?? id, uses: stringInput(task, "uses") ?? defaultUses, with: mergeTaskWith(defaultWith, task, context.variables) }]
  })

  return { status: "loaded", message: `Loaded ${tasks.length} tasks`, output: JSON.stringify({ tasks }) }
}

export async function openspecSyncAction(context: ActionContext): Promise<ActionResult> {
  const changeDir = resolveChangeDir(context)
  if (!changeDir) return { status: "failure", message: "OpenSpec sync requires 'changeDir'" }
  const specsDir = join(changeDir, "specs")
  if (!exists(specsDir)) return { status: "failure", message: `OpenSpec specs directory not found: ${specsDir}` }

  const destination = join(context.workDir, "specs")
  await copyDirectory(specsDir, destination)
  return { status: "success", message: "OpenSpec specs synced", output: JSON.stringify({ kind: "openspec-sync", source: specsDir, destination }) }
}

export async function archiveChangeAction(context: ActionContext): Promise<ActionResult> {
  const changeDir = resolveChangeDir(context)
  if (!changeDir) return { status: "failure", message: "Archive change requires 'changeDir'" }
  if (!exists(changeDir)) return { status: "failure", message: `Change directory not found: ${changeDir}` }

  const archiveDir = join(dirname(changeDir), "archive")
  await mkdir(archiveDir, { recursive: true })
  const destination = await uniqueDestination(archiveDir, `${new Date().toISOString().slice(0, 10)}-${changeDir.split(/[\\/]/).pop() ?? "change"}`)
  await rename(changeDir, destination)
  return { status: "success", message: "Change archived", output: JSON.stringify({ kind: "archive-change", source: changeDir, destination }) }
}

function mergeTaskWith(defaultWith: JsonObject | undefined, task: JsonObject, variables?: JsonObject) {
  const merged: JsonObject = { ...(defaultWith ?? {}) }
  const title = stringInput(task, "title") ?? stringInput(task, "id") ?? stringInput(task, "taskId") ?? "OpenSpec task"
  addString(merged, task, "description")
  addValue(merged, task, "acceptanceCriteria")
  addValue(merged, task, "dependsOn")
  addString(merged, task, "priority")
  addString(merged, task, "mode")
  addString(merged, task, "type")
  addValue(merged, task, "output")
  addValue(merged, task, "requireFiles")
  addValue(merged, task, "requireMarkers")
  const taskWith = objectInput(task, "with")
  if (taskWith) Object.assign(merged, taskWith)
  if (!stringInput(merged, "prompt")?.trim()) {
    merged.prompt = buildOpenSpecTaskPrompt(title, task, variables)
  }
  return Object.keys(merged).length === 0 ? null : merged
}

function buildOpenSpecTaskPrompt(title: string, task: JsonObject, variables?: JsonObject) {
  const buildPrompt = resolveBuildPrompt(variables)
  const taskSections = [
    stringSection("Task Title", title),
    stringSection("Description", stringInput(task, "description")),
    valueSection("Acceptance Criteria", task.acceptanceCriteria),
    valueSection("Depends On", task.dependsOn),
    stringSection("Output", stringInput(task, "output")),
    stringSection("Notes", stringInput(task, "notes")),
  ].filter(Boolean)

  if (buildPrompt) {
    // Use the full build.md prompt as the base, appending task-specific details
    return [buildPrompt, ...taskSections].join("\n\n")
  }

  // Fallback to the simple prompt when no build.md is available
  const sections = [
    `Implement this OpenSpec task: ${title}`,
    ...taskSections,
    "Follow the repository conventions. Make the smallest complete change that satisfies the task, and run the relevant verification before reporting completion.",
  ].filter(Boolean)
  return sections.join("\n\n")
}

function resolveBuildPrompt(variables?: JsonObject): string | undefined {
  if (!variables) return undefined
  const prompts = variables["prompts"]
  if (typeof prompts !== "object" || prompts === null || Array.isArray(prompts)) return undefined
  const build = (prompts as JsonObject)["build"]
  return typeof build === "string" ? build : undefined
}

function stringSection(title: string, value: string | undefined) {
  return value?.trim() ? `## ${title}\n${value.trim()}` : ""
}

function valueSection(title: string, value: unknown) {
  if (value === undefined || value === null) return ""
  if (Array.isArray(value) && value.length === 0) return ""
  return `## ${title}\n${formatValue(value)}`
}

function formatValue(value: unknown): string {
  if (Array.isArray(value)) return value.map((item) => `- ${String(item)}`).join("\n")
  if (typeof value === "object") return JSON.stringify(value, null, 2)
  return String(value)
}

function addString(target: JsonObject, source: JsonObject, key: string) {
  const value = stringInput(source, key)
  if (value?.trim()) target[key] = value
}

function addValue(target: JsonObject, source: JsonObject, key: string) {
  const value = source[key]
  if (value !== undefined && value !== null) target[key] = value
}

function resolveChangeDir(context: ActionContext) {
  const changeDir = stringInput(context.with, "changeDir")
  if (!changeDir?.trim()) return undefined
  return resolveActionPath(context, changeDir)
}

async function uniqueDestination(archiveDir: string, baseName: string) {
  let destination = resolve(join(archiveDir, baseName))
  if (!exists(destination)) return destination
  for (let version = 2; ; version++) {
    destination = resolve(join(archiveDir, `${baseName}-v${version}`))
    if (!exists(destination)) return destination
  }
}
