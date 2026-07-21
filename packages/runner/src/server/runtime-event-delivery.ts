// Real delivery adapter for `AgentSessionRuntimeEventOutbox`. It maps a
// `RuntimeEventRecord` to the corresponding `ServerConnection` method
// based on the record's `producerFamily` + `target.kind`. The Network
// surface is unchanged from issue 410 / pre-issue-461; both the
// Workflow and generic endpoints already return
// `AgentSessionRuntimeEventReceipt[]`.

import type { ServerConnection, AgentSessionRuntimeEventReceipt } from "./connection.js"
import type { RuntimeEventDelivery, RuntimeEventRecord } from "./runtime-event-outbox.js"

export interface RuntimeEventDeliveryOptions {
  readonly connection: ServerConnection
}

export function createServerRuntimeEventDelivery(options: RuntimeEventDeliveryOptions): RuntimeEventDelivery {
  const { connection } = options
  return {
    async send(record: RuntimeEventRecord, signal: AbortSignal): Promise<AgentSessionRuntimeEventReceipt[]> {
      if (record.producerFamily === "workflow-session" && record.target.kind === "workflow") {
        const accepted = await connection.workflowAgentSessionRuntimeEvents(
          record.target.projectId,
          record.target.workflowRunId,
          record.target.sessionName,
          envelope(record),
          signal,
        )
        return accepted.map<AgentSessionRuntimeEventReceipt>((a) => ({ type: a.type ?? "" }))
      }
      if (record.producerFamily === "generic-followup" && record.target.kind === "generic") {
        return await connection.agentSessionRuntimeEvents(
          record.target.projectId,
          record.target.sessionId,
          envelope(record),
          signal,
        )
      }
      throw new Error("runtime-event delivery: target does not match producer family")
    },
    async sendBatch(records: readonly RuntimeEventRecord[], signal: AbortSignal): Promise<AgentSessionRuntimeEventReceipt[][]> {
      if (records.length === 0) return []
      const head = records[0]
      if (head.producerFamily === "workflow-session" && head.target.kind === "workflow") {
        const workflowRecords = records as readonly (RuntimeEventRecord & { target: { kind: "workflow"; projectId: string; workflowRunId: string; sessionName: string } })[]
        const accepted = await connection.workflowAgentSessionRuntimeEvents(
          head.target.projectId,
          head.target.workflowRunId,
          head.target.sessionName,
          batchEnvelope(workflowRecords),
          signal,
        )
        // The server returns one receipt per submitted event, in order.
        // Preserve that order so the outbox can settle each record against
        // its own acknowledgement policy by position.
        return accepted.map<AgentSessionRuntimeEventReceipt[]>((a) => [{ type: a.type ?? "" }])
      }
      if (head.producerFamily === "generic-followup" && head.target.kind === "generic") {
        const genericRecords = records as readonly (RuntimeEventRecord & { target: { kind: "generic"; projectId: string; sessionId: string } })[]
        // The generic endpoint returns receipts per record directly.
        const accepted = await connection.agentSessionRuntimeEvents(
          head.target.projectId,
          head.target.sessionId,
          batchEnvelope(genericRecords),
          signal,
        )
        return accepted.map<AgentSessionRuntimeEventReceipt[]>((a) => [{ type: a.type ?? "" }])
      }
      throw new Error("runtime-event delivery: target does not match producer family")
    },
  }
}

function envelope(record: RuntimeEventRecord) {
  const work = record.work
  return {
    workId: work?.workId ?? null,
    workType: work?.workType ?? null,
    stage: work?.stage ?? null,
    runtimeSessionId: record.runtimeSessionId,
    runtimeEvents: [{ type: record.event.type, payload: record.event.payload }],
  }
}

function batchEnvelope(records: readonly RuntimeEventRecord[]) {
  const head = records[0]
  const work = head.work
  return {
    workId: work?.workId ?? null,
    workType: work?.workType ?? null,
    stage: work?.stage ?? null,
    runtimeSessionId: head.runtimeSessionId,
    runtimeEvents: records.map((record) => ({ type: record.event.type, payload: record.event.payload })),
  }
}
