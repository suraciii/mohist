import { defaultPromptLoaderRegistry } from './prompt.js'
import { TASK_LIST_PROMPT_LOADER_NAME, taskListPromptLoader } from '../actions/task-list-prompt.js'

export function registerDefaultPromptLoaders(): void {
  const registry = defaultPromptLoaderRegistry()
  if (!registry.has(TASK_LIST_PROMPT_LOADER_NAME)) registry.register(TASK_LIST_PROMPT_LOADER_NAME, taskListPromptLoader)
}

registerDefaultPromptLoaders()
