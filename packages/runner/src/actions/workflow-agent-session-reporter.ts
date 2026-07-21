import { errorMessage } from "../core/errors.js"
import type { RuntimeTurnEvent } from "../runtime/opencode/index.js"
import type { AgentSessionRuntimeEventOutbox, RuntimeEventRecord } from "../server/runtime-event-outbox.js"

export interface WorkflowAgentSessionWorkMetadata {
  readonly workId: string
  readonly workType: string
  readonly stage?: string | null
}

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

  async awaitInput(prompt: string, runtimeSessionId: string): Promise<void> {
    if (this.closed) return
    const record = this.buildRecord(runtimeSessionId, {
      type: "session.input",
      payload: {
        text: prompt,
        kind: "task",
        source: "workflow",
        role: "user",
        runtimeSessionId,
      },
    })
    const promise = this.outbox.enqueueBeforeExecution(record)
      .then(() => {
        this.inputAccepted = true
      })
      .catch((error) => {
        if (this.inputRejected) return
        this.inputRejected = true
        console.error(
          `workflow agent-session input enqueue failed for workflow=${this.workflowRunId} work=${this.workMetadata.workId} session=${this.sessionName}: ${errorMessage(error)}`,
        )
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
    const record = this.buildRecord(event.runtimeSessionId, {
      type: event.type,
      payload: event.payload,
    })
    const promise = this.outbox.enqueueProducedFact(record)
      .catch((error) => {
        console.error(
          `workflow agent-session event enqueue failed for workflow=${this.workflowRunId} work=${this.workMetadata.workId} session=${this.sessionName} type=${event.type}: ${errorMessage(error)}`,
        )
        // Surface as a settled rejection so `settle()` can observe it; do not
        // rethrow synchronously (would crash the synchronous observer).
        throw error
      })
    this.pendingPromises.add(promise)
    promise.then(() => this.pendingPromises.delete(promise), () => this.pendingPromises.delete(promise))
  }

  registerClose(payload: {
    readonly status: "completed" | "failed"
    readonly exitCode: number
    readonly failureReason?: string | null
    readonly runtimeSessionId: string
  }): void {
    if (this.closed) return
    if (this.inputRejected) return
    const eventRecord = this.buildRecord(payload.runtimeSessionId, {
      type: "session.closed",
      payload: {
        status: payload.status,
        exitCode: payload.exitCode,
        ...(payload.failureReason ? { failureReason: payload.failureReason } : {}),
        runtimeSessionId: payload.runtimeSessionId,
      },
    })
    const promise = this.outbox.enqueueProducedFact(eventRecord)
      .catch((error) => {
        console.error(
          `workflow agent-session close enqueue failed for workflow=${this.workflowRunId} work=${this.workMetadata.workId} session=${this.sessionName}: ${errorMessage(error)}`,
        )
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
    const promises = [...this.pendingPromises]
    if (promises.length === 0) return
    await Promise.allSettled(promises)
    this.pendingPromises.clear()
  }

  private buildRecord(runtimeSessionId: string, event: { type: string; payload: Record<string, unknown> }): RuntimeEventRecord {
    return {
      id: this.randomId(),
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
