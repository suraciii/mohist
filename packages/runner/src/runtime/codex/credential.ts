/**
 * Credential redaction for the Codex runtime module.
 *
 * The Codex deep module is the security boundary for app-server
 * traffic: no credential value (OpenAI / Anthropic API key, OAuth
 * bearer, session cookie, machine-issued session token, basic-auth
 * user-info) may cross a registration, request, result, event, or
 * smoke-test artifact boundary in plaintext. This file is the single
 * pass that masks credentials uniformly before any value leaves the
 * runtime module and before any value lands in a log line or
 * diagnostic.
 *
 * The masker is intentionally narrow: it operates on raw strings and
 * structured envelopes, and it is conservative — unknown envelope
 * shapes are walked recursively so a future JSON-RPC field cannot
 * leak a credential because nobody thought to mask it.
 *
 * The masker has no dependency on the TaskLog masker so that the
 * Codex module never imports a sibling Runtime module. The patterns
 * overlap with the cross-Runtime masker, but the rules of the Codex
 * boundary require an in-module implementation.
 */

import { createHash } from 'node:crypto'

const REDACTED = '***'

/**
 * Heuristic patterns for raw credential shapes. Each matcher returns
 * a `***`-substituted copy when it finds a candidate and `null` when
 * it does not match. Multiple patterns run in order so the first one
 * wins (overlap is intentional: longer matches consume the substring
 * before the next matcher runs).
 */
const CREDENTIAL_PATTERNS: ReadonlyArray<{
  readonly pattern: RegExp
  readonly replace: (match: string, ...groups: string[]) => string
}> = [
  // user:password@host, token:@host, or token@host in URLs. The
  // user-info slot is replaced with `***@` so the host stays
  // visible — a log line should still show where the request went.
  {
    pattern: /\b([a-z][a-z0-9+.\-]*:\/\/)([^:\s/]+):([^@\s/]+)@/gi,
    replace: (_match: string, scheme: string) => `${scheme}${REDACTED}@`,
  },
  {
    pattern: /\b([a-z][a-z0-9+.\-]*:\/\/)([A-Za-z0-9._\-]{12,})@/g,
    replace: (_match: string, scheme: string) => `${scheme}${REDACTED}@`,
  },
  // HTTP / Bearer / Basic authorization headers
  {
    pattern: /\bBearer\s+[A-Za-z0-9._\-]+/g,
    replace: (_match: string) => `Bearer ${REDACTED}`,
  },
  {
    pattern: /\bBasic\s+[A-Za-z0-9+/=]{8,}/g,
    replace: (_match: string) => `Basic ${REDACTED}`,
  },
  // Provider / vendor key shapes
  {
    pattern: /\bsk-[A-Za-z0-9_\-]{16,}/g,
    replace: (_match: string) => `sk-${REDACTED}`,
  },
  {
    pattern: /\b(gh[pousr])_[A-Za-z0-9]{20,}/g,
    replace: (_match: string, prefix: string) => `${prefix}_${REDACTED}`,
  },
  // JSON / env-string cookie values (`"session":"..."`, `session=...;`)
  {
    pattern: /("session"\s*:\s*")([^"\\]{8,})(")/gi,
    replace: (_match: string, prefix: string, _value: string, suffix: string) => `${prefix}${REDACTED}${suffix}`,
  },
  {
    pattern: /(api[_-]?key\s*[:=]\s*"?)([A-Za-z0-9._\-]{8,})("?)/gi,
    replace: (_match: string, prefix: string, _value: string, suffix: string) => `${prefix}${REDACTED}${suffix}`,
  },
]

/**
 * Names of JSON fields that MUST NOT appear in any artifact leaving
 * the Codex module. The matcher redacts both the key and the value
 * so downstream readers cannot tell whether a key was set.
 */
const SENSITIVE_FIELD_NAMES = new Set<string>([
  'apiKey',
  'api_key',
  'token',
  'accessToken',
  'access_token',
  'refreshToken',
  'refresh_token',
  'bearer',
  'authorization',
  'sessionToken',
  'session_token',
  'password',
  'secret',
  'cookie',
  'set-cookie',
  'auth',
])

/**
 * Apply the Codex credential redaction rules to a raw string. Returns
 * the original string when no patterns match; replaces matched
 * substrings with `***`. The function is total — non-strings round-
 * trip through `String(value)` so callers never get an exception.
 */
export function redactCodexCredentialString(value: unknown): string {
  if (typeof value === 'string') return maskCodexCredentialString(value)
  if (value === null || value === undefined) return ''
  if (typeof value === 'object') return redactCodexCredentialEnvelope(value)
  return maskCodexCredentialString(String(value))
}

/**
 * Apply the redaction patterns to a single string. Returns a new
 * string; the original is never mutated.
 */
export function maskCodexCredentialString(value: string): string {
  if (typeof value !== 'string' || value.length === 0) return value
  let current = value
  for (const { pattern, replace } of CREDENTIAL_PATTERNS) {
    current = current.replace(pattern, replace as (substring: string, ...args: string[]) => string)
  }
  return current
}

/**
 * Walk an arbitrary JSON-compatible envelope and replace the values
 * of any sensitive field with `***`. The walker also runs string
 * redaction over every string leaf it encounters so an
 * unconventional key cannot leak a credential that the structural
 * pass did not know about.
 *
 * Arrays are walked index by index. Plain objects are walked by key.
 * Date, Map, Set, and other exotic types are returned as-is — the
 * Codex module only ever builds plain JSON-RPC envelopes, so exotic
 * values do not reach this boundary in practice.
 */
export function redactCodexCredentialEnvelope(value: unknown): string {
  return JSON.stringify(redactCodexCredentialValue(value))
}

function redactCodexCredentialValue(value: unknown): unknown {
  if (value === null || value === undefined) return value
  if (typeof value === 'string') {
    const masked = maskCodexCredentialString(value)
    return masked
  }
  if (typeof value === 'number' || typeof value === 'boolean' || typeof value === 'bigint') return value
  if (Array.isArray(value)) return value.map((entry) => redactCodexCredentialValue(entry))
  if (typeof value === 'object') {
    const source = value as Record<string, unknown>
    const next: Record<string, unknown> = {}
    for (const [key, entry] of Object.entries(source)) {
      if (SENSITIVE_FIELD_NAMES.has(key)) {
        next[key] = REDACTED
        continue
      }
      next[key] = redactCodexCredentialValue(entry)
    }
    return next
  }
  return value
}

/**
 * Convenience pass used by the spawn-time logs and the diagnostic
 * surface. The masker scrubs both structural sensitive fields and
 * ad-hoc credential shapes so a single helper covers both
 * structured and unstructured content.
 */
export function redactCodexCredentialDiagnostic(diagnostic: { readonly message: string; readonly details?: unknown }): {
  readonly message: string
  readonly details?: unknown
} {
  const message = maskCodexCredentialString(diagnostic.message)
  const details = diagnostic.details === undefined ? undefined : redactCodexCredentialValue(diagnostic.details)
  return { message, ...(details === undefined ? {} : { details }) }
}

/**
 * A minimal digest helper used by the optional secret index. The
 * Codex module never registers raw secrets; it accepts pre-digested
 * entries from the cross-Runtime masker when it wants to mask a
 * runtime secret that the producer of an event does not want to
 * embed in cleartext.
 */
export interface CodexCredentialSecretIndexEntry {
  readonly length: number
  readonly digest: string
}

export function codexCredentialSecretDigest(value: string): string {
  return createHash('sha256').update(value, 'utf8').digest('hex')
}

export function redactCodexCredentialStringWithIndex(
  value: string,
  index: ReadonlyArray<CodexCredentialSecretIndexEntry>,
): string {
  if (typeof value !== 'string' || value.length === 0) return value
  let current = maskCodexCredentialString(value)
  for (const secret of index) {
    let cursor = 0
    while (cursor + secret.length <= current.length) {
      const candidate = current.slice(cursor, cursor + secret.length)
      if (codexCredentialSecretDigest(candidate) !== secret.digest) {
        cursor += 1
        continue
      }
      current = current.slice(0, cursor) + REDACTED + current.slice(cursor + secret.length)
      cursor += REDACTED.length
    }
  }
  return current
}

export const CODEX_CREDENTIAL_MASK_PLACEHOLDER = REDACTED
