import { defaultPromptLoaderRegistry } from "./prompt.js"
import { OPENSPEC_TASK_PROMPT_LOADER_NAME, openspecTaskPromptLoader } from "../actions/openspec-task-prompt.js"

export function registerDefaultPromptLoaders(): void {
  const registry = defaultPromptLoaderRegistry()
  if (!registry.has(OPENSPEC_TASK_PROMPT_LOADER_NAME)) {
    registry.register(OPENSPEC_TASK_PROMPT_LOADER_NAME, openspecTaskPromptLoader)
  }
}

registerDefaultPromptLoaders()
