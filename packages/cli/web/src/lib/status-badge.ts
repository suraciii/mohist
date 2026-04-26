import { IssueStatus } from './types'

export function statusBadge(status: IssueStatus): string {
  switch (status) {
    case IssueStatus.Active:
      return 'text-green-700 bg-green-50'
    case IssueStatus.Paused:
      return 'text-amber-700 bg-amber-50'
    case IssueStatus.Blocked:
      return 'text-red-700 bg-red-50'
    case IssueStatus.Interrupted:
      return 'text-orange-700 bg-orange-50'
    default:
      return 'text-gray-700 bg-gray-50'
  }
}
