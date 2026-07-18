import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { act, renderHook } from '@testing-library/react'
import { createElement, type ReactNode } from 'react'
import { http, HttpResponse } from 'msw'
import { sessionToCard, useActivityCards } from './activity-cards'
import type { AgentActivity, AgentActivitySession } from '../../../entities/agent'
import { ProjectProvider } from '../../../entities/project'
import { useMswServer } from '../../../../tests/support/msw'

// react-query resolves via notifyManager's scheduled timers; advance the clock
// ourselves under fake timers instead of polling wall-clock time (waitFor's
// default 1000ms is too tight on slow CI — design/testing.md: advance fake
// time, don't poll harder).
async function flush() {
  await act(async () => {
    await vi.advanceTimersByTimeAsync(1000)
  })
}

const PROJECT_ID = 'project-1'
const ACTIVITY_PATH = '*/api/projects/:projectId/agent/activity'

let activityResponse: AgentActivity | 'never' | 'error'

useMswServer(
  http.get(ACTIVITY_PATH, () => {
    if (activityResponse === 'never') return new Promise<never>(() => {})
    if (activityResponse === 'error') return new HttpResponse(null, { status: 500 })
    return HttpResponse.json({ success: true, data: activityResponse })
  }),
)

function makeActivity(sessions: AgentActivitySession[] = []): AgentActivity {
  const active = sessions.filter((session) => session.status === 'active').length
  return {
    summary: {
      active,
      waiting: 0,
      completed: sessions.filter((session) => session.status === 'completed').length,
      failed: sessions.filter((session) => session.status === 'failed').length,
      slots: { active, max: 8 },
    },
    sessions,
    waiting: [],
  }
}

function renderActivityCards(data?: AgentActivity) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 } },
  })
  if (data) {
    queryClient.setQueryData(['agent-activity', undefined, PROJECT_ID], data)
  }

  const wrapper = ({ children }: { children: ReactNode }) =>
    createElement(
      QueryClientProvider,
      { client: queryClient },
      createElement(ProjectProvider, { initialProjectId: PROJECT_ID, children }),
    )

  return renderHook(() => useActivityCards(), { wrapper })
}

function makeSession(overrides: Partial<AgentActivitySession> = {}): AgentActivitySession {
  return {
    issueNumber: 12,
    issueTitle: 'Fix project selector',
    issueStage: 'Build',
    issueStatus: null,
    sessionId: 'session-1',
    status: 'active',
    model: 'claude-opus-4-7',
    taskDescription: 'Implement CLI active project state',
    createdAt: '2026-01-01T00:00:00Z',
    completedAt: null,
    lastActivityAt: '2026-01-01T00:00:30Z',
    currentWorkItem: null,
    taskProgress: null,
    lastActivity: null,
    failureReason: null,
    ...overrides,
  }
}

beforeEach(() => {
  vi.useFakeTimers()
})

afterEach(() => {
  vi.useRealTimers()
})

describe('sessionToCard', () => {
  it('maps the activity DTO usage into the SessionCard snapshot fields unchanged', () => {
    const card = sessionToCard(
      makeSession({
        usage: {
          inputTokens: 100,
          outputTokens: 50,
          totalTokens: 150,
          costAmount: 0.02,
          costCurrency: 'USD',
          contextWindowUsed: 500_000,
          contextWindowSize: 1_000_000,
          contextUsagePercent: 50,
        },
      }),
    )

    expect(card.inputTokens).toBe(100)
    expect(card.outputTokens).toBe(50)
    expect(card.totalTokens).toBe(150)
    expect(card.costAmount).toBe(0.02)
    expect(card.costCurrency).toBe('USD')
    expect(card.contextWindowUsed).toBe(500_000)
    expect(card.contextWindowSize).toBe(1_000_000)
    expect(card.contextUsagePercent).toBe(50)
  })

  it('maps the bounded context-usage history through to SessionCard.contextUsageHistory', () => {
    const history = [
      { at: '2026-01-01T00:00:00Z', percent: 10 },
      { at: '2026-01-01T00:01:00Z', percent: 30 },
      { at: '2026-01-01T00:02:00Z', percent: 60 },
      { at: '2026-01-01T00:03:00Z', percent: 80 },
    ]

    const card = sessionToCard(
      makeSession({
        usage: {
          contextWindowUsed: 800_000,
          contextWindowSize: 1_000_000,
          contextUsagePercent: 80,
          contextUsageHistory: history,
        },
      }),
    )

    expect(card.contextUsageHistory).toEqual(history)
    expect(card.contextUsageHistory).toHaveLength(4)
  })

  it('maps an absent contextUsageHistory field to null (wire omits the field when empty)', () => {
    const card = sessionToCard(makeSession({ usage: { totalTokens: 100 } }))

    expect(card.contextUsageHistory).toBeNull()
  })

  it('maps an empty array explicitly to null so the Pulse chart knows there is nothing to plot', () => {
    const card = sessionToCard(
      makeSession({
        usage: {
          contextUsageHistory: [],
        },
      }),
    )

    expect(card.contextUsageHistory).toBeNull()
  })

  it('treats missing usage object as null for every usage field', () => {
    const card = sessionToCard(makeSession({ usage: undefined }))

    expect(card.inputTokens).toBeNull()
    expect(card.outputTokens).toBeNull()
    expect(card.totalTokens).toBeNull()
    expect(card.costAmount).toBeNull()
    expect(card.costCurrency).toBeNull()
    expect(card.contextWindowUsed).toBeNull()
    expect(card.contextWindowSize).toBeNull()
    expect(card.contextUsagePercent).toBeNull()
    expect(card.contextUsageHistory).toBeNull()
  })
})

describe('useActivityCards — activeCardByIssueNumber', () => {
  beforeEach(() => {
    activityResponse = makeActivity()
  })

  it('indexes active sessions by numeric issue number for O(1) join from Pulse zone', () => {
    const { result } = renderActivityCards(makeActivity([
      makeSession({ sessionId: 'a-12', issueNumber: 12, status: 'active' }),
      makeSession({ sessionId: 'a-99', issueNumber: 99, status: 'active' }),
    ]))

    expect(result.current.activeCardByIssueNumber.size).toBe(2)
    expect(result.current.activeCardByIssueNumber.get(12)?.sessionId).toBe('a-12')
    expect(result.current.activeCardByIssueNumber.get(99)?.sessionId).toBe('a-99')
    expect(result.current.activeCardByIssueNumber.get(1)).toBeUndefined()
  })

  it('does NOT index non-active sessions (completed, failed, etc.)', () => {
    const { result } = renderActivityCards(makeActivity([
      makeSession({ sessionId: 'a-active', issueNumber: 12, status: 'active' }),
      makeSession({ sessionId: 'a-completed', issueNumber: 13, status: 'completed' }),
      makeSession({ sessionId: 'a-failed', issueNumber: 14, status: 'failed' }),
    ]))

    expect(result.current.activeCardByIssueNumber.size).toBe(1)
    expect(result.current.activeCardByIssueNumber.get(12)?.sessionId).toBe('a-active')
    expect(result.current.activeCardByIssueNumber.get(13)).toBeUndefined()
    expect(result.current.activeCardByIssueNumber.get(14)).toBeUndefined()
  })

  it('returns an empty map when the activity feed has no sessions', () => {
    const { result } = renderActivityCards(makeActivity())

    expect(result.current.activeCardByIssueNumber).toBeInstanceOf(Map)
    expect(result.current.activeCardByIssueNumber.size).toBe(0)
  })

  it('preserves the activity feed loading state for page-level gates', () => {
    activityResponse = 'never'

    const { result } = renderActivityCards()

    expect(result.current.isLoading).toBe(true)
    expect(result.current.isError).toBe(false)
  })

  it('preserves the activity feed error state for page-level gates', async () => {
    activityResponse = 'error'

    const { result } = renderActivityCards()

    await flush()
    expect(result.current.isLoading).toBe(false)
    expect(result.current.isError).toBe(true)
  })

  it('keeps only one active session per issue number when duplicates exist', () => {
    const { result } = renderActivityCards(makeActivity([
      makeSession({ sessionId: 'first-12', issueNumber: 12, status: 'active' }),
      makeSession({ sessionId: 'second-12', issueNumber: 12, status: 'active' }),
    ]))

    expect(result.current.activeCardByIssueNumber.size).toBe(1)
    expect(result.current.activeCardByIssueNumber.get(12)).toBeDefined()
    expect(['first-12', 'second-12']).toContain(result.current.activeCardByIssueNumber.get(12)!.sessionId)
  })
})
