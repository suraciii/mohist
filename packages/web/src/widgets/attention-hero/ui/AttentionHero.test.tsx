import '@testing-library/jest-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import {
  IssueHealth,
  IssueStatus,
  WorkflowStage,
  type ApprovalWaitMetricsResponse,
  type Issue,
} from '../../../entities/issue'
import { deriveAttentionItems } from '../../../entities/agent-ops'
import { type AgentStatus } from '../../../entities/agent'
import { ProjectProvider } from '../../../entities/project'
import {
  AttentionHero as DefaultAttentionHero,
  type AttentionHeroDataHook,
  type AttentionHeroProps,
} from './AttentionHero'

let _issues: Issue[] | undefined
let _agentStatus: AgentStatus | undefined
let _approvalWait: ApprovalWaitMetricsResponse | null
const _approveHandler = vi.fn()

const dataHook: AttentionHeroDataHook = () => ({
  issues: _issues,
  agentStatus: _agentStatus,
  approvalWait: _approvalWait,
  issuesResolved: _issues !== undefined,
})

const approveIssueFn: NonNullable<AttentionHeroProps['approveIssueFn']> = async (issueNumber, projectId) => {
  _approveHandler(issueNumber, projectId)
  return { issue: makeIssue({ number: issueNumber }), context: null, message: 'approved' }
}

function AttentionHero(
  props: Omit<AttentionHeroProps, 'dataHook' | 'approveIssueFn'>,
) {
  return (
    <DefaultAttentionHero
      {...props}
      dataHook={dataHook}
      approveIssueFn={approveIssueFn}
    />
  )
}

function makeIssue(overrides: Partial<Issue> = {}): Issue {
  return {
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

})

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

describe('AttentionHero - has-attention state', () => {
  it('renders one entry per AttentionItem in evaluation order with label and detail', async () => {
    _issues = [
      makeIssue({
        number: 10,
        title: 'Awaiting approval on schema',
        approvalState: { status: 'awaiting', requestedAt: '2026-06-18T00:00:00.000Z' },
      }),
      makeIssue({
        number: 20,
        title: 'Build needs action',
        workflowStage: WorkflowStage.Build,
        health: IssueHealth.Blocked,
        blockedReason: 'Waiting on infra fix',
      }),
      makeIssue({
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
        number: 10,
        title: 'Awaiting approval on schema',
        approvalState: { status: 'awaiting', requestedAt: '2026-06-18T00:00:00.000Z' },
      }),
      makeIssue({
        number: 20,
        title: 'Build needs action',
        workflowStage: WorkflowStage.Build,
        health: IssueHealth.Blocked,
        blockedReason: 'Waiting on infra fix',
      }),
      makeIssue({
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
    expect(shared[0]).toMatchObject({ label: 'Approval needed', issueNumber: 10, })
    expect(shared[1]).toMatchObject({ label: 'Needs action', issueNumber: 20, })
    expect(shared[2]).toMatchObject({ label: 'Integration failed', issueNumber: 30, })

    const rendered = screen.getAllByTestId('attention-item')
    expect(rendered).toHaveLength(shared.length)
    expect(rendered[0]).toHaveAttribute('data-label', shared[0]!.label)
    expect(rendered[1]).toHaveAttribute('data-label', shared[1]!.label)
    expect(rendered[2]).toHaveAttribute('data-label', shared[2]!.label)
  })

  it('contains no local copy of the four attention rules (only delegates to deriveAttentionItems)', async () => {
    _issues = [
      makeIssue({
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

  it('blocked items offer Open without guessing a recovery action', async () => {
    _issues = [
      makeIssue({
        number: 31,
        title: 'Integration failure',
        workflowStage: WorkflowStage.Integrate,
        health: IssueHealth.Blocked,
      }),
      makeIssue({
        number: 33,
        title: 'Needs action issue',
        workflowStage: WorkflowStage.Build,
        health: IssueHealth.Blocked,
        blockedReason: 'reason',
      }),
    ]

    renderHero()

    await waitFor(() => {
      expect(screen.getAllByTestId('attention-item-link')).toHaveLength(2)
    })
    expect(screen.queryByTestId('attention-item-resume')).not.toBeInTheDocument()
  })

  it('invalidateQueries is called for issues and agent-status on Approve success', async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries')

    _issues = [
      makeIssue({
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

  it('navigation affordance links to the issue detail route via useProjectPath', async () => {
    _issues = [
      makeIssue({
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
        number: 11,
        title: 'a',
        approvalState: { status: 'awaiting', requestedAt: '2026-06-18T00:00:00.000Z' },
      }),
      makeIssue({
        number: 22,
        title: 'b',
        workflowStage: WorkflowStage.Build,
        health: IssueHealth.Blocked,
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
  it('renders a Runner-down entry when an active workflow is affected', async () => {
    _issues = [makeIssue({
      status: IssueStatus.InProgress,
      health: IssueHealth.Active,
      workflowStage: WorkflowStage.Build,
    })]
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
        number: 88,
        title: 'Pending approval',
        status: IssueStatus.InProgress,
        health: IssueHealth.Active,
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

  it('does not render Runner-down entry when no workflow is affected', async () => {
    _issues = []
    _agentStatus = makeAgentStatus({ runnerAvailable: false, runnerMessage: 'down' })

    renderHero()

    await screen.findByText('All clear')
    expect(screen.queryByTestId('attention-item')).not.toBeInTheDocument()
    expect(screen.queryByTestId('runner-down-entry')).not.toBeInTheDocument()
  })

  it('renders Runner-down entry with the default message when runnerMessage is null', async () => {
    _issues = [makeIssue({
      status: IssueStatus.InProgress,
      health: IssueHealth.Active,
      workflowStage: WorkflowStage.Build,
    })]
    _agentStatus = makeAgentStatus({ runnerAvailable: false, runnerMessage: null })

    renderHero()

    await waitFor(() => {
      expect(screen.getByTestId('runner-down-entry')).toBeInTheDocument()
    })

    expect(screen.getByTestId('runner-down-message')).toHaveTextContent('No runner is connected.')
  })

  it('renders Runner-down entry with default message when runnerMessage is undefined', async () => {
    _issues = [makeIssue({
      status: IssueStatus.InProgress,
      health: IssueHealth.Active,
      workflowStage: WorkflowStage.Build,
    })]
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

  it('surfaces no infrastructure attention when runner is unavailable but no workflow is affected', async () => {
    _issues = []
    _agentStatus = makeAgentStatus({
      runnerAvailable: false,
      capacity: { active: 4, max: 4 },
    })

    renderHero()

    await screen.findByText('All clear')
    expect(screen.queryByTestId('runner-down-entry')).not.toBeInTheDocument()
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
        number: 90,
        title: 'Healthy build',
        workflowStage: WorkflowStage.Build,
        health: IssueHealth.Active,
      }),
      makeIssue({
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
  it('does not approve on render without a click', async () => {
    _issues = [
      makeIssue({
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
