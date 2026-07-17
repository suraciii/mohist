import { toast } from 'sonner'

/**
 * Surface a lifecycle toast (pause or error) from canonical issue context.
 * Suppressed while the user is viewing that exact issue.
 */
export function notifyRunLifecycleToast(
  viewedIssue: number | null,
  issueNumber: number,
  kind: 'pause' | 'error',
): void {
  if (issueNumber === viewedIssue) return
  if (kind === 'pause') {
    toast.info(`Issue #${issueNumber} needs approval`)
  } else {
    toast.error(`Issue #${issueNumber} encountered an error`)
  }
}

/**
 * Surface an approval-requested toast for a `StageApprovalRequested` event.
 * Suppressed when canonical issue context is absent or already being viewed.
 */
export function notifyApprovalRequestedToast(
  viewedIssue: number | null,
  evt: { issueNumber?: number },
): void {
  const issueNumber = evt.issueNumber
  if (issueNumber === undefined || issueNumber === viewedIssue) return
  toast.info(`Issue #${issueNumber} needs approval`)
}
