// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { describe, expect, it } from 'vitest'
import type { SessionCard as SessionCardType } from '../model/activity-cards'
import { RecentCard } from './SessionCard'

function makeCard(overrides: Partial<SessionCardType> = {}): SessionCardType {
  return {
    issueId: 'issue-1',
    issueNumber: '12',
    issueTitle: 'Fix project selector',
    issueStage: 'Build',
    sessionId: 'session-1',
    status: 'completed',
    model: null,
    taskDescription: null,
    title: 'Implement CLI active project state',
    createdAt: '2026-01-01T00:00:00Z',
    completedAt: '2026-01-01T00:10:00Z',
    lastActivityAt: '2026-01-01T00:10:00Z',
    activityPreviews: [],
    taskProgress: null,
    currentWorkTitle: 'Implement CLI active project state',
    failureReason: null,
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
