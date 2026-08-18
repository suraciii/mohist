import type {
  PendingUpdateOperation,
  RecoveryReceiptAcknowledgement,
  RuntimeRecoveryReceipt,
} from '../runtime/recovery-receipt.js'

type Fetcher = (input: string, init: RequestInit) => Promise<Response>

export async function fetchPendingUpdateOperation(
  fetcher: Fetcher,
  url: (path: string) => string,
  signal: AbortSignal,
): Promise<PendingUpdateOperation | null> {
  const response = await fetcher(url('update-operation/pending'), { method: 'GET', signal })
  if (!response.ok) throw new Error(`pending update operation failed: ${response.status} ${await response.text()}`)
  const payload = (await response.json()) as unknown
  const operation = readRecord(payload)?.operation ?? readRecord(readRecord(payload)?.data)?.operation
  return operation && isRecord(operation) ? parsePendingUpdateOperation(operation) : null
}

export async function sendRecoveryReceipt(
  fetcher: Fetcher,
  url: (path: string) => string,
  receipt: RuntimeRecoveryReceipt,
  signal: AbortSignal,
): Promise<RecoveryReceiptAcknowledgement> {
  const response = await fetcher(url('recovery-receipt'), {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(receipt),
    signal,
  })
  const text = await response.text()
  let payload: unknown = null
  if (text) {
    try {
      payload = JSON.parse(text)
    } catch {
      payload = null
    }
  }
  if (!response.ok && response.status !== 409) {
    throw new Error(`recovery receipt failed: ${response.status} ${text}`)
  }
  const acknowledgement = parseRecoveryReceiptAcknowledgement(payload)
  if (response.status === 409 || acknowledgement.status === 'retryable') {
    const error = new Error(
      `recovery receipt is retryable: ${acknowledgement.reason ?? 'server not ready'}`,
    ) as Error & { retryable?: boolean }
    error.retryable = true
    throw error
  }
  return acknowledgement
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

function parseRecoveryReceiptAcknowledgement(value: unknown): RecoveryReceiptAcknowledgement {
  if (!isRecord(value) || typeof value.appliedReceiptId !== 'string' || typeof value.status !== 'string')
    throw new Error('recovery receipt returned a malformed acknowledgement')
  return {
    appliedReceiptId: value.appliedReceiptId,
    status: value.status,
    ...(typeof value.reason === 'string' ? { reason: value.reason } : {}),
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
