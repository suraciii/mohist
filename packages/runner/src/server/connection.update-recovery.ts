import type { PendingUpdateOperation } from '../runtime/update-operation.js'

type Fetcher = (input: string, init: RequestInit) => Promise<Response>

export async function fetchPendingUpdateOperation(
  fetcher: Fetcher,
  url: (path: string) => string,
  signal: AbortSignal,
): Promise<PendingUpdateOperation | null> {
  const response = await fetcher(url('update-operation/pending'), {
    method: 'GET',
    signal,
  })
  if (!response.ok) throw new Error(`pending update operation failed: ${response.status} ${await response.text()}`)
  const payload = (await response.json()) as unknown
  const operation = readRecord(payload)?.operation ?? readRecord(readRecord(payload)?.data)?.operation
  return operation && isRecord(operation) ? parsePendingUpdateOperation(operation) : null
}

export interface RecoveryStopFailure {
  readonly runnerId: string
  readonly ownerKind: 'workflow' | 'agent-job'
  readonly ownerId: string
  readonly workId: string
  readonly taskRunId?: string | null
  readonly operationId: string
  readonly message: string
}

export async function reportRecoveryStopFailure(
  fetcher: Fetcher,
  url: (path: string) => string,
  failure: RecoveryStopFailure,
  signal: AbortSignal,
): Promise<void> {
  const response = await fetcher(url('recovery-stop-failure'), {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(failure),
    signal,
  })
  if (!response.ok) throw new Error(`recovery stop failure failed: ${response.status} ${await response.text()}`)
}

function parsePendingUpdateOperation(value: Record<string, unknown>): PendingUpdateOperation {
  const operationId = readString(value, 'operationId')
  const createdAt = readString(value, 'createdAt')
  const affectedWorks = value.affectedWorks
  if (!operationId || !createdAt || !Array.isArray(affectedWorks))
    throw new Error('pending update operation returned a malformed response')
  return {
    operationId,
    createdAt,
    ...(typeof value.runnerId === 'string' ? { runnerId: value.runnerId } : {}),
    affectedWorks: affectedWorks.map((item) => {
      if (!isRecord(item)) throw new Error('pending update operation returned a malformed work')
      const ownerKind = readString(item, 'ownerKind')
      const ownerId = readString(item, 'ownerId')
      const workId = readString(item, 'workId')
      const workType = readString(item, 'workType')
      if (!ownerKind || !ownerId || !workId || !workType)
        throw new Error('pending update operation returned a malformed work')
      return {
        ownerKind,
        ownerId,
        workId,
        workType,
        ...(typeof item.taskRunId === 'string' ? { taskRunId: item.taskRunId } : {}),
        ...(typeof item.status === 'string' ? { status: item.status } : {}),
      }
    }),
  }
}

function readRecord(value: unknown): Record<string, unknown> | null {
  return isRecord(value) ? value : null
}

function readString(value: Record<string, unknown>, key: string): string | null {
  return typeof value[key] === 'string' ? (value[key] as string) : null
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}
