import { describe, it, expect } from 'vitest'
import { computeUsageSnapshot } from './usage-snapshot'
import type { AgentActivitySession } from '../../../entities/agent'

function makeSession(overrides: Partial<AgentActivitySession> = {}): AgentActivitySession {
  return {
    issueNumber: 1,
    issueTitle: 'Test',
    issueStage: 'build',
    issueStatus: null,
    sessionId: 'session-1',
    status: 'completed',
    model: null,
    taskDescription: null,
    createdAt: '2026-01-01T00:00:00Z',
    completedAt: null,
    lastActivityAt: '2026-01-01T00:00:00Z',
    currentWorkItem: null,
    taskProgress: null,
    lastActivity: null,
    failureReason: null,
    ...overrides,
  }
}

describe('computeUsageSnapshot', () => {
  it('sums token/cost totals across sessions', () => {
    const sessions: AgentActivitySession[] = [
      makeSession({
        usage: { inputTokens: 100, outputTokens: 50, totalTokens: 150, costAmount: 0.02, costCurrency: 'USD' },
      }),
      makeSession({
        usage: { inputTokens: 200, outputTokens: 80, totalTokens: 280, costAmount: 0.05, costCurrency: 'USD' },
      }),
    ]

    const result = computeUsageSnapshot(sessions)
    expect(result.inputTokens).toBe(300)
    expect(result.outputTokens).toBe(130)
    expect(result.totalTokens).toBe(430)
    expect(result.costAmount).toBe(0.07)
    expect(result.costCurrency).toBe('USD')
  })

  it('returns zeroes for an empty sessions array', () => {
    const result = computeUsageSnapshot([])
    expect(result.inputTokens).toBe(0)
    expect(result.outputTokens).toBe(0)
    expect(result.totalTokens).toBe(0)
    expect(result.costAmount).toBe(0)
    expect(result.costCurrency).toBeNull()
  })

  it('treats missing usage object as zero contribution', () => {
    const sessions: AgentActivitySession[] = [
      makeSession({ usage: undefined }),
      makeSession({ usage: { inputTokens: 100, outputTokens: 50, totalTokens: 150 } }),
    ]

    const result = computeUsageSnapshot(sessions)
    expect(result.inputTokens).toBe(100)
    expect(result.outputTokens).toBe(50)
    expect(result.totalTokens).toBe(150)
  })

  it('treats null additive fields as zero', () => {
    const sessions: AgentActivitySession[] = [
      makeSession({
        usage: { inputTokens: null, outputTokens: null, totalTokens: null, costAmount: null, costCurrency: null },
      }),
      makeSession({
        usage: { inputTokens: 50, outputTokens: 25, totalTokens: 75, costAmount: 0.01, costCurrency: 'USD' },
      }),
    ]

    const result = computeUsageSnapshot(sessions)
    expect(result.inputTokens).toBe(50)
    expect(result.outputTokens).toBe(25)
    expect(result.totalTokens).toBe(75)
    expect(result.costAmount).toBe(0.01)
    expect(result.costCurrency).toBe('USD')
  })

  it('treats undefined additive fields as zero', () => {
    const sessions: AgentActivitySession[] = [
      makeSession({
        usage: {},
      }),
      makeSession({
        usage: { inputTokens: 100, outputTokens: 50, totalTokens: 150 },
      }),
    ]

    const result = computeUsageSnapshot(sessions)
    expect(result.inputTokens).toBe(100)
    expect(result.outputTokens).toBe(50)
    expect(result.totalTokens).toBe(150)
  })

  it('does not aggregate non-additive context-window fields', () => {
    const sessions: AgentActivitySession[] = [
      makeSession({
        usage: {
          inputTokens: 100,
          outputTokens: 50,
          totalTokens: 150,
          contextWindowUsed: 500000,
          contextWindowSize: 1000000,
          contextUsagePercent: 50,
          healthStatus: 'green',
        },
      }),
      makeSession({
        usage: {
          inputTokens: 200,
          outputTokens: 80,
          totalTokens: 280,
          contextWindowUsed: 300000,
          contextWindowSize: 1000000,
          contextUsagePercent: 30,
          healthStatus: 'yellow',
        },
      }),
    ]

    const result = computeUsageSnapshot(sessions)
    expect(result.inputTokens).toBe(300)
    expect(result.outputTokens).toBe(130)
    expect(result.totalTokens).toBe(430)

    const snapshotKeys = Object.keys(result) as (keyof typeof result)[]
    expect(snapshotKeys).not.toContain('contextWindowUsed')
    expect(snapshotKeys).not.toContain('contextWindowSize')
    expect(snapshotKeys).not.toContain('contextUsagePercent')
    expect(snapshotKeys).not.toContain('healthStatus')
  })

  it('does not aggregate cachedReadTokens or thoughtTokens (non-additive per the task)', () => {
    const sessions: AgentActivitySession[] = [
      makeSession({
        usage: { inputTokens: 100, outputTokens: 50, totalTokens: 150, cachedReadTokens: 1000, thoughtTokens: 500 },
      }),
    ]

    const result = computeUsageSnapshot(sessions)
    expect(result.inputTokens).toBe(100)
    expect(result.outputTokens).toBe(50)
    expect(result.totalTokens).toBe(150)

    const snapshotKeys = Object.keys(result) as (keyof typeof result)[]
    expect(snapshotKeys).not.toContain('cachedReadTokens')
    expect(snapshotKeys).not.toContain('thoughtTokens')
  })

  it('echoes costCurrency from first non-null session', () => {
    const sessions: AgentActivitySession[] = [
      makeSession({
        usage: { costCurrency: null },
      }),
      makeSession({
        usage: { inputTokens: 100, outputTokens: 50, totalTokens: 150, costAmount: 0.01, costCurrency: 'EUR' },
      }),
    ]

    const result = computeUsageSnapshot(sessions)
    expect(result.costCurrency).toBe('EUR')
  })

  it('returns null costCurrency when no session has a currency', () => {
    const sessions: AgentActivitySession[] = [
      makeSession({ usage: {} }),
      makeSession({ usage: { inputTokens: 100, outputTokens: 50, totalTokens: 150, costAmount: 0.01 } }),
    ]

    const result = computeUsageSnapshot(sessions)
    expect(result.costCurrency).toBeNull()
  })

  it('does not throw for any combination of missing fields', () => {
    const sessions: AgentActivitySession[] = [
      makeSession({ usage: null as unknown as undefined }),
      makeSession({ usage: { inputTokens: 'invalid' as unknown as number } }),
      makeSession({ usage: undefined }),
      makeSession({ usage: {} }),
    ]

    expect(() => computeUsageSnapshot(sessions)).not.toThrow()
  })
})
