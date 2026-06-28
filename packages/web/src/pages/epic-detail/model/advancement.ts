import { IssueStatus } from '../../../entities/issue/@x/types'
import { EpicStatus } from '../../../entities/epic/model/types'
import type { LinkedIssue } from '../../../entities/epic/model/types'

export type AdvancementState =
  | { kind: 'running-but-idle' }
  | { kind: 'waiting-for-in-progress'; issueNumber: number }
  | { kind: 'draft-blocker'; issueNumber: number }
  | { kind: 'external-prerequisite-blocker'; issueNumber: number; prerequisiteNumbers: number[] }
  | { kind: 'idle-no-next'; reason: string }
  | { kind: 'has-next'; issueNumber: number }
  | { kind: 'nothing-pending' }

export type AdvancementStateKind = AdvancementState['kind']

export interface AdvancementContext {
  epicStatus: EpicStatus
  linkedIssues: LinkedIssue[]
}

export interface AdvancementCopy {
  text: string
  linkNumbers: number[]
}

function isUndelivered(issue: LinkedIssue): boolean {
  return issue.status !== IssueStatus.Done && issue.status !== IssueStatus.Cancelled
}

function isInProgress(issue: LinkedIssue): boolean {
  return issue.status === IssueStatus.InProgress
}

function isStartableCandidate(issue: LinkedIssue): boolean {
  return (
    issue.status === IssueStatus.Backlog
    && issue.canStart
    && issue.startBlocker === null
  )
}

function priorityRank(priority: LinkedIssue['priority']): number {
  switch (priority) {
    case 'p0': return 0
    case 'p1': return 1
    case 'p2': return 2
    case 'p3': return 3
    case 'p4': return 4
    default: return 9
  }
}

function byServerCandidateOrder(a: LinkedIssue, b: LinkedIssue): number {
  const rankDelta = priorityRank(a.priority) - priorityRank(b.priority)
  if (rankDelta !== 0) return rankDelta
  return a.number - b.number
}

function describeCandidateBlocker(issue: LinkedIssue): string {
  const blocker = issue.startBlocker
  if (blocker === null) return 'no startable issue'
  if (blocker.kind === 'draft') return `still a draft`
  if (blocker.kind === 'waiting-for') return `waiting for #${blocker.issue.number} to finish`
  return 'no startable issue'
}

export function deriveAdvancementState(context: AdvancementContext): AdvancementState {
  const { epicStatus, linkedIssues } = context

  const inProgressIssue = linkedIssues.filter(isInProgress).sort((a, b) => a.number - b.number)[0]
  if (inProgressIssue) {
    return { kind: 'waiting-for-in-progress', issueNumber: inProgressIssue.number }
  }

  const undelivered = linkedIssues.filter(isUndelivered).sort(byServerCandidateOrder)
  if (undelivered.length === 0) {
    return { kind: 'nothing-pending' }
  }

  const candidate = undelivered[0]

  if (candidate.startBlocker?.kind === 'draft') {
    return { kind: 'draft-blocker', issueNumber: candidate.number }
  }

  const externalPrereqs = candidate.externalPrerequisites ?? []
  if (externalPrereqs.length > 0) {
    return {
      kind: 'external-prerequisite-blocker',
      issueNumber: candidate.number,
      prerequisiteNumbers: externalPrereqs.map(prereq => prereq.number),
    }
  }

  if (isStartableCandidate(candidate)) {
    return { kind: 'has-next', issueNumber: candidate.number }
  }

  if (epicStatus === EpicStatus.Running) {
    return { kind: 'running-but-idle' }
  }

  return {
    kind: 'idle-no-next',
    reason: describeCandidateBlocker(candidate),
  }
}

export function advancementCopy(state: AdvancementState): AdvancementCopy {
  switch (state.kind) {
    case 'waiting-for-in-progress':
      return {
        text: `Waiting for #${state.issueNumber} to finish`,
        linkNumbers: [state.issueNumber],
      }
    case 'draft-blocker':
      return {
        text: `Next candidate #${state.issueNumber} is still a draft`,
        linkNumbers: [state.issueNumber],
      }
    case 'external-prerequisite-blocker': {
      const list = state.prerequisiteNumbers.map(n => `#${n}`).join(', ')
      const suffix = state.prerequisiteNumbers.length === 1 ? '' : 's'
      return {
        text: `Next candidate #${state.issueNumber} is blocked by external issue${suffix} ${list}`,
        linkNumbers: state.prerequisiteNumbers,
      }
    }
    case 'running-but-idle':
      return {
        text: 'Running but no issue is currently advancing',
        linkNumbers: [],
      }
    case 'idle-no-next':
      return {
        text: `No startable next issue: ${state.reason}`,
        linkNumbers: [],
      }
    case 'has-next':
      return {
        text: `Next startable issue: #${state.issueNumber}`,
        linkNumbers: [state.issueNumber],
      }
    case 'nothing-pending':
      return {
        text: 'All linked issues are delivered',
        linkNumbers: [],
      }
  }
}

export const ADVANCEMENT_STATE_KINDS: AdvancementStateKind[] = [
  'running-but-idle',
  'waiting-for-in-progress',
  'draft-blocker',
  'external-prerequisite-blocker',
  'idle-no-next',
  'has-next',
  'nothing-pending',
]
