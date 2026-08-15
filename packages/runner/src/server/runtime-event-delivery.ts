// Real delivery adapter for `AgentSessionRuntimeEventOutbox`. It maps a
// `RuntimeEventRecord` to the corresponding `ServerConnection` method
// based on the record's `producerFamily` + `target.kind`. Both the
// Workflow and generic endpoints already return
// `AgentSessionRuntimeEventReceipt[]`.

import type { ServerConnection, AgentSessionRuntimeEventReceipt } from './connection.js'
import { runtimeEventDeliveryKey, type RuntimeEventDelivery, type RuntimeEventRecord } from './runtime-event-outbox.js'

export interface RuntimeEventDeliveryOptions {
  readonly connection: ServerConnection
}

export function createServerRuntimeEventDelivery(options: RuntimeEventDeliveryOptions): RuntimeEventDelivery {
  const { connection } = options
  const deliverWorkflowCleanup = async (
    record: RuntimeEventRecord,
    signal: AbortSignal,
  ): Promise<AgentSessionRuntimeEventReceipt[]> => {
    if (record.target.kind !== 'workflow') throw new Error('workflow cleanup delivery requires a Workflow target')
    const target = record.target
    const work = record.work
    const prompt = record.event.payload.text
    const operationId = record.event.payload.cleanupOperationId
    if (
      !work ||
      typeof prompt !== 'string' ||
      prompt.length === 0 ||
      typeof operationId !== 'string' ||
      operationId !== record.id ||
      !work.taskRunId ||
      !work.workId ||
      !work.agentSessionId ||
      !record.runtime ||
      !record.runtimeSessionId
    ) {
      throw new Error('workflow cleanup delivery requires its complete immutable execution identity')
    }
    const accepted = await connection.workflowAgentSessionCleanupTurn(
      target.projectId,
      target.workflowRunId,
      target.sessionName,
      {
        cleanupOperationId: operationId,
        prompt,
        taskRunId: work.taskRunId,
        workId: work.workId,
        agentSessionId: work.agentSessionId,
        runtime: record.runtime,
        runtimeSessionId: record.runtimeSessionId,
      },
      signal,
    )
    return [
      {
        type: 'session.cleanup',
        inputDeliveryId: accepted.inputDeliveryId,
        agentTurnId: accepted.agentTurnId,
        agentSessionId: accepted.agentSessionId,
      },
    ]
  }
  return {
    async send(record: RuntimeEventRecord, signal: AbortSignal): Promise<AgentSessionRuntimeEventReceipt[]> {
      if (record.producerFamily === 'workflow-session' && record.target.kind === 'workflow') {
        const accepted = await connection.workflowAgentSessionRuntimeEvents(
          record.target.projectId,
          record.target.workflowRunId,
          record.target.sessionName,
          envelope(record),
          signal,
        )
        return accepted.map<AgentSessionRuntimeEventReceipt>((a) => ({
          type: a.type ?? '',
          inputDeliveryId: a.inputDeliveryId,
          agentTurnId: a.agentTurnId,
          agentSessionId: a.agentSessionId,
        }))
      }
      if (record.producerFamily === 'workflow-cleanup' && record.target.kind === 'workflow') {
        return await deliverWorkflowCleanup(record, signal)
      }
      if (record.producerFamily === 'generic-followup' && record.target.kind === 'generic') {
        return await connection.agentSessionRuntimeEvents(
          record.target.projectId,
          record.target.sessionId,
          envelope(record),
          signal,
        )
      }
      if (record.producerFamily === 'binding-reconcile' && record.target.kind === 'session') {
        return await connection.reconcileAgentSessionRuntimeEvents(record.target.sessionId, envelope(record), signal)
      }
      if (record.producerFamily === 'session-followup' && record.target.kind === 'session') {
        return await connection.reconcileAgentSessionRuntimeEvents(record.target.sessionId, envelope(record), signal)
      }
      throw new Error('runtime-event delivery: target does not match producer family')
    },
    async sendBatch(
      records: readonly RuntimeEventRecord[],
      signal: AbortSignal,
    ): Promise<AgentSessionRuntimeEventReceipt[][]> {
      if (records.length === 0) return []
      assertHomogeneousBatch(records)
      const head = records[0]
      if (head.producerFamily === 'workflow-session' && head.target.kind === 'workflow') {
        const workflowRecords = records as readonly (RuntimeEventRecord & {
          target: { kind: 'workflow'; projectId: string; workflowRunId: string; sessionName: string }
        })[]
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
        return accepted.map<AgentSessionRuntimeEventReceipt[]>((a) => [
          {
            type: a.type ?? '',
            inputDeliveryId: a.inputDeliveryId,
            agentTurnId: a.agentTurnId,
            agentSessionId: a.agentSessionId,
          },
        ])
      }
      if (head.producerFamily === 'workflow-cleanup' && head.target.kind === 'workflow') {
        if (records.length !== 1)
          throw new Error('workflow cleanup delivery must be isolated from other runtime events')
        return [await deliverWorkflowCleanup(head, signal)]
      }
      if (head.producerFamily === 'generic-followup' && head.target.kind === 'generic') {
        const genericRecords = records as readonly (RuntimeEventRecord & {
          target: { kind: 'generic'; projectId: string; sessionId: string }
        })[]
        const accepted = await connection.agentSessionRuntimeEvents(
          head.target.projectId,
          head.target.sessionId,
          batchEnvelope(genericRecords),
          signal,
        )
        return accepted.map<AgentSessionRuntimeEventReceipt[]>((a) => [{ type: a.type ?? '' }])
      }
      if (head.producerFamily === 'binding-reconcile' && head.target.kind === 'session') {
        const accepted = await connection.reconcileAgentSessionRuntimeEvents(
          head.target.sessionId,
          batchEnvelope(records),
          signal,
        )
        return accepted.map<AgentSessionRuntimeEventReceipt[]>((a) => [{ type: a.type ?? '' }])
      }
      if (head.producerFamily === 'session-followup' && head.target.kind === 'session') {
        const accepted = await connection.reconcileAgentSessionRuntimeEvents(
          head.target.sessionId,
          batchEnvelope(records),
          signal,
        )
        return accepted.map<AgentSessionRuntimeEventReceipt[]>((a) => [{ type: a.type ?? '' }])
      }
      throw new Error('runtime-event delivery: target does not match producer family')
    },
  }
}

function envelope(record: RuntimeEventRecord) {
  const work = record.work
  return {
    workId: work?.workId ?? null,
    workType: work?.workType ?? null,
    stage: work?.stage ?? null,
    taskRunId: work?.taskRunId ?? null,
    inputDeliveryId: work?.inputDeliveryId ?? null,
    agentSessionId: work?.agentSessionId ?? null,
    agentTurnId: work?.agentTurnId ?? null,
    ...(record.sessionTurnId
      ? {
          agentSessionId: record.target.kind === 'session' ? record.target.sessionId : null,
          agentTurnId: record.sessionTurnId,
        }
      : {}),
    runtime: record.runtime ?? null,
    runtimeSessionId: record.runtimeSessionId,
    runtimeEvents: [{ type: record.event.type, payload: record.event.payload }],
  }
}

function batchEnvelope(records: readonly RuntimeEventRecord[]) {
  assertHomogeneousBatch(records)
  const head = records[0]
  const work = head.work
  return {
    workId: work?.workId ?? null,
    workType: work?.workType ?? null,
    stage: work?.stage ?? null,
    taskRunId: work?.taskRunId ?? null,
    inputDeliveryId: work?.inputDeliveryId ?? null,
    agentSessionId: work?.agentSessionId ?? null,
    agentTurnId: work?.agentTurnId ?? null,
    ...(head.sessionTurnId
      ? {
          agentSessionId: head.target.kind === 'session' ? head.target.sessionId : null,
          agentTurnId: head.sessionTurnId,
        }
      : {}),
    runtime: head.runtime ?? null,
    runtimeSessionId: head.runtimeSessionId,
    runtimeEvents: records.map((record) => ({ type: record.event.type, payload: record.event.payload })),
  }
}

function assertHomogeneousBatch(records: readonly RuntimeEventRecord[]): void {
  const head = records[0]
  if (!head) return
  const expected = runtimeEventDeliveryKey(head)
  if (records.some((record) => runtimeEventDeliveryKey(record) !== expected))
    throw new Error('runtime-event delivery: mixed execution identity batch')
}
