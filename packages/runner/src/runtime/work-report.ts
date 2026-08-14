import type { DispatchWorkItem, WorkItemResult } from "../core/types.js"
import type { ServerConnection } from "../server/connection.js"

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
  connection: Pick<ServerConnection, "report">,
  work: DispatchWorkItem,
  result: WorkItemResult,
): Promise<void> {
  const controller = new AbortController()
  const timeout = setTimeout(() => controller.abort(), REPORT_TIMEOUT_MS)
  timeout.unref?.()
  try {
    const acknowledgement = await connection.report(work, result, controller.signal)
    if (acknowledgement.tracked !== true) {
      const reason = typeof acknowledgement.reason === "string" ? `: ${acknowledgement.reason}` : ""
      throw new Error(`work report was not durably acknowledged${reason}`)
    }
  } finally {
    clearTimeout(timeout)
  }
}
