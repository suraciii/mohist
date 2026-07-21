import type { ActionInvocationContext } from "./context.js"
import type { PromptLoaderContext } from "../core/prompt.js"
import { stringInput } from "../core/json.js"

export function buildPromptLoaderContext(context: ActionInvocationContext): PromptLoaderContext {
  return {
    with: {},
    workDir: context.workDir,
    workId: context.workId,
    title: context.title ?? null,
    stage: context.stage ?? null,
  }
}

export function sessionNameFromContext(context: ActionInvocationContext): string | undefined {
  return stringInput(context.with, "session") ?? context.workId
}
