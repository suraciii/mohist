import type { JsonObject, RenderedWorkItem, WorkItemResult } from "../core/types.js"
import { extractSetVars } from "./set-vars.js"

export interface SetVarsPatcher {
  patchRunVars(workflowRunId: string, vars: JsonObject, signal: AbortSignal): Promise<unknown>
}

export async function applySetVarsForWork(
  patcher: SetVarsPatcher,
  work: RenderedWorkItem,
  result: WorkItemResult,
  signal: AbortSignal,
): Promise<WorkItemResult> {
  if (result.status !== "completed") return result
  if (!work.setVars || Object.keys(work.setVars).length === 0) return result

  const extraction = extractSetVars(work.setVars, result.output)
  if (extraction.error) return { ...result, status: "failed", message: `setVars: ${extraction.error}` }
  if (!extraction.vars) return result

  try {
    await patcher.patchRunVars(work.workflowRunId, extraction.vars, signal)
    return result
  } catch (error) {
    return {
      ...result,
      status: "failed",
      message: `setVars patch failed: ${error instanceof Error ? error.message : String(error)}`.slice(0, 4000),
    }
  }
}
