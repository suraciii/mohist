import type { JsonObject } from '../core/types.js'
import { isObject } from '../core/json.js'
import type { PromptLoader } from '../core/prompt.js'

export const TASK_LIST_PROMPT_LOADER_NAME = 'mohist/task-list-prompt'

export const taskListPromptLoader: PromptLoader = async (ctx) => {
  const task = ctx.with.task
  if (!isObject(task)) throw new Error(`${TASK_LIST_PROMPT_LOADER_NAME} requires a validated 'task' snapshot`)
  const id = strictString(task, 'id')
  const title = strictString(task, 'title')
  const goal = strictString(task, 'goal')
  const acceptance = strictStringArray(task, 'acceptance')
  const refs = strictStringArray(task, 'refs')
  const base = typeof ctx.with.base === 'string' ? ctx.with.base.trim() : ''
  return [
    base,
    `<task id="${id}">`,
    `<title>${title}</title>`,
    `<goal>${goal}</goal>`,
    '<acceptance>',
    ...acceptance.map((item) => `- ${item}`),
    '</acceptance>',
    '<refs>',
    ...refs.map((item) => `- ${item}`),
    '</refs>',
    '</task>',
  ]
    .filter(Boolean)
    .join('\n')
}

function strictString(value: JsonObject, key: string): string {
  const field = value[key]
  if (typeof field !== 'string' || !field.trim()) throw new Error(`Task snapshot '${key}' must be a non-empty string`)
  return field
}

function strictStringArray(value: JsonObject, key: string): string[] {
  const field = value[key]
  if (!Array.isArray(field) || field.some((item) => typeof item !== 'string')) {
    throw new Error(`Task snapshot '${key}' must be an array of strings`)
  }
  return field as string[]
}
