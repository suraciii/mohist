import type { ActionInvocationContext } from "./context.js"
import type { JsonObject } from "../core/types.js"
import { stringInput } from "../core/json.js"

export function workflowSessionName(input: JsonObject | null | undefined, workId: string): string {
  const explicit = stringInput(input, "session")?.trim()
  return explicit || workId.trim()
}

export function sessionNameFromContext(context: ActionInvocationContext): string {
  return workflowSessionName(context.with, context.workId)
}
