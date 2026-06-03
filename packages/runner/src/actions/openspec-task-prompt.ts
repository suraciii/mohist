import { isAbsolute, join } from "node:path"
import { exists, readText } from "../system/process.js"
import type { JsonObject, JsonValue } from "../core/types.js"
import { isObject, numberInput, stringInput } from "../core/json.js"
import type { PromptLoader, PromptLoaderContext } from "../core/prompt.js"

export const OPENSPEC_TASK_PROMPT_LOADER_NAME = "mohist/openspec-task-prompt"

const DEFAULT_ITEMS_PATH = "tasks"
const DEFAULT_ROOT_TAG = "artifact"
const TASK_ID_FIELDS = ["id", "taskId"] as const
const RENDERED_TASK_FIELDS = [
  "title",
  "description",
  "acceptanceCriteria",
  "dependsOn",
  "output",
  "notes",
] as const

export const openspecTaskPromptLoader: PromptLoader = async (ctx) => {
  const file = resolveLoaderFile(ctx)
  if (!file) throw new Error("mohist/openspec-task-prompt loader requires 'file'")
  if (!exists(file)) throw new Error(`mohist/openspec-task-prompt loader could not find task file: ${file}`)

  const raw = await readText(file)
  const root = parseRoot(raw, file)

  const itemsPath = stringInput(ctx.with, "items") ?? DEFAULT_ITEMS_PATH
  const tasks = locateTaskList(root, itemsPath, file)

  const taskId = stringInput(ctx.with, "taskId")
  const index = numberInput(ctx.with, "index")
  const { task, taskIdValue, indexValue } = selectTask(tasks, taskId, index, file)

  const base = ctx.with["base"]
  const baseString = typeof base === "string" && base.trim().length > 0 ? base : undefined
  const rootTag = stringInput(ctx.with, "root") ?? DEFAULT_ROOT_TAG

  return buildStructuredPrompt(rootTag, taskIdValue, indexValue, baseString, task)
}

function resolveLoaderFile(ctx: PromptLoaderContext): string | undefined {
  const file = stringInput(ctx.with, "file")
  if (!file?.trim()) return undefined
  if (isAbsolute(file) || /^[A-Za-z]:[\\/]/.test(file)) return file
  return join(ctx.workDir, file)
}

function parseRoot(raw: string, file: string): JsonObject {
  try {
    const parsed = JSON.parse(raw) as JsonValue
    if (!isObject(parsed)) throw new Error("root is not a JSON object")
    return parsed
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error)
    throw new Error(`mohist/openspec-task-prompt loader could not parse task file '${file}': ${message}`)
  }
}

function locateTaskList(root: JsonObject, itemsPath: string, file: string): JsonObject[] {
  const located = readPath(root, itemsPath)
  if (located === undefined) {
    throw new Error(`mohist/openspec-task-prompt loader could not find task array at path '${itemsPath}' in '${file}'`)
  }
  if (!Array.isArray(located)) {
    throw new Error(`mohist/openspec-task-prompt loader path '${itemsPath}' did not resolve to an array in '${file}'`)
  }
  return located.filter(isObject)
}

function selectTask(
  tasks: JsonObject[],
  taskId: string | undefined,
  index: number | undefined,
  file: string,
): { task: JsonObject; taskIdValue: string | undefined; indexValue: number | undefined } {
  if (taskId?.trim()) {
    for (const task of tasks) {
      if (TASK_ID_FIELDS.some((field) => stringInput(task, field) === taskId)) {
        return { task, taskIdValue: taskId, indexValue: undefined }
      }
    }
    throw new Error(`mohist/openspec-task-prompt loader could not find task with id '${taskId}' in '${file}'`)
  }
  if (typeof index === "number" && Number.isInteger(index) && index >= 0) {
    if (index >= tasks.length) {
      throw new Error(`mohist/openspec-task-prompt loader index ${index} is out of range for '${file}' (${tasks.length} tasks)`)
    }
    const task = tasks[index]
    const resolvedId = TASK_ID_FIELDS.map((field) => stringInput(task, field)).find((value) => value?.trim())
    return { task, taskIdValue: resolvedId, indexValue: index }
  }
  throw new Error("mohist/openspec-task-prompt loader requires either 'taskId' or 'index' to select a task")
}

function buildStructuredPrompt(
  rootTag: string,
  taskIdValue: string | undefined,
  indexValue: number | undefined,
  base: string | undefined,
  task: JsonObject,
): JsonObject {
  const attrs: JsonObject = {}
  if (taskIdValue) attrs["id"] = taskIdValue
  else if (typeof indexValue === "number") attrs["index"] = indexValue

  const selectedTask: JsonObject = { attrs }
  for (const field of RENDERED_TASK_FIELDS) {
    const value = task[field]
    if (value === undefined) continue
    selectedTask[field] = value
  }

  const rootValue: JsonObject = {}
  if (Object.keys(attrs).length > 0) rootValue["attrs"] = { ...attrs }
  if (base !== undefined) rootValue["base_instructions"] = base
  rootValue["selected_task"] = selectedTask

  return { [rootTag]: rootValue }
}

function readPath(root: JsonObject, path: string): JsonValue | undefined {
  if (!path.trim()) return undefined
  const segments = path.split(".").map((segment) => segment.trim()).filter(Boolean)
  let current: JsonValue | undefined = root
  for (const segment of segments) {
    if (!isObject(current)) return undefined
    current = current[segment]
    if (current === undefined) return undefined
  }
  return current
}
