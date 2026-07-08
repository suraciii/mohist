import { IssueHealth } from '../model/types'
import { statusTreatment } from '@/shared/status-presentation'

/**
 * Legacy entry point. New code should call
 * `statusTreatment('issue-health', health)` from `@/shared/status-presentation`
 * directly; the helper is retained here so call sites that already pass a
 * class string to a JSX element (`className={statusBadge(health)}`) keep
 * compiling. The returned string is composed entirely of semantic-token
 * utilities — no raw Tailwind palette classes — so the visual treatment is
 * still owned by the shared layer.
 */
export function statusBadge(health: IssueHealth | string): string {
  return statusTreatment('issue-health', health).container
}

export function statusLabel(health: IssueHealth): string {
  switch (health) {
    case IssueHealth.Active:
      return 'Active'
    case IssueHealth.Paused:
      return 'Paused'
    case IssueHealth.Blocked:
      return 'Needs Action'
    case IssueHealth.Interrupted:
      return 'Interrupted'
    case IssueHealth.Cancelled:
      return 'Cancelled'
    case IssueHealth.Done:
      return 'Done'
    default:
      return health
  }
}