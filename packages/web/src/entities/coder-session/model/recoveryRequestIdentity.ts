import { createIdempotencyKey } from '../../../shared/lib/idempotency-key'

export type RecoveryOperation = 'compact' | 'reset'

/** One Compact or Reset, named by the caller that owns its identity. */
export interface RecoveryRequestScope {
  projectId: string
  sessionKey: string
  operation: RecoveryOperation
}

const STORAGE_PREFIX = 'mohist.recovery-request'
const memory = new Map<string, string>()

function storageKey(scope: RecoveryRequestScope): string {
  return JSON.stringify([STORAGE_PREFIX, scope.projectId, scope.sessionKey, scope.operation])
}

function sessionStorageOrNull(): Storage | null {
  try {
    return typeof window === 'undefined' ? null : window.sessionStorage
  } catch {
    // Storage can be blocked (privacy mode, disabled cookies); the identity
    // then lives only for this page instead of failing the action.
    return null
  }
}

// Session storage is the source of truth while it is available; the in-memory
// map only carries the identity when the browser refuses storage entirely.
function withSessionStorage<T>(use: (storage: Storage) => T, fallback: () => T): T {
  const storage = sessionStorageOrNull()
  if (!storage) return fallback()
  try {
    return use(storage)
  } catch {
    return fallback()
  }
}

/**
 * Returns the operation's existing key, or mints and stores one. Called before
 * the request, because the Server rejects a Compact or Reset without a key and
 * a lost response can only be retried with the identity that was sent.
 */
export function beginRecoveryRequest(scope: RecoveryRequestScope): string {
  const key = storageKey(scope)
  return withSessionStorage(
    (storage) => {
      const existing = storage.getItem(key)
      if (existing) return existing
      const minted = createIdempotencyKey()
      storage.setItem(key, minted)
      return minted
    },
    () => {
      const existing = memory.get(key)
      if (existing) return existing
      const minted = createIdempotencyKey()
      memory.set(key, minted)
      return minted
    },
  )
}

/**
 * Releases the operation's identity once its outcome is known, so the next
 * Compact or Reset is a new intent rather than a replay.
 */
export function completeRecoveryRequest(scope: RecoveryRequestScope): void {
  const key = storageKey(scope)
  memory.delete(key)
  withSessionStorage<void>(
    (storage) => storage.removeItem(key),
    () => undefined,
  )
}
