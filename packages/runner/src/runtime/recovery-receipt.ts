import { createHash, randomUUID } from 'node:crypto'
import type { DispatchWorkItem, WorkItemResult } from '../core/types.js'

export type RuntimeRecoveryReceiptPayload =
  | {
      readonly type: 'terminal-result'
      readonly result: WorkItemResult
      readonly fingerprint: string
    }
  | {
      readonly type: 'update-interrupted'
      readonly updateOperationId: string
      readonly stopConfirmed: true
    }

export interface RuntimeRecoveryReceipt {
  readonly workflowRunId: string
  readonly taskRunId: string
  readonly workId: string
  readonly runnerId: string
  readonly agentSessionId: string
  readonly agentTurnId: string
  readonly runtime: string
  readonly runtimeSessionId: string
  readonly recoveryGeneration: number
  readonly receiptId: string
  readonly payload: RuntimeRecoveryReceiptPayload
}

export interface RuntimeRecoveryBinding {
  readonly agentSessionId: string
  readonly agentTurnId: string | null
  readonly runtime: 'opencode' | 'pi'
  readonly runtimeSessionId: string | null
  readonly workDir: string
}

export interface PendingUpdateWork {
  readonly ownerKind: string
  readonly ownerId: string
  readonly workId: string
  readonly taskRunId?: string | null
  readonly workType: string
  readonly status?: string | null
}

export interface PendingUpdateOperation {
  readonly operationId: string
  readonly runnerId?: string | null
  readonly createdAt: string
  readonly affectedWorks: readonly PendingUpdateWork[]
}

export interface RecoveryReceiptAcknowledgement {
  readonly appliedReceiptId: string
  readonly status: 'accepted' | 'stale' | 'rejected-mismatch' | 'retryable' | string
  readonly reason?: string | null
}

export function createTerminalRecoveryReceipt(
  work: DispatchWorkItem,
  binding: RuntimeRecoveryBinding,
  runnerId: string,
  result: WorkItemResult,
  receiptId: string = randomUUID(),
): RuntimeRecoveryReceipt | null {
  const identity = receiptIdentity(work, binding, runnerId, receiptId)
  if (!identity) return null
  return {
    ...identity,
    payload: {
      type: 'terminal-result',
      result: structuredClone(result),
      fingerprint: workResultFingerprint(result),
    },
  }
}

export function createInterruptedRecoveryReceipt(
  work: DispatchWorkItem,
  binding: RuntimeRecoveryBinding,
  runnerId: string,
  updateOperationId: string,
  receiptId: string = randomUUID(),
): RuntimeRecoveryReceipt | null {
  const identity = receiptIdentity(work, binding, runnerId, receiptId)
  if (!identity || updateOperationId.trim().length === 0) return null
  return {
    ...identity,
    payload: {
      type: 'update-interrupted',
      updateOperationId,
      stopConfirmed: true,
    },
  }
}

export function workResultFingerprint(result: WorkItemResult): string {
  // Match the Server WorkResult serializer: nullable properties are omitted,
  // and the record order is status, message, output, exitCode, artifacts,
  // addTasks, error. Runner-private projection fields never enter the hash.
  const canonical: Record<string, unknown> = { status: result.status }
  if (result.message !== undefined && result.message !== null) canonical.message = result.message
  if (result.output !== undefined && result.output !== null) canonical.output = canonicalizeJson(result.output)
  if (result.exitCode !== undefined && result.exitCode !== null) canonical.exitCode = result.exitCode
  if (result.artifactUploadIds !== undefined && result.artifactUploadIds !== null)
    canonical.artifactUploadIds = canonicalizeJson(result.artifactUploadIds)
  if (result.addTasks !== undefined && result.addTasks !== null)
    canonical.addTasks = canonicalizeNonNull(result.addTasks)
  if (result.error !== undefined && result.error !== null) canonical.error = canonicalizeNonNull(result.error)
  return createHash('sha256').update(JSON.stringify(canonical)).digest('hex')
}

export function recoveryGeneration(work: DispatchWorkItem): number {
  const value = work.recovery?.['generation']
  return typeof value === 'number' && Number.isInteger(value) && value >= 0 ? value : 0
}

function receiptIdentity(
  work: DispatchWorkItem,
  binding: RuntimeRecoveryBinding,
  runnerId: string,
  receiptId: string,
): Omit<RuntimeRecoveryReceipt, 'payload'> | null {
  const taskRunId = work.taskRunId?.trim()
  const agentSessionId = binding.agentSessionId.trim()
  const agentTurnId = binding.agentTurnId?.trim()
  const runtimeSessionId = binding.runtimeSessionId?.trim()
  if (
    !work.workflowRunId.trim() ||
    !taskRunId ||
    !work.workId.trim() ||
    !runnerId.trim() ||
    !agentSessionId ||
    !agentTurnId ||
    !binding.runtime.trim() ||
    !runtimeSessionId ||
    !receiptId.trim()
  ) {
    return null
  }
  return {
    workflowRunId: work.workflowRunId,
    taskRunId,
    workId: work.workId,
    runnerId,
    agentSessionId,
    agentTurnId,
    runtime: binding.runtime,
    runtimeSessionId,
    recoveryGeneration: recoveryGeneration(work),
    receiptId,
  }
}

function canonicalizeJson(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(canonicalizeJson)
  if (!value || typeof value !== 'object') return value
  const record = value as Record<string, unknown>
  return Object.fromEntries(Object.keys(record).map((key) => [key, canonicalizeJson(record[key])]))
}

function canonicalizeNonNull(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(canonicalizeNonNull)
  if (!value || typeof value !== 'object') return value
  const record = value as Record<string, unknown>
  return Object.fromEntries(
    Object.keys(record)
      .filter((key) => record[key] !== null && record[key] !== undefined)
      .map((key) => [key, canonicalizeNonNull(record[key])]),
  )
}
