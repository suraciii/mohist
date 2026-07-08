// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { render, screen, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { describe, expect, it, vi, beforeEach } from 'vitest'
import { ProjectProvider } from '@/entities/project/model/ProjectContext'
import { IssueHealth, IssueStatus, WorkflowStage, type Issue } from '@/entities/issue'
import type {
  AgentActivity,
  AgentActivitySession,
} from '@/entities/agent/model/types'
import { PulseZone } from './PulseZone'

const mocks = vi.hoisted(() => ({
  activity: null as AgentActivity | null | undefined,
  issues: null as Issue[] | null | undefined,
  useAgentActivity: vi.fn(() => ({ data: mocks.activity })),
  useIssues: vi.fn((_params?: unknown) => ({ data: mocks.issues })),
}))

vi.mock('@/entities/agent/api/queries', () => ({
  useAgentActivity: mocks.useAgentActivity,
  useAgentStatus: () => ({ data: undefined }),
  useGlobalAgentSessions: () => ({ data: [] }),
}))

vi.mock('@/entities/issue/api/queries', () => ({
  useIssues: (params?: unknown) => mocks.useIssues(params),
  useArchivedIssues: () => ({ data: [] }),
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

function makeRunningIssue(overrides: Partial<Issue> = {}): Issue {
  return {
    id: 'issue-1',
    number: 12,
    title: 'Fix project selector',
    status: IssueStatus.InProgress,
    health: IssueHealth.Active,
    workflowStage: WorkflowStage.Build,
    projectId: TEST_PROJECT.id,
    labels: {},
    createdAt: '2026-01-01T00:00:00.000Z',
    updatedAt: '2026-01-01T00:00:00.000Z',
    isDraft: false,
    canStart: true,
    blocker: null,
    ...overrides,
  }
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
    createdAt: '2026-01-01T00:00:00.000Z',
    completedAt: null,
    lastActivityAt: '2026-01-01T00:00:00.000Z',
    currentWorkItem: null,
    taskProgress: null,
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

describe('PulseZone — issue-led active production', () => {
  beforeEach(() => {
    mocks.activity = null
    mocks.issues = null
    mocks.useAgentActivity.mockClear()
    mocks.useIssues.mockClear()
  })

  it('keeps an in-progress issue visible with its workflow stage even when no agent session is active', () => {
    mocks.issues = [
      makeRunningIssue({
        id: 'paused-issue',
        number: 50,
        title: 'Refactor the dashboard',
        workflowStage: WorkflowStage.Build,
      }),
    ]
    mocks.activity = makeActivity([])

    renderZone()

    expect(screen.queryByTestId('pulse-empty-state')).not.toBeInTheDocument()
    const card = screen.getByTestId('pulse-compact-card')
    expect(card).toHaveAttribute('data-issue-number', '50')
    expect(within(card).getByTestId('pulse-compact-title')).toHaveTextContent('Refactor the dashboard')
    expect(within(card).getByTestId('pulse-compact-stage')).toHaveTextContent('Build')
    expect(within(card).getByTestId('pulse-compact-paused-dot')).toBeInTheDocument()
  })

  it('renders the stage chip with the categorical stage-identity palette', () => {
    mocks.issues = [
      makeRunningIssue({
        id: 'plan-issue',
        number: 60,
        title: 'Draft implementation plan',
        workflowStage: WorkflowStage.Plan,
      }),
    ]
    mocks.activity = makeActivity([])

    renderZone()

    const stageChip = screen.getByTestId('pulse-compact-stage')
    expect(stageChip.className).toContain('bg-blue-100')
    expect(stageChip.className).toContain('dark:bg-blue-900/40')
  })

  it('shows the owner-action cue for a blocked in-progress issue', () => {
    mocks.issues = [
      makeRunningIssue({
        id: 'blocked-issue',
        number: 70,
        title: 'Stuck on auth bug',
        workflowStage: WorkflowStage.Build,
        health: IssueHealth.Blocked,
        blockedReason: 'Cannot reach auth provider',
      }),
    ]
    mocks.activity = makeActivity([])

    renderZone()

    const cue = screen.getByTestId('pulse-compact-owner-action-cue')
    expect(cue).toHaveTextContent('Owner action')
    expect(cue.className).toContain('bg-danger-subtle')
    expect(cue.className).toContain('text-danger')
  })

  it('hides the owner-action cue for a normally-running issue', () => {
    mocks.issues = [
      makeRunningIssue({
        id: 'normal-issue',
        number: 80,
        title: 'Running normally',
        workflowStage: WorkflowStage.Build,
        health: IssueHealth.Active,
      }),
    ]
    mocks.activity = makeActivity([])

    renderZone()

    expect(screen.queryByTestId('pulse-compact-owner-action-cue')).not.toBeInTheDocument()
    expect(screen.getByTestId('pulse-compact-card')).toBeInTheDocument()
  })

  it('shows the cue for an awaiting-approval issue', () => {
    mocks.issues = [
      makeRunningIssue({
        id: 'await-issue',
        number: 90,
        title: 'Approve the plan',
        workflowStage: WorkflowStage.Plan,
        health: IssueHealth.Active,
        approvalState: {
          status: 'awaiting',
          requestedAt: '2026-01-01T00:00:00.000Z',
        },
      }),
    ]
    mocks.activity = makeActivity([])

    renderZone()

    expect(screen.getByTestId('pulse-compact-owner-action-cue')).toBeInTheDocument()
  })

  it('shows the cue for an Integrate-stage Blocked issue (integration-failed)', () => {
    mocks.issues = [
      makeRunningIssue({
        id: 'integrate-blocked',
        number: 100,
        title: 'Merge blocked',
        workflowStage: WorkflowStage.Integrate,
        health: IssueHealth.Blocked,
      }),
    ]
    mocks.activity = makeActivity([])

    renderZone()

    expect(screen.getByTestId('pulse-compact-owner-action-cue')).toBeInTheDocument()
  })

  it('shows the cue for a non-integrate Interrupted issue', () => {
    mocks.issues = [
      makeRunningIssue({
        id: 'build-interrupted',
        number: 110,
        title: 'Build halted',
        workflowStage: WorkflowStage.Build,
        health: IssueHealth.Interrupted,
      }),
    ]
    mocks.activity = makeActivity([])

    renderZone()

    expect(screen.getByTestId('pulse-compact-owner-action-cue')).toBeInTheDocument()
  })

  it('joins active session telemetry into the matching running-issue row by issue number', () => {
    mocks.issues = [
      makeRunningIssue({
        id: 'with-session',
        number: 120,
        title: 'Issue with active session',
        workflowStage: WorkflowStage.Build,
        health: IssueHealth.Active,
      }),
    ]
    mocks.activity = makeActivity([
      makeSession({
        sessionId: 'session-120',
        issueId: 'with-session',
        issueNumber: 120,
        issueTitle: 'Issue with active session',
        issueStage: 'Build',
        taskProgress: { completed: 3, total: 8 },
        usage: {
          totalTokens: 15_600,
          costAmount: 0.18,
          costCurrency: 'USD',
        },
      }),
    ])

    renderZone()

    expect(screen.getByTestId('pulse-compact-usage')).toHaveTextContent('15.6k tok')
    expect(screen.getByTestId('pulse-compact-progress')).toHaveTextContent('3/8 tasks')
  })

  it('does NOT hide an in-progress issue that lacks an active session', () => {
    mocks.issues = [
      makeRunningIssue({
        id: 'no-session',
        number: 130,
        title: 'Work paused between stages',
        workflowStage: WorkflowStage.Check,
        health: IssueHealth.Paused,
      }),
    ]
    mocks.activity = makeActivity([
      makeSession({ sessionId: 'unrelated', issueNumber: 999 }),
    ])

    renderZone()

    const card = screen.getByTestId('pulse-compact-card')
    expect(card).toHaveAttribute('data-issue-number', '130')
    expect(within(card).getByTestId('pulse-compact-title')).toHaveTextContent('Work paused between stages')
  })

  it('renders the cue on a session-enriched row when the issue also needs owner action', () => {
    mocks.issues = [
      makeRunningIssue({
        id: 'session-blocked',
        number: 140,
        title: 'Blocked AND active session',
        workflowStage: WorkflowStage.Build,
        health: IssueHealth.Blocked,
        blockedReason: 'merge lock',
      }),
    ]
    mocks.activity = makeActivity([
      makeSession({
        sessionId: 'session-140',
        issueId: 'session-blocked',
        issueNumber: 140,
        issueStage: 'Build',
      }),
    ])

    renderZone()

    expect(screen.getByTestId('pulse-compact-owner-action-cue')).toBeInTheDocument()
    expect(screen.getByTestId('pulse-compact-card')).toHaveAttribute('data-issue-number', '140')
  })

  it('does not render the removed pulse-capacity-header or pulse-slots (capacity relocated to its own level)', () => {
    mocks.issues = [
      makeRunningIssue({
        id: 'i-1',
        number: 150,
        title: 'With capacity data present',
        workflowStage: WorkflowStage.Build,
      }),
    ]
    mocks.activity = makeActivity([], { active: 4, slots: { active: 4, max: 4 } })

    renderZone()

    expect(screen.queryByTestId('pulse-capacity-header')).not.toBeInTheDocument()
    expect(screen.queryByTestId('pulse-slots')).not.toBeInTheDocument()
  })

  it('caps at 4 cards and shows a +N more overflow link when running issues exceed the cap', () => {
    const issues = Array.from({ length: 6 }, (_, i) =>
      makeRunningIssue({
        id: `overflow-${i}`,
        number: 200 + i,
        title: `Running issue ${i}`,
        workflowStage: WorkflowStage.Build,
      }),
    )
    mocks.issues = issues
    mocks.activity = makeActivity([])

    renderZone()

    expect(screen.getAllByTestId('pulse-compact-card')).toHaveLength(4)
    const link = screen.getByTestId('pulse-overflow-link')
    expect(link).toHaveTextContent('+2 more running issues')
    expect(link.getAttribute('href')).toMatch(/\/issues$/)
  })

  it('shows the overflow link as +1 more when only one card is over the cap', () => {
    const issues = Array.from({ length: 5 }, (_, i) =>
      makeRunningIssue({
        id: `overflow2-${i}`,
        number: 300 + i,
        title: `Running issue ${i}`,
        workflowStage: WorkflowStage.Build,
      }),
    )
    mocks.issues = issues
    mocks.activity = makeActivity([])

    renderZone()

    expect(screen.getAllByTestId('pulse-compact-card')).toHaveLength(4)
    expect(screen.getByTestId('pulse-overflow-link')).toHaveTextContent('+1 more running issues')
  })

  it('renders an empty-state affordance when there are no running issues', () => {
    mocks.issues = [
      makeRunningIssue({
        id: 'done-issue',
        number: 400,
        status: IssueStatus.Done,
        health: IssueHealth.Done,
      }),
      makeRunningIssue({
        id: 'cancelled-issue',
        number: 401,
        status: IssueStatus.Cancelled,
        health: IssueHealth.Cancelled,
      }),
      makeRunningIssue({
        id: 'backlog-issue',
        number: 402,
        status: IssueStatus.Backlog,
        health: IssueHealth.Active,
      }),
    ]
    mocks.activity = makeActivity([])

    renderZone()

    const empty = screen.getByTestId('pulse-empty-state')
    expect(empty).toHaveTextContent('No running issues')
    expect(screen.queryByTestId('pulse-compact-card')).not.toBeInTheDocument()
  })

  it('preserves existing pulse-compact-* test-ids', () => {
    mocks.issues = [
      makeRunningIssue({
        id: 'i-testids',
        number: 500,
        title: 'Verify testids',
        workflowStage: WorkflowStage.Build,
      }),
    ]
    mocks.activity = makeActivity([])

    renderZone()

    expect(screen.getByTestId('pulse-compact-card')).toBeInTheDocument()
    expect(screen.getByTestId('pulse-compact-title')).toBeInTheDocument()
    expect(screen.getByTestId('pulse-compact-stage')).toBeInTheDocument()
  })

  it('lists multiple running issues and ranks them by issue number ascending', () => {
    mocks.issues = [
      makeRunningIssue({ id: 'big', number: 30, title: 'Thirty', workflowStage: WorkflowStage.Build }),
      makeRunningIssue({ id: 'small', number: 5, title: 'Five', workflowStage: WorkflowStage.Plan }),
      makeRunningIssue({ id: 'mid', number: 12, title: 'Twelve', workflowStage: WorkflowStage.Check }),
    ]
    mocks.activity = makeActivity([])

    renderZone()

    const cards = screen.getAllByTestId('pulse-compact-card')
    expect(cards.map((card) => card.getAttribute('data-issue-number'))).toEqual(['5', '12', '30'])
  })

  it('does not list non-in-progress issues (Done/Cancelled/Backlog), even with an active session', () => {
    mocks.issues = [
      makeRunningIssue({
        id: 'done-with-session',
        number: 600,
        status: IssueStatus.Done,
        health: IssueHealth.Done,
      }),
      makeRunningIssue({
        id: 'in-progress-no-session',
        number: 601,
        title: 'In progress, no session',
        workflowStage: WorkflowStage.Build,
      }),
    ]
    mocks.activity = makeActivity([
      makeSession({
        sessionId: 'session-600',
        issueId: 'done-with-session',
        issueNumber: 600,
        issueStatus: 'done',
      }),
    ])

    renderZone()

    const cards = screen.getAllByTestId('pulse-compact-card')
    expect(cards).toHaveLength(1)
    expect(cards[0]).toHaveAttribute('data-issue-number', '601')
  })

  it('renders both a session-enriched row and a session-less row side by side (mixed state)', () => {
    mocks.issues = [
      makeRunningIssue({
        id: 'with-session',
        number: 700,
        title: 'With session',
        workflowStage: WorkflowStage.Build,
        health: IssueHealth.Active,
      }),
      makeRunningIssue({
        id: 'no-session',
        number: 701,
        title: 'Paused between stages',
        workflowStage: WorkflowStage.Check,
        health: IssueHealth.Paused,
      }),
    ]
    mocks.activity = makeActivity([
      makeSession({
        sessionId: 'session-700',
        issueId: 'with-session',
        issueNumber: 700,
        issueStage: 'Build',
      }),
    ])

    renderZone()

    const cards = screen.getAllByTestId('pulse-compact-card')
    expect(cards).toHaveLength(2)
    expect(screen.queryByTestId('pulse-compact-paused-dot')).toBeInTheDocument()
  })
})
