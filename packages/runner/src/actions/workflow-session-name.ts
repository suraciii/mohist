import type { ActionContext, JsonObject } from "../core/types.js"
import { stringInput } from "../core/json.js"

export function workflowSessionName(input: JsonObject | null | undefined, workId: string): string {
  const explicit = stringInput(input, "session")?.trim()
  return explicit || workId.trim()
}

export function sessionNameFromContext(context: ActionContext): string {
  return workflowSessionName(context.with, context.workId)
}
