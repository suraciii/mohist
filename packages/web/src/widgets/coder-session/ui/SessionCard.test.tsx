// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { describe, expect, it } from 'vitest'
import type { SessionCard as SessionCardType } from '../model/activity-cards'
import { ActiveSessionCard, RecentCard } from './SessionCard'

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

  it('renders a green dot indicator at low usage', () => {
    render(
      <MemoryRouter>
        <ActiveSessionCard
          card={makeCard({
            status: 'active',
            contextWindowUsed: 300_000,
            contextWindowSize: 1_000_000,
            contextUsagePercent: 30,
          })}
          now={NOW}
        />
      </MemoryRouter>,
    )
    const indicator = screen.getByTestId('context-health-indicator')
    expect(indicator).toHaveAttribute('data-status', 'green')
    expect(indicator).toHaveTextContent('30%')
  })

  it('renders a yellow dot indicator at moderate usage', () => {
    render(
      <MemoryRouter>
        <ActiveSessionCard
          card={makeCard({
            status: 'active',
            contextWindowUsed: 720_000,
            contextWindowSize: 1_000_000,
            contextUsagePercent: 72,
          })}
          now={NOW}
        />
      </MemoryRouter>,
    )
    const indicator = screen.getByTestId('context-health-indicator')
    expect(indicator).toHaveAttribute('data-status', 'yellow')
    expect(indicator).toHaveTextContent('72%')
  })

  it('renders a red dot indicator at high usage', () => {
    render(
      <MemoryRouter>
        <ActiveSessionCard
          card={makeCard({
            status: 'active',
            contextWindowUsed: 950_000,
            contextWindowSize: 1_000_000,
            contextUsagePercent: 95,
          })}
          now={NOW}
        />
      </MemoryRouter>,
    )
    const indicator = screen.getByTestId('context-health-indicator')
    expect(indicator).toHaveAttribute('data-status', 'red')
    expect(indicator).toHaveTextContent('95%')
  })
})
