import type { ActionError, ActionResult } from "../core/types.js"

type ActionFacts = Pick<ActionResult, "exitCode" | "turnFact">

export function succeed(output: string | null = null, facts: ActionFacts = {}): ActionResult {
  return { output, ...facts }
}

export function fail(code: string, message: string, facts: ActionFacts = {}): ActionResult {
  return { error: { code, message }, ...facts }
}

export function actionErrorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error)
}

export function isActionFailure(result: ActionResult): result is Extract<ActionResult, { error: ActionError }> {
  return "error" in result
}
