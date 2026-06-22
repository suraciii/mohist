import type { IssueStartBlocker, IssueStatus } from '../../../entities/issue/@x/types'
import type { LinkedIssue } from '../../../entities/epic/model/types'

export type Readiness = 'can-start' | 'waiting' | 'in-progress' | 'done'

export interface DerivedReadiness {
  readiness: Readiness
  waitingForIssueNumber: number | null
}

const READINESS_LABELS: Record<Readiness, string> = {
  'can-start': 'Can start',
  waiting: 'Waiting',
  'in-progress': 'In progress',
  done: 'Done',
}

export function readinessLabel(readiness: Readiness): string {
  return READINESS_LABELS[readiness]
}

function isWaitingForBlocker(blocker: IssueStartBlocker | null): blocker is { kind: 'waiting-for'; issue: { number: number; title: string; stage?: string; status?: string } } {
  return blocker !== null && blocker.kind === 'waiting-for'
}

export function deriveReadiness(issue: Pick<LinkedIssue, 'status' | 'canStart' | 'startBlocker'>): DerivedReadiness {
  if (issue.status === 'done') {
    return { readiness: 'done', waitingForIssueNumber: null }
  }
  if (issue.status === 'in_progress') {
    return { readiness: 'in-progress', waitingForIssueNumber: null }
  }
  if (isWaitingForBlocker(issue.startBlocker)) {
    return { readiness: 'waiting', waitingForIssueNumber: issue.startBlocker.issue.number }
  }
  if (issue.canStart) {
    return { readiness: 'can-start', waitingForIssueNumber: null }
  }
  return { readiness: 'waiting', waitingForIssueNumber: null }
}

export interface StatusColorToken {
  background: string
  border: string
  text: string
  accent: string
}

const STATUS_COLORS: Record<IssueStatus, StatusColorToken> = {
  backlog: {
    background: '#f3f4f6',
    border: '#d1d5db',
    text: '#374151',
    accent: '#6b7280',
  },
  in_progress: {
    background: '#dbeafe',
    border: '#93c5fd',
    text: '#1e3a8a',
    accent: '#2563eb',
  },
  done: {
    background: '#dcfce7',
    border: '#86efac',
    text: '#14532d',
    accent: '#16a34a',
  },
  cancelled: {
    background: '#f3f4f6',
    border: '#9ca3af',
    text: '#6b7280',
    accent: '#9ca3af',
  },
}

export function statusColors(status: IssueStatus): StatusColorToken {
  return STATUS_COLORS[status]
}

const READINESS_TONE: Record<Readiness, string> = {
  'can-start': '#16a34a',
  waiting: '#d97706',
  'in-progress': '#2563eb',
  done: '#15803d',
}

export function readinessTone(readiness: Readiness): string {
  return READINESS_TONE[readiness]
}
