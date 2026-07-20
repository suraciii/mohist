import { errorMessage } from "../core/errors.js"
import type { ServerConnection } from "../server/connection.js"
import type { RuntimeTurnEvent } from "../runtime/opencode/index.js"

const DEFAULT_REPORT_TIMEOUT_MS = 30_000

export interface WorkflowAgentSessionWorkMetadata {
  readonly workId: string
  readonly workType: string
  readonly stage?: string | null
}

export interface WorkflowAgentSessionReporterOptions {
  readonly connection: ServerConnection
  readonly projectId: string
  readonly workflowRunId: string
  readonly sessionName: string
  readonly workMetadata: WorkflowAgentSessionWorkMetadata
  readonly signal: AbortSignal
  readonly timeoutMs?: number
}

/**
 * Turn-scoped, Workflow-local event reporter that uploads the
 * composed prompt and the runtime's normalized events to the
 * Workflow AgentSession endpoint, in production order, with one
 * serialized promise chain.
 *
 * Best-effort by design: a single failed upload is logged once
 * and the chain keeps draining. When the initial `session.input`
 * upload is rejected the reporter suppresses later activity and
 * close reports for that turn so orphan events cannot attach to
 * a previously persisted turn.
 *
 * Reporting cancellation is independent of the runner host signal
 * (`options.signal` is the caller's abort source, not the runtime
 * signal). A bounded timer guarantees stalled reporting cannot
 * retain the Action indefinitely.
 */
export class WorkflowAgentSessionReporter {
  private readonly connection: ServerConnection
  private readonly projectId: string
  private readonly workflowRunId: string
  private readonly sessionName: string
  private readonly workMetadata: WorkflowAgentSessionWorkMetadata
  private readonly timeoutMs: number
  private readonly controller: AbortController
  private timeoutHandle: ReturnType<typeof setTimeout> | null
  private tail: Promise<void>
  private inputAccepted = false
  private inputRejected = false
  private inputDecided = false
  private closed = false

  constructor(options: WorkflowAgentSessionReporterOptions) {
    this.connection = options.connection
    this.projectId = options.projectId
    this.workflowRunId = options.workflowRunId
    this.sessionName = options.sessionName
    this.workMetadata = options.workMetadata
    this.timeoutMs = options.timeoutMs ?? DEFAULT_REPORT_TIMEOUT_MS
    this.controller = new AbortController()
    this.timeoutHandle = setTimeout(() => this.controller.abort(), this.timeoutMs)
    if (this.timeoutHandle && typeof this.timeoutHandle.unref === "function") this.timeoutHandle.unref()
    this.tail = Promise.resolve()
  }

  enqueueInput(prompt: string, runtimeSessionId: string): void {
    if (this.closed) return
    this.tail = this.tail.then(() => this.uploadInput(prompt, runtimeSessionId))
  }

  enqueueEvent(event: RuntimeTurnEvent): void {
    if (this.closed) return
    this.tail = this.tail.then(() => this.maybeUploadEvent(event))
  }

  enqueueClose(payload: WorkflowAgentSessionClosePayload): void {
    if (this.closed) return
    this.tail = this.tail.then(() => this.maybeUploadClose(payload))
  }

  inputWasAccepted(): boolean {
    return this.inputAccepted
  }

  async settle(): Promise<void> {
    try {
      await this.tail
    } finally {
      this.disposeTimeout()
      this.closed = true
    }
  }

  private async uploadInput(prompt: string, runtimeSessionId: string): Promise<void> {
    try {
      await this.connection.workflowAgentSessionRuntimeEvents(
        this.projectId,
        this.workflowRunId,
        this.sessionName,
        this.envelope({
          runtimeSessionId,
          runtimeEvents: [{
            type: "session.input",
            payload: {
              text: prompt,
              kind: "task",
              source: "workflow",
              role: "user",
              runtimeSessionId,
            },
          }],
        }),
        this.controller.signal,
      )
      if (!this.inputDecided) {
        this.inputAccepted = true
        this.inputDecided = true
      }
    } catch (error) {
      if (!this.inputDecided) {
        this.inputRejected = true
        this.inputDecided = true
      }
      logReporterFailure(this.workflowRunId, this.workMetadata.workId, this.sessionName, "session.input", error)
    }
  }

  private async maybeUploadEvent(event: RuntimeTurnEvent): Promise<void> {
    if (this.inputRejected) return
    try {
      await this.connection.workflowAgentSessionRuntimeEvents(
        this.projectId,
        this.workflowRunId,
        this.sessionName,
        this.envelope({
          runtimeSessionId: event.runtimeSessionId,
          runtimeEvents: [{ type: event.type, payload: event.payload }],
        }),
        this.controller.signal,
      )
    } catch (error) {
      logReporterFailure(this.workflowRunId, this.workMetadata.workId, this.sessionName, event.type, error)
    }
  }

  private async maybeUploadClose(payload: WorkflowAgentSessionClosePayload): Promise<void> {
    if (this.inputRejected) return
    try {
      await this.connection.workflowAgentSessionRuntimeEvents(
        this.projectId,
        this.workflowRunId,
        this.sessionName,
        this.envelope({
          runtimeSessionId: payload.runtimeSessionId,
          runtimeEvents: [{
            type: "session.closed",
            payload: {
              status: payload.status,
              exitCode: payload.exitCode,
              ...(payload.failureReason ? { failureReason: payload.failureReason } : {}),
              runtimeSessionId: payload.runtimeSessionId,
            },
          }],
        }),
        this.controller.signal,
      )
    } catch (error) {
      logReporterFailure(this.workflowRunId, this.workMetadata.workId, this.sessionName, "session.closed", error)
    }
  }

  private envelope(body: {
    runtimeSessionId: string
    runtimeEvents: ReadonlyArray<{ type: string; payload: Record<string, unknown> }>
  }) {
    return {
      workId: this.workMetadata.workId,
      workType: this.workMetadata.workType,
      stage: this.workMetadata.stage ?? null,
      runtimeSessionId: body.runtimeSessionId,
      runtimeEvents: body.runtimeEvents,
    }
  }

  private disposeTimeout(): void {
    if (this.timeoutHandle !== null) {
      clearTimeout(this.timeoutHandle)
      this.timeoutHandle = null
    }
  }
}

export interface WorkflowAgentSessionClosePayload {
  readonly status: "completed" | "failed"
  readonly exitCode: number
  readonly failureReason?: string | null
  readonly runtimeSessionId: string
}

function logReporterFailure(
  workflowRunId: string,
  workId: string,
  sessionName: string,
  eventType: string,
  error: unknown,
): void {
  console.error(
    `workflow agent-session event upload failed for workflow=${workflowRunId} work=${workId} session=${sessionName} type=${eventType}: ${errorMessage(error)}`,
  )
}