import '@testing-library/jest-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import type { AgentStatus } from '../../../entities/agent'
import { IssueStatus, IssueHealth, WorkflowStage, type ApprovalState } from '../../../entities/issue'
import { ProjectProvider } from '../../../entities/project'
import { useRunnerSummary } from '../../../entities/runner'
import { makeIssue, makeIssues, mockAgentStatus } from './_kanbanBoardQueryTestUtils'

const TEST_PROJECT = { id: 'test-project', name: 'test', createdAt: '2025-01-01T00:00:00Z', updatedAt: '2025-01-01T00:00:00Z', repositories: [] }

let _runners: unknown[] = []
let previousUrl = ''
let previousHistoryState: unknown

const runnerSummaryHook: typeof useRunnerSummary = () => {
  const rows = _runners as ReturnType<typeof useRunnerSummary>['rows']
  const connectedIdleCount = rows.filter((row) => row.status === 'idle').length
  const connectedBusyCount = rows.filter((row) => row.status === 'busy').length
  return {
    connectedIdleCount,
    connectedBusyCount,
    hasConnectedCapacity: connectedIdleCount > 0 || connectedBusyCount > 0,
    rows,
  }
}

import { KanbanBoard } from './KanbanBoard'

function renderBoard(issues: unknown[], agentStatus: unknown) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId={TEST_PROJECT.id} initialProjects={[TEST_PROJECT]}>
        <MemoryRouter>
          <KanbanBoard
            issues={issues as any}
            agentStatus={agentStatus as any}
            runnerSummaryHook={runnerSummaryHook}
          />
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

beforeEach(() => {
  previousUrl = window.location.href
  previousHistoryState = window.history.state
  window.history.replaceState(null, '', '/')
  _runners = [{ id: 'r-default', kind: 'external', hostname: 'h', scope: { type: 'global' }, status: 'idle', capabilities: [], coderModels: [], coderModelCount: 0, connectionState: 'connected', activeWorks: [] }]
})

afterEach(() => {
  cleanup()
  window.history.replaceState(previousHistoryState, '', previousUrl)
  vi.clearAllMocks()
})

describe('KanbanBoard Component - Filtered Stage Counts', () => {
  it('renders all columns with unfiltered issues', () => {
    const issues = [
      makeIssue({ number: 1, status: IssueStatus.Backlog }),
      makeIssue({ number: 2, status: IssueStatus.Backlog }),
      makeIssue({ number: 3, status: IssueStatus.Backlog }),
      makeIssue({ number: 4, status: IssueStatus.InProgress }),
    ]

    renderBoard(issues, mockAgentStatus)

    expect(screen.getAllByText('Backlog').length).toBeGreaterThan(0)
    expect(screen.getAllByText('In Progress').length).toBeGreaterThan(0)
  })

  it('shows runner unavailable banner when no runner is connected', () => {
    _runners = []

    const agentStatus: AgentStatus = {
      ...mockAgentStatus,
      runnerAvailable: false,
      embeddedRunnerEnabled: false,
      runnerMessage: 'No runner is connected. Enable the embedded runner or start Mohist.Runner.',
      runners: [],
    }

    renderBoard(makeIssues(1), agentStatus)

    expect(screen.getByText(/No runner is connected/i)).toBeInTheDocument()
  })

  it('does not show runner unavailable banner when connected idle runner exists', async () => {
    _runners = [{ id: 'runner-1', kind: 'external', hostname: 'host1', scope: { type: 'global' }, status: 'idle', capabilities: [], coderModels: [], coderModelCount: 0, connectionState: 'connected', activeWorks: [] }]

    renderBoard(makeIssues(1), mockAgentStatus)

    await waitFor(() => {
      expect(screen.queryByText(/No runner is connected/i)).not.toBeInTheDocument()
    })
  })

  it('does not show runner unavailable banner when connected busy runner exists', async () => {
    _runners = [{ id: 'runner-1', kind: 'external', hostname: 'host1', scope: { type: 'global' }, status: 'busy', capabilities: [], coderModels: [], coderModelCount: 0, connectionState: 'connected', activeWorks: [{ workId: 'w1', ownerKind: 'workflow', ownerId: 'wf1', workType: 'workflow' }] }]

    renderBoard(makeIssues(1), mockAgentStatus)

    await waitFor(() => {
      expect(screen.queryByText(/No runner is connected/i)).not.toBeInTheDocument()
    })
  })

  it('shows runner unavailable banner when only stale or offline runners exist', () => {
    _runners = [{ id: 'runner-1', kind: 'external', hostname: 'host1', scope: { type: 'global' }, status: 'stale', capabilities: [], coderModels: [], coderModelCount: 0, connectionState: null, activeWorks: [] }]

    renderBoard(makeIssues(1), mockAgentStatus)

    expect(screen.getByText(/No runner is connected/i)).toBeInTheDocument()
  })

  it('shows link to runner status in the banner', () => {
    _runners = []

    const agentStatus: AgentStatus = {
      ...mockAgentStatus,
      runnerAvailable: false,
      embeddedRunnerEnabled: false,
      runnerMessage: 'No runner is connected.',
      runners: [],
    }

    renderBoard(makeIssues(1), agentStatus)

    expect(screen.getByText('View runner status')).toBeInTheDocument()
  })

  it('displays filtered issue count after priority filter applied', () => {
    const issues = [
      makeIssue({ number: 1, status: IssueStatus.Backlog, priority: 'p0' }),
      makeIssue({ number: 2, status: IssueStatus.Backlog, priority: 'p1' }),
      makeIssue({ number: 3, status: IssueStatus.Backlog, priority: 'p2' }),
      makeIssue({ number: 4, status: IssueStatus.Backlog, priority: 'p0' }),
    ]

    window.history.replaceState(null, '', '/?priorities=p0')

    renderBoard(issues, mockAgentStatus)

    const backlogElements = screen.getAllByText('Backlog')
    const backlogCol = backlogElements[0].closest('[class*="flex-col"]')
      || backlogElements[0].closest('div')
    expect(backlogCol?.textContent).toContain('1')
  })
})

describe('Needs attention summary - user-action wording', () => {
  it('renders attention summary item with user-action label for approval awaiting issue', () => {
    const approvalAwaitingIssue = makeIssue({
      number: 180,
      title: 'Plan awaits review',
      status: IssueStatus.Backlog,
      health: IssueHealth.Active,
      approvalState: { status: 'awaiting', requestedAt: '2026-01-01T00:00:00Z' } as ApprovalState,
    })

    renderBoard([approvalAwaitingIssue], mockAgentStatus)

    const summary = screen.getByTestId('needs-attention-summary')
    expect(summary).toBeTruthy()
    expect(within(summary as HTMLElement).getByText(/Needs attention/i)).toBeInTheDocument()
    expect(within(summary as HTMLElement).getByText(/Approval needed/i)).toBeInTheDocument()
    expect(within(summary as HTMLElement).getByText(/#180/i)).toBeInTheDocument()
  })

  it('bases the issue-board summary treatment on rendered issue attention items only', () => {
    const approvalAwaitingIssue = makeIssue({
      number: 181,
      title: 'Plan awaits review while runner is down',
      status: IssueStatus.Backlog,
      health: IssueHealth.Active,
      approvalState: { status: 'awaiting', requestedAt: '2026-01-01T00:00:00Z' } as ApprovalState,
    })
    const agentStatus: AgentStatus = {
      ...mockAgentStatus,
      runnerAvailable: false,
      runnerMessage: 'No runner is connected.',
    }

    renderBoard([approvalAwaitingIssue], agentStatus)

    const summary = screen.getByTestId('needs-attention-summary')
    expect(summary).toHaveAttribute('data-family', 'warning')
    expect(summary).toHaveTextContent('(1)')
    expect(screen.getByTestId('attention-link-181')).toHaveAttribute('data-family', 'warning')
  })

  it('renders attention summary item with user-action label for blocked issue', () => {
    const blockedIssue = makeIssue({
      number: 17,
      title: 'Resume available',
      status: IssueStatus.InProgress,
      health: IssueHealth.Blocked,
    })

    renderBoard([blockedIssue], mockAgentStatus)

    const summary = screen.getByTestId('needs-attention-summary')
    expect(summary).toBeTruthy()
    expect(within(summary as HTMLElement).getByText(/Needs attention/i)).toBeInTheDocument()
    expect(within(summary as HTMLElement).getByText(/Needs action/i)).toBeInTheDocument()
    expect(within(summary as HTMLElement).getByText(/#17/i)).toBeInTheDocument()
  })

  it('renders attention summary item with user-action label for integration failed issue', () => {
    const failedIssue = makeIssue({
      number: 206,
      title: 'integrate task failed',
      status: IssueStatus.InProgress,
      workflowStage: WorkflowStage.Integrate,
      health: IssueHealth.Blocked,
      blockedReason: 'integration task failed',
    })

    renderBoard([failedIssue], mockAgentStatus)

    const summary = screen.getByTestId('needs-attention-summary')
    expect(summary).toBeTruthy()
    expect(within(summary as HTMLElement).getByText(/Needs attention/i)).toBeInTheDocument()
    expect(within(summary as HTMLElement).getByText(/Integration failed/i)).toBeInTheDocument()
    expect(within(summary as HTMLElement).getByText(/#206/i)).toBeInTheDocument()
  })

  it('renders integration failed label for blocked integrate issue', () => {
    const failedIssue = makeIssue({
      number: 207,
      title: 'integration blocked by merge conflict',
      status: IssueStatus.InProgress,
      workflowStage: WorkflowStage.Integrate,
      health: IssueHealth.Blocked,
      blockedReason: 'merge conflict',
    })

    renderBoard([failedIssue], mockAgentStatus)

    const summary = screen.getByTestId('needs-attention-summary')
    expect(summary).toBeTruthy()
    expect(within(summary as HTMLElement).getByText(/Needs attention/i)).toBeInTheDocument()
    expect(within(summary as HTMLElement).getByText(/Integration failed/i)).toBeInTheDocument()
    expect(within(summary as HTMLElement).queryByText(/Needs action/i)).not.toBeInTheDocument()
    expect(within(summary as HTMLElement).getByText(/#207/i)).toBeInTheDocument()
  })


  it('does not render attention summary item for completed workflow', () => {
    const doneUnmergedIssue = makeIssue({
      number: 42,
      title: 'Completed issue',
      status: IssueStatus.Done,
      health: IssueHealth.Done,
    })

    renderBoard([doneUnmergedIssue], mockAgentStatus)

    const summary = screen.queryByTestId('needs-attention-summary')
    expect(summary).toBeNull()
  })

  it('renders the real workflow stage on completed issue cards', () => {
    const doneIntegratedIssue = makeIssue({
      number: 43,
      title: 'Completed integrated issue',
      status: IssueStatus.Done,
      health: IssueHealth.Done,
      workflowStage: WorkflowStage.Integrate,
    })

    renderBoard([doneIntegratedIssue], mockAgentStatus)

    expect(screen.getAllByText('Completed integrated issue').length).toBeGreaterThan(0)
    expect(screen.queryByTestId('integration-badge')).not.toBeInTheDocument()
    const stageBadges = screen.getAllByTestId('workflow-stage-badge').map((el) => el.textContent)
    expect(stageBadges).toContain('Integrate')
    expect(stageBadges).not.toContain('Done')
    expect(screen.queryByText(/Integrating/i)).not.toBeInTheDocument()
  })

  it('renders the current workflow stage on active issue cards', () => {
    const buildIssue = makeIssue({
      number: 45,
      title: 'Build stage issue',
      status: IssueStatus.InProgress,
      health: IssueHealth.Active,
      workflowStage: WorkflowStage.Build,
    })

    renderBoard([buildIssue], mockAgentStatus)

    expect(screen.getAllByText('Build stage issue').length).toBeGreaterThan(0)
    expect(screen.getAllByTestId('workflow-stage-badge').map((el) => el.textContent)).toContain('Build')
  })

  it('renders generic blocked overlay for blocked done issue', () => {
    const doneUnmergedIssue = makeIssue({
      number: 44,
      title: 'Blocked completed issue',
      status: IssueStatus.Done,
      health: IssueHealth.Blocked,
      blockedReason: 'Manual intervention required',
    })

    renderBoard([doneUnmergedIssue], mockAgentStatus)

    expect(screen.getAllByText(/Needs Action/i).length).toBeGreaterThan(0)
    expect(screen.queryByText(/Not merged/i)).not.toBeInTheDocument()
  })

  it('renders attention summary item with Needs action label for blocked issue', () => {
    const blockedIssue = makeIssue({
      number: 99,
      title: 'Issue blocked by dependency',
      status: IssueStatus.InProgress,
      health: IssueHealth.Blocked,
      blockedReason: 'waiting on #88',
    })

    renderBoard([blockedIssue], mockAgentStatus)

    const summary = screen.getByTestId('needs-attention-summary')
    expect(summary).toBeTruthy()
    expect(within(summary as HTMLElement).getByText(/Needs attention/i)).toBeInTheDocument()
    expect(within(summary as HTMLElement).getByText(/Needs action/i)).toBeInTheDocument()
    expect(within(summary as HTMLElement).getByText(/#99/i)).toBeInTheDocument()
  })

  it('does not render attention summary when no actionable items exist', () => {
    const normalIssue = makeIssue({
      number: 1,
      title: 'Normal issue',
      status: IssueStatus.Backlog,
      health: IssueHealth.Active,
    })

    renderBoard([normalIssue], mockAgentStatus)

    expect(screen.queryByText(/Needs attention/i)).not.toBeInTheDocument()
  })
})
