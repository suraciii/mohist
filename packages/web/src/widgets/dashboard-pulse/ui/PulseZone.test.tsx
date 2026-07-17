import '@testing-library/jest-dom'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { cleanup, render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { http, HttpResponse } from 'msw'
import { ProjectProvider } from '@/entities/project/model/ProjectContext'
import { IssueHealth, IssueStatus, WorkflowStage, type Issue } from '@/entities/issue'
import type {
  AgentStatus,
  AgentActivity,
  AgentActivitySession,
} from '@/entities/agent/model/types'
import { sessionToCard, useActivityCards } from '@/entities/agent-ops'
import { TEST_PROJECT } from '../../../../tests/test-utils'
import { useMswServer } from '../../../../tests/support/msw'
import { PulseZone } from './PulseZone'

let _issues: Issue[] = []
let _agentStatus: AgentStatus
let _agentActivity: AgentActivity | null = null

useMswServer(
  http.get('*/api/projects/:projectId/issues', () =>
    HttpResponse.json({ success: true, data: _issues }),
  ),
  http.get('*/api/projects/:projectId/agent/status', () =>
    HttpResponse.json({ success: true, data: _agentStatus }),
  ),
)

function mockIssuesResponse(issues: Issue[]) {
  _issues = issues
}

function mockAgentStatusResponse(data: AgentStatus) {
  _agentStatus = data
}

function mockAgentActivityResponse(data: AgentActivity | null) {
  _agentActivity = data
}

const activityCardsHook = (): ReturnType<typeof useActivityCards> => {
  const cards = (_agentActivity?.sessions ?? []).map(sessionToCard)
  const activeCards = cards.filter((card) => card.status === 'active')
  const recentCards = cards.filter((card) => card.status !== 'active')
  const activeCardByIssueNumber = new Map<number, (typeof activeCards)[number]>()
  for (const card of activeCards) {
    const issueNumber = Number(card.issueNumber)
    if (Number.isFinite(issueNumber)) activeCardByIssueNumber.set(issueNumber, card)
  }
  return {
    activeCards,
    activeCardByIssueNumber,
    recentCards,
    waitingCards: [],
    statusCounts: _agentActivity?.summary ?? {
      active: activeCards.length,
      waiting: 0,
      completed: 0,
      failed: 0,
      slots: { active: activeCards.length, max: 0 },
    },
    slotUsage: _agentActivity?.summary.slots ?? { active: 0, max: 0 },
    isLoading: false,
    isError: false,
  }
}

function renderZone() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId={TEST_PROJECT.id} initialProjects={[TEST_PROJECT]}>
        <MemoryRouter initialEntries={['/']}>
          <PulseZone
            issuesOverride={_issues}
            agentStatusOverride={_agentStatus}
            activityCardsHook={activityCardsHook}
          />
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

function makeRunningIssue(overrides: Partial<Issue> = {}): Issue {
  return {
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

function makeAgentStatus(overrides: Partial<AgentStatus> = {}): AgentStatus {
  return {
    running: false,
    issueNumber: null,
    activeAgents: [],
    capacity: { active: 0, max: 8 },
    runnerAvailable: true,
    ...overrides,
  }
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

beforeEach(() => {
  mockIssuesResponse([])
  mockAgentActivityResponse(null)
  mockAgentStatusResponse(makeAgentStatus())
})

afterEach(() => {
  cleanup()
})

describe('PulseZone — issue-led active production', () => {
  it('keeps an in-progress issue visible with its workflow stage even when no agent session is active', async () => {
    mockIssuesResponse([
      makeRunningIssue({
        number: 50,
        title: 'Refactor the dashboard',
        workflowStage: WorkflowStage.Build,
      }),
    ])
    mockAgentActivityResponse(makeActivity([]))

    renderZone()

    await waitFor(() => {
      expect(screen.queryByTestId('pulse-empty-state')).not.toBeInTheDocument()
    })
    const card = screen.getByTestId('pulse-compact-card')
    expect(card).toHaveAttribute('data-issue-number', '50')
    expect(within(card).getByTestId('pulse-compact-title')).toHaveTextContent('Refactor the dashboard')
    expect(within(card).getByTestId('pulse-compact-stage')).toHaveTextContent('Build')
    expect(within(card).getByTestId('pulse-compact-paused-dot')).toBeInTheDocument()
  })

  it('renders the stage chip with the categorical stage-identity palette', async () => {
    mockIssuesResponse([
      makeRunningIssue({
        number: 60,
        title: 'Draft implementation plan',
        workflowStage: WorkflowStage.Plan,
      }),
    ])
    mockAgentActivityResponse(makeActivity([]))

    renderZone()

    const stageChip = await waitFor(() => screen.getByTestId('pulse-compact-stage'))
    expect(stageChip.className).toContain('bg-blue-100')
    expect(stageChip.className).toContain('dark:bg-blue-900/40')
  })

  it('shows the owner-action cue for a blocked in-progress issue', async () => {
    mockIssuesResponse([
      makeRunningIssue({
        number: 70,
        title: 'Stuck on auth bug',
        workflowStage: WorkflowStage.Build,
        health: IssueHealth.Blocked,
        blockedReason: 'Cannot reach auth provider',
      }),
    ])
    mockAgentActivityResponse(makeActivity([]))

    renderZone()

    const cue = await waitFor(() => screen.getByTestId('pulse-compact-owner-action-cue'))
    expect(cue).toHaveTextContent('Owner action')
    expect(cue).toHaveAttribute('data-family', 'danger')
    expect(cue.className).toContain('bg-danger-subtle')
    expect(cue.className).toContain('text-danger')
  })

  it('hides the owner-action cue for a normally-running issue', async () => {
    mockIssuesResponse([
      makeRunningIssue({
        number: 80,
        title: 'Running normally',
        workflowStage: WorkflowStage.Build,
        health: IssueHealth.Active,
      }),
    ])
    mockAgentActivityResponse(makeActivity([]))

    renderZone()

    await waitFor(() => {
      expect(screen.getByTestId('pulse-compact-card')).toBeInTheDocument()
    })
    expect(screen.queryByTestId('pulse-compact-owner-action-cue')).not.toBeInTheDocument()
    expect(screen.getByTestId('pulse-compact-card')).toBeInTheDocument()
  })

  it('shows the cue for an awaiting-approval issue', async () => {
    mockIssuesResponse([
      makeRunningIssue({
        number: 90,
        title: 'Approve the plan',
        workflowStage: WorkflowStage.Plan,
        health: IssueHealth.Active,
        approvalState: {
          status: 'awaiting',
          requestedAt: '2026-01-01T00:00:00.000Z',
        },
      }),
    ])
    mockAgentActivityResponse(makeActivity([]))

    renderZone()

    const cue = await waitFor(() => screen.getByTestId('pulse-compact-owner-action-cue'))
    expect(cue).toBeInTheDocument()
    expect(cue).toHaveAttribute('data-family', 'warning')
    expect(cue.className).toContain('bg-warning-subtle')
    expect(cue.className).toContain('text-warning')
  })

  it('shows the cue for an Integrate-stage Blocked issue (integration-failed)', async () => {
    mockIssuesResponse([
      makeRunningIssue({
        number: 100,
        title: 'Merge blocked',
        workflowStage: WorkflowStage.Integrate,
        health: IssueHealth.Blocked,
      }),
    ])
    mockAgentActivityResponse(makeActivity([]))

    renderZone()

    await waitFor(() => {
      expect(screen.getByTestId('pulse-compact-owner-action-cue')).toBeInTheDocument()
    })
  })

  it('shows the cue for a non-integrate blocked issue', async () => {
    mockIssuesResponse([
      makeRunningIssue({
        number: 110,
        title: 'Build halted',
        workflowStage: WorkflowStage.Build,
        health: IssueHealth.Blocked,
      }),
    ])
    mockAgentActivityResponse(makeActivity([]))

    renderZone()

    await waitFor(() => {
      expect(screen.getByTestId('pulse-compact-owner-action-cue')).toBeInTheDocument()
    })
  })

  it('joins active session telemetry into the matching running-issue row by issue number', async () => {
    mockIssuesResponse([
      makeRunningIssue({
        number: 120,
        title: 'Issue with active session',
        workflowStage: WorkflowStage.Build,
        health: IssueHealth.Active,
      }),
    ])
    mockAgentActivityResponse(
      makeActivity([
        makeSession({
          sessionId: 'session-120',
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
      ]),
    )

    renderZone()

    await waitFor(() => {
      expect(screen.getByTestId('pulse-compact-usage')).toHaveTextContent('15.6k tok')
    })
    expect(screen.getByTestId('pulse-compact-progress')).toHaveTextContent('3/8 tasks')
  })

  it('shows an active session even when no in-progress issue row exists', async () => {
    mockIssuesResponse([])
    mockAgentActivityResponse(
      makeActivity([
        makeSession({
          sessionId: 'session-only-999',
          issueNumber: 999,
          issueTitle: 'Session-only active work',
          issueStage: 'Check',
          taskDescription: 'Continue active session',
        }),
      ]),
    )

    renderZone()

    await waitFor(() => {
      expect(screen.queryByTestId('pulse-empty-state')).not.toBeInTheDocument()
    })
    const card = screen.getByTestId('pulse-compact-card')
    expect(card).toHaveAttribute('data-issue-number', '999')
    expect(within(card).getByTestId('pulse-compact-title')).toHaveTextContent('Continue active session')
    expect(within(card).getByTestId('pulse-compact-stage')).toHaveTextContent('Check')
  })

  it('renders an active-agent placeholder when runner status has active work but activity cards are empty', async () => {
    mockIssuesResponse([])
    mockAgentActivityResponse(makeActivity([]))
    mockAgentStatusResponse(
      makeAgentStatus({
        activeAgents: [
          {
            issueNumber: 432,
            projectId: TEST_PROJECT.id,
            progress: {
              stage: 'check',
              lastActivityAt: '2026-01-01T00:00:00.000Z',
            },
          },
        ],
      }),
    )

    renderZone()

    await waitFor(() => {
      expect(screen.queryByTestId('pulse-empty-state')).not.toBeInTheDocument()
    })
    const card = screen.getByTestId('pulse-agent-status-card')
    expect(card).toHaveAttribute('data-issue-number', '432')
    expect(within(card).getByTestId('pulse-agent-status-title')).toHaveTextContent('Agent active')
    expect(within(card).getByTestId('pulse-agent-status-stage')).toHaveTextContent('Check')
  })

  it('renders a generic active-agent placeholder when runner status has no issue number', async () => {
    mockIssuesResponse([])
    mockAgentActivityResponse(makeActivity([]))
    mockAgentStatusResponse(makeAgentStatus({ running: true, issueNumber: null }))

    renderZone()

    const card = await waitFor(() => screen.getByTestId('pulse-agent-status-card'))
    expect(card).toHaveAttribute('data-issue-number', 'unknown')
    expect(card.getAttribute('href')).toMatch(/\/activity$/)
  })

  it('uses the current issue title and workflow stage when session telemetry is stale', async () => {
    mockIssuesResponse([
      makeRunningIssue({
        number: 125,
        title: 'Current issue title',
        workflowStage: WorkflowStage.Check,
        health: IssueHealth.Active,
      }),
    ])
    mockAgentActivityResponse(
      makeActivity([
        makeSession({
          sessionId: 'session-125',
          issueNumber: 125,
          issueTitle: 'Stale session issue title',
          issueStage: 'Plan',
          taskDescription: 'Stale session task description',
          currentWorkItem: {
            type: 'task',
            id: 'task-1',
            title: 'Stale current work title',
            stage: 'Plan',
            sessionWorkType: null,
          },
          taskProgress: { completed: 1, total: 3 },
        }),
      ]),
    )

    renderZone()

    const card = await waitFor(() => screen.getByTestId('pulse-compact-card'))
    expect(within(card).getByTestId('pulse-compact-title')).toHaveTextContent('Current issue title')
    expect(within(card).getByTestId('pulse-compact-stage')).toHaveTextContent('Check')
    expect(within(card).queryByText('Stale current work title')).not.toBeInTheDocument()
    expect(within(card).queryByText('Plan')).not.toBeInTheDocument()
    expect(within(card).getByTestId('pulse-compact-progress')).toHaveTextContent('1/3 tasks')
  })

  it('does NOT hide an in-progress issue that lacks an active session', async () => {
    mockIssuesResponse([
      makeRunningIssue({
        number: 130,
        title: 'Work paused between stages',
        workflowStage: WorkflowStage.Check,
        health: IssueHealth.Paused,
      }),
    ])
    mockAgentActivityResponse(
      makeActivity([makeSession({ sessionId: 'unrelated', issueNumber: 999, })]),
    )

    renderZone()

    const cards = await waitFor(() => screen.getAllByTestId('pulse-compact-card'))
    expect(cards.map((card) => card.getAttribute('data-issue-number'))).toContain('130')
    const issueCard = cards.find((card) => card.getAttribute('data-issue-number') === '130')
    if (!issueCard) throw new Error('Expected issue 130 to render')
    expect(within(issueCard).getByTestId('pulse-compact-title')).toHaveTextContent('Work paused between stages')
  })

  it('renders the cue on a session-enriched row when the issue also needs owner action', async () => {
    mockIssuesResponse([
      makeRunningIssue({
        number: 140,
        title: 'Blocked AND active session',
        workflowStage: WorkflowStage.Build,
        health: IssueHealth.Blocked,
        blockedReason: 'merge lock',
      }),
    ])
    mockAgentActivityResponse(
      makeActivity([
        makeSession({
          sessionId: 'session-140',
          issueNumber: 140,
          issueStage: 'Build',
        }),
      ]),
    )

    renderZone()

    await waitFor(() => {
      expect(screen.getByTestId('pulse-compact-owner-action-cue')).toBeInTheDocument()
    })
    expect(screen.getByTestId('pulse-compact-card')).toHaveAttribute('data-issue-number', '140')
  })

  it('does not render the removed pulse-capacity-header or pulse-slots (capacity relocated to its own level)', async () => {
    mockIssuesResponse([
      makeRunningIssue({
        number: 150,
        title: 'With capacity data present',
        workflowStage: WorkflowStage.Build,
      }),
    ])
    mockAgentActivityResponse(makeActivity([], { active: 4, slots: { active: 4, max: 4 } }))

    renderZone()

    await waitFor(() => {
      expect(screen.getByTestId('pulse-compact-card')).toBeInTheDocument()
    })
    expect(screen.queryByTestId('pulse-capacity-header')).not.toBeInTheDocument()
    expect(screen.queryByTestId('pulse-slots')).not.toBeInTheDocument()
  })

  it('caps at 4 cards and shows a +N more overflow link when running issues exceed the cap', async () => {
    const issues = Array.from({ length: 6 }, (_, i) =>
      makeRunningIssue({
        number: 200 + i,
        title: `Running issue ${i}`,
        workflowStage: WorkflowStage.Build,
      }),
    )
    mockIssuesResponse(issues)
    mockAgentActivityResponse(makeActivity([]))

    renderZone()

    await waitFor(() => {
      expect(screen.getAllByTestId('pulse-compact-card')).toHaveLength(4)
    })
    const link = screen.getByTestId('pulse-overflow-link')
    expect(link).toHaveTextContent('+2 more active items')
    expect(link.getAttribute('href')).toMatch(/\/issues$/)
  })

  it('shows the overflow link as +1 more when only one card is over the cap', async () => {
    const issues = Array.from({ length: 5 }, (_, i) =>
      makeRunningIssue({
        number: 300 + i,
        title: `Running issue ${i}`,
        workflowStage: WorkflowStage.Build,
      }),
    )
    mockIssuesResponse(issues)
    mockAgentActivityResponse(makeActivity([]))

    renderZone()

    await waitFor(() => {
      expect(screen.getAllByTestId('pulse-compact-card')).toHaveLength(4)
    })
    expect(screen.getByTestId('pulse-overflow-link')).toHaveTextContent('+1 more active items')
  })

  it('renders an empty-state affordance when there are no running issues or active sessions', async () => {
    mockIssuesResponse([
      makeRunningIssue({
        number: 400,
        status: IssueStatus.Done,
        health: IssueHealth.Done,
      }),
      makeRunningIssue({
        number: 401,
        status: IssueStatus.Cancelled,
        health: IssueHealth.Cancelled,
      }),
      makeRunningIssue({
        number: 402,
        status: IssueStatus.Backlog,
        health: IssueHealth.Active,
      }),
    ])
    mockAgentActivityResponse(makeActivity([]))

    renderZone()

    const empty = await waitFor(() => screen.getByTestId('pulse-empty-state'))
    expect(empty).toHaveTextContent('No active production')
    expect(screen.queryByTestId('pulse-compact-card')).not.toBeInTheDocument()
  })

  it('preserves existing pulse-compact-* test-ids', async () => {
    mockIssuesResponse([
      makeRunningIssue({
        number: 500,
        title: 'Verify testids',
        workflowStage: WorkflowStage.Build,
      }),
    ])
    mockAgentActivityResponse(makeActivity([]))

    renderZone()

    await waitFor(() => {
      expect(screen.getByTestId('pulse-compact-card')).toBeInTheDocument()
    })
    expect(screen.getByTestId('pulse-compact-title')).toBeInTheDocument()
    expect(screen.getByTestId('pulse-compact-stage')).toBeInTheDocument()
  })

  it('lists multiple running issues and ranks them by issue number ascending', async () => {
    mockIssuesResponse([
      makeRunningIssue({ number: 30, title: 'Thirty', workflowStage: WorkflowStage.Build }),
      makeRunningIssue({ number: 5, title: 'Five', workflowStage: WorkflowStage.Plan }),
      makeRunningIssue({ number: 12, title: 'Twelve', workflowStage: WorkflowStage.Check }),
    ])
    mockAgentActivityResponse(makeActivity([]))

    renderZone()

    const cards = await waitFor(() => screen.getAllByTestId('pulse-compact-card'))
    expect(cards.map((card) => card.getAttribute('data-issue-number'))).toEqual(['5', '12', '30'])
  })

  it('lists active sessions that do not have a matching running issue row', async () => {
    mockIssuesResponse([
      makeRunningIssue({
        number: 600,
        status: IssueStatus.Done,
        health: IssueHealth.Done,
      }),
      makeRunningIssue({
        number: 601,
        title: 'In progress, no session',
        workflowStage: WorkflowStage.Build,
      }),
    ])
    mockAgentActivityResponse(
      makeActivity([
        makeSession({
          sessionId: 'session-600',
          issueNumber: 600,
          issueStatus: 'done',
        }),
      ]),
    )

    renderZone()

    const cards = await waitFor(() => screen.getAllByTestId('pulse-compact-card'))
    expect(cards).toHaveLength(2)
    expect(cards.map((card) => card.getAttribute('data-issue-number'))).toEqual(['601', '600'])
  })

  it('renders both a session-enriched row and a session-less row side by side (mixed state)', async () => {
    mockIssuesResponse([
      makeRunningIssue({
        number: 700,
        title: 'With session',
        workflowStage: WorkflowStage.Build,
        health: IssueHealth.Active,
      }),
      makeRunningIssue({
        number: 701,
        title: 'Paused between stages',
        workflowStage: WorkflowStage.Check,
        health: IssueHealth.Paused,
      }),
    ])
    mockAgentActivityResponse(
      makeActivity([
        makeSession({
          sessionId: 'session-700',
          issueNumber: 700,
          issueStage: 'Build',
        }),
      ]),
    )

    renderZone()

    const cards = await waitFor(() => screen.getAllByTestId('pulse-compact-card'))
    expect(cards).toHaveLength(2)
    expect(screen.queryByTestId('pulse-compact-paused-dot')).toBeInTheDocument()
  })
})
