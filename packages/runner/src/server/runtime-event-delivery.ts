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
