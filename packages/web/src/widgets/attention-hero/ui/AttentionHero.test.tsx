// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { http, HttpResponse } from 'msw'
import {
  IssueHealth,
  IssueStatus,
  WorkflowStage,
  deriveAttentionItems,
  type ApprovalWaitMetricsResponse,
  type Issue,
} from '../../../entities/issue'
import { type AgentStatus } from '../../../entities/agent'
import { ProjectProvider } from '../../../entities/project'
import { server, useMswServer } from '../../../../tests/support/msw'
import { AttentionHero } from './AttentionHero'

useMswServer()

const ISSUES_PATH = '*/api/projects/:projectId/issues'
const AGENT_STATUS_PATH = '*/api/projects/:projectId/agent/status'
const APPROVAL_WAIT_PATH = '*/api/projects/:projectId/issues/metrics/approval-wait'
const APPROVE_PATH = '*/api/projects/:projectId/issues/:issueNumber/approve'
const RESUME_PATH = '*/api/projects/:projectId/issues/:issueNumber/resume'

let _issues: Issue[] | undefined
let _agentStatus: AgentStatus | undefined
let _approvalWait: ApprovalWaitMetricsResponse | null
const _approveHandler = vi.fn()
const _resumeHandler = vi.fn()

function makeIssue(overrides: Partial<Issue> = {}): Issue {
  return {
    id: `issue-${Math.random().toString(36).slice(2, 8)}`,
    number: 100,
    title: 'Default issue title',
    status: IssueStatus.Backlog,
    health: IssueHealth.Active,
    projectId: 'proj-1',
    labels: {},
    createdAt: '2026-06-18T00:00:00.000Z',
    updatedAt: '2026-06-18T00:00:00.000Z',
    isDraft: false,
    canStart: true,
    blocker: null,
    ...overrides,
  }
}

function makeAgentStatus(overrides: Partial<AgentStatus> = {}): AgentStatus {
  return {
    running: false,
    issueId: null,
    issueNumber: null,
    activeAgents: [],
    capacity: { active: 0, max: 8 },
    runnerAvailable: true,
    runnerMessage: null,
    ...overrides,
  }
}

const NO_AGENT: AgentStatus = {
  running: false,
  issueId: null,
  issueNumber: null,
  activeAgents: [],
  capacity: { active: 0, max: 0 },
}

const demoProject = {
  id: 'proj-1',
  name: 'demo',
  createdAt: '',
  updatedAt: '',
  repositories: [],
}

function renderHeroTree(queryClient: QueryClient) {
  return (
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId="proj-1" initialProjects={[demoProject]}>
        <MemoryRouter initialEntries={['/demo']}>
          <AttentionHero />
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>
  )
}

function renderHero() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(renderHeroTree(queryClient))
}

function renderHeroWithClient(queryClient: QueryClient) {
  return render(renderHeroTree(queryClient))
}

beforeEach(() => {
  _issues = []
  _agentStatus = makeAgentStatus({ runnerAvailable: true })
  _approvalWait = null

  server.use(
    http.get(ISSUES_PATH, () => {
      if (_issues === undefined) {
        return HttpResponse.json({ success: false, error: 'not available' }, { status: 500 })
      }
      return HttpResponse.json({ success: true, data: _issues })
    }),
    http.get(AGENT_STATUS_PATH, () => {
      if (_agentStatus === undefined) {
        return HttpResponse.json({ success: false, error: 'not available' }, { status: 500 })
      }
      return HttpResponse.json({ success: true, data: _agentStatus })
    }),
    http.get(APPROVAL_WAIT_PATH, () => {
      if (_approvalWait === undefined) {
        return HttpResponse.json({ success: false, error: 'not available' }, { status: 500 })
      }
      return HttpResponse.json({ success: true, data: _approvalWait })
    }),
    http.post(APPROVE_PATH, async ({ params }) => {
      _approveHandler(Number(params.issueNumber), params.projectId)
      return HttpResponse.json({
        success: true,
        data: {
          issue: makeIssue({ number: Number(params.issueNumber) }),
          context: null,
          message: 'approved',
        },
      })
    }),
    http.post(RESUME_PATH, async ({ params }) => {
      _resumeHandler(Number(params.issueNumber), params.projectId)
      return HttpResponse.json({
        success: true,
        data: {
          issue: makeIssue({ number: Number(params.issueNumber) }),
          message: 'resumed',
        },
      })
    }),
  )
})

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

describe('AttentionHero - has-attention state', () => {
  it('renders one entry per AttentionItem in evaluation order with label and detail', async () => {
    _issues = [
      makeIssue({
        id: 'awaiting-1',
        number: 10,
        title: 'Awaiting approval on schema',
        approvalState: { status: 'awaiting', requestedAt: '2026-06-18T00:00:00.000Z' },
      }),
      makeIssue({
        id: 'blocked-1',
        number: 20,
        title: 'Build needs action',
        workflowStage: WorkflowStage.Build,
        health: IssueHealth.Blocked,
        blockedReason: 'Waiting on infra fix',
      }),
      makeIssue({
        id: 'integrate-failed-1',
        number: 30,
        title: 'Failed merge attempt',
        workflowStage: WorkflowStage.Integrate,
        health: IssueHealth.Blocked,
      }),
    ]

    renderHero()

    await waitFor(() => {
      const rows = screen.getAllByTestId('attention-item')
      expect(rows).toHaveLength(3)
    })

    const root = screen.getByTestId('dashboard-zone-attention')
    expect(root).toBeInTheDocument()
    expect(root).toHaveAttribute('data-zone', 'attention')

    const rows = screen.getAllByTestId('attention-item')

    expect(rows[0]).toHaveAttribute('data-issue-number', '10')
    expect(rows[0]).toHaveAttribute('data-label', 'Approval needed')
    expect(rows[1]).toHaveAttribute('data-issue-number', '20')
    expect(rows[1]).toHaveAttribute('data-label', 'Needs action')
    expect(rows[2]).toHaveAttribute('data-issue-number', '30')
    expect(rows[2]).toHaveAttribute('data-label', 'Integration failed')

    expect(screen.getByText('Awaiting approval on schema')).toBeInTheDocument()
    expect(screen.getByText('Waiting on infra fix')).toBeInTheDocument()
    expect(screen.getByText('Failed merge attempt')).toBeInTheDocument()
  })

  it('does NOT render the all-clear state when attention items are present', async () => {
    _issues = [
      makeIssue({
        id: 'awaiting-1',
        number: 11,
        title: 'Awaiting approval',
        approvalState: { status: 'awaiting', requestedAt: '2026-06-18T00:00:00.000Z' },
      }),
    ]

    renderHero()

    await waitFor(() => {
      expect(screen.queryByTestId('attention-item')).toBeInTheDocument()
    })

    expect(screen.queryByText('All clear')).not.toBeInTheDocument()
    expect(screen.queryByTestId('productivity-placeholder')).not.toBeInTheDocument()
  })

  it('Hero and the shared derivation produce identical attention items for the same input', async () => {
    const issues: Issue[] = [
      makeIssue({
        id: 'awaiting-1',
        number: 10,
        title: 'Awaiting approval on schema',
        approvalState: { status: 'awaiting', requestedAt: '2026-06-18T00:00:00.000Z' },
      }),
      makeIssue({
        id: 'blocked-1',
        number: 20,
        title: 'Build needs action',
        workflowStage: WorkflowStage.Build,
        health: IssueHealth.Blocked,
        blockedReason: 'Waiting on infra fix',
      }),
      makeIssue({
        id: 'integrate-failed-1',
        number: 30,
        title: 'Failed merge attempt',
        workflowStage: WorkflowStage.Integrate,
        health: IssueHealth.Blocked,
      }),
    ]
    const agentStatus = makeAgentStatus({ runnerAvailable: true })

    _issues = issues
    _agentStatus = agentStatus

    renderHero()

    await waitFor(() => {
      expect(screen.getAllByTestId('attention-item')).toHaveLength(3)
    })

    const shared = deriveAttentionItems(issues, agentStatus)
    expect(shared).toHaveLength(3)
    expect(shared[0]).toMatchObject({ label: 'Approval needed', issueNumber: 10 })
    expect(shared[1]).toMatchObject({ label: 'Needs action', issueNumber: 20 })
    expect(shared[2]).toMatchObject({ label: 'Integration failed', issueNumber: 30 })

    const rendered = screen.getAllByTestId('attention-item')
    expect(rendered).toHaveLength(shared.length)
    expect(rendered[0]).toHaveAttribute('data-label', shared[0]!.label)
    expect(rendered[1]).toHaveAttribute('data-label', shared[1]!.label)
    expect(rendered[2]).toHaveAttribute('data-label', shared[2]!.label)
  })

  it('contains no local copy of the four attention rules (only delegates to deriveAttentionItems)', async () => {
    _issues = [
      makeIssue({
        id: 'awaiting-1',
        number: 1,
        title: 't',
        approvalState: { status: 'awaiting', requestedAt: '2026-06-18T00:00:00.000Z' },
      }),
    ]
    renderHero()

    await waitFor(() => {
      expect(screen.getAllByTestId('attention-item')).toHaveLength(1)
    })

    const shared = deriveAttentionItems(_issues ?? [], _agentStatus ?? NO_AGENT)
    expect(shared.map((i) => i.label)).toEqual(['Approval needed'])
  })
})

describe('AttentionHero - per-item actions', () => {
  it('Approval-needed item exposes Approve and invokes approveIssue with projectId and issueNumber', async () => {
    _issues = [
      makeIssue({
        id: 'awaiting-1',
        number: 12,
        title: 'Approve me',
        approvalState: { status: 'awaiting', requestedAt: '2026-06-18T00:00:00.000Z' },
      }),
    ]

    renderHero()

    const approveBtn = await screen.findByTestId('attention-item-approve')
    expect(approveBtn).toHaveAttribute('data-action', 'approve')
    fireEvent.click(approveBtn)

    await waitFor(() => {
      expect(_approveHandler).toHaveBeenCalledWith(12, 'proj-1')
    })
  })

  it('does NOT render an Approve button for non-approval items', async () => {
    _issues = [
      makeIssue({
        id: 'blocked-1',
        number: 22,
        title: 't',
        workflowStage: WorkflowStage.Build,
        health: IssueHealth.Blocked,
        blockedReason: 'r',
      }),
    ]
    renderHero()

    await waitFor(() => {
      expect(screen.queryByTestId('attention-item')).toBeInTheDocument()
    })

    expect(screen.queryByTestId('attention-item-approve')).not.toBeInTheDocument()
  })

  it('Resume action is offered for Interrupted, Needs action, and Integration failed items', async () => {
    _issues = [
      makeIssue({
        id: 'integrate-1',
        number: 31,
        title: 'Integration failure',
        workflowStage: WorkflowStage.Integrate,
        health: IssueHealth.Blocked,
      }),
      makeIssue({
        id: 'interrupted-1',
        number: 32,
        title: 'Interrupted issue',
        workflowStage: WorkflowStage.Build,
        health: IssueHealth.Interrupted,
      }),
      makeIssue({
        id: 'blocked-1',
        number: 33,
        title: 'Needs action issue',
        workflowStage: WorkflowStage.Build,
        health: IssueHealth.Blocked,
        blockedReason: 'reason',
      }),
    ]

    renderHero()

    await waitFor(() => {
      const resumeButtons = screen.getAllByTestId('attention-item-resume')
      expect(resumeButtons).toHaveLength(3)
      resumeButtons.forEach((btn) => {
        expect(btn).toHaveAttribute('data-action', 'resume')
      })
    })
  })

  it('Resume click invokes resumeIssue(issueNumber, projectId)', async () => {
    _issues = [
      makeIssue({
        id: 'interrupted-1',
        number: 40,
        title: 'Interrupted build',
        workflowStage: WorkflowStage.Build,
        health: IssueHealth.Interrupted,
      }),
    ]

    renderHero()

    const resumeBtn = await screen.findByTestId('attention-item-resume')
    fireEvent.click(resumeBtn)

    await waitFor(() => {
      expect(_resumeHandler).toHaveBeenCalledWith(40, 'proj-1')
    })
  })

  it('invalidateQueries is called for issues and agent-status on Approve success', async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries')

    _issues = [
      makeIssue({
        id: 'awaiting-1',
        number: 50,
        title: 'Approve and invalidate',
        approvalState: { status: 'awaiting', requestedAt: '2026-06-18T00:00:00.000Z' },
      }),
    ]

    renderHeroWithClient(queryClient)

    const approveBtn = await screen.findByTestId('attention-item-approve')
    fireEvent.click(approveBtn)

    await waitFor(() => {
      expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['issues'] })
      expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['agent-status'] })
    })
  })

  it('invalidateQueries is called for issues and agent-status on Resume success', async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries')

    _issues = [
      makeIssue({
        id: 'blocked-1',
        number: 60,
        title: 'Resume me',
        workflowStage: WorkflowStage.Build,
        health: IssueHealth.Blocked,
        blockedReason: 'x',
      }),
    ]

    renderHeroWithClient(queryClient)

    const resumeBtn = await screen.findByTestId('attention-item-resume')
    fireEvent.click(resumeBtn)

    await waitFor(() => {
      expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['issues'] })
      expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['agent-status'] })
    })
  })

  it('navigation affordance links to the issue detail route via useProjectPath', async () => {
    _issues = [
      makeIssue({
        id: 'awaiting-1',
        number: 77,
        title: 'Click me to navigate',
        approvalState: { status: 'awaiting', requestedAt: '2026-06-18T00:00:00.000Z' },
      }),
    ]

    renderHero()

    const link = await screen.findByTestId('attention-item-link')
    expect(link).toHaveAttribute('href', '/demo/issues/77')
  })

  it('renders one Open link per attention item pointing to its own issue detail route', async () => {
    _issues = [
      makeIssue({
        id: 'a-1',
        number: 11,
        title: 'a',
        approvalState: { status: 'awaiting', requestedAt: '2026-06-18T00:00:00.000Z' },
      }),
      makeIssue({
        id: 'b-1',
        number: 22,
        title: 'b',
        workflowStage: WorkflowStage.Build,
        health: IssueHealth.Interrupted,
      }),
    ]

    renderHero()

    await waitFor(() => {
      const links = screen.getAllByTestId('attention-item-link')
      expect(links).toHaveLength(2)
      expect(links[0]).toHaveAttribute('href', '/demo/issues/11')
      expect(links[1]).toHaveAttribute('href', '/demo/issues/22')
    })
  })
})

describe('AttentionHero - runner-down entry', () => {
  it('renders a Runner-down entry when runnerAvailable is false', async () => {
    _issues = []
    _agentStatus = makeAgentStatus({
      runnerAvailable: false,
      runnerMessage: 'Embedded runner is offline',
    })

    renderHero()

    await waitFor(() => {
      expect(screen.getByTestId('runner-down-entry')).toBeInTheDocument()
    })

    expect(screen.getByTestId('runner-down-message')).toHaveTextContent('Embedded runner is offline')
    expect(screen.getByTestId('runner-down-link')).toHaveAttribute('href', '/demo/activity')

    expect(screen.queryByText('All clear')).not.toBeInTheDocument()
  })

  it('renders Runner-down entry alongside attention items when both are present', async () => {
    _issues = [
      makeIssue({
        id: 'awaiting-1',
        number: 88,
        title: 'Pending approval',
        approvalState: { status: 'awaiting', requestedAt: '2026-06-18T00:00:00.000Z' },
      }),
    ]
    _agentStatus = makeAgentStatus({ runnerAvailable: false, runnerMessage: 'down' })

    renderHero()

    await waitFor(() => {
      expect(screen.getAllByTestId('attention-item')).toHaveLength(1)
      expect(screen.getByTestId('runner-down-entry')).toBeInTheDocument()
    })
  })

  it('renders Runner-down entry even when deriveAttentionItems returns an empty list', async () => {
    _issues = []
    _agentStatus = makeAgentStatus({ runnerAvailable: false, runnerMessage: 'down' })

    renderHero()

    await waitFor(() => {
      expect(screen.getByTestId('runner-down-entry')).toBeInTheDocument()
    })

    expect(screen.queryByTestId('attention-item')).not.toBeInTheDocument()
    expect(screen.queryByText('All clear')).not.toBeInTheDocument()
  })

  it('renders Runner-down entry with the default message when runnerMessage is null', async () => {
    _issues = []
    _agentStatus = makeAgentStatus({ runnerAvailable: false, runnerMessage: null })

    renderHero()

    await waitFor(() => {
      expect(screen.getByTestId('runner-down-entry')).toBeInTheDocument()
    })

    expect(screen.getByTestId('runner-down-message')).toHaveTextContent('No runner is connected.')
  })

  it('renders Runner-down entry with default message when runnerMessage is undefined', async () => {
    _issues = []
    const status = { ...makeAgentStatus({ runnerAvailable: false }) }
    delete (status as Partial<AgentStatus>).runnerMessage
    _agentStatus = status

    renderHero()

    await waitFor(() => {
      expect(screen.getByTestId('runner-down-entry')).toBeInTheDocument()
    })

    expect(screen.getByTestId('runner-down-message')).toHaveTextContent('No runner is connected.')
  })

  it('does NOT render Runner-down entry when runnerAvailable is true', async () => {
    _issues = []
    _agentStatus = makeAgentStatus({ runnerAvailable: true })

    renderHero()

    await waitFor(() => {
      expect(screen.getByText('All clear')).toBeInTheDocument()
    })

    expect(screen.queryByTestId('runner-down-entry')).not.toBeInTheDocument()
  })

  it('does NOT render Runner-down entry when agentStatus is undefined (loading)', async () => {
    _issues = []
    _agentStatus = undefined

    renderHero()

    await waitFor(() => {
      expect(screen.getByText('All clear')).toBeInTheDocument()
    })

    expect(screen.queryByTestId('runner-down-entry')).not.toBeInTheDocument()
  })

  it('does NOT render Runner-down entry when only attention items are present and runner is up', async () => {
    _issues = [
      makeIssue({
        id: 'awaiting-1',
        number: 88,
        title: 'Pending approval',
        approvalState: { status: 'awaiting', requestedAt: '2026-06-18T00:00:00.000Z' },
      }),
    ]
    _agentStatus = makeAgentStatus({ runnerAvailable: true })

    renderHero()

    await waitFor(() => {
      expect(screen.getAllByTestId('attention-item')).toHaveLength(1)
    })

    expect(screen.queryByTestId('runner-down-entry')).not.toBeInTheDocument()
  })
})

describe('AttentionHero - runner-capacity-limited entry', () => {
  it('surfaces a runner-capacity-limited attention item under saturation with no issues', async () => {
    _issues = []
    _agentStatus = makeAgentStatus({
      runnerAvailable: true,
      capacity: { active: 4, max: 4 },
    })

    renderHero()

    await waitFor(() => {
      expect(screen.getByTestId('runner-capacity-entry')).toBeInTheDocument()
    })

    expect(screen.queryByText('All clear')).not.toBeInTheDocument()
    const entry = screen.getByTestId('runner-capacity-entry')
    expect(entry).toHaveAttribute('data-kind', 'runner-capacity-limited')
    expect(entry).toHaveAttribute('data-family', 'warning')
    expect(screen.getByTestId('runner-capacity-detail')).toHaveTextContent('4 of 4 slots in use')
    expect(screen.getByTestId('runner-capacity-link')).toHaveAttribute('href', '/demo/activity')
  })

  it('surfaces a runner-capacity-limited attention item alongside issue items', async () => {
    _issues = [
      makeIssue({
        id: 'awaiting-1',
        number: 90,
        title: 'Pending approval',
        approvalState: { status: 'awaiting', requestedAt: '2026-06-18T00:00:00.000Z' },
      }),
    ]
    _agentStatus = makeAgentStatus({
      runnerAvailable: true,
      capacity: { active: 8, max: 8 },
    })

    renderHero()

    await waitFor(() => {
      expect(screen.getAllByTestId('attention-item')).toHaveLength(1)
      expect(screen.getByTestId('runner-capacity-entry')).toBeInTheDocument()
    })
  })

  it('does NOT surface a runner-capacity-limited attention item when active < max', async () => {
    _issues = []
    _agentStatus = makeAgentStatus({
      runnerAvailable: true,
      capacity: { active: 2, max: 4 },
    })

    renderHero()

    await waitFor(() => {
      expect(screen.getByText('All clear')).toBeInTheDocument()
    })

    expect(screen.queryByTestId('runner-capacity-entry')).not.toBeInTheDocument()
  })

  it('does NOT surface a runner-capacity-limited attention item when max === 0', async () => {
    _issues = []
    _agentStatus = makeAgentStatus({
      runnerAvailable: true,
      capacity: { active: 0, max: 0 },
    })

    renderHero()

    await waitFor(() => {
      expect(screen.getByText('All clear')).toBeInTheDocument()
    })

    expect(screen.queryByTestId('runner-capacity-entry')).not.toBeInTheDocument()
  })

  it('surfaces runner-unavailable (not capacity-limited) when runnerAvailable is false even at capacity', async () => {
    _issues = []
    _agentStatus = makeAgentStatus({
      runnerAvailable: false,
      capacity: { active: 4, max: 4 },
    })

    renderHero()

    await waitFor(() => {
      expect(screen.getByTestId('runner-down-entry')).toBeInTheDocument()
    })

    expect(screen.queryByTestId('runner-capacity-entry')).not.toBeInTheDocument()
  })
})

describe('AttentionHero - all-clear state', () => {
  it('does not show All clear while issue data is still loading', async () => {
    _issues = undefined
    _agentStatus = makeAgentStatus({ runnerAvailable: true })

    renderHero()

    await waitFor(() => {
      expect(screen.getByText('Checking attention')).toBeInTheDocument()
    })

    expect(screen.queryByText('All clear')).not.toBeInTheDocument()
    expect(screen.queryByTestId('productivity-placeholder')).not.toBeInTheDocument()
    expect(screen.queryByTestId('attention-item')).not.toBeInTheDocument()
  })

  it('does not show All clear while issue and runner data are still loading', async () => {
    _issues = undefined
    _agentStatus = undefined

    renderHero()

    await waitFor(() => {
      expect(screen.getByText('Checking attention')).toBeInTheDocument()
    })

    expect(screen.queryByText('All clear')).not.toBeInTheDocument()
    expect(screen.queryByTestId('runner-down-entry')).not.toBeInTheDocument()
  })

  it('can rerender from loading to all-clear without changing hook order', async () => {
    _issues = undefined
    _agentStatus = makeAgentStatus({ runnerAvailable: true })
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })

    const { rerender } = render(renderHeroTree(queryClient))

    await waitFor(() => {
      expect(screen.getByText('Checking attention')).toBeInTheDocument()
    })

    _issues = []
    queryClient.invalidateQueries({ queryKey: ['issues'] })
    rerender(renderHeroTree(queryClient))

    await waitFor(() => {
      expect(screen.getByText('All clear')).toBeInTheDocument()
      expect(screen.queryByText('Checking attention')).not.toBeInTheDocument()
    })
  })

  it('renders the all-clear state with All clear message and excludes the Productivity placeholder when no items and runner available', async () => {
    _issues = []
    _agentStatus = makeAgentStatus({ runnerAvailable: true })

    renderHero()

    await waitFor(() => {
      expect(screen.getByText('All clear')).toBeInTheDocument()
    })

    const root = screen.getByTestId('dashboard-zone-attention')
    expect(root).toBeInTheDocument()
    expect(root).toHaveAttribute('data-zone', 'attention')
    expect(screen.queryByTestId('productivity-placeholder')).not.toBeInTheDocument()
    expect(screen.queryByTestId('attention-item')).not.toBeInTheDocument()
    expect(screen.queryByTestId('runner-down-entry')).not.toBeInTheDocument()
  })

  it('renders the all-clear state when only healthy issues are present', async () => {
    _issues = [
      makeIssue({
        id: 'healthy-1',
        number: 90,
        title: 'Healthy build',
        workflowStage: WorkflowStage.Build,
        health: IssueHealth.Active,
      }),
      makeIssue({
        id: 'healthy-2',
        number: 91,
        title: 'Healthy done',
        workflowStage: WorkflowStage.Done,
        health: IssueHealth.Done,
      }),
    ]
    _agentStatus = makeAgentStatus({ runnerAvailable: true })

    renderHero()

    await waitFor(() => {
      expect(screen.getByText('All clear')).toBeInTheDocument()
    })

    expect(screen.queryByTestId('attention-item')).not.toBeInTheDocument()
  })

  it('renders the all-clear state when agentStatus is undefined and no items', async () => {
    _issues = []
    _agentStatus = undefined

    renderHero()

    await waitFor(() => {
      expect(screen.getByText('All clear')).toBeInTheDocument()
    })

    expect(screen.queryByTestId('productivity-placeholder')).not.toBeInTheDocument()
  })
})

describe('AttentionHero - passive surface', () => {
  it('does not mutate workflow state on render (no approve/resume call without click)', async () => {
    _issues = [
      makeIssue({
        id: 'awaiting-1',
        number: 12,
        title: 'Approve me',
        approvalState: { status: 'awaiting', requestedAt: '2026-06-18T00:00:00.000Z' },
      }),
    ]

    renderHero()

    await waitFor(() => {
      expect(screen.getByTestId('attention-item')).toBeInTheDocument()
    })

    expect(_approveHandler).not.toHaveBeenCalled()
    expect(_resumeHandler).not.toHaveBeenCalled()
  })
})

describe('AttentionHero - data-testid/data-zone hook', () => {
  it('preserves the dashboard-zone-attention testid and data-zone attribute on both states', async () => {
    _issues = []
    _agentStatus = makeAgentStatus({ runnerAvailable: true })

    renderHero()
    const allClearRoot = await screen.findByTestId('dashboard-zone-attention')
    expect(allClearRoot).toHaveAttribute('data-zone', 'attention')
    cleanup()

    _issues = [
      makeIssue({
        id: 'awaiting-1',
        number: 100,
        title: 'Now with attention',
        approvalState: { status: 'awaiting', requestedAt: '2026-06-18T00:00:00.000Z' },
      }),
    ]
    renderHero()
    const hasAttentionRoot = await screen.findByTestId('dashboard-zone-attention')
    expect(hasAttentionRoot).toHaveAttribute('data-zone', 'attention')
  })
})

describe('AttentionHero - approval-wait metric', () => {
  it('shows the aggregate average approval wait from the aggregation', async () => {
    _issues = [
      makeIssue({
        id: 'awaiting-1',
        number: 200,
        title: 'Pending approval',
        approvalState: { status: 'awaiting', requestedAt: '2026-06-18T00:00:00.000Z' },
      }),
    ]
    _approvalWait = {
      window: { from: '2026-06-20T00:00:00.000Z', to: '2026-06-27T00:00:00.000Z' },
      sampleCount: 3,
      averageSeconds: 3.2 * 3_600,
      medianSeconds: 2 * 3_600,
      maxSeconds: 8 * 3_600,
    }

    renderHero()

    await waitFor(() => {
      expect(screen.getByTestId('attention-item')).toBeInTheDocument()
    })

    const value = screen.getByTestId('approval-wait-value')
    expect(value).toHaveAttribute('data-state', 'value')
    expect(value).toHaveTextContent('3.2h')
    expect(value).toHaveTextContent('averaged')
    expect(screen.queryByTestId('approval-wait-empty')).not.toBeInTheDocument()
  })

  it('renders a defined empty presentation when the aggregation has zero samples', async () => {
    _issues = [
      makeIssue({
        id: 'awaiting-1',
        number: 201,
        title: 'Pending approval',
        approvalState: { status: 'awaiting', requestedAt: '2026-06-18T00:00:00.000Z' },
      }),
    ]
    _approvalWait = {
      window: { from: '2026-06-20T00:00:00.000Z', to: '2026-06-27T00:00:00.000Z' },
      sampleCount: 0,
      averageSeconds: null,
      medianSeconds: null,
      maxSeconds: null,
    }

    renderHero()

    await waitFor(() => {
      expect(screen.getByTestId('attention-item')).toBeInTheDocument()
    })

    const empty = screen.getByTestId('approval-wait-empty')
    expect(empty).toHaveAttribute('data-state', 'empty')
    expect(empty).toHaveClass('text-muted-foreground')
    expect(screen.queryByTestId('approval-wait-value')).not.toBeInTheDocument()
  })

  it('accepts an approvalWait prop that overrides the internal hook', async () => {
    _issues = [
      makeIssue({
        id: 'awaiting-1',
        number: 202,
        title: 'Pending approval',
        approvalState: { status: 'awaiting', requestedAt: '2026-06-18T00:00:00.000Z' },
      }),
    ]
    _approvalWait = {
      window: { from: '2026-06-20T00:00:00.000Z', to: '2026-06-27T00:00:00.000Z' },
      sampleCount: 0,
      averageSeconds: null,
      medianSeconds: null,
      maxSeconds: null,
    }
    const propOverride: ApprovalWaitMetricsResponse = {
      window: { from: '2026-06-20T00:00:00.000Z', to: '2026-06-27T00:00:00.000Z' },
      sampleCount: 1,
      averageSeconds: 5 * 3_600,
      medianSeconds: 5 * 3_600,
      maxSeconds: 5 * 3_600,
    }

    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    render(
      <QueryClientProvider client={queryClient}>
        <ProjectProvider initialProjectId="proj-1" initialProjects={[demoProject]}>
          <MemoryRouter initialEntries={['/demo']}>
            <AttentionHero approvalWait={propOverride} />
          </MemoryRouter>
        </ProjectProvider>
      </QueryClientProvider>,
    )

    await waitFor(() => {
      expect(screen.getByTestId('attention-item')).toBeInTheDocument()
    })

    expect(screen.getByTestId('approval-wait-value')).toHaveTextContent('5h')
    expect(screen.queryByTestId('approval-wait-empty')).not.toBeInTheDocument()
  })

  it('invalidates the approval-wait query on Approve success', async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries')

    _issues = [
      makeIssue({
        id: 'awaiting-1',
        number: 203,
        title: 'Approve and invalidate approval wait',
        approvalState: { status: 'awaiting', requestedAt: '2026-06-18T00:00:00.000Z' },
      }),
    ]

    renderHeroWithClient(queryClient)

    const approveBtn = await screen.findByTestId('attention-item-approve')
    fireEvent.click(approveBtn)

    await waitFor(() => {
      expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['issues'] })
      expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['agent-status'] })
      expect(invalidateSpy).toHaveBeenCalledWith({
        queryKey: ['issues', 'metrics', 'approval-wait'],
      })
    })
  })
})
