import '@testing-library/jest-dom'
import { afterEach, describe, expect, it } from 'vitest'
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import { TEST_PROJECT } from '../../../../tests/test-utils'
import { ActivityPage, type ActivityPageDependencies } from './ActivityPage'
import { RunnerSummary } from '../../../widgets/runner-status'
import { deriveRunnerSummary } from '../../../entities/runner'
import type { ActivityEvent, ActivityEventType } from '../../../widgets/coder-session'

const NOW = Date.parse('2026-01-01T03:00:00.000Z')

function makeEvent(
  type: ActivityEventType,
  attention: ActivityEvent['attention'],
  overrides: Partial<ActivityEvent> = {},
): ActivityEvent {
  return {
    id: overrides.id ?? `${type}-${attention}-${overrides.title ?? 'event'}`,
    type,
    attention,
    time: '2026-01-01T00:00:00.000Z',
    title: 'Event',
    description: 'Description',
    targets: {},
    ...overrides,
  }
}

const dependencies: ActivityPageDependencies = {
  activityEventsHook: () => ({ events: [], isLoading: false, isError: false }),
  activityCardsHook: () => ({
    activeCards: [],
    activeCardByIssueNumber: new Map(),
    recentCards: [],
    waitingCards: [],
    statusCounts: { active: 0, waiting: 0, completed: 0, failed: 0 },
    slotUsage: { active: 0, max: 0 },
    isLoading: false,
    isError: false,
  }),
  activityUsageSnapshotHook: () => ({
    inputTokens: 0,
    outputTokens: 0,
    totalTokens: 0,
    costAmount: 0,
    costCurrency: null,
  }),
  RunnerSummaryBadge: () => <RunnerSummary summary={deriveRunnerSummary([])} />,
}

function renderPage(events: ActivityEvent[]) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 }, mutations: { retry: false } },
  })

  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId={TEST_PROJECT.id} initialProjects={[TEST_PROJECT]}>
        <MemoryRouter initialEntries={[`/${TEST_PROJECT.name}/activity`]}>
          <ActivityPage
            dependencies={{ ...dependencies, activityEventsHook: () => ({ events, isLoading: false, isError: false }) }}
            now={NOW}
          />
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

afterEach(() => {
  cleanup()
})

describe('ActivityPage evidence feed', () => {
  it('renders event identity and keeps attention ahead of routine evidence', () => {
    renderPage([
      makeEvent('issue-state', 'routine', { id: 'issue-1', title: 'Routine issue' }),
      makeEvent('workflow-stage', 'approval', { id: 'approval-1', title: 'Needs review' }),
      makeEvent('failure', 'failure', { id: 'failure-1', title: 'Stage failed' }),
    ])

    const entries = screen.getAllByTestId('activity-event-entry')
    expect(entries).toHaveLength(3)
    expect(entries.map((entry) => entry.getAttribute('data-event-type'))).toEqual([
      'failure',
      'workflow-stage',
      'issue-state',
    ])
    expect(entries.map((entry) => entry.getAttribute('data-attention'))).toEqual([
      'failure',
      'approval',
      'routine',
    ])

    const attention = screen.getByTestId('activity-attention-zone')
    const routine = screen.getByTestId('activity-routine-zone')
    expect(attention.compareDocumentPosition(routine) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
    expect(within(attention).getByText('Stage failed')).toBeInTheDocument()
  })

  it('omits an empty attention zone and supports collapsing routine evidence', () => {
    renderPage([makeEvent('issue-state', 'routine', { id: 'routine-1', title: 'Routine issue' })])

    expect(screen.queryByTestId('activity-attention-zone')).not.toBeInTheDocument()
    expect(screen.getByTestId('activity-routine-zone')).toBeInTheDocument()
    expect(screen.getByTestId('activity-event-entry')).toBeInTheDocument()

    fireEvent.click(screen.getByTestId('activity-routine-toggle'))

    expect(screen.getByTestId('activity-routine-toggle')).toHaveAttribute('aria-expanded', 'false')
    expect(screen.queryByTestId('activity-event-entry')).not.toBeInTheDocument()
  })

  it('filters by event type and attention, then clears to restore both zones', () => {
    renderPage([
      makeEvent('issue-state', 'routine', { id: 'issue-1' }),
      makeEvent('workflow-stage', 'approval', { id: 'approval-1' }),
      makeEvent('failure', 'failure', { id: 'failure-1' }),
    ])

    fireEvent.click(screen.getByTestId('activity-filter-issue-state'))
    expect(screen.getAllByTestId('activity-event-entry')).toHaveLength(1)
    expect(screen.getByTestId('activity-event-entry')).toHaveAttribute('data-event-type', 'issue-state')

    fireEvent.click(screen.getByTestId('activity-filter-clear'))
    fireEvent.click(screen.getByTestId('activity-filter-attention'))
    expect(screen.getAllByTestId('activity-event-entry')).toHaveLength(2)
    expect(screen.getByTestId('activity-attention-zone')).toBeInTheDocument()
    expect(screen.queryByTestId('activity-routine-zone')).not.toBeInTheDocument()

    fireEvent.click(screen.getByTestId('activity-filter-clear'))
    expect(screen.getAllByTestId('activity-event-entry')).toHaveLength(3)
    expect(screen.getByTestId('activity-routine-zone')).toBeInTheDocument()
  })

  it('uses shared semantic tokens for failure evidence', () => {
    renderPage([makeEvent('failure', 'failure', { id: 'failure-1', title: 'Stage failed' })])

    const entry = screen.getByTestId('activity-event-entry')
    expect(entry).toHaveClass('bg-danger-subtle', 'border-danger-border')
    expect(entry.querySelector('.bg-danger')).toBeInTheDocument()
  })

  it('derives terminal counts from rendered evidence instead of legacy snapshot counts', () => {
    renderPage([makeEvent('failure', 'failure', { id: 'failure-1', title: 'Stage failed', outcome: 'failed' })])

    expect(screen.getByTestId('status-bar-failed')).toHaveTextContent('Failed:1')
  })
})
