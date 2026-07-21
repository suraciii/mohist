import type { ActionInvocationContext } from "./context.js"
import type { PromptLoaderContext } from "../core/prompt.js"
import { sessionNameFromContext } from "./workflow-session-name.js"

export function buildPromptLoaderContext(context: ActionInvocationContext): PromptLoaderContext {
  return {
    with: {},
    workDir: context.workDir,
    workId: context.workId,
    title: context.title ?? null,
    stage: context.stage ?? null,
  }
}

export { sessionNameFromContext }
