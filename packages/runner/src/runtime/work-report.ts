import type { DispatchWorkItem, WorkItemResult } from '../core/types.js'
import type { ServerConnection } from '../server/connection.js'
import type { RuntimeRecoveryReceipt } from './recovery-receipt.js'

export const RUNNER_RESTARTED_REASON = 'runner-restarted'

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
      status: isWorkflowAgentWork(work) ? 'unknown' : 'failed',
      message,
      error,
      exitCode: 1,
    },
    interruption,
  }
}

const REPORT_TIMEOUT_MS = 10_000

export async function reportAndRequireDurableAck(
  connection: Pick<ServerConnection, 'report'>,
  work: DispatchWorkItem,
  result: WorkItemResult,
  binding?: Pick<RuntimeRecoveryReceipt, 'agentSessionId' | 'agentTurnId' | 'runtime' | 'runtimeSessionId'>,
): Promise<void> {
  const controller = new AbortController()
  const timeout = setTimeout(() => controller.abort(), REPORT_TIMEOUT_MS)
  timeout.unref?.()
  try {
    const acknowledgement = binding
      ? await connection.report(work, result, controller.signal, binding)
      : await connection.report(work, result, controller.signal)
    if (acknowledgement.verdict !== 'accepted' && acknowledgement.verdict !== 'refused')
      throw new Error('work report remains outstanding')
  } finally {
    clearTimeout(timeout)
  }
}
