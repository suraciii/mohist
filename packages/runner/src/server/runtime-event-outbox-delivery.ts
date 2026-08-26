import type { AgentSessionRuntimeEventReceipt } from './connection.js'
import { runtimeEventSchedulingKey } from './runtime-event-outbox-identity.js'
import type { RuntimeEventAcknowledgementPolicy, RuntimeEventRecord } from './runtime-event-outbox-ports.js'

export interface InternalRecord extends RuntimeEventRecord {
  readonly sequence: number
  readonly enqueuedAt: string
}

interface GroupSnapshot {
  readonly label: string
  readonly records: InternalRecord[]
}

export function collectGroups(records: InternalRecord[]): GroupSnapshot[] {
  const groups = new Map<string, InternalRecord[]>()
  for (const record of records) {
    const label = runtimeEventSchedulingKey(record)
    const list = groups.get(label)
    if (list) list.push(record)
    else groups.set(label, [record])
  }
  return [...groups.entries()].map(([label, list]) => ({
    label,
    records: list.sort(sortBySequence),
  }))
}

export function selectDeliveryGroups(
  groups: GroupSnapshot[],
  limit: number,
  nextLabel: string | null,
): { groups: GroupSnapshot[]; nextLabel: string | null } {
  if (groups.length === 0) return { groups: [], nextLabel: null }

  const start =
    nextLabel === null
      ? 0
      : Math.max(
          0,
          groups.findIndex((group) => group.label === nextLabel),
        )
  const count = Math.min(limit, groups.length)
  const selected = Array.from({ length: count }, (_, offset) => groups[(start + offset) % groups.length])
  return {
    groups: selected,
    nextLabel: groups[(start + count) % groups.length]?.label ?? null,
  }
}

export function matchingReceipt(
  policy: RuntimeEventAcknowledgementPolicy,
  record: RuntimeEventRecord,
  receipts: AgentSessionRuntimeEventReceipt[],
): AgentSessionRuntimeEventReceipt | null {
  if (policy === 'successful-response') return receipts[0] ?? { type: record.event.type }
  const matching = receipts.find((entry) => entry.type === record.event.type)
  if (!matching) return null
  if (record.producerFamily === 'workflow-session' && record.event.type === 'session.input' && record.work?.taskRunId) {
    return matching.inputDeliveryId === record.id &&
      matching.agentSessionId === record.work.agentSessionId &&
      typeof matching.agentTurnId === 'string' &&
      matching.agentTurnId.length > 0
      ? matching
      : null
  }
  if (record.producerFamily === 'workflow-cleanup' && record.event.type === 'session.cleanup') {
    const cleanupOperationId = record.event.payload.cleanupOperationId
    const inputDeliveryId = record.event.payload.inputDeliveryId
    const agentTurnId = record.event.payload.turnId
    return typeof cleanupOperationId === 'string' &&
      typeof inputDeliveryId === 'string' &&
      typeof agentTurnId === 'string' &&
      matching.cleanupOperationId === cleanupOperationId &&
      matching.inputDeliveryId === inputDeliveryId &&
      matching.agentSessionId === record.work?.agentSessionId &&
      matching.agentTurnId === agentTurnId
      ? matching
      : null
  }
  return matching
}

export function requiresInputReceipt(record: RuntimeEventRecord): boolean {
  return (
    record.event.type === 'session.input' ||
    (record.producerFamily === 'workflow-cleanup' && record.event.type === 'session.cleanup')
  )
}

export function isConfirmedConsumedRecord(record: RuntimeEventRecord): boolean {
  return record.acknowledgementPolicy === 'matching-receipt' && requiresInputReceipt(record)
}

export function sortBySequence(a: InternalRecord, b: InternalRecord): number {
  return a.sequence - b.sequence
}

export async function defaultDelivery(
  _record: RuntimeEventRecord,
  _signal: AbortSignal,
): Promise<AgentSessionRuntimeEventReceipt[]> {
  throw new Error('Runtime event outbox has no delivery implementation; inject one via options.deliver')
}
