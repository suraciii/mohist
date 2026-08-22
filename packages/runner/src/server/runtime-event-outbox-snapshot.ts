// Snapshot serialization and validation extracted from the outbox to keep
// the main module within the file-size ratchet. The snapshot shape is the
// durable contract of `.mohist/runner-state/runtime-events.json`.
import type {
  RuntimeEventEntry,
  RuntimeEventRecord,
  RuntimeEventTarget,
  RuntimeEventWorkMetadata,
} from './runtime-event-outbox-ports.js'
import type { InternalRecord } from './runtime-event-outbox.js'

export const RUNTIME_EVENT_OUTBOX_FILE = '.mohist/runner-state/runtime-events.json'
export const RUNTIME_EVENT_OUTBOX_VERSION = 1

interface SnapshotShape {
  version: number
  entries: InternalRecord[]
}

export function serializeSnapshot(entries: InternalRecord[]): string {
  const snapshot: SnapshotShape = { version: RUNTIME_EVENT_OUTBOX_VERSION, entries: entries.map(cloneInternal) }
  return JSON.stringify(snapshot, null, 2)
}

export function parseSnapshot(raw: string): SnapshotShape | null {
  try {
    const value = JSON.parse(raw) as unknown
    if (
      !isPlainObject(value) ||
      value['version'] !== RUNTIME_EVENT_OUTBOX_VERSION ||
      !Array.isArray(value['entries'])
    ) {
      return null
    }
    const entries: InternalRecord[] = []
    for (const item of value['entries']) {
      const parsed = parseInternalRecord(item)
      if (!parsed) return null
      entries.push(parsed)
    }
    return { version: RUNTIME_EVENT_OUTBOX_VERSION, entries }
  } catch {
    return null
  }
}

function parseInternalRecord(value: unknown): InternalRecord | null {
  if (!isPlainObject(value)) return null
  const id = value['id']
  const target = value['target']
  const family = value['producerFamily']
  const runtimeSessionId = value['runtimeSessionId']
  const runtime = value['runtime']
  const sessionTurnId = value['sessionTurnId']
  const event = value['event']
  const policy = value['acknowledgementPolicy']
  const work = value['work'] ?? null
  const sequence = value['sequence']
  const enqueuedAt = value['enqueuedAt']
  if (
    typeof id !== 'string' ||
    !isRuntimeTarget(target) ||
    (family !== 'workflow-session' &&
      family !== 'workflow-cleanup' &&
      family !== 'session-followup' &&
      family !== 'generic-followup' &&
      family !== 'binding-reconcile') ||
    typeof runtimeSessionId !== 'string' ||
    (runtime !== undefined && runtime !== null && typeof runtime !== 'string') ||
    (sessionTurnId !== undefined && sessionTurnId !== null && typeof sessionTurnId !== 'string') ||
    !isRuntimeEvent(event) ||
    (policy !== 'matching-receipt' && policy !== 'successful-response') ||
    typeof sequence !== 'number' ||
    typeof enqueuedAt !== 'string'
  ) {
    return null
  }
  if (work !== null && !isRuntimeWorkMetadata(work)) return null
  return {
    id,
    producerFamily: family,
    target,
    runtimeSessionId,
    runtime: typeof runtime === 'string' ? runtime : null,
    sessionTurnId: typeof sessionTurnId === 'string' ? sessionTurnId : null,
    work,
    event,
    acknowledgementPolicy: policy,
    sequence,
    enqueuedAt,
  }
}

export function stripInternal(record: InternalRecord): RuntimeEventRecord {
  return {
    id: record.id,
    producerFamily: record.producerFamily,
    target: record.target,
    runtimeSessionId: record.runtimeSessionId,
    runtime: record.runtime ?? null,
    sessionTurnId: record.sessionTurnId ?? null,
    work: record.work,
    event: record.event,
    acknowledgementPolicy: record.acknowledgementPolicy,
  }
}

function isPlainObject(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
}

function isRuntimeTarget(value: unknown): value is RuntimeEventTarget {
  if (!isPlainObject(value)) return false
  if (value['kind'] === 'workflow') {
    return (
      typeof value['projectId'] === 'string' &&
      typeof value['workflowRunId'] === 'string' &&
      typeof value['sessionName'] === 'string'
    )
  }
  if (value['kind'] === 'generic') {
    return typeof value['projectId'] === 'string' && typeof value['sessionId'] === 'string'
  }
  if (value['kind'] === 'session') return typeof value['sessionId'] === 'string'
  return false
}

function isRuntimeEvent(value: unknown): value is RuntimeEventEntry {
  if (!isPlainObject(value)) return false
  const type = value['type']
  const payload = value['payload']
  return typeof type === 'string' && isPlainObject(payload)
}

function isRuntimeWorkMetadata(value: unknown): value is RuntimeEventWorkMetadata {
  if (!isPlainObject(value)) return false
  return (
    typeof value['workId'] === 'string' &&
    typeof value['workType'] === 'string' &&
    (value['stage'] === null || typeof value['stage'] === 'string') &&
    (value['taskRunId'] === undefined || value['taskRunId'] === null || typeof value['taskRunId'] === 'string') &&
    (value['runnerId'] === undefined || value['runnerId'] === null || typeof value['runnerId'] === 'string') &&
    (value['agentSessionId'] === undefined ||
      value['agentSessionId'] === null ||
      typeof value['agentSessionId'] === 'string') &&
    (value['inputDeliveryId'] === undefined ||
      value['inputDeliveryId'] === null ||
      typeof value['inputDeliveryId'] === 'string') &&
    (value['agentTurnId'] === undefined || value['agentTurnId'] === null || typeof value['agentTurnId'] === 'string')
  )
}

function cloneInternal(record: InternalRecord): InternalRecord {
  return {
    id: record.id,
    producerFamily: record.producerFamily,
    target: { ...record.target },
    runtimeSessionId: record.runtimeSessionId,
    runtime: record.runtime ?? null,
    sessionTurnId: record.sessionTurnId ?? null,
    work: record.work ? { ...record.work } : null,
    event: {
      type: record.event.type,
      payload: { ...record.event.payload },
    },
    acknowledgementPolicy: record.acknowledgementPolicy,
    sequence: record.sequence,
    enqueuedAt: record.enqueuedAt,
  }
}
