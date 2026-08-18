import type { RuntimeTurnEvent } from '../runtime/opencode/index.js'
import type { AgentSessionRuntimeEventOutbox, RuntimeEventRecord } from '../server/runtime-event-outbox.js'
import { runnerLogger } from '../system/logger.js'

const log = runnerLogger.child('session')

export interface WorkflowAgentSessionWorkMetadata {
  readonly workId: string
  readonly taskRunId: string
  readonly runnerId: string
  readonly agentSessionId: string
  readonly workType: string
  readonly stage?: string | null
}

const STREAMING_DELTA_TYPES = new Set(['reasoning.delta', 'message.delta'])
const MAX_STREAMING_DELTAS_PER_BATCH = 256

export interface WorkflowAgentSessionReporterOptions {
  readonly outbox: AgentSessionRuntimeEventOutbox
  readonly workflowRunId: string
  readonly projectId: string
  readonly sessionName: string
  readonly workMetadata: WorkflowAgentSessionWorkMetadata
  readonly runtime: 'opencode' | 'pi'
  readonly randomId: () => string
  /**
   * A positive bounded worktree-cleanup attempt creates a separate Session
   * follow-up turn. It is deliberately not part of action input.
   */
  readonly cleanupAttempt?: number | null
}

/**
 * Turn-scoped Workflow AgentSession reporter (issue 461). The reporter
 * no longer performs HTTP: durable delivery is delegated to the host-owned
 * `AgentSessionRuntimeEventOutbox`. The reporter's responsibility is
 * sequence preservation (input → activity → close), registering
 * ordered produced-fact promises synchronously, and waiting for every
 * local promise to settle before the Workflow result returns.
 *
 * `enqueueBeforeExecution` rolls back an uncommitted input on snapshot
 * failure (orchestrated by the outbox), and the reporter surfaces that
 * rejection by returning an explicit error from `awaitInput()` so the
 * Action can return execution-unavailable without invoking OpenCode.
 * Failed post-start fact enqueues settle their returned promise with a
 * rejection, but the reporter never replaces the runtime result with
 * an outbox error.
 */
export class WorkflowAgentSessionReporter {
  private readonly outbox: AgentSessionRuntimeEventOutbox
  private readonly projectId: string
  private readonly workflowRunId: string
  private readonly sessionName: string
  private readonly workMetadata: WorkflowAgentSessionWorkMetadata
  private readonly runtime: 'opencode' | 'pi'
  private readonly randomId: () => string
  private readonly cleanupAttempt: number | null
  private readonly cleanupOperationId: string | null
  private readonly pendingPromises: Set<Promise<void>> = new Set()
  private readonly deltaBuffer: RuntimeTurnEvent[] = []
  private closed = false
  private inputAccepted = false
  private inputRejected = false
  private inputDeliveryId: string | null = null
  private agentTurnId: string | null = null

  constructor(options: WorkflowAgentSessionReporterOptions) {
    this.outbox = options.outbox
    this.projectId = options.projectId
    this.workflowRunId = options.workflowRunId
    this.sessionName = options.sessionName
    this.workMetadata = options.workMetadata
    this.runtime = options.runtime
    this.randomId = options.randomId
    this.cleanupAttempt = isCleanupAttempt(options.cleanupAttempt) ? options.cleanupAttempt : null
    this.cleanupOperationId =
      this.cleanupAttempt === null
        ? null
        : workflowCleanupOperationId(
            this.workflowRunId,
            this.workMetadata.taskRunId,
            this.workMetadata.workId,
            this.cleanupAttempt,
          )
  }

  getAgentTurnId(): string | null {
    return this.agentTurnId
  }

  async awaitInput(prompt: string, runtimeSessionId: string): Promise<void> {
    if (this.closed) return
    if (this.cleanupOperationId !== null) {
      await this.awaitCleanupInput(prompt, runtimeSessionId)
      return
    }
    const pending = this.outbox
      .snapshot()
      .find(
        (record) =>
          record.producerFamily === 'workflow-session' &&
          record.target.kind === 'workflow' &&
          record.target.projectId === this.projectId &&
          record.target.workflowRunId === this.workflowRunId &&
          record.target.sessionName === this.sessionName &&
          record.runtimeSessionId === runtimeSessionId &&
          record.work?.workId === this.workMetadata.workId &&
          record.work?.taskRunId === this.workMetadata.taskRunId &&
          record.event.type === 'session.input',
      )
    if (pending && pending.event.payload.text !== prompt)
      throw new Error('pending Workflow session.input does not match the requested prompt')

    const inputDeliveryId = pending?.id ?? this.randomId()
    this.inputDeliveryId = inputDeliveryId
    const record =
      pending ??
      this.buildRecord(
        runtimeSessionId,
        {
          type: 'session.input',
          payload: {
            text: prompt,
            kind: 'task',
            source: 'workflow',
            role: 'user',
            runtimeSessionId,
          },
        },
        inputDeliveryId,
      )
    const promise = (pending ? Promise.resolve() : this.outbox.enqueueBeforeExecution(record))
      .then(async () => {
        const awaitReceipt = this.outbox.awaitInputReceipt
        if (!awaitReceipt) throw new Error('Workflow AgentSession outbox does not support Server input receipts')
        const receipt = await awaitReceipt.call(this.outbox, inputDeliveryId)
        if (
          receipt.inputDeliveryId !== inputDeliveryId ||
          receipt.agentSessionId !== this.workMetadata.agentSessionId ||
          !receipt.agentTurnId
        )
          throw new Error('Workflow AgentSession returned a malformed session.input receipt')
        this.agentTurnId = receipt.agentTurnId
        this.inputAccepted = true
      })
      .catch((error) => {
        if (this.inputRejected) return
        this.inputRejected = true
        log.error('workflow agent-session input enqueue failed', {
          run: this.workflowRunId,
          work: this.workMetadata.workId,
          session: this.sessionName,
          exception: error,
        })
        throw error
      })
    this.pendingPromises.add(promise)
    try {
      await promise
    } finally {
      this.pendingPromises.delete(promise)
    }
  }

  registerEvent(event: RuntimeTurnEvent): void {
    if (this.closed) return
    if (this.inputRejected) return
    if (this.cleanupOperationId !== null && !this.inputAccepted) return
    if (STREAMING_DELTA_TYPES.has(event.type)) {
      this.deltaBuffer.push(event)
      if (this.deltaBuffer.length >= MAX_STREAMING_DELTAS_PER_BATCH) this.flushDeltaBuffer()
      return
    }
    this.flushDeltaBuffer()
    const record = this.buildRecord(event.runtimeSessionId, {
      type: event.type,
      payload: event.payload,
    })
    const promise = this.outbox.enqueueProducedFact(record).catch((error) => {
      log.error('workflow agent-session event enqueue failed', {
        run: this.workflowRunId,
        work: this.workMetadata.workId,
        session: this.sessionName,
        reason: event.type,
        exception: error,
      })
      // Surface as a settled rejection so `settle()` can observe it; do not
      // rethrow synchronously (would crash the synchronous observer).
      throw error
    })
    this.pendingPromises.add(promise)
    promise.then(
      () => this.pendingPromises.delete(promise),
      () => this.pendingPromises.delete(promise),
    )
  }

  private flushDeltaBuffer(): void {
    if (this.deltaBuffer.length === 0) return
    const buffered = this.deltaBuffer.splice(0)
    const records = buffered.map((event) =>
      this.buildRecord(event.runtimeSessionId, {
        type: event.type,
        payload: event.payload,
      }),
    )
    const promise = this.outbox.enqueueProducedFactBatch(records).catch((error) => {
      log.error('workflow agent-session delta batch enqueue failed', {
        run: this.workflowRunId,
        work: this.workMetadata.workId,
        session: this.sessionName,
        reason: `count=${records.length}`,
        exception: error,
      })
      throw error
    })
    this.pendingPromises.add(promise)
    promise.then(
      () => this.pendingPromises.delete(promise),
      () => this.pendingPromises.delete(promise),
    )
  }

  registerClose(payload: {
    readonly status: 'completed' | 'failed' | 'unknown'
    readonly exitCode: number
    readonly failureReason?: string | null
    readonly runtimeSessionId: string
  }): void {
    if (this.closed) return
    if (this.inputRejected) return
    if (this.cleanupOperationId !== null && !this.inputAccepted) return
    // Flush buffered deltas before the activity fact so the outbox preserves turn order.
    this.flushDeltaBuffer()
    const records: RuntimeEventRecord[] = []
    if (payload.status !== 'completed')
      records.push(
        this.buildRecord(payload.runtimeSessionId, {
          type: 'turn.failed',
          payload: {
            status: payload.status,
            exitCode: payload.exitCode,
            ...(payload.status === 'unknown' ? { failureCategory: 'unknown' } : {}),
            ...(payload.failureReason ? { failureReason: payload.failureReason } : {}),
          },
        }),
      )
    records.push(
      this.buildRecord(payload.runtimeSessionId, {
        type: 'session.activity',
        payload: {
          activity: 'idle',
          status: payload.status,
          exitCode: payload.exitCode,
          ...(payload.status === 'unknown' ? { failureCategory: 'unknown' } : {}),
          ...(payload.failureReason ? { failureReason: payload.failureReason } : {}),
          runtimeSessionId: payload.runtimeSessionId,
          observedAt: new Date().toISOString(),
        },
      }),
    )
    const promise = this.outbox.enqueueProducedFactBatch(records).catch((error) => {
      log.error('workflow agent-session close enqueue failed', {
        run: this.workflowRunId,
        work: this.workMetadata.workId,
        session: this.sessionName,
        exception: error,
      })
      throw error
    })
    this.pendingPromises.add(promise)
    promise.then(
      () => this.pendingPromises.delete(promise),
      () => this.pendingPromises.delete(promise),
    )
    this.closed = true
  }

  inputWasAccepted(): boolean {
    return this.inputAccepted
  }

  inputWasRejected(): boolean {
    return this.inputRejected
  }

  async settle(): Promise<void> {
    // A turn that ended without registerClose still needs to ship its
    // buffered deltas. Rejected inputs must drop the buffer — there is
    // no session to attach the deltas to, and registerEvent/registerClose
    // already short-circuit on inputRejected.
    if (this.inputRejected) {
      this.deltaBuffer.length = 0
    } else {
      this.flushDeltaBuffer()
    }
    const promises = [...this.pendingPromises]
    if (promises.length === 0) return
    await Promise.allSettled(promises)
    this.pendingPromises.clear()
  }

  private buildRecord(
    runtimeSessionId: string,
    event: { type: string; payload: Record<string, unknown> },
    id = this.randomId(),
  ): RuntimeEventRecord {
    const agentTurnId = this.agentTurnId
    if (this.cleanupOperationId !== null) {
      if (!agentTurnId) throw new Error('Workflow cleanup runtime event requires an acknowledged cleanup turn')
      return {
        id,
        producerFamily: 'session-followup',
        target: {
          kind: 'session',
          sessionId: this.workMetadata.agentSessionId,
        },
        runtimeSessionId,
        // The runner-scoped Session route rejects task execution identity,
        // including runtime. Its immutable Session turn is the sole owner.
        runtime: null,
        sessionTurnId: agentTurnId,
        work: null,
        event: {
          type: event.type,
          payload: {
            ...event.payload,
            turnId: agentTurnId,
            cleanupOperationId: this.cleanupOperationId,
            source: 'workflow-cleanup',
          },
        },
        acknowledgementPolicy: 'matching-receipt',
      }
    }
    return {
      id,
      producerFamily: 'workflow-session',
      target: {
        kind: 'workflow',
        projectId: this.projectId,
        workflowRunId: this.workflowRunId,
        sessionName: this.sessionName,
      },
      runtimeSessionId,
      runtime: this.runtime,
      work: {
        workId: this.workMetadata.workId,
        taskRunId: this.workMetadata.taskRunId,
        runnerId: this.workMetadata.runnerId,
        agentSessionId: this.workMetadata.agentSessionId,
        workType: this.workMetadata.workType,
        stage: this.workMetadata.stage ?? null,
        inputDeliveryId: this.inputDeliveryId,
        agentTurnId,
      },
      event: {
        type: event.type,
        payload: agentTurnId ? { ...event.payload, turnId: agentTurnId } : event.payload,
      },
      acknowledgementPolicy: 'matching-receipt',
    }
  }

  private async awaitCleanupInput(prompt: string, runtimeSessionId: string): Promise<void> {
    const operationId = this.cleanupOperationId
    if (operationId === null) throw new Error('Workflow cleanup operation is unavailable')
    const inputDeliveryId = workflowCleanupInputDeliveryId(operationId)
    const agentTurnId = workflowCleanupTurnId(operationId)
    const pending = this.outbox
      .snapshot()
      .find(
        (record) =>
          record.producerFamily === 'workflow-cleanup' &&
          record.id === operationId &&
          record.target.kind === 'workflow' &&
          record.target.projectId === this.projectId &&
          record.target.workflowRunId === this.workflowRunId &&
          record.target.sessionName === this.sessionName &&
          record.runtimeSessionId === runtimeSessionId &&
          record.event.type === 'session.cleanup',
      )
    if (pending && pending.event.payload.text !== prompt)
      throw new Error('pending Workflow cleanup prompt does not match the requested prompt')

    this.inputDeliveryId = inputDeliveryId
    const start = pending ?? {
      id: operationId,
      producerFamily: 'workflow-cleanup' as const,
      target: {
        kind: 'workflow' as const,
        projectId: this.projectId,
        workflowRunId: this.workflowRunId,
        sessionName: this.sessionName,
      },
      runtimeSessionId,
      runtime: this.runtime,
      work: {
        workId: this.workMetadata.workId,
        taskRunId: this.workMetadata.taskRunId,
        runnerId: this.workMetadata.runnerId,
        agentSessionId: this.workMetadata.agentSessionId,
        workType: this.workMetadata.workType,
        stage: this.workMetadata.stage ?? null,
        inputDeliveryId,
        agentTurnId: null,
      },
      event: {
        type: 'session.cleanup',
        payload: {
          text: prompt,
          cleanupOperationId: operationId,
          inputDeliveryId,
          turnId: agentTurnId,
          attempt: this.cleanupAttempt,
        },
      },
      acknowledgementPolicy: 'matching-receipt' as const,
    }
    const promise = (pending ? Promise.resolve() : this.outbox.enqueueBeforeExecution(start))
      .then(async () => {
        const awaitReceipt = this.outbox.awaitInputReceipt
        if (!awaitReceipt) throw new Error('Workflow AgentSession outbox does not support Server input receipts')
        const receipt = await awaitReceipt.call(this.outbox, operationId)
        if (
          receipt.inputDeliveryId !== inputDeliveryId ||
          receipt.agentSessionId !== this.workMetadata.agentSessionId ||
          receipt.agentTurnId !== agentTurnId
        )
          throw new Error('Workflow AgentSession returned a malformed cleanup receipt')
        this.agentTurnId = agentTurnId
        const runtimeInput = this.buildRecord(
          runtimeSessionId,
          {
            type: 'session.input',
            payload: {
              text: prompt,
              kind: 'cleanup',
              source: 'workflow-cleanup',
              role: 'user',
              runtimeSessionId,
              turnId: agentTurnId,
              cleanupOperationId: operationId,
            },
          },
          `${operationId}:runtime-input`,
        )
        await this.outbox.enqueueBeforeExecution(runtimeInput)
        await awaitReceipt.call(this.outbox, runtimeInput.id)
        this.inputAccepted = true
      })
      .catch((error) => {
        if (this.inputRejected) return
        this.inputRejected = true
        log.error('workflow cleanup input enqueue failed', {
          run: this.workflowRunId,
          work: this.workMetadata.workId,
          session: this.sessionName,
          exception: error,
        })
        throw error
      })
    this.pendingPromises.add(promise)
    try {
      await promise
    } finally {
      this.pendingPromises.delete(promise)
    }
  }
}

export function workflowCleanupOperationId(
  workflowRunId: string,
  taskRunId: string,
  workId: string,
  cleanupAttempt: number,
): string {
  return `workflow-cleanup:${workflowRunId}:${taskRunId}:${workId}:${cleanupAttempt}`
}

export function workflowCleanupInputDeliveryId(operationId: string): string {
  return `workflow-cleanup-input:${operationId}`
}

export function workflowCleanupTurnId(operationId: string): string {
  return `workflow-cleanup-turn:${operationId}`
}

function isCleanupAttempt(value: number | null | undefined): value is number {
  return typeof value === 'number' && Number.isInteger(value) && value > 0
}
