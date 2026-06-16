import { useMemo } from 'react'
import { useAgentActivity } from '../../../entities/agent'
import type { AgentActivitySession, AgentActivityWaiting } from '../../../entities/agent'

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
  resolvedModel: string | null
  taskDescription: string | null
  title: string | null
  createdAt: string
  completedAt: string | null
  lastActivityAt: string | null
  activityPreviews: ActivityPreview[]
  taskProgress: TaskProgress | null
  currentWorkTitle: string | null
  failureReason: string | null
  failureCategory: string | null
  inputTokens: number | null
  outputTokens: number | null
  totalTokens: number | null
  costAmount: number | null
  costCurrency: string | null
  contextWindowUsed: number | null
  contextWindowSize: number | null
  contextUsagePercent?: number | null
  toolCallCount: number | null
  toolErrorCount: number | null
}

export interface StatusCounts {
  active: number
  waiting: number
  completed: number
  failed: number
}

const ACTIVE_STATUSES = new Set(['active'])

function sessionToCard(s: AgentActivitySession): SessionCard {
  const usage = s.usage
  const eventSummary = s.eventSummary
  return {
    issueId: s.issueId,
    issueNumber: String(s.issueNumber),
    issueTitle: s.issueTitle,
    issueStage: s.issueStage,
    sessionId: s.sessionId,
    status: s.status,
    model: s.model,
    resolvedModel: eventSummary?.resolvedModel ?? null,
    taskDescription: s.taskDescription,
    title: s.currentWorkItem?.title ?? s.taskDescription,
    createdAt: s.createdAt,
    completedAt: s.completedAt,
    lastActivityAt: s.lastActivityAt,
    activityPreviews: s.lastActivity ? [{ kind: s.lastActivity.kind, text: s.lastActivity.text }] : [],
    taskProgress: s.taskProgress,
    currentWorkTitle: s.currentWorkItem?.title ?? null,
    failureReason: s.failureReason,
    failureCategory: eventSummary?.failureCategory ?? null,
    inputTokens: usage?.inputTokens ?? null,
    outputTokens: usage?.outputTokens ?? null,
    totalTokens: usage?.totalTokens ?? null,
    costAmount: usage?.costAmount ?? null,
    costCurrency: usage?.costCurrency ?? null,
    contextWindowUsed: usage?.contextWindowUsed ?? null,
    contextWindowSize: usage?.contextWindowSize ?? null,
    contextUsagePercent: usage?.contextUsagePercent ?? null,
    toolCallCount: eventSummary?.toolCallCount ?? null,
    toolErrorCount: eventSummary?.toolErrorCount ?? null,
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
        completed: 0,
        failed: 0,
      },
      slotUsage: data?.summary.slots ?? { active: 0, max: 0 },
    }
  }, [data])
}
