import type { DispatchWorkItem } from '../core/types.js'
import type { ServerConnection } from '../server/connection.js'
import { runnerLogger } from '../system/logger.js'
import { reportAndRequireDurableAck } from './work-report.js'
import { WorkResultJournal, workKey } from './work-result-journal.js'

const log = runnerLogger.child('recovered-started-work')

const RECOVERED_STARTED_RESULT_MESSAGE =
  'Runner restarted after a durable started fence without a completed result receipt.'

const RETRY_INTERVAL_MS = 5_000

interface RecoveredStartedEntry {
  readonly work: DispatchWorkItem
  attempts: number
  retryAt: number | null
}

/**
 * Reconciles only fences that were durable before this host process started.
 * They can establish an unknown Agent result observation, never a terminal
 * result or permission to replay the physical dispatch.
 */
export class RecoveredStartedWork {
  private readonly entries = new Map<string, RecoveredStartedEntry>()

  constructor(
    private readonly journal: WorkResultJournal,
    private readonly connection: Pick<ServerConnection, 'report'>,
  ) {}

  recover(): void {
    if (!this.journal.ready()) return
    for (const entry of this.journal.started()) {
      if (!isRecoverableAgentStartedWork(entry.work)) continue
      const key = workKey(entry.work)
      if (this.entries.has(key)) continue
      this.entries.set(key, { work: entry.work, attempts: 0, retryAt: 0 })
    }
  }

  async retryDue(now: number): Promise<void> {
    const due = [...this.entries.entries()].filter(([, entry]) => entry.retryAt !== null && entry.retryAt <= now)
    await Promise.all(
      due.map(async ([key, entry]) => {
        entry.retryAt = null
        try {
          await this.reportOnce(key)
        } catch (error) {
          this.scheduleRetry(key, now)
          log.warn('recovered started work observation retry failed', {
            work: entry.work.workId,
            attempt: entry.attempts,
            exception: error,
          })
        }
      }),
    )
  }

  nextRetryAt(): number | null {
    let earliest: number | null = null
    for (const entry of this.entries.values()) {
      if (entry.retryAt !== null && (earliest === null || entry.retryAt < earliest)) earliest = entry.retryAt
    }
    return earliest
  }

  earlierRetryAt(current: number | null): number | null {
    const recovered = this.nextRetryAt()
    return recovered === null || (current !== null && current <= recovered) ? current : recovered
  }

  private async reportOnce(key: string): Promise<void> {
    const entry = this.entries.get(key)
    if (!entry) return
    entry.attempts += 1
    await reportAndRequireDurableAck(this.connection, entry.work, {
      status: 'unknown',
      message: RECOVERED_STARTED_RESULT_MESSAGE,
    })
    await this.journal.acknowledgeUnconfirmed(entry.work)
    this.entries.delete(key)
  }

  private scheduleRetry(key: string, now: number): void {
    const entry = this.entries.get(key)
    if (entry) entry.retryAt = now + RETRY_INTERVAL_MS
  }
}

function isRecoverableAgentStartedWork(work: DispatchWorkItem): boolean {
  const ownerKind = work.ownerKind?.trim().toLowerCase()
  const uses = work.uses?.trim().toLowerCase()
  return (
    (!ownerKind || ownerKind === 'workflow') &&
    work.workType === 'task' &&
    Boolean(work.workflowRunId.trim()) &&
    Boolean(work.workId.trim()) &&
    Boolean(work.taskRunId?.trim()) &&
    (uses === 'mohist/opencode' || uses === 'mohist/pi')
  )
}
