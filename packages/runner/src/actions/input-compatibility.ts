import type { DispatchWorkItem, JsonObject } from '../core/types.js'

const RETIRED_CORE_SCRIPT_INPUT = 'resourceProfile'

/**
 * Projects a historical Workflow Action input onto the current execution
 * contract without changing the persisted dispatch declaration.
 */
export function normalizeWorkflowActionInput(
  work: Pick<DispatchWorkItem, 'workType' | 'uses' | 'ownerKind'>,
  clonedWith: JsonObject | null,
): JsonObject | null {
  if (clonedWith === null) return null

  const normalized = { ...clonedWith }
  if (isHistoricalCoreScriptWorkflowTask(work)) delete normalized[RETIRED_CORE_SCRIPT_INPUT]
  return normalized
}

function isHistoricalCoreScriptWorkflowTask(work: Pick<DispatchWorkItem, 'workType' | 'uses' | 'ownerKind'>): boolean {
  return (
    work.workType === 'task' &&
    work.uses?.trim().toLowerCase() === 'core/script' &&
    work.ownerKind?.trim().toLowerCase() !== 'agent-job'
  )
}
