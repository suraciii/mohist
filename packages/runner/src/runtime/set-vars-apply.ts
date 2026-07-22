import type { JsonObject, DispatchWorkItem, WorkItemResult } from "../core/types.js"
import { extractSetVars } from "./set-vars.js"

export interface SetVarsPatcher {
  patchRunVars(workflowRunId: string, vars: JsonObject, signal: AbortSignal): Promise<unknown>
}

export async function applySetVarsForWork(
  patcher: SetVarsPatcher,
  work: DispatchWorkItem,
  result: WorkItemResult,
  signal: AbortSignal,
  effectVars: JsonObject = {},
): Promise<WorkItemResult> {
  if (result.status !== "completed") return result

  const extraction = work.setVars ? extractSetVars(work.setVars, result.output) : { vars: {}, error: null }
  if (extraction.error) return { ...result, status: "failed", message: `setVars: ${extraction.error}` }
  const vars = { ...effectVars, ...(extraction.vars ?? {}) }
  if (Object.keys(vars).length === 0) return result

  try {
    await patcher.patchRunVars(work.workflowRunId, vars, signal)
    return result
  } catch (error) {
    return {
      ...result,
      status: "failed",
      message: `setVars patch failed: ${error instanceof Error ? error.message : String(error)}`.slice(0, 4000),
    }
  }
}
