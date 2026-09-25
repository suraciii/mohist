import { useMemo } from 'react'
import { useAgentActivity } from '../../agent/@x/activity'
import type { ActivityExecutionState, AgentActivitySession, AgentActivityWaiting } from '../../agent/@x/activity'
import type { ContextUsageHistoryEntry } from '../../coder-session/@x/activity'

export interface TaskProgress {
  completed: number
  total: number
}

export interface ActivityPreview {
  kind: 'text' | 'tool'
  text: string
}

export interface WaitingCard {
  issueNumber: number
  issueTitle: string
  issueStage: string | null
  label: 'Needs Approval' | 'Blocked'
  questionPreview?: string
  questionId?: string
  questionAskedAt?: string
}

/**
 * One sample of the bounded context-usage history carried through
 * `SessionCard.contextUsageHistory`. Mirrors the wire projection of
 * `AgentUsageDto.contextUsageHistory` exposed by the activity feed
 * (issue-245 T-002 / design D5). `null` means the server emitted no
 * history for this session; the Pulse mini-chart component degrades
 * to hidden in that case (and when fewer than 2 samples are present).
 */
export type SessionCardUsageHistoryEntry = ContextUsageHistoryEntry

export interface SessionCard {
  issueNumber: number | null
  issueTitle: string
  issueStage: string
  sessionId: string
  status: string
  executionState?: ActivityExecutionState
  evidence?: AgentActivitySession['evidence']
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
  healthStatus?: string | null
  /**
   * Bounded context-usage history carried from the live activity
   * source (`AgentUsageDto.contextUsageHistory`). Drives the
   * `ContextUsageTrendMiniChart` on the Pulse compact card.
   * Absent/null when the session has not recorded a usage sample
   * (the wire omits the field entirely in that case).
   */
  contextUsageHistory?: SessionCardUsageHistoryEntry[] | null
  toolCallCount: number | null
  toolErrorCount: number | null
}

export interface StatusCounts {
  active: number
  waiting: number
  completed: number
  failed: number
}

export function sessionToCard(s: AgentActivitySession): SessionCard {
  const usage = s.usage
  const eventSummary = s.eventSummary
  const rawHistory = usage?.contextUsageHistory
  const contextUsageHistory = rawHistory == null || rawHistory.length === 0 ? null : rawHistory
  return {
    issueNumber: s.issueNumber,
    issueTitle: s.issueTitle,
    issueStage: s.issueStage,
    sessionId: s.sessionId,
    status: s.status,
    executionState: s.executionState ?? (s.status === 'active' ? 'running' : 'not-running'),
    evidence: s.evidence ?? null,
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
    healthStatus: usage?.healthStatus ?? null,
    contextUsageHistory,
    toolCallCount: eventSummary?.toolCallCount ?? null,
    toolErrorCount: eventSummary?.toolErrorCount ?? null,
  }
}

function waitingToCard(w: AgentActivityWaiting): WaitingCard {
  return {
    issueNumber: w.issueNumber,
    issueTitle: w.issueTitle,
    issueStage: w.stage,
    label: w.label,
    questionPreview: w.preview ?? undefined,
    questionAskedAt: w.requestedAt ?? undefined,
  }
}

export interface ActivityCardsState {
  activeCards: SessionCard[]
  needsVerificationCards?: SessionCard[]
  activeCardByIssueNumber: Map<number, SessionCard>
  sessionCardByIssueNumber?: Map<number, SessionCard>
  recentCards: SessionCard[]
  waitingCards: WaitingCard[]
  statusCounts: StatusCounts & { needsVerification?: number }
  slotUsage: { active: number; max: number }
  isLoading: boolean
  isError: boolean
}

export function useActivityCards(): ActivityCardsState {
  const { data, isLoading = false, isError = false } = useAgentActivity()

  return useMemo(() => {
    const cards = (data?.sessions ?? []).map(sessionToCard)
    const activeCards = cards.filter((c) => c.executionState === 'running' || c.executionState === 'queued')
    const needsVerificationCards = cards.filter((c) => c.executionState === 'needs-verification')
    const recentCards = cards.filter((c) => c.executionState === 'not-running')
    const waitingCards = (data?.waiting ?? []).map(waitingToCard)

    const activeCardByIssueNumber = new Map<number, SessionCard>()
    for (const card of activeCards) {
      if (card.issueNumber !== null) activeCardByIssueNumber.set(card.issueNumber, card)
    }
    const sessionCardByIssueNumber = new Map<number, SessionCard>(activeCardByIssueNumber)
    for (const card of needsVerificationCards) {
      if (card.issueNumber !== null) sessionCardByIssueNumber.set(card.issueNumber, card)
    }

    return {
      activeCards,
      needsVerificationCards,
      activeCardByIssueNumber,
      sessionCardByIssueNumber,
      recentCards,
      waitingCards,
      statusCounts: data?.summary ?? {
        active: activeCards.length,
        waiting: waitingCards.length,
        completed: 0,
        failed: 0,
        needsVerification: needsVerificationCards.length,
      },
      slotUsage: data?.summary.slots ?? { active: 0, max: 0 },
      isLoading,
      isError,
    }
  }, [data, isLoading, isError])
}
