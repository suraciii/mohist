import '@testing-library/jest-dom'
import { render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { describe, expect, it } from 'vitest'
import type { SessionCard as SessionCardType, WaitingCard as WaitingCardType } from '@/entities/agent-ops'
import { ActiveSessionCard, RecentCard, WaitingCard } from './SessionCard'

function makeCard(overrides: Partial<SessionCardType> = {}): SessionCardType {
  return {
    issueId: 'issue-1',
    issueNumber: '12',
    issueTitle: 'Fix project selector',
    issueStage: 'Build',
    sessionId: 'session-1',
    status: 'completed',
    model: null,
    resolvedModel: null,
    taskDescription: null,
    title: 'Implement CLI active project state',
    createdAt: '2026-01-01T00:00:00Z',
    completedAt: '2026-01-01T00:10:00Z',
    lastActivityAt: '2026-01-01T00:10:00Z',
    activityPreviews: [],
    taskProgress: null,
    currentWorkTitle: 'Implement CLI active project state',
    failureReason: null,
    failureCategory: null,
    inputTokens: null,
    outputTokens: null,
    totalTokens: null,
    costAmount: null,
    costCurrency: null,
    contextWindowUsed: null,
    contextWindowSize: null,
    contextUsagePercent: null,
    healthStatus: null,
    toolCallCount: null,
    toolErrorCount: null,
    ...overrides,
  }
}

describe('RecentCard', () => {
  it('shows workflow stage and work item title for completed activity entries', () => {
    render(
      <MemoryRouter>
        <RecentCard card={makeCard({ issueStage: 'Check', title: 'AI review' })} />
      </MemoryRouter>,
    )

    expect(screen.getByText('#12')).toBeInTheDocument()
    expect(screen.getByText('Check')).toBeInTheDocument()
    expect(screen.getByText('Fix project selector')).toBeInTheDocument()
    expect(screen.getByText('AI review')).toBeInTheDocument()
  })

  it('distinguishes multiple completed work items for the same issue', () => {
    render(
      <MemoryRouter>
        <RecentCard card={makeCard({ issueStage: 'Plan', title: 'Draft implementation plan' })} />
        <RecentCard card={makeCard({ issueStage: 'Build', sessionId: 'session-2', title: 'Update CLI commands' })} />
      </MemoryRouter>,
    )

    expect(screen.getAllByText('#12')).toHaveLength(2)
    expect(screen.getByText('Plan')).toBeInTheDocument()
    expect(screen.getByText('Build')).toBeInTheDocument()
    expect(screen.getByText('Draft implementation plan')).toBeInTheDocument()
    expect(screen.getByText('Update CLI commands')).toBeInTheDocument()
  })
})

describe('ActiveSessionCard context health', () => {
  const NOW = new Date('2026-01-01T00:10:00Z').getTime()

  it('hides the context health indicator when window size is zero', () => {
    render(
      <MemoryRouter>
        <ActiveSessionCard
          card={makeCard({
            status: 'active',
            contextWindowUsed: 0,
            contextWindowSize: 0,
            contextUsagePercent: null,
          })}
          now={NOW}
        />
      </MemoryRouter>,
    )
    expect(screen.queryByTestId('context-health-indicator')).toBeNull()
  })

  it('hides the context health indicator when contextWindowSize is null', () => {
    render(
      <MemoryRouter>
        <ActiveSessionCard
          card={makeCard({
            status: 'active',
            contextWindowUsed: null,
            contextWindowSize: null,
          })}
          now={NOW}
        />
      </MemoryRouter>,
    )
    expect(screen.queryByTestId('context-health-indicator')).toBeNull()
  })

  it('renders a quiet green indicator at low usage (no glyph, no role)', () => {
    render(
      <MemoryRouter>
        <ActiveSessionCard
          card={makeCard({
            status: 'active',
            contextWindowUsed: 300_000,
            contextWindowSize: 1_000_000,
            contextUsagePercent: 30,
            healthStatus: 'green',
          })}
          now={NOW}
        />
      </MemoryRouter>,
    )
    const indicator = screen.getByTestId('context-health-indicator')
    expect(indicator).toHaveAttribute('data-status', 'green')
    expect(indicator).toHaveAttribute('data-severity', 'ok')
    expect(indicator).toHaveTextContent('30%')
    expect(indicator).toHaveAttribute('title', 'Context usage 30%')
    expect(indicator).not.toHaveAttribute('role')
    expect(indicator).not.toHaveAttribute('aria-live')
    expect(screen.queryByTestId('context-health-glyph')).toBeNull()
  })

  it('renders yellow alert treatment at moderate usage with role="status" and warning glyph', () => {
    render(
      <MemoryRouter>
        <ActiveSessionCard
          card={makeCard({
            status: 'active',
            contextWindowUsed: 720_000,
            contextWindowSize: 1_000_000,
            contextUsagePercent: 72,
            healthStatus: 'yellow',
          })}
          now={NOW}
        />
      </MemoryRouter>,
    )
    const indicator = screen.getByTestId('context-health-indicator')
    expect(indicator).toHaveAttribute('data-status', 'yellow')
    expect(indicator).toHaveAttribute('data-severity', 'warning')
    expect(indicator).toHaveTextContent('72%')
    expect(indicator).toHaveAttribute('role', 'status')
    expect(indicator).toHaveAttribute('title', 'Context window 72% full — near limit')
    expect(indicator).toHaveAttribute('aria-label', 'Context window 72% full — near limit')
    expect(screen.getByTestId('context-health-glyph')).toBeInTheDocument()
  })

  it('renders red critical alert treatment at high usage with role="alert" and aria-live="polite"', () => {
    render(
      <MemoryRouter>
        <ActiveSessionCard
          card={makeCard({
            status: 'active',
            contextWindowUsed: 950_000,
            contextWindowSize: 1_000_000,
            contextUsagePercent: 95,
            healthStatus: 'red',
          })}
          now={NOW}
        />
      </MemoryRouter>,
    )
    const indicator = screen.getByTestId('context-health-indicator')
    expect(indicator).toHaveAttribute('data-status', 'red')
    expect(indicator).toHaveAttribute('data-severity', 'critical')
    expect(indicator).toHaveTextContent('95%')
    expect(indicator).toHaveAttribute('role', 'alert')
    expect(indicator).toHaveAttribute('aria-live', 'polite')
    expect(indicator).toHaveAttribute('title', 'Context window 95% full — at limit, compact or reset recommended')
    expect(indicator).toHaveAttribute('aria-label', 'Context window 95% full — at limit, compact or reset recommended')
    expect(screen.getByTestId('context-health-glyph')).toBeInTheDocument()
  })
})

describe('WaitingCard', () => {
  it('uses warning treatment for a blocked wait', () => {
    const card: WaitingCardType = { issueId: 'issue-1', issueNumber: '12', issueTitle: 'Resolve merge conflict', issueStage: 'Check', label: 'Blocked' }
    render(<MemoryRouter><WaitingCard card={card} /></MemoryRouter>)

    expect(screen.getByTestId('waiting-card-chip')).toHaveAttribute('data-tone', 'warning')
  })
})
