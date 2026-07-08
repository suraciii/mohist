import { describe, it, expect, vi } from 'vitest'
import { renderHook } from '@testing-library/react'
import { sessionToCard, useActivityCards } from './activity-cards'
import type { AgentActivitySession } from '../../../entities/agent'

const useAgentActivityMock = vi.fn()

vi.mock('../../../entities/agent', () => ({
  useAgentActivity: (...args: unknown[]) => useAgentActivityMock(...args),
}))

function makeSession(overrides: Partial<AgentActivitySession> = {}): AgentActivitySession {
  return {
    issueId: 'issue-1',
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
    useAgentActivityMock.mockReset()
  })

  it('indexes active sessions by numeric issue number for O(1) join from Pulse zone', () => {
    useAgentActivityMock.mockReturnValue({
      data: {
        summary: { active: 2, waiting: 0, completed: 0, failed: 0, slots: { active: 2, max: 8 } },
        sessions: [
          makeSession({ sessionId: 'a-12', issueNumber: 12, status: 'active' }),
          makeSession({ sessionId: 'a-99', issueNumber: 99, status: 'active' }),
        ],
        waiting: [],
      },
    })

    const { result } = renderHook(() => useActivityCards())

    expect(result.current.activeCardByIssueNumber.size).toBe(2)
    expect(result.current.activeCardByIssueNumber.get(12)?.sessionId).toBe('a-12')
    expect(result.current.activeCardByIssueNumber.get(99)?.sessionId).toBe('a-99')
    expect(result.current.activeCardByIssueNumber.get(1)).toBeUndefined()
  })

  it('does NOT index non-active sessions (completed, failed, etc.)', () => {
    useAgentActivityMock.mockReturnValue({
      data: {
        summary: { active: 1, waiting: 0, completed: 1, failed: 1, slots: { active: 1, max: 8 } },
        sessions: [
          makeSession({ sessionId: 'a-active', issueNumber: 12, status: 'active' }),
          makeSession({ sessionId: 'a-completed', issueNumber: 13, status: 'completed' }),
          makeSession({ sessionId: 'a-failed', issueNumber: 14, status: 'failed' }),
        ],
        waiting: [],
      },
    })

    const { result } = renderHook(() => useActivityCards())

    expect(result.current.activeCardByIssueNumber.size).toBe(1)
    expect(result.current.activeCardByIssueNumber.get(12)?.sessionId).toBe('a-active')
    expect(result.current.activeCardByIssueNumber.get(13)).toBeUndefined()
    expect(result.current.activeCardByIssueNumber.get(14)).toBeUndefined()
  })

  it('returns an empty map when the activity feed has no sessions', () => {
    useAgentActivityMock.mockReturnValue({ data: undefined })

    const { result } = renderHook(() => useActivityCards())

    expect(result.current.activeCardByIssueNumber).toBeInstanceOf(Map)
    expect(result.current.activeCardByIssueNumber.size).toBe(0)
  })

  it('preserves the activity feed loading and error state for page-level gates', () => {
    useAgentActivityMock.mockReturnValue({
      data: undefined,
      isLoading: true,
      isError: true,
    })

    const { result } = renderHook(() => useActivityCards())

    expect(result.current.isLoading).toBe(true)
    expect(result.current.isError).toBe(true)
  })

  it('keeps only one active session per issue number when duplicates exist', () => {
    useAgentActivityMock.mockReturnValue({
      data: {
        summary: { active: 2, waiting: 0, completed: 0, failed: 0, slots: { active: 2, max: 8 } },
        sessions: [
          makeSession({ sessionId: 'first-12', issueNumber: 12, status: 'active' }),
          makeSession({ sessionId: 'second-12', issueNumber: 12, status: 'active' }),
        ],
        waiting: [],
      },
    })

    const { result } = renderHook(() => useActivityCards())

    expect(result.current.activeCardByIssueNumber.size).toBe(1)
    expect(result.current.activeCardByIssueNumber.get(12)).toBeDefined()
    expect(['first-12', 'second-12']).toContain(result.current.activeCardByIssueNumber.get(12)!.sessionId)
  })
})
