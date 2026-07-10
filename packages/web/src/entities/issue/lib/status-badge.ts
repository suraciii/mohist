import { IssueHealth } from '../model/types'

export function statusBadge(health: IssueHealth): string {
  switch (health) {
    case IssueHealth.Active:
      return 'text-green-700 bg-green-50'
    case IssueHealth.Paused:
      return 'text-amber-700 bg-amber-50'
    case IssueHealth.Blocked:
      return 'text-red-700 bg-red-50'
    default:
      return 'text-gray-700 bg-gray-50'
  }
}

export function statusLabel(health: IssueHealth): string {
  switch (health) {
    case IssueHealth.Active:
      return 'Active'
    case IssueHealth.Paused:
      return 'Paused'
    case IssueHealth.Blocked:
      return 'Needs Action'
    case IssueHealth.Cancelled:
      return 'Cancelled'
    case IssueHealth.Done:
      return 'Done'
    default:
      return health
  }
}
