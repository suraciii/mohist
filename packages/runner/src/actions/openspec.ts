import { dirname, join, resolve } from "node:path"
import { mkdir, readFile, rename } from "node:fs/promises"
import { exists, copyDirectory } from "../system/process.js"
import type { ActionContext, ActionResult, JsonObject, JsonValue } from "../core/types.js"
import { isObject, objectInput, stringInput } from "../core/json.js"
import { resolveActionPath } from "./expectations.js"
import { OPENSPEC_TASK_PROMPT_LOADER_NAME } from "./openspec-task-prompt.js"

const DEFAULT_OPENSPEC_ITEMS_PATH = "tasks"

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
  const itemsPath = stringInput(context.with, "items") ?? DEFAULT_OPENSPEC_ITEMS_PATH
  const tasks = sourceTasks.flatMap((task) => {
    const id = stringInput(task, "id") ?? stringInput(task, "taskId")
    if (!id?.trim()) return []
    const title = stringInput(task, "title") ?? id
    const uses = stringInput(task, "uses") ?? defaultUses
    const mergedWith = mergeTaskWith(defaultWith, task, id, { file: path, items: itemsPath }, context.variables)
    return [{ id, title, uses, with: mergedWith ? JSON.stringify(mergedWith) : null }]
  })

  if (!context.serverConnection) return { status: "failure", message: "Server connection not available" }
  await context.serverConnection.addTasks(context.workflowRunId, tasks)

  return { status: "success", message: `Loaded ${tasks.length} tasks`, output: JSON.stringify({ loaded: tasks.length }) }
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

function mergeTaskWith(
  defaultWith: JsonObject | undefined,
  task: JsonObject,
  taskId: string | undefined,
  loaderConfig: { file: string; items: string },
  variables?: JsonObject,
) {
  const merged: JsonObject = { ...(defaultWith ?? {}) }
  const taskWith = objectInput(task, "with")
  if (taskWith) Object.assign(merged, taskWith)
  if (merged.prompt === undefined) {
    merged.prompt = buildOpenSpecTaskPromptSpec(taskId, loaderConfig, variables)
  } else {
    merged.prompt = injectOpenSpecTaskPromptSelector(merged.prompt, taskId)
  }
  return Object.keys(merged).length === 0 ? null : merged
}

function injectOpenSpecTaskPromptSelector(prompt: JsonValue, taskId: string | undefined): JsonValue {
  if (!isObject(prompt)) return prompt
  if (prompt["uses"] !== OPENSPEC_TASK_PROMPT_LOADER_NAME) return prompt
  const existingWith = objectInput(prompt, "with")
  const nextWith: JsonObject = { ...(existingWith ?? {}) }
  if (taskId?.trim()) nextWith["taskId"] = taskId
  return { ...prompt, with: nextWith }
}

function buildOpenSpecTaskPromptSpec(
  taskId: string | undefined,
  loaderConfig: { file: string; items: string },
  variables?: JsonObject,
): JsonObject {
  const loaderWith: JsonObject = {
    file: loaderConfig.file,
    items: loaderConfig.items,
  }
  const base = resolveBuildPrompt(variables)
  if (base !== undefined) loaderWith["base"] = base
  if (taskId?.trim()) loaderWith["taskId"] = taskId
  return {
    uses: OPENSPEC_TASK_PROMPT_LOADER_NAME,
    with: loaderWith,
  }
}

function resolveBuildPrompt(variables?: JsonObject): string | undefined {
  if (!variables) return undefined
  const prompts = variables["prompts"]
  if (typeof prompts !== "object" || prompts === null || Array.isArray(prompts)) return undefined
  const build = (prompts as JsonObject)["build"]
  return typeof build === "string" ? build : undefined
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
