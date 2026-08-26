import { workflowCleanupOperationId } from '../actions/workflow-agent-session-reporter.js'

export interface CleanupTurnAdmissionInput {
  readonly projectId?: string | null
  readonly workflowRunId: string
  readonly sessionName: string
  readonly workId: string
  readonly taskRunId?: string | null
  readonly cleanupAttempt?: number | null
}

export interface CleanupPredecessorTarget {
  readonly projectId: string
  readonly workflowRunId: string
  readonly sessionName: string
  readonly cleanupAttempt: number
  readonly precedingCleanupOperationId: string | null
}

export function cleanupPredecessorTarget(input: CleanupTurnAdmissionInput): CleanupPredecessorTarget | null {
  const cleanupAttempt = input.cleanupAttempt
  const projectId = input.projectId
  if (!isPositiveCleanupAttempt(cleanupAttempt) || !projectId || !input.workflowRunId || !input.sessionName) return null
  let precedingCleanupOperationId: string | null = null
  if (cleanupAttempt > 1) {
    if (!input.taskRunId || !input.workId) return null
    precedingCleanupOperationId = workflowCleanupOperationId(
      input.workflowRunId,
      input.taskRunId,
      input.workId,
      cleanupAttempt - 1,
    )
  }
  return { projectId, workflowRunId: input.workflowRunId, sessionName: input.sessionName, cleanupAttempt, precedingCleanupOperationId }
}

export function isPositiveCleanupAttempt(value: number | null | undefined): value is number {
  return typeof value === 'number' && Number.isInteger(value) && value > 0
}
