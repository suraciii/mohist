import { beforeEach, describe, expect, it } from 'vitest'
import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { render, TEST_PROJECT } from '../tests/test-utils'
import { ActivityPage } from '../src/pages/activity/ui/ActivityPage'
import { useMswServer } from '../tests/support/msw'
import type { AgentActivity } from '../src/entities/agent'
import type { RunnerStatusListResponse } from '../src/entities/runner'
import type { ProjectEventDto } from '../src/entities/project'

function makeProjectEvent(overrides: Partial<ProjectEventDto> = {}): ProjectEventDto {
  return {
    id: 1,
    origin: 'issue',
    sourceAggregateKind: 'issue',
    sourceAggregateId: 'issue-1',
    source: '/mohist/issues/issue-1',
    type: 'com.mohist.issue.created',
    time: '2026-01-01T00:00:00.000Z',
    envelopeId: 'env-1',
    specVersion: '1.0',
    subject: '1',
    dataContentType: 'application/json',
    data: {},
    extensions: { issueno: '1' },
    runnerId: null,
    ...overrides,
  }
}

let projectEvents: ProjectEventDto[] = []
let agentActivity: AgentActivity = {
  summary: { active: 0, waiting: 0, completed: 0, failed: 0, slots: { active: 0, max: 0 } },
  sessions: [],
  waiting: [],
}
let runners: RunnerStatusListResponse = { runners: [] }
let projectEventsFailed = false

useMswServer(
  http.get('*/api/projects/:projectId/events', () => {
    if (projectEventsFailed) return new HttpResponse(null, { status: 500 })
    return HttpResponse.json({ success: true, data: projectEvents })
  }),
  http.get('*/api/projects/:projectId/agent/activity', () => {
    return HttpResponse.json({ success: true, data: agentActivity })
  }),
  http.get('*/api/projects/:projectId/runners', () => {
    return HttpResponse.json({ success: true, data: runners })
  }),
)

beforeEach(() => {
  projectEvents = []
  agentActivity = {
    summary: { active: 0, waiting: 0, completed: 0, failed: 0, slots: { active: 0, max: 0 } },
    sessions: [],
    waiting: [],
  }
  runners = { runners: [] }
  projectEventsFailed = false
  document.documentElement.classList.remove('dark')
})

function renderPage() {
  return render(<ActivityPage now={Date.parse('2026-01-01T01:00:00.000Z')} />, {
    route: `/${TEST_PROJECT.name}/activity`,
  })
}

describe('Activity evidence view', () => {
  it('distinguishes event types from recorded events and snapshots', async () => {
    projectEvents = [
      makeProjectEvent({ id: 1, type: 'com.mohist.issue.created', data: { title: 'New issue' } }),
      makeProjectEvent({
        id: 2,
        origin: 'workflow-run',
        sourceAggregateKind: 'workflow-run',
        sourceAggregateId: 'wr-1',
        source: '/mohist/workflow-runs/wr-1',
        type: 'com.mohist.workflow.stage.failed',
        data: { stage: 'Build', reason: 'compile error' },
      }),
      makeProjectEvent({
        id: 3,
        origin: 'agent-session',
        sourceAggregateKind: 'agent-session',
        sourceAggregateId: 'session-1',
        source: '/mohist/agent-session/session-1',
        type: 'com.mohist.agent-session.context-exhausted',
        data: { failureCategory: 'context exhaustion' },
      }),
    ]

    renderPage()

    await waitFor(() => {
      expect(screen.getAllByTestId('activity-event-entry')).toHaveLength(3)
    })

    const entries = screen.getAllByTestId('activity-event-entry')
    expect(entries[0]).toHaveAttribute('data-event-type', 'failure')
    expect(entries[1]).toHaveAttribute('data-event-type', 'failure')
    expect(entries[2]).toHaveAttribute('data-event-type', 'issue-state')
  })

  it('surfaces attention events before routine events', async () => {
    projectEvents = [
      makeProjectEvent({ id: 1, type: 'com.mohist.issue.created', time: '2026-01-01T03:00:00.000Z' }),
      makeProjectEvent({
        id: 2,
        origin: 'workflow-run',
        sourceAggregateKind: 'workflow-run',
        sourceAggregateId: 'wr-1',
        source: '/mohist/workflow-runs/wr-1',
        type: 'com.mohist.workflow.stage.approval-requested',
        data: { stage: 'Review' },
        time: '2026-01-01T02:00:00.000Z',
      }),
    ]

    renderPage()

    await waitFor(() => {
      expect(screen.getByTestId('activity-attention-zone')).toBeInTheDocument()
    })
    expect(screen.getByTestId('activity-routine-zone')).toBeInTheDocument()
    expect(within(screen.getByTestId('activity-attention-zone')).getByTestId('activity-event-primary-link')).toHaveTextContent(
      /needs approval/,
    )
  })

  it('omits the attention zone when there are no attention events', async () => {
    projectEvents = [makeProjectEvent({ id: 1, type: 'com.mohist.issue.created' })]

    renderPage()

    await waitFor(() => {
      expect(screen.getByTestId('activity-routine-zone')).toBeInTheDocument()
    })
    expect(screen.queryByTestId('activity-attention-zone')).not.toBeInTheDocument()
  })

  it('filters the feed by event type and attention, and clears filters', async () => {
    projectEvents = [
      makeProjectEvent({ id: 1, type: 'com.mohist.issue.created' }),
      makeProjectEvent({
        id: 2,
        origin: 'workflow-run',
        sourceAggregateKind: 'workflow-run',
        sourceAggregateId: 'wr-1',
        source: '/mohist/workflow-runs/wr-1',
        type: 'com.mohist.workflow.stage.failed',
        data: { stage: 'Build' },
      }),
    ]

    renderPage()

    await waitFor(() => {
      expect(screen.getAllByTestId('activity-event-entry')).toHaveLength(2)
    })

    fireEvent.click(screen.getByTestId('activity-filter-issue-state'))
    await waitFor(() => {
      expect(screen.getAllByTestId('activity-event-entry')).toHaveLength(1)
    })
    expect(screen.getByTestId('activity-event-entry')).toHaveAttribute('data-event-type', 'issue-state')

    fireEvent.click(screen.getByTestId('activity-filter-clear'))
    await waitFor(() => {
      expect(screen.getAllByTestId('activity-event-entry')).toHaveLength(2)
    })

    fireEvent.click(screen.getByTestId('activity-filter-attention'))
    await waitFor(() => {
      expect(screen.getAllByTestId('activity-event-entry')).toHaveLength(1)
    })
    expect(screen.getByTestId('activity-event-entry')).toHaveAttribute('data-attention', 'failure')
  })

  it('surfaces generic agent sessions alongside workflow-bound sessions', async () => {
    agentActivity = {
      summary: { active: 2, waiting: 0, completed: 0, failed: 0, slots: { active: 2, max: 8 } },
      sessions: [
        {
          issueId: 'issue_1_42',
          issueNumber: 42,
          issueTitle: 'Workflow issue',
          issueStage: 'Build',
          issueStatus: null,
          sessionId: 'workflow-session-1',
          status: 'active',
          model: null,
          taskDescription: null,
          createdAt: '2026-01-01T00:00:00.000Z',
          completedAt: null,
          lastActivityAt: '2026-01-01T00:00:00.000Z',
          currentWorkItem: null,
          taskProgress: null,
          lastActivity: null,
          failureReason: null,
        },
        {
          issueId: 'agent_agent-1',
          issueNumber: 0,
          issueTitle: 'Agent session',
          issueStage: '',
          issueStatus: null,
          sessionId: 'generic-session-1',
          status: 'active',
          model: null,
          taskDescription: null,
          createdAt: '2026-01-01T00:00:00.000Z',
          completedAt: null,
          lastActivityAt: '2026-01-01T00:00:00.000Z',
          currentWorkItem: null,
          taskProgress: null,
          lastActivity: null,
          failureReason: null,
          agentId: 'agent-1',
          agentName: 'Reviewer',
        },
      ],
      waiting: [],
    }

    renderPage()

    await waitFor(() => {
      expect(screen.getAllByTestId('activity-event-entry')).toHaveLength(2)
    })

    const entries = screen.getAllByTestId('activity-event-entry')
    const generic = entries.find((e) => e.textContent?.includes('Reviewer'))
    const workflow = entries.find((e) => e.textContent?.includes('Issue #42'))

    expect(generic).toBeDefined()
    expect(workflow).toBeDefined()

    const genericLink = within(generic!).getByTestId('activity-event-primary-link')
    expect(genericLink).toHaveAttribute('href', expect.stringContaining('/agent-sessions/generic-session-1'))
    expect(genericLink).toHaveAttribute('href', expect.stringContaining('from=activity'))
  })

  it('links recorded issue-bound sessions directly with an Activity return target', async () => {
    projectEvents = [
      makeProjectEvent({
        id: 1,
        origin: 'agent-session',
        sourceAggregateKind: 'agent-session',
        sourceAggregateId: 'session-42',
        source: '/mohist/agent-session/session-42',
        type: 'coder_session_started',
        data: { issueNumber: 42 },
      }),
    ]

    renderPage()

    await waitFor(() => {
      expect(screen.getByTestId('activity-event-entry')).toBeInTheDocument()
    })

    expect(screen.getByTestId('activity-event-primary-link')).toHaveAttribute(
      'href',
      `/${encodeURIComponent(TEST_PROJECT.name)}/issues/42/session/session-42?from=activity`,
    )
  })

  it('uses shared theme-token families in light and dark mode', async () => {
    projectEvents = [
      makeProjectEvent({
        id: 1,
        origin: 'workflow-run',
        sourceAggregateKind: 'workflow-run',
        sourceAggregateId: 'wr-1',
        source: '/mohist/workflow-runs/wr-1',
        type: 'com.mohist.workflow.stage.failed',
        data: { stage: 'Build' },
      }),
    ]

    const { rerender } = renderPage()

    await waitFor(() => {
      expect(screen.getByTestId('activity-event-entry')).toBeInTheDocument()
    })

    const entry = screen.getByTestId('activity-event-entry')
    expect(entry.className).toContain('bg-danger-subtle')
    expect(entry.className).toContain('border-danger-border')

    document.documentElement.classList.add('dark')
    rerender(<ActivityPage now={Date.parse('2026-01-01T01:00:00.000Z')} />)

    await waitFor(() => {
      expect(screen.getByTestId('activity-event-entry').className).toContain('bg-danger-subtle')
    })
    expect(screen.getByTestId('activity-event-entry').className).toContain('border-danger-border')

    document.documentElement.classList.remove('dark')
  })

  it('shows incomplete evidence when the recorded event request fails', async () => {
    projectEventsFailed = true

    renderPage()

    await waitFor(() => {
      expect(screen.getByTestId('activity-evidence-error')).toBeInTheDocument()
    })
    expect(screen.queryByText('No activity yet.')).not.toBeInTheDocument()
  })
})
