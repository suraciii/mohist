import { createHash } from 'node:crypto'
import type {
  DispatchWorkItem,
  WorkflowTaskCompletionBoundary,
  WorkflowTaskExecutionIdentity,
  WorkItemResult,
} from '../core/types.js'
import type { ServerConnection } from '../server/connection.js'
import { runnerLogger } from '../system/logger.js'
import { reportAndRequireDurableAck } from './work-report.js'
import type { WorkResultJournal, WorkResultJournalState } from './work-result-journal.js'

const log = runnerLogger.child('legacy-workflow-recovery')
const LEGACY_OBSERVATION_TIME = '1970-01-01T00:00:00.000Z'
const LEGACY_BOUNDARY_REASON = 'boundary-missing'
const LEGACY_MESSAGE =
  'Legacy Workflow journal entry has no v1 completion boundary; operator reconciliation is required.'

interface RecoveredLegacyEntry {
  readonly work: DispatchWorkItem
  readonly state: WorkResultJournalState
  readonly boundary: WorkflowTaskCompletionBoundary
  attempts: number
  retryAt: number | null
}

/**
 * Converts only pre-v1 Workflow task journal fences into an explicit,
 * non-settling v1 observation. It never executes the Action and never reuses
 * the old result payload, because that payload has no authoritative boundary.
 */
export class LegacyWorkflowRecovery {
  private readonly entries = new Map<string, RecoveredLegacyEntry>()

  constructor(
    private readonly journal: WorkResultJournal,
    private readonly connection: Pick<ServerConnection, 'report'>,
    private readonly runnerId: string,
  ) {}

  recover(): void {
    if (!this.journal.ready()) return
    for (const entry of [...this.journal.started(), ...this.journal.completed()]) {
      this.enqueueEntry(entry.work)
    }
  }

  enqueue(work: DispatchWorkItem): void {
    this.enqueueEntry(work)
  }

  has(key: string): boolean {
    return this.entries.has(key)
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
          log.warn('legacy Workflow boundary-missing observation retry failed', {
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
    const retryAt = this.nextRetryAt()
    return retryAt === null || (current !== null && current <= retryAt) ? current : retryAt
  }

  private enqueueEntry(work: DispatchWorkItem): void {
    const ownerKind = (work.ownerKind ?? 'workflow').trim().toLowerCase()
    if (ownerKind !== 'workflow' || work.workType !== 'task') return

    const key = `${ownerKind}:${work.workflowRunId}:${work.workId}`
    if (this.entries.has(key)) return

    const state = this.journal.legacyWorkflowState(work)
    if (state === null) return

    const boundary = buildLegacyBoundary(work, this.runnerId)
    if (boundary === null) {
      // The local fence is deliberately left untouched. Without the complete
      // identity the server cannot bind the observation to one attempt.
      log.warn('legacy Workflow fence needs operator reconciliation; identity is incomplete', {
        work: work.workId,
        workflow: work.workflowRunId,
      })
      return
    }

    this.entries.set(key, {
      work,
      state,
      boundary,
      attempts: 0,
      retryAt: 0,
    })
  }

  private async reportOnce(key: string): Promise<void> {
    const entry = this.entries.get(key)
    if (!entry) return
    entry.attempts += 1
    await reportAndRequireDurableAck(this.connection, entry.work, legacyResult(entry.boundary), entry.boundary)
    if (entry.state === 'completed') await this.journal.acknowledge(entry.work)
    else await this.journal.acknowledgeUnconfirmed(entry.work)
    this.entries.delete(key)
  }

  private scheduleRetry(key: string, now: number): void {
    const entry = this.entries.get(key)
    if (entry) entry.retryAt = now + 5_000
  }
}

export function buildLegacyBoundary(work: DispatchWorkItem, runnerId: string): WorkflowTaskCompletionBoundary | null {
  const ownerKind = (work.ownerKind ?? 'workflow').trim().toLowerCase() || 'workflow'
  const effectiveRunnerId = work.runnerId?.trim() || runnerId
  if (
    ownerKind !== 'workflow' ||
    work.workType !== 'task' ||
    !work.workflowRunId.trim() ||
    !work.workId.trim() ||
    !work.taskRunId?.trim() ||
    !work.stage?.trim() ||
    effectiveRunnerId !== runnerId
  ) {
    return null
  }

  const identity: WorkflowTaskExecutionIdentity = {
    workflowRunId: work.workflowRunId,
    stage: work.stage,
    taskAttemptId: work.taskRunId,
    workId: work.workId,
    ownerKind: 'workflow',
    ownerId: work.workflowRunId,
    runnerId: effectiveRunnerId,
    workspaceId: work.workspaceId ?? null,
    workspaceGeneration: work.workspaceGeneration ?? null,
  }
  const completion = {
    version: 1 as const,
    actionStarted: false,
    outcome: 'unknown' as const,
    phase: 'legacy-reconciliation',
    output: null,
    error: { code: LEGACY_BOUNDARY_REASON, message: LEGACY_MESSAGE },
    artifactUploadIds: [],
    capturedOutputs: null,
    completedAt: LEGACY_OBSERVATION_TIME,
  }
  const receipt = {
    version: 1 as const,
    identity: structuredClone(identity),
    expectedBranch: workspaceBranch(work),
    expectedHead: work.workspaceHead ?? null,
    expectedTree: work.workspaceTree ?? null,
    observedBranch: null,
    observedHead: null,
    observedTree: null,
    staged: [],
    unstaged: [],
    untracked: [],
    authoritative: false,
    reason: LEGACY_BOUNDARY_REASON,
    probedAt: LEGACY_OBSERVATION_TIME,
  }
  const unsigned = {
    version: 1 as const,
    identity,
    actionCompletion: completion,
    commitReceipt: receipt,
    workspaceOutcome: 'unconfirmed' as const,
    workspaceReason: LEGACY_BOUNDARY_REASON,
    cleanupScope: [],
  }
  return {
    ...unsigned,
    fingerprint: createHash('sha256').update(JSON.stringify(unsigned)).digest('hex'),
  }
}

function legacyResult(boundary: WorkflowTaskCompletionBoundary): WorkItemResult {
  return {
    status: 'unknown',
    message: LEGACY_MESSAGE,
    error: boundary.actionCompletion.error,
    output: null,
    artifactUploadIds: [],
    workspaceOutcome: boundary.workspaceOutcome,
    workspaceReason: boundary.workspaceReason,
  }
}

function workspaceBranch(work: DispatchWorkItem): string | null {
  const workspace = work.variables?.workspace
  if (!workspace || typeof workspace !== 'object' || Array.isArray(workspace)) return null
  const branch = (workspace as Record<string, unknown>).branch
  return typeof branch === 'string' && branch.trim() ? branch : null
}
