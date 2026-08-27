import { isAbsolute, join, relative, resolve } from 'node:path'
import { exists, readText } from '../system/process.js'
import { currentRunnerFileSystem } from '../system/filesystem.js'
import type { ActionResult, AddTaskInput, JsonObject } from '../core/types.js'
import type { ActionHost } from './host.js'
import { isObject, objectInput, stringInput } from '../core/json.js'
import { fail, succeed } from './action-result.js'

const FIELDS = new Set(['id', 'title', 'goal', 'acceptance', 'refs'])

export async function taskListAction(inputs: JsonObject, host: ActionHost): Promise<ActionResult> {
  const rawPath = stringInput(inputs, 'path')
  const template = objectInput(inputs, 'task')
  const uses = template && stringInput(template, 'uses')
  if (!rawPath || !uses) return fail('invalid-input', "mohist/task-list requires 'path' and 'task.uses'")
  const path = await workspaceContainedPath(host.workDir, rawPath)
  if (!path) return fail('invalid-input', "mohist/task-list 'path' must be a Workspace-relative path without traversal")
  if (!exists(path)) return fail('missing-source', `Task list not found: ${path}`)
  let root: unknown
  try {
    root = JSON.parse(await readText(path))
  } catch (error) {
    return fail(
      'invalid-input',
      `Task list is not valid JSON: ${error instanceof Error ? error.message : String(error)}`,
    )
  }
  if (!isObject(root) || Object.keys(root).some((key) => key !== 'tasks') || !Array.isArray(root.tasks)) {
    return fail('invalid-input', "Task list root must contain only a 'tasks' array")
  }
  const ids = new Set<string>()
  const defaultWith = objectInput(template, 'with') ?? {}
  const base = stringInput(inputs, 'buildPrompt')
  const tasks: AddTaskInput[] = []
  for (const [index, value] of root.tasks.entries()) {
    if (!isObject(value)) return fail('invalid-input', `tasks[${index}] must be an object`)
    const unknown = Object.keys(value).filter((key) => !FIELDS.has(key))
    if (unknown.length) return fail('invalid-input', `tasks[${index}] contains unknown fields: ${unknown.join(', ')}`)
    const id = authoredString(value, 'id')
    const title = authoredString(value, 'title')
    const goal = authoredString(value, 'goal')
    if (!id || !title || !goal)
      return fail('invalid-input', `tasks[${index}] requires non-empty string id, title, and goal`)
    if (ids.has(id)) return fail('invalid-input', `Duplicate task id '${id}'`)
    ids.add(id)
    const acceptance = stringArray(value.acceptance)
    const refs = stringArray(value.refs)
    if (!acceptance) return fail('invalid-input', `tasks[${index}].acceptance must be an array of strings`)
    if (!refs) return fail('invalid-input', `tasks[${index}].refs must be an array of strings`)
    tasks.push({
      id,
      title,
      uses,
      with: {
        ...defaultWith,
        prompt: buildTaskPrompt({ id, title, goal, acceptance, refs }, base),
      },
      expect: null,
    })
  }
  if (!tasks.length) return succeed({ loaded: 0 })
  return { output: { loaded: tasks.length }, effects: { addTasks: tasks } } as unknown as ActionResult
}

function buildTaskPrompt(
  task: { id: string; title: string; goal: string; acceptance: string[]; refs: string[] },
  base: string | null | undefined,
): string {
  return [
    base,
    `<task id="${task.id}">`,
    `<title>${task.title}</title>`,
    `<goal>${task.goal}</goal>`,
    '<acceptance>',
    ...task.acceptance.map((item) => `- ${item}`),
    '</acceptance>',
    '<refs>',
    ...task.refs.map((item) => `- ${item}`),
    '</refs>',
    '</task>',
  ]
    .filter(Boolean)
    .join('\n')
}

function authoredString(value: JsonObject, key: string): string | null {
  const field = value[key]
  return typeof field === 'string' && field.trim() ? field.trim() : null
}

function stringArray(value: unknown): string[] | null {
  return Array.isArray(value) && value.every((item) => typeof item === 'string') ? [...value] : null
}

async function workspaceContainedPath(workDir: string, rawPath: string): Promise<string | null> {
  if (isAbsolute(rawPath)) return null
  const root = resolve(workDir)
  const path = resolve(root, rawPath)
  const rel = relative(root, path)
  if (!rel || rel.startsWith('..') || isAbsolute(rel)) return null
  let current = root
  for (const component of rel.split(/[\\/]+/).filter(Boolean)) {
    current = join(current, component)
    try {
      if ((await currentRunnerFileSystem().lstat(current)).isSymbolicLink()) return null
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw error
      break
    }
  }
  return path
}
