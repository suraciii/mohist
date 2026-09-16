import { isRunningIssue, type Issue } from '../../../entities/issue'
import type { AgentCostMetricDto } from '../../../entities/agent'

export interface FactoryStatusFields {
  inFlight: number
  awaitingApproval: number
  shippedToday: number
  todayCost: AgentCostMetricDto | undefined
}

export function isTodayLocal(iso: string): boolean {
  const date = new Date(iso)
  const now = new Date()
  return (
    date.getFullYear() === now.getFullYear() && date.getMonth() === now.getMonth() && date.getDate() === now.getDate()
  )
}

export function deriveFactoryStatus(issues: Issue[] | undefined, todayCost?: AgentCostMetricDto): FactoryStatusFields {
  let inFlight = 0
  let awaitingApproval = 0
  let shippedToday = 0

  for (const issue of issues ?? []) {
    if (isRunningIssue(issue)) {
      inFlight += 1
    }

    if (issue.approvalState?.status === 'awaiting') {
      awaitingApproval += 1
    }

    if (issue.status === 'done' && issue.completedAt && isTodayLocal(issue.completedAt)) {
      shippedToday += 1
    }
  }

  return {
    inFlight,
    awaitingApproval,
    shippedToday,
    todayCost,
  }
}
