import type { DispatchWorkItem } from '../core/types.js'
import type {
  AgentSessionRuntimeEventOutbox,
  CleanupPredecessorDeliveryTarget,
} from '../server/runtime-event-outbox.js'
import { CleanupPredecessorDeliveryWaitTimeoutError } from '../server/runtime-event-outbox.js'
import { workflowCleanupOperationId } from '../actions/workflow-agent-session-reporter.js'

export const CLEANUP_TERMINAL_FACT_DELIVERY_BUDGET_MS = 60_000
export const CLEANUP_TERMINAL_FACT_DELIVERY_TIMEOUT_CODE = 'session-delivery-wait-timeout'

export interface CleanupTurnAdmissionInput {
  readonly projectId?: string | null
  readonly workflowRunId: string
  readonly sessionName: string
  readonly workId: string
  readonly taskRunId?: string | null
  readonly cleanupAttempt?: number | null
}

export function cleanupPredecessorTarget(input: CleanupTurnAdmissionInput): CleanupPredecessorDeliveryTarget | null {
  const cleanupAttempt = input.cleanupAttempt
  const projectId = input.projectId
  if (
    !isPositiveCleanupAttempt(cleanupAttempt) ||
    typeof projectId !== 'string' ||
    projectId.length === 0 ||
    !input.workflowRunId ||
    !input.sessionName
  )
    return null

  let precedingCleanupOperationId: string | null = null
  if (cleanupAttempt > 1) {
    const taskRunId = input.taskRunId
    if (typeof taskRunId !== 'string' || taskRunId.length === 0 || !input.workId) return null
    precedingCleanupOperationId = workflowCleanupOperationId(
      input.workflowRunId,
      taskRunId,
      input.workId,
      cleanupAttempt - 1,
    )
  }

  return {
    projectId,
    workflowRunId: input.workflowRunId,
    sessionName: input.sessionName,
    cleanupAttempt,
    precedingCleanupOperationId,
  }
}

export function isPositiveCleanupAttempt(value: number | null | undefined): value is number {
  return typeof value === 'number' && Number.isInteger(value) && value > 0
}

export async function waitForCleanupPredecessorDelivery(
  outbox: AgentSessionRuntimeEventOutbox | null | undefined,
  input: CleanupTurnAdmissionInput,
  signal: AbortSignal,
  budgetMs = CLEANUP_TERMINAL_FACT_DELIVERY_BUDGET_MS,
): Promise<CleanupPredecessorDeliveryTarget | null> {
  const target = cleanupPredecessorTarget(input)
  const wait = outbox?.awaitCleanupPredecessorDelivery
  if (!target || !wait) return target
  await wait.call(outbox, target, { budgetMs, signal })
  return target
}

export function cleanupDeliveryWaitFailureMessage(error: unknown, work: Pick<DispatchWorkItem, 'workId'>): string {
  if (error instanceof CleanupPredecessorDeliveryWaitTimeoutError) {
    return (
      `Workflow cleanup admission timed out waiting for terminal-fact delivery for work item ${work.workId}; ` +
      `awaited session ${error.projectId}/${error.workflowRunId}/${error.sessionName}; ` +
      `cleanup attempt ${error.cleanupAttempt}; exhausted budget ${error.budgetMs}ms` +
      (error.precedingCleanupOperationId ? `; predecessor ${error.precedingCleanupOperationId}` : '')
    )
  }
  return `Workflow cleanup admission could not wait for terminal-fact delivery for work item ${work.workId}: ${error instanceof Error ? error.message : String(error)}`
}

export function isCleanupDeliveryWaitTimeout(error: unknown): error is CleanupPredecessorDeliveryWaitTimeoutError {
  return error instanceof CleanupPredecessorDeliveryWaitTimeoutError
}
