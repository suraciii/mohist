/**
 * Test helper that mimics the previous `makeFakeConnection` shape but
 * is backed by the new outbox-based delivery. Tests that previously
 * asserted on `connection.eventCalls` / `connection.setEventRejection`
 * inspect the recorded `RuntimeEventRecord[]` instead. The helper also
 * implements the small slice of `ServerConnection` used by
 * `opencodeAction` so the action's `openWorkflowAgentSession` /
 * `attachWorkflowAgentSession` still work.
 */
import { vi } from "vitest"
import type { ServerConnection, AgentSessionRuntimeEventReceipt } from "../../src/server/connection.js"
import type {
  AgentSessionRuntimeEventOutbox,
  RuntimeEventRecord,
  RuntimeEventOutboxFileSystem,
} from "../../src/server/runtime-event-outbox.js"

export interface OutboxHandles {
  /** Outbox-shaped object accepted by `ActionContext.agentSessionRuntimeEventOutbox`. */
  outbox: AgentSessionRuntimeEventOutbox
  /** Local capture of every record enqueued in production order. */
  records: RuntimeEventRecord[]
  /** Convenience: `records` filtered to a particular event type. */
  eventsByType(type: string): RuntimeEventRecord[]
  /** Convenience: maps `record.event.type → record.event.payload` in order. */
  eventTypeList(): string[]
  /** Fake `ServerConnection` still wired to `open`/`attach` so the action runs. */
  connection: ServerConnection
  /** Test seams: mark `session.input` and other types as rejected for the head. */
  setInputAccepted(accepted: boolean): void
  setEventRejection(types: ReadonlySet<string>): void
}

interface FakeOutboxOptions {
  fileSystem?: RuntimeEventOutboxFileSystem
}

export function makeRecordingOutbox(options: FakeOutboxOptions = {}): OutboxHandles {
  const records: RuntimeEventRecord[] = []
  let inputAccepted = true
  let rejectTypes: ReadonlySet<string> = new Set()
  const fileSystem = options.fileSystem
  const outbox: AgentSessionRuntimeEventOutbox = {
    ready: () => true,
    async load() {},
    async recover() {},
    async enqueueBeforeExecution(record) {
      const internal: RuntimeEventRecord = { ...record }
      records.push(internal)
      if (fileSystem) await fileSystem.writeAtomicText("outbox", JSON.stringify({ version: 1, entries: records }))
    },
    async awaitInputReceipt(recordId) {
      if (!inputAccepted) throw new Error("session.input was not acknowledged")
      return { type: "session.input", inputDeliveryId: recordId, agentTurnId: `turn-${recordId}` }
    },
    async enqueueProducedFact(record) {
      const internal: RuntimeEventRecord = { ...record }
      records.push(internal)
      if (fileSystem) await fileSystem.writeAtomicText("outbox", JSON.stringify({ version: 1, entries: records }))
    },
    async enqueueProducedFactBatch(batch) {
      for (const record of batch) {
        const internal: RuntimeEventRecord = { ...record }
        records.push(internal)
      }
      if (fileSystem) await fileSystem.writeAtomicText("outbox", JSON.stringify({ version: 1, entries: records }))
    },
    async kick() {},
    async stop() {},
    snapshot() {
      return [...records]
    },
  }
  // The fake connection still exposes `workflowAgentSessionRuntimeEvents` so any
  // legacy test stub wanting to drive retry behavior can plug into it. For the
  // outbox-driven path the helper emits receipts that match the recorded type
  // when acceptance is enabled and an empty array otherwise.
  const connection: ServerConnection = {
    async openWorkflowAgentSession() {
      return { runtimeSessionId: "ses_bound", workDir: "/tmp/work" }
    },
    async attachWorkflowAgentSession() {},
    async workflowAgentSessionRuntimeEvents(_projectId: string, _workflowRunId: string, _sessionName: string, body: unknown): Promise<AgentSessionRuntimeEventReceipt[]> {
      const runtimeEvents = (body as { runtimeEvents: Array<{ type: string }> }).runtimeEvents ?? []
      if (runtimeEvents.some((event) => rejectTypes.has(event.type))) {
        throw new Error(`rejected: ${runtimeEvents[0]?.type ?? "?"}`)
      }
      if (!inputAccepted && runtimeEvents.some((event) => event.type === "session.input")) return []
      return runtimeEvents.map((event) => ({ type: event.type }))
    },
  } as unknown as ServerConnection
  return {
    outbox,
    records,
    eventsByType: (type) => records.filter((r) => r.event.type === type),
    eventTypeList: () => records.map((r) => r.event.type),
    connection,
    setInputAccepted(accepted) {
      inputAccepted = accepted
    },
    setEventRejection(types) {
      rejectTypes = types
    },
  }
}

export function viFn() {
  return vi.fn()
}
