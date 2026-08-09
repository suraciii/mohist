import type { RuntimeTurnEvent } from "../runtime/opencode/index.js"
import type { AgentSessionRuntimeEventOutbox, RuntimeEventRecord } from "../server/runtime-event-outbox.js"
import { runnerLogger } from "../system/logger.js"

const log = runnerLogger.child("session")

export interface WorkflowAgentSessionWorkMetadata {
  readonly workId: string
  readonly workType: string
  readonly stage?: string | null
}

const STREAMING_DELTA_TYPES = new Set(["reasoning.delta", "message.delta"])
const MAX_STREAMING_DELTAS_PER_BATCH = 256

export interface WorkflowAgentSessionReporterOptions {
  readonly outbox: AgentSessionRuntimeEventOutbox
  readonly workflowRunId: string
  readonly projectId: string
  readonly sessionName: string
  readonly workMetadata: WorkflowAgentSessionWorkMetadata
  readonly randomId: () => string
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
  private readonly randomId: () => string
  private readonly pendingPromises: Set<Promise<void>> = new Set()
  private readonly deltaBuffer: RuntimeTurnEvent[] = []
  private inputId: string | null = null
  private turnId: string | null = null
  private closed = false
  private inputAccepted = false
  private inputRejected = false

  constructor(options: WorkflowAgentSessionReporterOptions) {
    this.outbox = options.outbox
    this.projectId = options.projectId
    this.workflowRunId = options.workflowRunId
    this.sessionName = options.sessionName
    this.workMetadata = options.workMetadata
    this.randomId = options.randomId
  }

  async awaitInput(prompt: string, runtimeSessionId: string, identity?: { inputId?: string | null; turnId?: string | null }): Promise<void> {
    if (this.closed) return
    this.inputId = identity?.inputId ?? null
    this.turnId = identity?.turnId ?? null
    const record = this.buildRecord(runtimeSessionId, {
      type: "session.input",
      payload: {
        text: prompt,
        kind: "task",
        source: "workflow",
        role: "user",
        runtimeSessionId,
        ...(identity?.inputId ? { inputId: identity.inputId } : {}),
        ...(identity?.turnId ? { turnId: identity.turnId } : {}),
      },
    }, this.inputId ? `workflow-input-event-${this.inputId}` : null)
    const promise = this.outbox.enqueueBeforeExecution(record)
      .then(() => {
        this.inputAccepted = true
      })
      .catch((error) => {
        if (this.inputRejected) return
        this.inputRejected = true
        log.error("workflow agent-session input enqueue failed", {
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
    const promise = this.outbox.enqueueProducedFact(record)
      .catch((error) => {
        log.error("workflow agent-session event enqueue failed", {
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
    promise.then(() => this.pendingPromises.delete(promise), () => this.pendingPromises.delete(promise))
  }

  private flushDeltaBuffer(): void {
    if (this.deltaBuffer.length === 0) return
    const buffered = this.deltaBuffer.splice(0)
    const records = buffered.map((event) => this.buildRecord(event.runtimeSessionId, {
      type: event.type,
      payload: event.payload,
    }))
    const promise = this.outbox.enqueueProducedFactBatch(records)
      .catch((error) => {
        log.error("workflow agent-session delta batch enqueue failed", {
          run: this.workflowRunId,
          work: this.workMetadata.workId,
          session: this.sessionName,
          reason: `count=${records.length}`,
          exception: error,
        })
        throw error
      })
    this.pendingPromises.add(promise)
    promise.then(() => this.pendingPromises.delete(promise), () => this.pendingPromises.delete(promise))
  }

  registerClose(payload: {
    readonly status: "completed" | "failed" | "cancelled"
    readonly exitCode: number
    readonly failureReason?: string | null
    readonly runtimeSessionId: string
    readonly turnId?: string | null
  }): void {
    if (this.closed) return
    if (this.inputRejected) return
    // Flush buffered deltas before the activity fact so the outbox preserves turn order.
    this.flushDeltaBuffer()
    const records: RuntimeEventRecord[] = []
    if (payload.status === "failed") records.push(this.buildRecord(payload.runtimeSessionId, {
      type: "turn.failed",
      payload: { status: payload.status, exitCode: payload.exitCode, ...(payload.failureReason ? { failureReason: payload.failureReason } : {}), ...(payload.turnId ? { turnId: payload.turnId } : {}) },
    }, this.turnId ? `workflow-turn-failed-event-${this.turnId}` : null))
    records.push(this.buildRecord(payload.runtimeSessionId, {
      type: "session.activity",
      payload: { activity: "idle", status: payload.status, exitCode: payload.exitCode, ...(payload.failureReason ? { failureReason: payload.failureReason } : {}), ...(payload.turnId ? { turnId: payload.turnId } : {}), runtimeSessionId: payload.runtimeSessionId, observedAt: new Date().toISOString() },
    }, this.turnId ? `workflow-turn-terminal-event-${this.turnId}` : null))
    const promise = this.outbox.enqueueProducedFactBatch(records)
      .catch((error) => {
        log.error("workflow agent-session close enqueue failed", {
          run: this.workflowRunId,
          work: this.workMetadata.workId,
          session: this.sessionName,
          exception: error,
        })
        throw error
      })
    this.pendingPromises.add(promise)
    promise.then(() => this.pendingPromises.delete(promise), () => this.pendingPromises.delete(promise))
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

  private buildRecord(runtimeSessionId: string, event: { type: string; payload: Record<string, unknown> }, stableId: string | null = null): RuntimeEventRecord {
    return {
      id: stableId ?? this.randomId(),
      producerFamily: "workflow-session",
      target: {
        kind: "workflow",
        projectId: this.projectId,
        workflowRunId: this.workflowRunId,
        sessionName: this.sessionName,
      },
      runtimeSessionId,
      work: {
        workId: this.workMetadata.workId,
        workType: this.workMetadata.workType,
        stage: this.workMetadata.stage ?? null,
      },
      event,
      acknowledgementPolicy: "matching-receipt",
    }
  }
}
