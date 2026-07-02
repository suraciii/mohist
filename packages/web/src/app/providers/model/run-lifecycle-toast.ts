import { useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import type { Issue } from '../../../entities/issue'

type QueryClient = ReturnType<typeof useQueryClient>

/**
 * Look up an issue's `.number` from the TanStack Query cache using its `id`.
 * Walks every `['issues', ...]` cache slice (lists + detail pages) so the
 * lookup works regardless of which slice has been hydrated. Returns `null`
 * when the issue is not cached.
 *
 * Pulled out of LiveTaskProvider.tsx so toast helpers can be relocated and
 * directly unit-tested. The function takes the queryClient as an explicit
 * parameter (D5) — no captured closure.
 */
export function findIssueNumber(
  queryClient: QueryClient,
  issueId: string,
): number | null {
  const matches = queryClient.getQueriesData<Issue[]>({ queryKey: ['issues'] })
  for (const [, data] of matches) {
    if (Array.isArray(data)) {
      const found = data.find((i) => i.id === issueId)
      if (found) {
        return found.number
      }
    }
  }
  return null
}

/**
 * Surface a lifecycle toast (pause or error) for a workflow event. Suppressed
 * when the event's issue is not in the cache, or when the user is currently
 * viewing that exact issue (they can already see the context).
 *
 * Explicit parameters (D5): `queryClient` and `viewedIssue` are passed in by
 * the caller — the helper does not capture them from any closure.
 */
export function notifyRunLifecycleToast(
  queryClient: QueryClient,
  viewedIssue: number | null,
  issueId: string,
  kind: 'pause' | 'error',
): void {
  const issueNumber = findIssueNumber(queryClient, issueId)
  if (issueNumber === null || issueNumber === viewedIssue) return
  if (kind === 'pause') {
    toast.info(`Issue #${issueNumber} needs approval`)
  } else {
    toast.error(`Issue #${issueNumber} encountered an error`)
  }
}

/**
 * Surface an approval-requested toast for a `StageApprovalRequested` event.
 * Suppressed when no issue number resolves (no `issueNumber` field and the
 * cache lookup fails) or when the user is viewing that exact issue.
 *
 * Explicit parameters (D5): `queryClient` and `viewedIssue` are passed in by
 * the caller.
 */
export function notifyApprovalRequestedToast(
  queryClient: QueryClient,
  viewedIssue: number | null,
  evt: { issueId?: string; issueNumber?: number },
): void {
  const issueNumber = evt.issueNumber ?? (evt.issueId ? findIssueNumber(queryClient, evt.issueId) : null)
  if (issueNumber === null || issueNumber === undefined || issueNumber === viewedIssue) return
  toast.info(`Issue #${issueNumber} needs approval`)
}