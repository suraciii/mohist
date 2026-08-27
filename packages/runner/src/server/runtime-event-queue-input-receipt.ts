import { errorMessage } from '../core/errors.js'

export interface InputReceiptWaitEvidence {
  readonly attempts: number
  readonly retries: number
  readonly lastReason: string | null
}

export class InputReceiptWaitTimeoutError extends Error {
  readonly classification = 'receipt-budget-exhausted' as const
  readonly recordId: string
  readonly elapsedMs: number
  readonly budgetMs: number
  readonly attempts: number
  readonly retries: number
  readonly lastReason: string

  constructor(recordId: string, evidence: InputReceiptWaitEvidence, elapsedMs: number, budgetMs: number) {
    const reason = evidence.lastReason ?? 'no retry/refusal reason was observed'
    super(
      `session.input acceptance exceeded its budget for ${recordId}: ` +
        `elapsed ${elapsedMs}ms of ${budgetMs}ms; last reason: ${reason}; ` +
        `delivery attempts: ${evidence.attempts}; retries: ${evidence.retries}`,
    )
    this.name = 'InputReceiptWaitTimeoutError'
    this.recordId = recordId
    this.elapsedMs = elapsedMs
    this.budgetMs = budgetMs
    this.attempts = evidence.attempts
    this.retries = evidence.retries
    this.lastReason = reason
  }
}

export class InputReceiptWaitCancelledError extends Error {
  readonly classification = 'cancelled' as const
  readonly recordId: string

  constructor(recordId: string, reason: unknown) {
    const detail = reason instanceof Error ? reason.message : typeof reason === 'string' ? reason : 'task was cancelled'
    super(`session.input receipt wait cancelled for ${recordId}: ${detail}`)
    this.name = 'InputReceiptWaitCancelledError'
    this.recordId = recordId
  }
}

export function structuredInputReason(reason: unknown): string {
  const message = errorMessage(reason)
  if (!reason || typeof reason !== 'object') return message
  const value = reason as Record<string, unknown>
  const code = typeof value.code === 'string' && value.code.length > 0 ? value.code : null
  const classification =
    typeof value.classification === 'string' && value.classification.length > 0 ? value.classification : null
  const prefix = [classification, code].filter((entry): entry is string => entry !== null).join('/')
  return prefix && !message.startsWith(prefix) ? `${prefix}: ${message}` : message
}
