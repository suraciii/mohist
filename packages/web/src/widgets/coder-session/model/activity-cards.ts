import { useMemo } from 'react'
import { useAgentActivity } from '../../../entities/agent/api/queries'
import type { AgentActivitySession, AgentActivityWaiting } from '../../../shared/api/types'

export interface TaskProgress {
  completed: number
  total: number
}

export interface ActivityPreview {
  kind: 'text' | 'tool'
  text: string
}

export interface WaitingCard {
  issueId: string
  issueNumber: string
  issueTitle: string
  issueStage: string | null
  label: 'Needs Approval'
  questionPreview?: string
  questionId?: string
  questionAskedAt?: string
}

export interface SessionCard {
  issueId: string
  issueNumber: string
  issueTitle: string
  issueStage: string
  sessionId: string
  status: string
  model: string | null
  taskDescription: string | null
  title: string | null
  createdAt: string
  completedAt: string | null
  lastActivityAt: string | null
  activityPreviews: ActivityPreview[]
  taskProgress: TaskProgress | null
  currentWorkTitle: string | null
  failureReason: string | null
}

export interface StatusCounts {
  active: number
  waiting: number
  completed: number
  failed: number
}

const ACTIVE_STATUSES = new Set(['created', 'running', 'probing'])

function sessionToCard(s: AgentActivitySession): SessionCard {
  return {
    issueId: s.issueId,
    issueNumber: String(s.issueNumber),
    issueTitle: s.issueTitle,
    issueStage: s.issueStage,
    sessionId: s.sessionId,
    status: s.status,
    model: s.model,
    taskDescription: s.taskDescription,
    title: s.currentWorkItem?.title ?? s.taskDescription,
    createdAt: s.createdAt,
    completedAt: s.completedAt,
    lastActivityAt: s.lastActivityAt,
    activityPreviews: s.lastActivity ? [{ kind: s.lastActivity.kind, text: s.lastActivity.text }] : [],
    taskProgress: s.taskProgress,
    currentWorkTitle: s.currentWorkItem?.title ?? null,
    failureReason: s.failureReason,
  }
}

function waitingToCard(w: AgentActivityWaiting): WaitingCard {
  return {
    issueId: w.issueId,
    issueNumber: String(w.issueNumber),
    issueTitle: w.issueTitle,
    issueStage: w.stage,
    label: w.label,
    questionPreview: w.preview ?? undefined,
    questionAskedAt: w.requestedAt ?? undefined,
  }
}

export function useActivityCards() {
  const { data } = useAgentActivity()

  return useMemo(() => {
    const cards = (data?.sessions ?? []).map(sessionToCard)
    const activeCards = cards.filter((c) => ACTIVE_STATUSES.has(c.status))
    const recentCards = cards.filter((c) => !ACTIVE_STATUSES.has(c.status))
    const waitingCards = (data?.waiting ?? []).map(waitingToCard)

    return {
      activeCards,
      recentCards,
      waitingCards,
      statusCounts: data?.summary ?? {
        active: activeCards.length,
        waiting: waitingCards.length,
        completed: recentCards.filter((c) => c.status === 'completed').length,
        failed: recentCards.filter((c) => c.status === 'failed' || c.status === 'cancelled').length,
      },
      slotUsage: data?.summary.slots ?? { active: 0, max: 0 },
    }
  }, [data])
}
