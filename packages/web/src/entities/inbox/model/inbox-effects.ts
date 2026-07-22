import type { QueryClient } from '@tanstack/react-query'
import { inboxCountQueryKey, inboxListQueryKey } from '../api/queries'

/**
 * Identity-only payload of the `com.mohist.inbox.item-persisted` realtime hint.
 * Mirrors the server-side `Mohist.Server.Inbox.InboxItemPersistedHint` record
 * (issue-284 / T-001): the server emits this exact shape as the CloudEvents
 * `payload`, the Web treats it as invalidation only and never trusts it as
 * complete state.
 */
export interface InboxItemPersistedHintPayload {
  itemId: string
  projectId: string
  kind: string
  issueNumber: number
}

const KNOWN_IDENTITY_KEYS = ['itemId', 'projectId', 'kind', 'issueNumber'] as const

/**
 * Narrow an unknown realtime payload into the hint shape. Returns `null` when
 * the payload is missing required identity fields — those hints must not
 * trigger any state change. This is the only place that parses a hint, so the
 * rest of the inbox-effects surface works with the typed shape.
 */
export function parseInboxItemPersistedHint(value: unknown): InboxItemPersistedHintPayload | null {
  if (!value || typeof value !== 'object') return null
  const candidate = value as Record<string, unknown>
  const itemId = candidate.itemId
  const projectId = candidate.projectId
  const kind = candidate.kind
  const issueNumber = candidate.issueNumber
  if (typeof itemId !== 'string' || !itemId) return null
  if (typeof projectId !== 'string' || !projectId) return null
  if (typeof kind !== 'string' || !kind) return null
  if (typeof issueNumber !== 'number' || !Number.isFinite(issueNumber) || issueNumber <= 0) return null
  return { itemId, projectId, kind, issueNumber }
}

/**
 * The hint is invalidation only: the Web never synthesizes or persists an
 * `InboxItem` from the hint payload (the inbox HTTP API remains the source
 * of truth). This module exports the single mutation that runs on hint
 * receipt — the `['inbox', projectId]` TanStack Query invalidation, which
 * drives both the inbox page and the shared unread-count query.
 *
 * Project filtering: a hint that targets a different project than the
 * session's current project is ignored (no cross-project leakage, even
 * though the server already gates on project affinity — this is the second
 * line of defence per the design's strict-isolation contract).
 *
 * Recovery: dropped hints, missed realtime events, or a fresh reconnect all
 * recover via the next query. The invalidation is idempotent and does not
 * rely on the hint payload's correctness — the API always reconciles truth.
 */
export interface ApplyInboxHintOptions {
  /** The project this Web session is currently scoped to. */
  currentProjectId: string | null
  /** Optional override used by tests; defaults to a real React QueryClient.invalidateQueries. */
  invalidate?: (projectId: string) => void
}

export interface ApplyInboxHintResult {
  /** Whether the hint was applied (i.e. matched the current project). */
  applied: boolean
  /** The project whose query key was invalidated (only set when applied). */
  projectId: string | null
}

/**
 * Apply a parsed hint. Returns `applied: false` when the hint targets a
 * different project, the hint is malformed, or there is no current project
 * to reconcile against. Returns `applied: true` and triggers the inbox
 * invalidation when the hint targets the current project.
 *
 * The invalidation is the entire surface for T-003 — it deliberately does
 * not mutate the inbox query cache, does not push synthetic items, and does
 * not decide that an event becomes an inbox item.
 */
export function applyInboxHint(
  hint: InboxItemPersistedHintPayload,
  queryClient: Pick<QueryClient, 'invalidateQueries'>,
  options: ApplyInboxHintOptions,
): ApplyInboxHintResult {
  if (!options.currentProjectId) {
    return { applied: false, projectId: null }
  }
  if (hint.projectId !== options.currentProjectId) {
    return { applied: false, projectId: null }
  }
  const target = options.currentProjectId
  if (options.invalidate) {
    options.invalidate(target)
  } else {
    queryClient.invalidateQueries({ queryKey: inboxListQueryKey(target) })
    queryClient.invalidateQueries({ queryKey: inboxCountQueryKey(target) })
  }
  return { applied: true, projectId: target }
}

/**
 * Return the set of canonical identity keys used by the hint payload —
 * exposed for tests that assert the hint never carries additional state
 * beyond what the Web trusts for invalidation routing.
 */
export const INBOX_HINT_IDENTITY_KEYS: readonly string[] = KNOWN_IDENTITY_KEYS

const HIGH_ATTENTION_KINDS = ['workflow_failed', 'approval_requested'] as const

export function isHighAttentionKind(kind: string): boolean {
  return (HIGH_ATTENTION_KINDS as readonly string[]).includes(kind)
}

/**
 * Evaluate whether a high-attention in-app notice should be suppressed based
 * on the current route. Suppression applies when:
 * 1. The user is on the inbox page — items appear live via invalidation (D7).
 * 2. The user is viewing the same issue — the context is already visible.
 *
 * The inbox page has no per-item detail view today; if one is added later,
 * narrow the suppression check to the focused itemId (per D7 notes).
 */
export function shouldSuppressInAppNotice(
  hint: InboxItemPersistedHintPayload,
  pathname: string,
  viewedIssueNumber: number | null,
): boolean {
  if (pathname.endsWith('/inbox') || pathname.endsWith('/inbox/')) return true
  if (viewedIssueNumber !== null && hint.issueNumber === viewedIssueNumber) return true
  return false
}
