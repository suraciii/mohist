import type { DispatchWorkItem, WorkItemResult } from '../core/types.js'
import type { ServerConnection } from '../server/connection.js'
import type { AgentExecutionBinding } from '../core/types.js'

const REPORT_TIMEOUT_MS = 10_000

export async function reportAndRequireDurableAck(
  connection: Pick<ServerConnection, 'report'>,
  work: DispatchWorkItem,
  result: WorkItemResult,
  binding?: AgentExecutionBinding,
  signal: AbortSignal = new AbortController().signal,
): Promise<void> {
  const controller = new AbortController()
  const timeout = setTimeout(() => controller.abort(), REPORT_TIMEOUT_MS)
  const abort = () => controller.abort(signal.reason)
  signal.addEventListener('abort', abort, { once: true })
  timeout.unref?.()
  try {
    const acknowledgement = binding
      ? await connection.report(work, result, controller.signal, binding)
      : await connection.report(work, result, controller.signal)
    if (acknowledgement.verdict !== 'accepted' && acknowledgement.verdict !== 'refused')
      throw new Error('work report remains outstanding')
  } finally {
    signal.removeEventListener('abort', abort)
    clearTimeout(timeout)
  }
}
