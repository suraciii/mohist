import type { Issue } from '../../../entities/issue'
import type { AgentStatus } from '../../../entities/agent'

export interface FactoryStatusFields {
  runnerAvailable: boolean
  inFlight: number
  awaitingApproval: number
  shippedToday: number
  todayCost: undefined
}

export function isTodayLocal(iso: string): boolean {
  const date = new Date(iso)
  const now = new Date()
  return (
    date.getFullYear() === now.getFullYear()
    && date.getMonth() === now.getMonth()
    && date.getDate() === now.getDate()
  )
}

export function deriveFactoryStatus(
  issues: Issue[] | undefined,
  agentStatus: AgentStatus | undefined,
): FactoryStatusFields {
  const runnerAvailable = agentStatus?.runnerAvailable === true

  let inFlight = 0
  let awaitingApproval = 0
  let shippedToday = 0

  for (const issue of issues ?? []) {
    if (issue.status === 'in_progress' && issue.health !== 'done' && issue.health !== 'cancelled') {
      inFlight += 1
    }

    if (issue.approvalState?.status === 'awaiting') {
      awaitingApproval += 1
    }

    if (issue.status === 'done' && isTodayLocal(issue.updatedAt)) {
      shippedToday += 1
    }
  }

  return {
    runnerAvailable,
    inFlight,
    awaitingApproval,
    shippedToday,
    todayCost: undefined,
  }
}
