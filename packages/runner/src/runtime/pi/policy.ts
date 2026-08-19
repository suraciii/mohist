import type { PiDiagnostic, PiProviderErrorPolicy } from './types.js'

const DEFAULT_PATTERNS = [
  /quota/i,
  /credit/i,
  /billing/i,
  /usage[ -]?limit/i,
  /payment[ -]?required/i,
  /insufficient[ _-]?balance/i,
  /额度/i,
  /余额/i,
  /计费/i,
  /欠费/i,
  /使用上限/i,
  /已达到[^。]*(?:限额|上限)/i,
  /限额[^。]*(?:重置|恢复)/i,
] as const

export const DEFAULT_PI_PROVIDER_ERROR_POLICY: PiProviderErrorPolicy = Object.freeze({
  nonRecoverablePatterns: DEFAULT_PATTERNS,
  consecutiveRetryThreshold: 5,
})

export function isProviderFailure(message: string, policy: PiProviderErrorPolicy): boolean {
  return policy.nonRecoverablePatterns.some((pattern) => {
    pattern.lastIndex = 0
    return pattern.test(message)
  })
}

export function classifyRetryFailure(
  event: unknown,
  policy: PiProviderErrorPolicy,
): { message: string; provider: boolean } | null {
  if (!event || typeof event !== 'object' || (event as { type?: unknown }).type !== 'auto_retry_start') return null
  const value = event as { errorMessage?: unknown; attempt?: unknown }
  const text = typeof value.errorMessage === 'string' ? value.errorMessage.trim() : ''
  if (isProviderFailure(text, policy)) {
    return { message: text || 'Pi provider retries exhausted', provider: true }
  }
  if (typeof value.attempt === 'number' && value.attempt >= policy.consecutiveRetryThreshold) {
    return { message: 'Pi provider retries exhausted', provider: false }
  }
  return null
}

export function parseProviderErrorPolicy(env: Record<string, string | undefined>): PiResultPolicy {
  const diagnostics: PiDiagnostic[] = []
  const patterns = [...DEFAULT_PATTERNS]
  const rawPatterns = env.MOHIST_PROVIDER_ERROR_PATTERNS
  if (rawPatterns !== undefined && rawPatterns.trim() !== '') {
    let values: unknown
    try {
      values = JSON.parse(rawPatterns)
    } catch {
      return invalidPolicy('MOHIST_PROVIDER_ERROR_PATTERNS must be a JSON array of regex sources')
    }
    if (!Array.isArray(values) || values.some((value) => typeof value !== 'string')) {
      return invalidPolicy('MOHIST_PROVIDER_ERROR_PATTERNS must be a JSON array of regex sources')
    }
    try {
      for (const source of values) patterns.push(new RegExp(source, 'i'))
    } catch (cause) {
      return invalidPolicy(`MOHIST_PROVIDER_ERROR_PATTERNS contains an invalid regex: ${safeMessage(cause)}`)
    }
  }
  const rawThreshold = env.MOHIST_PROVIDER_RETRY_THRESHOLD
  let threshold = DEFAULT_PI_PROVIDER_ERROR_POLICY.consecutiveRetryThreshold
  if (rawThreshold !== undefined && rawThreshold.trim() !== '') {
    if (!/^\d+$/.test(rawThreshold.trim()) || Number(rawThreshold) <= 0) {
      return invalidPolicy('MOHIST_PROVIDER_RETRY_THRESHOLD must be a positive integer')
    }
    threshold = Number(rawThreshold)
  }
  return {
    ok: true,
    value: Object.freeze({ nonRecoverablePatterns: Object.freeze(patterns), consecutiveRetryThreshold: threshold }),
    diagnostics,
  }
}

export type PiResultPolicy =
  | { readonly ok: true; readonly value: PiProviderErrorPolicy; readonly diagnostics: readonly PiDiagnostic[] }
  | { readonly ok: false; readonly error: PiDiagnostic; readonly diagnostics: readonly PiDiagnostic[] }

function invalidPolicy(message: string): PiResultPolicy {
  const error = { severity: 'error' as const, code: 'invalid-provider-policy', message }
  return { ok: false, error, diagnostics: [error] }
}

function safeMessage(cause: unknown): string {
  return cause instanceof Error ? cause.message : 'invalid regular expression'
}
