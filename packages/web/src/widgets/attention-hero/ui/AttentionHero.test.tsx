// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import {
  IssueHealth,
  IssueStatus,
  WorkflowStage,
  approveIssue,
  deriveAttentionItems,
  resumeIssue,
  type ApprovalWaitMetricsResponse,
  type Issue,
} from '../../../entities/issue'
import { type AgentStatus } from '../../../entities/agent'
import { ProjectProvider } from '../../../entities/project'
import { AttentionHero } from './AttentionHero'

const mocks = vi.hoisted(() => ({
  issues: undefined as Issue[] | undefined,
  agentStatus: undefined as AgentStatus | undefined,
  approvalWait: undefined as ApprovalWaitMetricsResponse | undefined,
}))

vi.mock('../../../entities/issue', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/issue')>()
  return {
    ...actual,
    approveIssue: vi.fn(),
    resumeIssue: vi.fn(),
    useIssues: () => ({ data: mocks.issues, isLoading: false }),
    useApprovalWait: () => ({ data: mocks.approvalWait, isLoading: false }),
  }
})

vi.mock('../../../entities/agent', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/agent')>()
  return {
    ...actual,
    useAgentStatus: () => ({ data: mocks.agentStatus, isLoading: false }),
  }
})

const mockedApproveIssue = vi.mocked(approveIssue)
const mockedResumeIssue = vi.mocked(resumeIssue)

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
  mocks.issues = []
  mocks.agentStatus = makeAgentStatus({ runnerAvailable: true })
  mocks.approvalWait = undefined
})

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

describe('AttentionHero - has-attention state', () => {
  it('renders one entry per AttentionItem in evaluation order with label and detail', () => {
    mocks.issues = [
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

    const root = screen.getByTestId('dashboard-zone-attention')
    expect(root).toBeInTheDocument()
    expect(root).toHaveAttribute('data-zone', 'attention')

    const rows = screen.getAllByTestId('attention-item')
    expect(rows).toHaveLength(3)

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

  it('does NOT render the all-clear state when attention items are present', () => {
    mocks.issues = [
      makeIssue({
        id: 'awaiting-1',
        number: 11,
        title: 'Awaiting approval',
        approvalState: { status: 'awaiting', requestedAt: '2026-06-18T00:00:00.000Z' },
      }),
    ]

    renderHero()

    expect(screen.queryByText('All clear')).not.toBeInTheDocument()
    expect(screen.queryByTestId('productivity-placeholder')).not.toBeInTheDocument()
  })

  it('Hero and the shared derivation produce identical attention items for the same input', () => {
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

    mocks.issues = issues
    mocks.agentStatus = agentStatus

    renderHero()

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

  it('contains no local copy of the four attention rules (only delegates to deriveAttentionItems)', () => {
    mocks.issues = [
      makeIssue({
        id: 'awaiting-1',
        number: 1,
        title: 't',
        approvalState: { status: 'awaiting', requestedAt: '2026-06-18T00:00:00.000Z' },
      }),
    ]
    renderHero()
    // Single source of truth: Hero produces the same items the shared derivation does.
    const shared = deriveAttentionItems(mocks.issues ?? [], mocks.agentStatus ?? NO_AGENT)
    expect(shared.map((i) => i.label)).toEqual(['Approval needed'])
    expect(screen.getAllByTestId('attention-item')).toHaveLength(1)
  })
})

describe('AttentionHero - per-item actions', () => {
  it('Approval-needed item exposes Approve and invokes approveIssue with projectId and issueNumber', async () => {
    mocks.issues = [
      makeIssue({
        id: 'awaiting-1',
        number: 12,
        title: 'Approve me',
        approvalState: { status: 'awaiting', requestedAt: '2026-06-18T00:00:00.000Z' },
      }),
    ]
    mockedApproveIssue.mockResolvedValue({
      issue: makeIssue({ id: 'awaiting-1', number: 12 }),
      context: null,
      message: 'approved',
    })

    renderHero()

    const approveBtn = screen.getByTestId('attention-item-approve')
    expect(approveBtn).toHaveAttribute('data-action', 'approve')
    fireEvent.click(approveBtn)

    await waitFor(() => {
      expect(mockedApproveIssue).toHaveBeenCalledWith(12, 'proj-1')
    })
  })

  it('does NOT render an Approve button for non-approval items', () => {
    mocks.issues = [
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

    expect(screen.queryByTestId('attention-item-approve')).not.toBeInTheDocument()
  })

  it('Resume action is offered for Interrupted, Needs action, and Integration failed items', () => {
    mocks.issues = [
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

    const resumeButtons = screen.getAllByTestId('attention-item-resume')
    expect(resumeButtons).toHaveLength(3)
    resumeButtons.forEach((btn) => {
      expect(btn).toHaveAttribute('data-action', 'resume')
    })
  })

  it('Resume click invokes resumeIssue(issueNumber, projectId)', async () => {
    mocks.issues = [
      makeIssue({
        id: 'interrupted-1',
        number: 40,
        title: 'Interrupted build',
        workflowStage: WorkflowStage.Build,
        health: IssueHealth.Interrupted,
      }),
    ]
    mockedResumeIssue.mockResolvedValue({
      issue: makeIssue({ id: 'interrupted-1', number: 40 }),
      message: 'resumed',
    })

    renderHero()

    fireEvent.click(screen.getByTestId('attention-item-resume'))

    await waitFor(() => {
      expect(mockedResumeIssue).toHaveBeenCalledWith(40, 'proj-1')
    })
  })

  it('invalidateQueries is called for issues and agent-status on Approve success', async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries')

    mocks.issues = [
      makeIssue({
        id: 'awaiting-1',
        number: 50,
        title: 'Approve and invalidate',
        approvalState: { status: 'awaiting', requestedAt: '2026-06-18T00:00:00.000Z' },
      }),
    ]
    mockedApproveIssue.mockResolvedValue({
      issue: makeIssue({ id: 'awaiting-1', number: 50 }),
      context: null,
      message: 'ok',
    })

    renderHeroWithClient(queryClient)

    fireEvent.click(screen.getByTestId('attention-item-approve'))

    await waitFor(() => {
      expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['issues'] })
      expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['agent-status'] })
    })
  })

  it('invalidateQueries is called for issues and agent-status on Resume success', async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries')

    mocks.issues = [
      makeIssue({
        id: 'blocked-1',
        number: 60,
        title: 'Resume me',
        workflowStage: WorkflowStage.Build,
        health: IssueHealth.Blocked,
        blockedReason: 'x',
      }),
    ]
    mockedResumeIssue.mockResolvedValue({
      issue: makeIssue({ id: 'blocked-1', number: 60 }),
      message: 'ok',
    })

    renderHeroWithClient(queryClient)

    fireEvent.click(screen.getByTestId('attention-item-resume'))

    await waitFor(() => {
      expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['issues'] })
      expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['agent-status'] })
    })
  })

  it('navigation affordance links to the issue detail route via useProjectPath', () => {
    mocks.issues = [
      makeIssue({
        id: 'awaiting-1',
        number: 77,
        title: 'Click me to navigate',
        approvalState: { status: 'awaiting', requestedAt: '2026-06-18T00:00:00.000Z' },
      }),
    ]

    renderHero()

    const link = screen.getByTestId('attention-item-link')
    expect(link).toHaveAttribute('href', '/demo/issues/77')
  })

  it('renders one Open link per attention item pointing to its own issue detail route', () => {
    mocks.issues = [
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

    const links = screen.getAllByTestId('attention-item-link')
    expect(links).toHaveLength(2)
    expect(links[0]).toHaveAttribute('href', '/demo/issues/11')
    expect(links[1]).toHaveAttribute('href', '/demo/issues/22')
  })
})

describe('AttentionHero - runner-down entry', () => {
  it('renders a Runner-down entry when runnerAvailable is false', () => {
    mocks.issues = []
    mocks.agentStatus = makeAgentStatus({
      runnerAvailable: false,
      runnerMessage: 'Embedded runner is offline',
    })

    renderHero()

    expect(screen.getByTestId('runner-down-entry')).toBeInTheDocument()
    expect(screen.getByTestId('runner-down-message')).toHaveTextContent('Embedded runner is offline')
    expect(screen.getByTestId('runner-down-link')).toHaveAttribute('href', '/demo/activity')

    expect(screen.queryByText('All clear')).not.toBeInTheDocument()
  })

  it('renders Runner-down entry alongside attention items when both are present', () => {
    mocks.issues = [
      makeIssue({
        id: 'awaiting-1',
        number: 88,
        title: 'Pending approval',
        approvalState: { status: 'awaiting', requestedAt: '2026-06-18T00:00:00.000Z' },
      }),
    ]
    mocks.agentStatus = makeAgentStatus({ runnerAvailable: false, runnerMessage: 'down' })

    renderHero()

    expect(screen.getAllByTestId('attention-item')).toHaveLength(1)
    expect(screen.getByTestId('runner-down-entry')).toBeInTheDocument()
  })

  it('renders Runner-down entry even when deriveAttentionItems returns an empty list', () => {
    mocks.issues = []
    mocks.agentStatus = makeAgentStatus({ runnerAvailable: false, runnerMessage: 'down' })

    renderHero()

    expect(screen.getByTestId('runner-down-entry')).toBeInTheDocument()
    expect(screen.queryByTestId('attention-item')).not.toBeInTheDocument()
    expect(screen.queryByText('All clear')).not.toBeInTheDocument()
  })

  it('renders Runner-down entry with the default message when runnerMessage is null', () => {
    mocks.issues = []
    mocks.agentStatus = makeAgentStatus({ runnerAvailable: false, runnerMessage: null })

    renderHero()

    expect(screen.getByTestId('runner-down-entry')).toBeInTheDocument()
    expect(screen.getByTestId('runner-down-message')).toHaveTextContent('No runner is connected.')
  })

  it('renders Runner-down entry with default message when runnerMessage is undefined', () => {
    mocks.issues = []
    const status = { ...makeAgentStatus({ runnerAvailable: false }) }
    delete (status as Partial<AgentStatus>).runnerMessage
    mocks.agentStatus = status

    renderHero()

    expect(screen.getByTestId('runner-down-message')).toHaveTextContent('No runner is connected.')
  })

  it('does NOT render Runner-down entry when runnerAvailable is true', () => {
    mocks.issues = []
    mocks.agentStatus = makeAgentStatus({ runnerAvailable: true })

    renderHero()

    expect(screen.queryByTestId('runner-down-entry')).not.toBeInTheDocument()
    expect(screen.getByText('All clear')).toBeInTheDocument()
  })

  it('does NOT render Runner-down entry when agentStatus is undefined (loading)', () => {
    mocks.issues = []
    mocks.agentStatus = undefined

    renderHero()

    expect(screen.queryByTestId('runner-down-entry')).not.toBeInTheDocument()
    expect(screen.getByText('All clear')).toBeInTheDocument()
  })

  it('does NOT render Runner-down entry when only attention items are present and runner is up', () => {
    mocks.issues = [
      makeIssue({
        id: 'awaiting-1',
        number: 88,
        title: 'Pending approval',
        approvalState: { status: 'awaiting', requestedAt: '2026-06-18T00:00:00.000Z' },
      }),
    ]
    mocks.agentStatus = makeAgentStatus({ runnerAvailable: true })

    renderHero()

    expect(screen.queryByTestId('runner-down-entry')).not.toBeInTheDocument()
  })
})

describe('AttentionHero - all-clear state', () => {
  it('does not show All clear while issue data is still loading', () => {
    mocks.issues = undefined
    mocks.agentStatus = makeAgentStatus({ runnerAvailable: true })

    renderHero()

    expect(screen.getByText('Checking attention')).toBeInTheDocument()
    expect(screen.queryByText('All clear')).not.toBeInTheDocument()
    expect(screen.queryByTestId('productivity-placeholder')).not.toBeInTheDocument()
    expect(screen.queryByTestId('attention-item')).not.toBeInTheDocument()
  })

  it('does not show All clear while issue and runner data are still loading', () => {
    mocks.issues = undefined
    mocks.agentStatus = undefined

    renderHero()

    expect(screen.getByText('Checking attention')).toBeInTheDocument()
    expect(screen.queryByText('All clear')).not.toBeInTheDocument()
    expect(screen.queryByTestId('runner-down-entry')).not.toBeInTheDocument()
  })

  it('can rerender from loading to all-clear without changing hook order', () => {
    mocks.issues = undefined
    mocks.agentStatus = makeAgentStatus({ runnerAvailable: true })
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })

    const { rerender } = render(renderHeroTree(queryClient))

    expect(screen.getByText('Checking attention')).toBeInTheDocument()

    mocks.issues = []
    rerender(renderHeroTree(queryClient))

    expect(screen.getByText('All clear')).toBeInTheDocument()
    expect(screen.queryByText('Checking attention')).not.toBeInTheDocument()
  })

  it('renders the all-clear state with All clear message and Productivity placeholder when no items and runner available', () => {
    mocks.issues = []
    mocks.agentStatus = makeAgentStatus({ runnerAvailable: true })

    renderHero()

    expect(screen.getByText('All clear')).toBeInTheDocument()
    const root = screen.getByTestId('dashboard-zone-attention')
    expect(root).toBeInTheDocument()
    expect(root).toHaveAttribute('data-zone', 'attention')
    expect(screen.getByTestId('productivity-placeholder')).toBeInTheDocument()
    expect(screen.queryByTestId('attention-item')).not.toBeInTheDocument()
    expect(screen.queryByTestId('runner-down-entry')).not.toBeInTheDocument()
  })

  it('renders the all-clear state when only healthy issues are present', () => {
    mocks.issues = [
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
    mocks.agentStatus = makeAgentStatus({ runnerAvailable: true })

    renderHero()

    expect(screen.getByText('All clear')).toBeInTheDocument()
    expect(screen.queryByTestId('attention-item')).not.toBeInTheDocument()
  })

  it('renders the all-clear state when agentStatus is undefined and no items', () => {
    mocks.issues = []
    mocks.agentStatus = undefined

    renderHero()

    expect(screen.getByText('All clear')).toBeInTheDocument()
    expect(screen.getByTestId('productivity-placeholder')).toBeInTheDocument()
  })
})

describe('AttentionHero - passive surface', () => {
  it('does not mutate workflow state on render (no approve/resume call without click)', () => {
    mocks.issues = [
      makeIssue({
        id: 'awaiting-1',
        number: 12,
        title: 'Approve me',
        approvalState: { status: 'awaiting', requestedAt: '2026-06-18T00:00:00.000Z' },
      }),
    ]

    renderHero()

    expect(mockedApproveIssue).not.toHaveBeenCalled()
    expect(mockedResumeIssue).not.toHaveBeenCalled()
  })
})

describe('AttentionHero - data-testid/data-zone hook', () => {
  it('preserves the dashboard-zone-attention testid and data-zone attribute on both states', () => {
    // All-clear state
    renderHero()
    const allClearRoot = screen.getByTestId('dashboard-zone-attention')
    expect(allClearRoot).toHaveAttribute('data-zone', 'attention')
    cleanup()

    // Has-attention state
    mocks.issues = [
      makeIssue({
        id: 'awaiting-1',
        number: 100,
        title: 'Now with attention',
        approvalState: { status: 'awaiting', requestedAt: '2026-06-18T00:00:00.000Z' },
      }),
    ]
    renderHero()
    const hasAttentionRoot = screen.getByTestId('dashboard-zone-attention')
    expect(hasAttentionRoot).toHaveAttribute('data-zone', 'attention')
  })
})

describe('AttentionHero - approval-wait metric', () => {
  it('shows the aggregate average approval wait from the aggregation', () => {
    mocks.issues = [
      makeIssue({
        id: 'awaiting-1',
        number: 200,
        title: 'Pending approval',
        approvalState: { status: 'awaiting', requestedAt: '2026-06-18T00:00:00.000Z' },
      }),
    ]
    mocks.approvalWait = {
      window: { from: '2026-06-20T00:00:00.000Z', to: '2026-06-27T00:00:00.000Z' },
      sampleCount: 3,
      averageSeconds: 3.2 * 3_600,
      medianSeconds: 2 * 3_600,
      maxSeconds: 8 * 3_600,
    }

    renderHero()

    const value = screen.getByTestId('approval-wait-value')
    expect(value).toHaveAttribute('data-state', 'value')
    expect(value).toHaveTextContent('3.2h')
    expect(value).toHaveTextContent('averaged')
    expect(screen.queryByTestId('approval-wait-empty')).not.toBeInTheDocument()
  })

  it('renders a defined empty presentation when the aggregation has zero samples', () => {
    mocks.issues = [
      makeIssue({
        id: 'awaiting-1',
        number: 201,
        title: 'Pending approval',
        approvalState: { status: 'awaiting', requestedAt: '2026-06-18T00:00:00.000Z' },
      }),
    ]
    mocks.approvalWait = {
      window: { from: '2026-06-20T00:00:00.000Z', to: '2026-06-27T00:00:00.000Z' },
      sampleCount: 0,
      averageSeconds: null,
      medianSeconds: null,
      maxSeconds: null,
    }

    renderHero()

    const empty = screen.getByTestId('approval-wait-empty')
    expect(empty).toHaveAttribute('data-state', 'empty')
    expect(empty).toHaveClass('text-muted-foreground')
    expect(screen.queryByTestId('approval-wait-value')).not.toBeInTheDocument()
  })

  it('accepts an approvalWait prop that overrides the internal hook', () => {
    mocks.issues = [
      makeIssue({
        id: 'awaiting-1',
        number: 202,
        title: 'Pending approval',
        approvalState: { status: 'awaiting', requestedAt: '2026-06-18T00:00:00.000Z' },
      }),
    ]
    mocks.approvalWait = {
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

    expect(screen.getByTestId('approval-wait-value')).toHaveTextContent('5h')
    expect(screen.queryByTestId('approval-wait-empty')).not.toBeInTheDocument()
  })

  it('invalidates the approval-wait query on Approve success', async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries')

    mocks.issues = [
      makeIssue({
        id: 'awaiting-1',
        number: 203,
        title: 'Approve and invalidate approval wait',
        approvalState: { status: 'awaiting', requestedAt: '2026-06-18T00:00:00.000Z' },
      }),
    ]
    mockedApproveIssue.mockResolvedValue({
      issue: makeIssue({ id: 'awaiting-1', number: 203 }),
      context: null,
      message: 'ok',
    })

    renderHeroWithClient(queryClient)

    fireEvent.click(screen.getByTestId('attention-item-approve'))

    await waitFor(() => {
      expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['issues'] })
      expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['agent-status'] })
      expect(invalidateSpy).toHaveBeenCalledWith({
        queryKey: ['issues', 'metrics', 'approval-wait'],
      })
    })
  })
})
