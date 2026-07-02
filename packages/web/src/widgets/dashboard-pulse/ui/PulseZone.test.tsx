// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { act, render, screen, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { describe, expect, it, vi, beforeEach } from 'vitest'
import { ProjectProvider } from '@/entities/project/model/ProjectContext'
import type {
  AgentActivity,
  AgentActivitySession,
} from '@/entities/agent/model/types'
import { PulseZone } from './PulseZone'

const mocks = vi.hoisted(() => ({
  activity: null as AgentActivity | null | undefined,
  useAgentActivity: vi.fn(() => ({ data: mocks.activity })),
}))

vi.mock('@/entities/agent/api/queries', () => ({
  useAgentActivity: mocks.useAgentActivity,
  useAgentStatus: () => ({ data: undefined }),
  useGlobalAgentSessions: () => ({ data: [] }),
}))

const TEST_PROJECT = {
  id: 'test-project',
  name: 'demo',
  createdAt: '2024-01-01T00:00:00.000Z',
  updatedAt: '2024-01-01T00:00:00.000Z',
  repositories: [],
}

function renderZone() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId={TEST_PROJECT.id} initialProjects={[TEST_PROJECT]}>
        <MemoryRouter initialEntries={['/']}>
          <PulseZone />
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

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
    currentWorkItem: { type: 'task', id: 't1', title: 'Wire the foobar handler', stage: 'Build', sessionWorkType: null },
    taskProgress: { completed: 3, total: 8 },
    lastActivity: null,
    failureReason: null,
    ...overrides,
  }
}

function makeActivity(
  sessions: AgentActivitySession[],
  summary?: Partial<AgentActivity['summary']>,
): AgentActivity {
  const active = sessions.filter((s) => s.status === 'active').length
  return {
    summary: {
      active,
      waiting: 0,
      completed: 0,
      failed: 0,
      slots: { active, max: 8 },
      ...summary,
    },
    sessions,
    waiting: [],
  }
}

describe('PulseZone', () => {
  beforeEach(() => {
    mocks.activity = null
    mocks.useAgentActivity.mockClear()
  })

  it('renders the capacity header with active/max slot usage', () => {
    mocks.activity = makeActivity([], { active: 0, slots: { active: 0, max: 8 } })

    renderZone()

    const header = screen.getByTestId('pulse-capacity-header')
    expect(within(header).getByTestId('pulse-slots')).toHaveTextContent('0/8 slots used')
  })

  it('does not render lifecycle status pills and keeps the slot-usage indicator', () => {
    mocks.activity = {
      summary: { active: 2, waiting: 1, completed: 4, failed: 1, slots: { active: 2, max: 8 } },
      sessions: [],
      waiting: [],
    }

    renderZone()

    expect(screen.queryByTestId('pulse-status-pills')).not.toBeInTheDocument()
    expect(screen.queryByTestId('pulse-pill-active')).not.toBeInTheDocument()
    expect(screen.queryByTestId('pulse-pill-waiting')).not.toBeInTheDocument()
    expect(screen.queryByTestId('pulse-pill-completed')).not.toBeInTheDocument()
    expect(screen.queryByTestId('pulse-pill-failed')).not.toBeInTheDocument()
    expect(screen.getByTestId('pulse-slots')).toHaveTextContent('2/8 slots used')
  })

  it('still shows 0/max when the active session list is empty', () => {
    mocks.activity = makeActivity([], { active: 0, slots: { active: 0, max: 8 } })

    renderZone()

    expect(screen.getByTestId('pulse-slots')).toHaveTextContent('0/8')
    expect(screen.getByTestId('pulse-empty-state')).toBeInTheDocument()
    expect(screen.queryByTestId('pulse-card-list')).not.toBeInTheDocument()
  })

  it('renders an empty-state affordance when there are no active sessions', () => {
    mocks.activity = makeActivity([], { active: 0, slots: { active: 0, max: 8 } })

    renderZone()

    const empty = screen.getByTestId('pulse-empty-state')
    expect(empty).toHaveTextContent('No active sessions')
  })

  it('renders one CompactSessionCard per active session up to the cap', () => {
    const sessions = Array.from({ length: 3 }, (_, i) =>
      makeSession({ sessionId: `session-${i}`, issueNumber: 100 + i }),
    )
    mocks.activity = makeActivity(sessions, { active: 3, slots: { active: 3, max: 8 } })

    renderZone()

    expect(screen.queryByTestId('pulse-empty-state')).not.toBeInTheDocument()
    const cards = screen.getAllByTestId('pulse-compact-card')
    expect(cards).toHaveLength(3)
    expect(cards[0]).toHaveAttribute('data-issue-number', '100')
    expect(cards[2]).toHaveAttribute('data-issue-number', '102')
    expect(screen.queryByTestId('pulse-overflow-link')).not.toBeInTheDocument()
  })

  it('caps at 4 cards and shows a +N more overflow link when active sessions exceed the cap', () => {
    const sessions = Array.from({ length: 6 }, (_, i) =>
      makeSession({ sessionId: `session-${i}`, issueNumber: 200 + i }),
    )
    mocks.activity = makeActivity(sessions, { active: 6, slots: { active: 6, max: 8 } })

    renderZone()

    const cards = screen.getAllByTestId('pulse-compact-card')
    expect(cards).toHaveLength(4)
    const link = screen.getByTestId('pulse-overflow-link')
    expect(link).toHaveTextContent('+2 more in Activity')
    expect(link.getAttribute('href')).toMatch(/\/activity$/)
  })

  it('shows the overflow link as +1 more when only one card is over the cap', () => {
    const sessions = Array.from({ length: 5 }, (_, i) =>
      makeSession({ sessionId: `session-${i}`, issueNumber: 300 + i }),
    )
    mocks.activity = makeActivity(sessions, { active: 5, slots: { active: 5, max: 8 } })

    renderZone()

    expect(screen.getAllByTestId('pulse-compact-card')).toHaveLength(4)
    expect(screen.getByTestId('pulse-overflow-link')).toHaveTextContent('+1 more in Activity')
  })

  it('treats non-active sessions as not visible in the card list', () => {
    const sessions = [
      makeSession({ sessionId: 's-a', status: 'active', issueNumber: 400 }),
      makeSession({ sessionId: 's-b', status: 'completed', issueNumber: 401 }),
      makeSession({ sessionId: 's-c', status: 'failed', issueNumber: 402 }),
    ]
    mocks.activity = makeActivity(sessions, { active: 1, completed: 1, failed: 1, slots: { active: 1, max: 8 } })

    renderZone()

    expect(screen.getAllByTestId('pulse-compact-card')).toHaveLength(1)
    expect(screen.getByTestId('pulse-compact-card')).toHaveAttribute('data-issue-number', '400')
  })

  it('shares the same useAgentActivity source — re-rendering on data update reflects the new state', () => {
    mocks.activity = makeActivity([], { active: 0, slots: { active: 0, max: 8 } })

    const { rerender } = render(
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <ProjectProvider initialProjectId={TEST_PROJECT.id} initialProjects={[TEST_PROJECT]}>
          <MemoryRouter initialEntries={['/']}>
            <PulseZone />
          </MemoryRouter>
        </ProjectProvider>
      </QueryClientProvider>,
    )

    expect(screen.getByTestId('pulse-empty-state')).toBeInTheDocument()

    act(() => {
      mocks.activity = makeActivity(
        [makeSession({ sessionId: 'shared-1', issueNumber: 500 })],
        { active: 1, slots: { active: 1, max: 8 } },
      )
    })

    rerender(
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <ProjectProvider initialProjectId={TEST_PROJECT.id} initialProjects={[TEST_PROJECT]}>
          <MemoryRouter initialEntries={['/']}>
            <PulseZone />
          </MemoryRouter>
        </ProjectProvider>
      </QueryClientProvider>,
    )

    expect(screen.queryByTestId('pulse-empty-state')).not.toBeInTheDocument()
    expect(screen.getAllByTestId('pulse-compact-card')).toHaveLength(1)
    expect(screen.getByTestId('pulse-slots')).toHaveTextContent('1/8')
  })

  it('uses the shared useAgentActivity source through useActivityCards', () => {
    mocks.activity = makeActivity([], { active: 0, slots: { active: 0, max: 8 } })

    renderZone()

    expect(mocks.useAgentActivity).toHaveBeenCalledTimes(1)
    expect(mocks.useAgentActivity).toHaveBeenCalledWith()
  })
})
