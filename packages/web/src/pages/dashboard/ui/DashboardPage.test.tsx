// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import {
  IssueHealth,
  IssueStatus,
  WorkflowStage,
  type Issue,
} from '../../../entities/issue'
import type { AgentStatus } from '../../../entities/agent'

const mocks = vi.hoisted(() => ({
  projects: [] as any[],
  isLoading: false,
  agentStatus: {
    running: false,
    issueId: null,
    issueNumber: null,
    activeAgents: [],
    capacity: { active: 0, max: 8 },
  } as AgentStatus | undefined,
  agentStatusLoading: false,
  createProjectMutate: vi.fn(),
  useIssuesMock: vi.fn(),
  useArchivedIssuesMock: vi.fn(),
  useAgentActivityMock: vi.fn(),
  epics: undefined as any[] | undefined,
  completionTrend: undefined as { bucket: string; window: { from: string; to: string }; buckets: { boundary: string; completed: number; failed: number }[] } | undefined,
  completionThroughput: { bucket: 'day', window: { from: '2026-06-01T00:00:00', to: '2026-06-07T23:59:59' }, buckets: [] } as { bucket: string; window: { from: string; to: string }; buckets: { boundary: string; completed: number; failed: number }[] },
  approvalWait: undefined as { window: { from: string; to: string }; sampleCount: number; averageSeconds: number | null; medianSeconds: number | null; maxSeconds: number | null } | undefined,
  qualityMetrics: undefined as any,
  deliveryTime: undefined as { points: { issueNumber: number; completedAt: string; leadDays: number; cycleDays: number | null }[] } | undefined,
}))

vi.mock('../../../entities/project', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/project')>()
  return {
    ...actual,
    useProjects: () => ({ data: mocks.projects, isLoading: mocks.isLoading }),
    useCreateProject: () => ({
      mutate: mocks.createProjectMutate,
      isPending: false,
      isError: false,
      reset: vi.fn(),
    }),
  }
})

vi.mock('../../../entities/agent', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/agent')>()
  return {
    ...actual,
    useAgentStatus: () => ({ data: mocks.agentStatus, isLoading: mocks.agentStatusLoading }),
    useAgentActivity: () => mocks.useAgentActivityMock(),
    useCostRollup: () => ({ data: undefined }),
    useAgentUsage: () => ({ data: undefined, isLoading: false, isError: false }),
  }
})

vi.mock('../../../entities/issue/api/queries', () => ({
  useIssues: (...args: unknown[]) => mocks.useIssuesMock(...args),
  useArchivedIssues: (...args: unknown[]) => mocks.useArchivedIssuesMock(...args),
}))

vi.mock('../../../widgets/coder-session/model/activity-cards', () => ({
  useActivityCards: () => mocks.useAgentActivityMock(),
}))

vi.mock('../../../widgets/create-project-dialog/ui/CreateProjectDialog', () => ({
  CreateProjectDialog: ({ open, onClose }: { open: boolean; onClose: () => void }) =>
    open ? (
      <div data-testid="create-project-dialog">
        <button data-testid="create-project-dialog-close" onClick={onClose}>
          Close
        </button>
      </div>
    ) : null,
}))

vi.mock('../../../entities/epic/api/queries', () => ({
  useEpics: () => ({ data: mocks.epics }),
}))

vi.mock('../../../entities/issue/api/completion-trend', () => ({
  useCompletionTrend: () => ({ data: mocks.completionTrend }),
  useCompletionThroughput: () => ({ data: mocks.completionThroughput }),
}))

vi.mock('../../../entities/issue/api/approval-wait', () => ({
  useApprovalWait: () => ({ data: mocks.approvalWait }),
}))

vi.mock('../../../entities/issue/api/quality-metrics', () => ({
  useQualityMetrics: () => ({ data: mocks.qualityMetrics }),
}))

vi.mock('../../../entities/issue/api/delivery-time', () => ({
  useDeliveryTime: () => ({ data: mocks.deliveryTime, isLoading: false, isError: false }),
}))

import { DashboardPage } from './DashboardPage'

const NO_AGENT_ACTIVITY = {
  activeCards: [],
  activeCardByIssueNumber: new Map<number, unknown>(),
  recentCards: [],
  waitingCards: [],
  statusCounts: { active: 0, waiting: 0, completed: 0, failed: 0 },
  slotUsage: { active: 0, max: 0 },
  isLoading: false,
  isError: false,
}

function makeActiveCard(overrides: Record<string, unknown> = {}) {
  return {
    issueId: 'session-only-issue',
    issueNumber: '999',
    issueTitle: 'Session-only issue',
    issueStage: 'Build',
    sessionId: 'session-only',
    status: 'active',
    model: null,
    resolvedModel: null,
    taskDescription: 'Session-only work',
    title: 'Session-only work',
    createdAt: '2026-01-01T00:00:00.000Z',
    completedAt: null,
    lastActivityAt: '2026-01-01T00:00:00.000Z',
    activityPreviews: [],
    taskProgress: null,
    currentWorkTitle: null,
    failureReason: null,
    failureCategory: null,
    inputTokens: null,
    outputTokens: null,
    totalTokens: null,
    costAmount: null,
    costCurrency: null,
    contextWindowUsed: null,
    contextWindowSize: null,
    toolCallCount: null,
    toolErrorCount: null,
    ...overrides,
  }
}

function makeIssue(overrides: Partial<Issue> = {}): Issue {
  return {
    id: overrides.id ?? `issue-${Math.random().toString(36).slice(2, 8)}`,
    number: overrides.number ?? 1,
    title: overrides.title ?? 'Issue title',
    status: overrides.status ?? IssueStatus.Backlog,
    health: overrides.health ?? IssueHealth.Active,
    projectId: 'p1',
    labels: {},
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    isDraft: false,
    canStart: true,
    blocker: null,
    workflowStage: overrides.workflowStage ?? null,
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

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId="p1" initialProjects={mocks.projects}>
        <MemoryRouter initialEntries={['/']}>
          <DashboardPage />
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

function resetMocks() {
  mocks.projects = []
  mocks.isLoading = false
  mocks.agentStatus = makeAgentStatus({ capacity: { active: 0, max: 8 } })
  mocks.agentStatusLoading = false
  mocks.useIssuesMock.mockReset()
  mocks.useIssuesMock.mockReturnValue({ data: undefined, isLoading: false })
  mocks.useArchivedIssuesMock.mockReset()
  mocks.useArchivedIssuesMock.mockReturnValue({ data: undefined, isLoading: false })
  mocks.useAgentActivityMock.mockReset()
  mocks.useAgentActivityMock.mockReturnValue({ ...NO_AGENT_ACTIVITY, activeCardByIssueNumber: new Map() })
  mocks.epics = undefined
  mocks.completionTrend = undefined
  mocks.approvalWait = undefined
  mocks.qualityMetrics = undefined
  mocks.deliveryTime = undefined
}

describe('DashboardPage — attention-first zone hierarchy', () => {
  beforeEach(() => {
    resetMocks()
  })

  afterEach(() => {
    cleanup()
  })

  describe('project gating', () => {
    it('shows the project empty-state instead of zones when no projects exist', () => {
      mocks.projects = []

      renderPage()

      expect(screen.getByTestId('dashboard-empty-state')).toBeInTheDocument()
      expect(screen.getByText('No projects yet')).toBeInTheDocument()
      expect(screen.getByTestId('dashboard-create-project')).toBeInTheDocument()

      expect(screen.queryByTestId('dashboard-zone-attention')).not.toBeInTheDocument()
      expect(screen.queryByTestId('dashboard-zone-pulse')).not.toBeInTheDocument()
      expect(screen.queryByTestId('dashboard-zone-capacity')).not.toBeInTheDocument()
      expect(screen.queryByTestId('dashboard-zone-digest')).not.toBeInTheDocument()
      expect(screen.queryByTestId('dashboard-ready-state')).not.toBeInTheDocument()
    })

    it('opens the CreateProjectDialog when the empty-state action is activated', async () => {
      mocks.projects = []

      renderPage()

      expect(screen.queryByTestId('create-project-dialog')).not.toBeInTheDocument()

      fireEvent.click(screen.getByTestId('dashboard-create-project'))

      await waitFor(() => {
        expect(screen.getByTestId('create-project-dialog')).toBeInTheDocument()
      })
    })
  })

  describe('headline subordination', () => {
    it('always renders the factory status headline above the attention zone when attention exists', () => {
      mocks.projects = [
        { id: 'p1', name: 'demo', createdAt: '', updatedAt: '' },
      ]
      mocks.useIssuesMock.mockReturnValue({
        data: [
          makeIssue({
            id: 'await-1',
            number: 11,
            title: 'Awaiting approval',
            approvalState: { status: 'awaiting', requestedAt: '2026-01-01T00:00:00Z' },
          }),
        ],
        isLoading: false,
      })

      renderPage()

      const headline = screen.getByTestId('factory-status-headline')
      const attention = screen.getByTestId('dashboard-zone-attention')

      expect(headline).toBeInTheDocument()
      expect(attention).toBeInTheDocument()

      expect(headline.compareDocumentPosition(attention) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
    })

    it('renders the headline as a compact strip (no data-zone attribute, single section)', () => {
      mocks.projects = [
        { id: 'p1', name: 'demo', createdAt: '', updatedAt: '' },
      ]

      renderPage()

      const headline = screen.getByTestId('factory-status-headline')
      expect(headline).toBeInTheDocument()
      expect(headline.querySelector('[data-testid="factory-status-runner"]')).toBeInTheDocument()
    })
  })

  describe('zone priority order', () => {
    it('renders the four levels in priority order (attention → pulse → capacity → digest) when all are populated', () => {
      mocks.projects = [
        { id: 'p1', name: 'demo', createdAt: '', updatedAt: '' },
      ]
      mocks.agentStatus = makeAgentStatus({ capacity: { active: 2, max: 8 } })
      mocks.useIssuesMock.mockReturnValue({
        data: [
          makeIssue({
            id: 'blocked-issue',
            number: 1,
            title: 'Blocked issue',
            status: IssueStatus.InProgress,
            health: IssueHealth.Blocked,
            workflowStage: WorkflowStage.Build,
          }),
          makeIssue({
            id: 'running-issue',
            number: 2,
            title: 'Running issue',
            status: IssueStatus.InProgress,
            health: IssueHealth.Active,
            workflowStage: WorkflowStage.Build,
          }),
        ],
        isLoading: false,
      })
      mocks.useArchivedIssuesMock.mockReturnValue({
        data: [
          makeIssue({
            id: 'archived-1',
            number: 99,
            title: 'Old archived issue',
            status: IssueStatus.Done,
            archivedAt: '2026-06-30T00:00:00Z',
          }),
        ],
        isLoading: false,
      })

      renderPage()

      const attention = screen.getByTestId('dashboard-zone-attention')
      const pulse = screen.getByTestId('dashboard-zone-pulse')
      const capacity = screen.getByTestId('dashboard-zone-capacity')
      const digest = screen.getByTestId('dashboard-zone-digest')

      expect(
        attention.compareDocumentPosition(pulse) & Node.DOCUMENT_POSITION_FOLLOWING,
      ).toBeTruthy()
      expect(
        pulse.compareDocumentPosition(capacity) & Node.DOCUMENT_POSITION_FOLLOWING,
      ).toBeTruthy()
      expect(
        capacity.compareDocumentPosition(digest) & Node.DOCUMENT_POSITION_FOLLOWING,
      ).toBeTruthy()
    })
  })

  describe('empty zone collapse', () => {
    it('omits the digest zone from the DOM when the digest has no items', () => {
      mocks.projects = [
        { id: 'p1', name: 'demo', createdAt: '', updatedAt: '' },
      ]
      mocks.useIssuesMock.mockReturnValue({
        data: [
          makeIssue({
            id: 'await-1',
            number: 11,
            title: 'Awaiting approval',
            approvalState: { status: 'awaiting', requestedAt: '2026-01-01T00:00:00Z' },
          }),
        ],
        isLoading: false,
      })
      mocks.useArchivedIssuesMock.mockReturnValue({ data: [], isLoading: false })

      renderPage()

      expect(screen.getByTestId('dashboard-zone-attention')).toBeInTheDocument()
      expect(screen.queryByTestId('dashboard-zone-digest')).not.toBeInTheDocument()
      expect(screen.queryByTestId('dashboard-digest-empty')).not.toBeInTheDocument()
    })

    it('omits the active-production zone when no running issues and no active sessions', () => {
      mocks.projects = [
        { id: 'p1', name: 'demo', createdAt: '', updatedAt: '' },
      ]
      mocks.useIssuesMock.mockReturnValue({
        data: [
          makeIssue({
            id: 'await-1',
            number: 12,
            title: 'Awaiting approval',
            approvalState: { status: 'awaiting', requestedAt: '2026-01-01T00:00:00Z' },
          }),
        ],
        isLoading: false,
      })

      renderPage()

      expect(screen.getByTestId('dashboard-zone-attention')).toBeInTheDocument()
      expect(screen.queryByTestId('dashboard-zone-pulse')).not.toBeInTheDocument()
    })

    it('omits the capacity zone when capacity data is absent', () => {
      mocks.projects = [
        { id: 'p1', name: 'demo', createdAt: '', updatedAt: '' },
      ]
      mocks.agentStatus = makeAgentStatus({ capacity: { active: 0, max: 0 } })

      renderPage()

      expect(screen.queryByTestId('dashboard-zone-capacity')).not.toBeInTheDocument()
    })

    it('omits the capacity zone when agentStatus is undefined', () => {
      mocks.projects = [
        { id: 'p1', name: 'demo', createdAt: '', updatedAt: '' },
      ]
      mocks.agentStatus = undefined as unknown as AgentStatus

      renderPage()

      expect(screen.queryByTestId('dashboard-zone-capacity')).not.toBeInTheDocument()
    })

    it('does not reserve a fixed-height box for absent zones', () => {
      mocks.projects = [
        { id: 'p1', name: 'demo', createdAt: '', updatedAt: '' },
      ]
      mocks.useIssuesMock.mockReturnValue({
        data: [
          makeIssue({
            id: 'await-1',
            number: 13,
            title: 'Awaiting approval',
            approvalState: { status: 'awaiting', requestedAt: '2026-01-01T00:00:00Z' },
          }),
        ],
        isLoading: false,
      })

      const { container } = renderPage()

      expect(container.querySelector('.min-h-\\[160px\\]')).toBeNull()
    })
  })

  describe('ready state when idle', () => {
    it('does not render the ready state while issue data is still loading', () => {
      mocks.projects = [
        { id: 'p1', name: 'demo', createdAt: '', updatedAt: '' },
      ]
      mocks.useIssuesMock.mockReturnValue({ data: undefined, isLoading: true })

      renderPage()

      expect(screen.queryByTestId('dashboard-ready-state')).not.toBeInTheDocument()
      expect(screen.queryByText(/Nothing needs your attention right now/i)).not.toBeInTheDocument()
    })

    it('does not render the ready state while activity data is still loading', () => {
      mocks.projects = [
        { id: 'p1', name: 'demo', createdAt: '', updatedAt: '' },
      ]
      mocks.useIssuesMock.mockReturnValue({ data: [], isLoading: false })
      mocks.useAgentActivityMock.mockReturnValue({
        ...NO_AGENT_ACTIVITY,
        activeCardByIssueNumber: new Map(),
        isLoading: true,
      })

      renderPage()

      expect(screen.queryByTestId('dashboard-ready-state')).not.toBeInTheDocument()
      expect(screen.queryByText(/Nothing needs your attention right now/i)).not.toBeInTheDocument()
    })

    it('does not render the ready state while runner status is still loading', () => {
      mocks.projects = [
        { id: 'p1', name: 'demo', createdAt: '', updatedAt: '' },
      ]
      mocks.agentStatus = undefined
      mocks.agentStatusLoading = true
      mocks.useIssuesMock.mockReturnValue({ data: [], isLoading: false })
      mocks.useAgentActivityMock.mockReturnValue({
        ...NO_AGENT_ACTIVITY,
        activeCardByIssueNumber: new Map(),
      })

      renderPage()

      expect(screen.queryByTestId('dashboard-ready-state')).not.toBeInTheDocument()
      expect(screen.queryByText(/Nothing needs your attention right now/i)).not.toBeInTheDocument()
    })

    it('does not render the ready state while activity data is unresolved and runner status reports live work', () => {
      mocks.projects = [
        { id: 'p1', name: 'demo', createdAt: '', updatedAt: '' },
      ]
      mocks.agentStatus = makeAgentStatus({ running: true })
      mocks.useIssuesMock.mockReturnValue({ data: [], isLoading: false })
      mocks.useAgentActivityMock.mockReturnValue({
        ...NO_AGENT_ACTIVITY,
        activeCardByIssueNumber: new Map(),
        isLoading: true,
      })

      renderPage()

      expect(screen.getByTestId('dashboard-page')).toHaveAttribute('data-state', 'active-only')
      expect(screen.getByTestId('dashboard-zone-pulse')).toBeInTheDocument()
      expect(screen.queryByTestId('dashboard-ready-state')).not.toBeInTheDocument()
    })

    it('renders the concise ready state when there are no attention items and no active work', () => {
      mocks.projects = [
        { id: 'p1', name: 'demo', createdAt: '', updatedAt: '' },
      ]
      mocks.useIssuesMock.mockReturnValue({ data: [], isLoading: false })
      mocks.useArchivedIssuesMock.mockReturnValue({ data: [], isLoading: false })

      renderPage()

      expect(screen.getByTestId('dashboard-ready-state')).toBeInTheDocument()
      expect(screen.getByText(/Nothing needs your attention right now/i)).toBeInTheDocument()

      expect(screen.queryByTestId('dashboard-zone-attention')).not.toBeInTheDocument()
      expect(screen.queryByTestId('dashboard-zone-pulse')).not.toBeInTheDocument()
    })

    it('does not render the ready state when attention items exist', () => {
      mocks.projects = [
        { id: 'p1', name: 'demo', createdAt: '', updatedAt: '' },
      ]
      mocks.useIssuesMock.mockReturnValue({
        data: [
          makeIssue({
            id: 'await-1',
            number: 14,
            title: 'Awaiting approval',
            approvalState: { status: 'awaiting', requestedAt: '2026-01-01T00:00:00Z' },
          }),
        ],
        isLoading: false,
      })

      renderPage()

      expect(screen.queryByTestId('dashboard-ready-state')).not.toBeInTheDocument()
      expect(screen.getByTestId('dashboard-zone-attention')).toBeInTheDocument()
    })

    it('does not render the ready state when running issues exist (active-only state)', () => {
      mocks.projects = [
        { id: 'p1', name: 'demo', createdAt: '', updatedAt: '' },
      ]
      mocks.useIssuesMock.mockReturnValue({
        data: [
          makeIssue({
            id: 'running-1',
            number: 20,
            title: 'Running',
            status: IssueStatus.InProgress,
            health: IssueHealth.Active,
            workflowStage: WorkflowStage.Build,
          }),
        ],
        isLoading: false,
      })

      renderPage()

      expect(screen.queryByTestId('dashboard-ready-state')).not.toBeInTheDocument()
      expect(screen.getByTestId('dashboard-zone-pulse')).toBeInTheDocument()
    })

    it('does not render the ready state when runner status lists active agents', () => {
      mocks.projects = [
        { id: 'p1', name: 'demo', createdAt: '', updatedAt: '' },
      ]
      mocks.agentStatus = makeAgentStatus({
        activeAgents: [
          {
            issueId: 'issue-agent-live',
            issueNumber: 42,
            projectId: 'p1',
          },
        ],
      })
      mocks.useIssuesMock.mockReturnValue({ data: [], isLoading: false })

      renderPage()

      expect(screen.getByTestId('dashboard-page')).toHaveAttribute('data-state', 'active-only')
      expect(screen.getByTestId('dashboard-zone-pulse')).toBeInTheDocument()
      expect(screen.getByTestId('pulse-agent-status-card')).toHaveAttribute('data-issue-number', '42')
      expect(screen.queryByTestId('pulse-empty-state')).not.toBeInTheDocument()
      expect(screen.queryByTestId('dashboard-ready-state')).not.toBeInTheDocument()
    })

    it('shows the ready state with the digest as a subordinate strip when digest has items', () => {
      mocks.projects = [
        { id: 'p1', name: 'demo', createdAt: '', updatedAt: '' },
      ]
      mocks.useIssuesMock.mockReturnValue({ data: [], isLoading: false })
      mocks.useArchivedIssuesMock.mockReturnValue({
        data: [
          makeIssue({
            id: 'archived-1',
            number: 88,
            title: 'Old done',
            status: IssueStatus.Done,
            archivedAt: '2026-06-30T00:00:00Z',
          }),
        ],
        isLoading: false,
      })

      renderPage()

      const ready = screen.getByTestId('dashboard-ready-state')
      const digest = screen.getByTestId('dashboard-zone-digest')

      expect(ready).toBeInTheDocument()
      expect(digest).toBeInTheDocument()
      expect(ready.compareDocumentPosition(digest) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
    })
  })

  describe('capacity level rendering and collapse', () => {
    it('renders the dashboard-zone-capacity when capacity data is present', () => {
      mocks.projects = [
        { id: 'p1', name: 'demo', createdAt: '', updatedAt: '' },
      ]
      mocks.agentStatus = makeAgentStatus({ capacity: { active: 4, max: 8 } })

      renderPage()

      const capacity = screen.getByTestId('dashboard-zone-capacity')
      expect(capacity).toBeInTheDocument()
      expect(capacity).toHaveAttribute('data-zone', 'capacity')
      expect(capacity).toHaveAttribute('data-active', '4')
      expect(capacity).toHaveAttribute('data-max', '8')
      expect(screen.getByTestId('dashboard-zone-capacity-label')).toHaveTextContent('Runner capacity')
      expect(screen.getByTestId('dashboard-zone-capacity-count')).toHaveTextContent('4/8')
    })

    it('collapses the capacity level when max is zero', () => {
      mocks.projects = [
        { id: 'p1', name: 'demo', createdAt: '', updatedAt: '' },
      ]
      mocks.agentStatus = makeAgentStatus({ capacity: { active: 0, max: 0 } })

      renderPage()

      expect(screen.queryByTestId('dashboard-zone-capacity')).not.toBeInTheDocument()
    })

    it('collapses the capacity level when capacity field has max=0', () => {
      mocks.projects = [
        { id: 'p1', name: 'demo', createdAt: '', updatedAt: '' },
      ]
      mocks.agentStatus = makeAgentStatus({ capacity: { active: 0, max: 0 } })

      renderPage()

      expect(screen.queryByTestId('dashboard-zone-capacity')).not.toBeInTheDocument()
    })

    it('renders the capacity level between active-production and digest', () => {
      mocks.projects = [
        { id: 'p1', name: 'demo', createdAt: '', updatedAt: '' },
      ]
      mocks.agentStatus = makeAgentStatus({ capacity: { active: 2, max: 8 } })
      mocks.useIssuesMock.mockReturnValue({
        data: [
          makeIssue({
            id: 'running-1',
            number: 30,
            title: 'Running',
            status: IssueStatus.InProgress,
            health: IssueHealth.Active,
            workflowStage: WorkflowStage.Build,
          }),
        ],
        isLoading: false,
      })
      mocks.useArchivedIssuesMock.mockReturnValue({
        data: [
          makeIssue({
            id: 'archived-1',
            number: 88,
            title: 'Old done',
            status: IssueStatus.Done,
            archivedAt: '2026-06-30T00:00:00Z',
          }),
        ],
        isLoading: false,
      })

      renderPage()

      const pulse = screen.getByTestId('dashboard-zone-pulse')
      const capacity = screen.getByTestId('dashboard-zone-capacity')
      const digest = screen.getByTestId('dashboard-zone-digest')

      expect(
        pulse.compareDocumentPosition(capacity) & Node.DOCUMENT_POSITION_FOLLOWING,
      ).toBeTruthy()
      expect(
        capacity.compareDocumentPosition(digest) & Node.DOCUMENT_POSITION_FOLLOWING,
      ).toBeTruthy()
    })

    it('keeps the capacity level independent of active-production (renders without active work)', () => {
      mocks.projects = [
        { id: 'p1', name: 'demo', createdAt: '', updatedAt: '' },
      ]
      mocks.agentStatus = makeAgentStatus({ capacity: { active: 3, max: 8 } })
      mocks.useIssuesMock.mockReturnValue({ data: [], isLoading: false })
      mocks.useArchivedIssuesMock.mockReturnValue({ data: [], isLoading: false })

      renderPage()

      expect(screen.queryByTestId('dashboard-zone-pulse')).not.toBeInTheDocument()
      expect(screen.getByTestId('dashboard-zone-capacity')).toBeInTheDocument()
    })
  })

  describe('centralized predicates', () => {
    it('renders the attention zone only when deriveAttentionItems produces items', () => {
      mocks.projects = [
        { id: 'p1', name: 'demo', createdAt: '', updatedAt: '' },
      ]
      mocks.agentStatus = makeAgentStatus({ runnerAvailable: false })

      renderPage()

      expect(screen.getByTestId('dashboard-zone-attention')).toBeInTheDocument()
    })

    it('renders the active-production zone when an in-progress issue is present', () => {
      mocks.projects = [
        { id: 'p1', name: 'demo', createdAt: '', updatedAt: '' },
      ]
      mocks.useIssuesMock.mockReturnValue({
        data: [
          makeIssue({
            id: 'running-1',
            number: 50,
            title: 'Running',
            status: IssueStatus.InProgress,
            health: IssueHealth.Active,
            workflowStage: WorkflowStage.Build,
          }),
        ],
        isLoading: false,
      })

      renderPage()

      expect(screen.getByTestId('dashboard-zone-pulse')).toBeInTheDocument()
    })

    it('renders active-production for an active session without a running issue and does not show the ready state', () => {
      mocks.projects = [
        { id: 'p1', name: 'demo', createdAt: '', updatedAt: '' },
      ]
      mocks.useIssuesMock.mockReturnValue({ data: [], isLoading: false })
      const activeCard = makeActiveCard({
        issueNumber: '999',
        issueTitle: 'Active session without issue row',
        title: 'Active session without issue row',
        sessionId: 'session-999',
      })
      mocks.useAgentActivityMock.mockReturnValue({
        ...NO_AGENT_ACTIVITY,
        activeCards: [activeCard],
        activeCardByIssueNumber: new Map([[999, activeCard]]),
      })

      renderPage()

      expect(screen.getByTestId('dashboard-zone-pulse')).toBeInTheDocument()
      expect(screen.queryByTestId('dashboard-ready-state')).not.toBeInTheDocument()
      expect(screen.queryByTestId('pulse-empty-state')).not.toBeInTheDocument()
      expect(screen.getByTestId('pulse-compact-card')).toHaveAttribute('data-issue-number', '999')
      expect(screen.getByTestId('pulse-compact-title')).toHaveTextContent('Active session without issue row')
    })
  })

  describe('test-id preservation', () => {
    it('preserves the factory-status-headline, dashboard-zone-attention/-pulse/-digest and dashboard-zone-capacity test-ids', () => {
      mocks.projects = [
        { id: 'p1', name: 'demo', createdAt: '', updatedAt: '' },
      ]
      mocks.agentStatus = makeAgentStatus({ capacity: { active: 1, max: 4 } })
      mocks.useIssuesMock.mockReturnValue({
        data: [
          makeIssue({
            id: 'await-1',
            number: 60,
            title: 'Awaiting approval',
            approvalState: { status: 'awaiting', requestedAt: '2026-01-01T00:00:00Z' },
          }),
          makeIssue({
            id: 'running-1',
            number: 61,
            title: 'Running',
            status: IssueStatus.InProgress,
            health: IssueHealth.Active,
            workflowStage: WorkflowStage.Build,
          }),
        ],
        isLoading: false,
      })
      mocks.useArchivedIssuesMock.mockReturnValue({
        data: [
          makeIssue({
            id: 'archived-1',
            number: 99,
            title: 'Old done',
            status: IssueStatus.Done,
            archivedAt: '2026-06-30T00:00:00Z',
          }),
        ],
        isLoading: false,
      })

      renderPage()

      expect(screen.getByTestId('factory-status-headline')).toBeInTheDocument()
      expect(screen.getByTestId('dashboard-zone-attention')).toBeInTheDocument()
      expect(screen.getByTestId('dashboard-zone-pulse')).toBeInTheDocument()
      expect(screen.getByTestId('dashboard-zone-capacity')).toBeInTheDocument()
      expect(screen.getByTestId('dashboard-zone-digest')).toBeInTheDocument()
    })
  })
})
