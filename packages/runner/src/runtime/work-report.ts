import type { DispatchWorkItem, WorkItemResult, WorkflowTaskCompletionBoundary } from '../core/types.js'
import type { ServerConnection } from '../server/connection.js'

export const RUNNER_RESTARTED_REASON = 'runner-restarted'

/**
 * A started journal entry has no authoritative result after the physical
 * execution disappears. This is the only runner-generated terminal fact for
 * that case; runtime observations are deliberately not promoted into a
 * successful result.
 */
export interface WorkInterruptionFact {
  readonly reason: typeof RUNNER_RESTARTED_REASON
  readonly ownerKind: string
  readonly ownerId: string
  readonly workId: string
  readonly recordedAt: string
}

export function isWorkflowAgentWork(work: DispatchWorkItem): boolean {
  if (work.agentDefinition?.runtime) return true
  const uses = work.uses?.trim().toLowerCase()
  if (uses === 'mohist/opencode' || uses === 'mohist/pi') return true
  if ((work.ownerKind ?? '').trim().toLowerCase() !== 'agent-job') return false
  const runtime = typeof work.with?.runtime === 'string' ? work.with.runtime.trim().toLowerCase() : ''
  return runtime === 'opencode' || runtime === 'pi'
}

export function runnerRestartedResult(work: DispatchWorkItem): {
  result: WorkItemResult
  interruption: WorkInterruptionFact
} {
  const ownerKind = (work.ownerKind ?? 'workflow').trim().toLowerCase() || 'workflow'
  const ownerId = ownerKind === 'agent-job' ? (work.agentJobId ?? '') : work.workflowRunId
  const interruption: WorkInterruptionFact = {
    reason: RUNNER_RESTARTED_REASON,
    ownerKind,
    ownerId,
    workId: work.workId,
    recordedAt: new Date().toISOString(),
  }
  const message = RUNNER_RESTARTED_REASON
  const error = { code: RUNNER_RESTARTED_REASON, message }
  return {
    result: {
      // Agent-task unknown is consumed by the server's existing settlement
      // arbitration. Ordinary work receives a definite failed outcome.
      status: isWorkflowAgentWork(work) ? 'unknown' : 'failed',
      message,
      error,
      exitCode: 1,
    },
    interruption,
  }
}

/**
 * Timeout for one report HTTP attempt. A report that does not complete within
 * this window is aborted and retried by the runner reconciliation loop.
 */
const REPORT_TIMEOUT_MS = 10_000

/**
 * Reports a settled work item and accepts only a server response that
 * explicitly confirms durable tracking. Untracked observations remain
 * retryable at the caller so a transient or stale response cannot lose work.
 */
export async function reportAndRequireDurableAck(
  connection: Pick<ServerConnection, 'report'>,
  work: DispatchWorkItem,
  result: WorkItemResult,
  boundary?: WorkflowTaskCompletionBoundary,
): Promise<void> {
  const controller = new AbortController()
  const timeout = setTimeout(() => controller.abort(), REPORT_TIMEOUT_MS)
  timeout.unref?.()
  try {
    const acknowledgement =
      boundary === undefined
        ? await connection.report(work, result, controller.signal)
        : await connection.report(work, result, controller.signal, boundary)
    if (
      acknowledgement.tracked !== true ||
      (boundary?.workspaceReason === 'boundary-missing' && acknowledgement.reason === 'stale')
    ) {
      const reason = typeof acknowledgement.reason === 'string' ? `: ${acknowledgement.reason}` : ''
      throw new Error(`work report was not durably acknowledged${reason}`)
    }
  } finally {
    clearTimeout(timeout)
  }
}
