// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'

const mocks = vi.hoisted(() => ({
  projects: [] as any[],
  isLoading: false,
  agentStatus: { running: false, activeAgents: [], capacity: { active: 0, max: 8 } } as any,
  createProjectMutate: vi.fn(),
  useIssuesMock: vi.fn(),
  useArchivedIssuesMock: vi.fn(),
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

vi.mock('../../../entities/agent', () => ({
  useAgentStatus: () => ({ data: mocks.agentStatus }),
  useCostRollup: () => ({ data: undefined }),
  useAgentUsage: () => ({ data: undefined, isLoading: false, isError: false }),
}))

vi.mock('../../../entities/issue/api/queries', () => ({
  useIssues: (...args: unknown[]) => mocks.useIssuesMock(...args),
  useArchivedIssues: (...args: unknown[]) => mocks.useArchivedIssuesMock(...args),
}))

vi.mock('@/widgets/coder-session/model/activity-cards', () => ({
  useActivityCards: () => ({
    activeCards: [],
    recentCards: [],
    waitingCards: [],
    statusCounts: { active: 0, waiting: 0, completed: 0, failed: 0 },
    slotUsage: { active: 0, max: 0 },
  }),
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

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider>
        <MemoryRouter initialEntries={['/']}>
          <DashboardPage />
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

describe('DashboardPage', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.projects = []
    mocks.isLoading = false
    mocks.useIssuesMock.mockReturnValue({ data: undefined, isLoading: false })
    mocks.useArchivedIssuesMock.mockReturnValue({ data: undefined, isLoading: false })
    mocks.epics = undefined
    mocks.completionTrend = undefined
    mocks.approvalWait = undefined
    mocks.qualityMetrics = undefined
    mocks.deliveryTime = undefined
  })

  afterEach(() => {
    cleanup()
  })

  it('keeps slot identities stable when projects exist and omits the productivity slot', () => {
    mocks.projects = [
      { id: 'p1', name: 'demo', createdAt: '', updatedAt: '' },
    ]

    renderPage()

    const attention = screen.getByTestId('dashboard-zone-attention')
    const pulse = screen.getByTestId('dashboard-zone-pulse')
    const digest = screen.getByTestId('dashboard-zone-digest')

    expect(attention).toHaveAttribute('data-zone', 'attention')
    expect(pulse).toHaveAttribute('data-zone', 'pulse')
    expect(digest).toHaveAttribute('data-zone', 'digest')
    expect(screen.queryByTestId('dashboard-zone-productivity')).not.toBeInTheDocument()
    expect(screen.queryByTestId('productivity-zone')).not.toBeInTheDocument()
  })

  it('mounts the dashboard-digest widget inside the digest zone slot', () => {
    mocks.projects = [
      { id: 'p1', name: 'demo', createdAt: '', updatedAt: '' },
    ]

    const { container } = renderPage()

    const digestSlot = screen.getByTestId('dashboard-zone-digest')
    // The digest widget should be present inside the digest slot wrapper.
    expect(digestSlot.contains(screen.getByTestId('dashboard-digest-empty'))).toBe(true)
    // Sanity: the widget is NOT mounted into the other slots.
    expect(container.querySelector('[data-testid="dashboard-zone-attention"] [data-testid="dashboard-digest-empty"]')).toBeNull()
    expect(container.querySelector('[data-testid="dashboard-zone-pulse"] [data-testid="dashboard-digest-empty"]')).toBeNull()
  })

  it('mounts AttentionHero in the hero slot and zone content in every surviving dashboard zone slot', () => {
    mocks.projects = [
      { id: 'p1', name: 'demo', createdAt: '', updatedAt: '' },
    ]

    const { container } = renderPage()

    const attentionSlots = screen.getAllByTestId('dashboard-zone-attention')
    expect(attentionSlots).toHaveLength(1)
    const attention = attentionSlots[0]
    const pulse = screen.getByTestId('dashboard-zone-pulse')
    const digest = screen.getByTestId('dashboard-zone-digest')

    expect(attention.className).not.toMatch(/border-dashed/)
    expect(pulse.className).toMatch(/border-dashed/)

    expect(attention.childElementCount).toBeGreaterThan(0)
    expect(pulse.childElementCount).toBeGreaterThan(0)

    expect(pulse.contains(screen.getByTestId('pulse-zone'))).toBe(true)
    expect(pulse.contains(screen.getByTestId('pulse-empty-state'))).toBe(true)

    expect(screen.getByTestId('dashboard-hero').contains(attention)).toBe(true)
    expect(screen.getByTestId('dashboard-zones').contains(attention)).toBe(false)

    expect(attention.querySelector('[data-testid="dashboard-digest-empty"], [data-testid="dashboard-digest-loading"], [data-testid="dashboard-digest-content"]')).toBeNull()
    expect(pulse.querySelector('[data-testid="dashboard-digest-empty"], [data-testid="dashboard-digest-loading"], [data-testid="dashboard-digest-content"]')).toBeNull()

    expect(digest.querySelector('[data-testid="pulse-zone"], [data-testid="pulse-empty-state"]')).toBeNull()
    expect(container).toBeTruthy()
  })

  it('renders the factory status headline at the top of the page', () => {
    mocks.projects = [
      { id: 'p1', name: 'demo', createdAt: '', updatedAt: '' },
    ]

    renderPage()

    expect(screen.getByTestId('factory-status-headline')).toBeInTheDocument()
    expect(screen.getByTestId('factory-status-runner')).toBeInTheDocument()
  })

  it('renders the three-stack layout in top-to-bottom order', () => {
    mocks.projects = [
      { id: 'p1', name: 'demo', createdAt: '', updatedAt: '' },
    ]

    renderPage()

    const page = screen.getByTestId('dashboard-page')
    const headline = screen.getByTestId('dashboard-headline')
    const hero = screen.getByTestId('dashboard-hero')
    const zones = screen.getByTestId('dashboard-zones')

    expect(page.contains(headline)).toBe(true)
    expect(page.contains(hero)).toBe(true)
    expect(page.contains(zones)).toBe(true)

    expect(headline.compareDocumentPosition(hero) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
    expect(hero.compareDocumentPosition(zones) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
  })

  it('renders digest content inside the digest slot when the widget has resolved data', () => {
    mocks.projects = [
      { id: 'p1', name: 'demo', createdAt: '', updatedAt: '' },
    ]
    mocks.useIssuesMock.mockReturnValue({
      data: [
        {
          id: 'i-1',
          number: 7,
          title: 'Done thing',
          status: 'done',
          health: 'active',
          projectId: 'p1',
          labels: {},
          createdAt: '2026-01-01T00:00:00Z',
          updatedAt: new Date(Date.now() - 5 * 60 * 1000).toISOString(),
          archivedAt: undefined,
          isDraft: false,
          canStart: true,
          blocker: null,
        },
      ],
      isLoading: false,
    })
    mocks.useArchivedIssuesMock.mockReturnValue({ data: [], isLoading: false })

    renderPage()

    const digestSlot = screen.getByTestId('dashboard-zone-digest')
    expect(digestSlot.contains(screen.getByTestId('dashboard-digest-content'))).toBe(true)
    expect(digestSlot.contains(screen.getByText('Done thing'))).toBe(true)
  })

  it('does not render the Kanban board on the dashboard', () => {
    mocks.projects = [
      { id: 'p1', name: 'demo', createdAt: '', updatedAt: '' },
    ]

    renderPage()

    expect(screen.queryByTestId('needs-attention-summary')).not.toBeInTheDocument()
    expect(screen.queryByTestId('search-input')).not.toBeInTheDocument()
    expect(screen.queryByTestId('priority-chip-p0')).not.toBeInTheDocument()
  })

  it('shows the project empty-state instead of zones when no projects exist', () => {
    mocks.projects = []

    renderPage()

    expect(screen.getByTestId('dashboard-empty-state')).toBeInTheDocument()
    expect(screen.getByText('No projects yet')).toBeInTheDocument()
    expect(screen.getByTestId('dashboard-create-project')).toBeInTheDocument()

    expect(screen.queryByTestId('dashboard-zone-attention')).not.toBeInTheDocument()
    expect(screen.queryByTestId('dashboard-zone-pulse')).not.toBeInTheDocument()
    expect(screen.queryByTestId('dashboard-zone-productivity')).not.toBeInTheDocument()
    expect(screen.queryByTestId('dashboard-zone-digest')).not.toBeInTheDocument()
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

  it('does not render the Ask Agent entry in the dashboard hero', () => {
    mocks.projects = [
      { id: 'p1', name: 'demo', createdAt: '', updatedAt: '' },
    ]

    renderPage()

    const hero = screen.getByTestId('dashboard-hero')
    expect(hero.querySelector('[data-testid="ask-agent-project"]')).toBeNull()
    expect(screen.queryByTestId('ask-agent-project')).not.toBeInTheDocument()
  })
})
