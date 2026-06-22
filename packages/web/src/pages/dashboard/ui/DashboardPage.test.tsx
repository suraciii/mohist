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
  issues: undefined as any[] | undefined,
  epics: undefined as any[] | undefined,
  completionTrend: undefined as { bucket: string; window: { from: string; to: string }; buckets: { boundary: string; completed: number; failed: number }[] } | undefined,
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

vi.mock('../../../entities/issue/api/queries', () => ({
  useIssues: () => ({ data: mocks.issues }),
}))

vi.mock('../../../entities/epic/api/queries', () => ({
  useEpics: () => ({ data: mocks.epics }),
}))

vi.mock('../../../entities/issue/api/completion-trend', () => ({
  useCompletionTrend: () => ({ data: mocks.completionTrend }),
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
    mocks.issues = undefined
    mocks.epics = undefined
    mocks.completionTrend = undefined
  })

  afterEach(() => {
    cleanup()
  })

  it('keeps slot identities stable when projects exist', () => {
    mocks.projects = [
      { id: 'p1', name: 'demo', createdAt: '', updatedAt: '' },
    ]

    renderPage()

    const attention = screen.getByTestId('dashboard-zone-attention')
    const pulse = screen.getByTestId('dashboard-zone-pulse')
    const productivity = screen.getByTestId('dashboard-zone-productivity')
    const digest = screen.getByTestId('dashboard-zone-digest')

    expect(attention).toHaveAttribute('data-zone', 'attention')
    expect(pulse).toHaveAttribute('data-zone', 'pulse')
    expect(productivity).toHaveAttribute('data-zone', 'productivity')
    expect(digest).toHaveAttribute('data-zone', 'digest')
  })

  it('renders the productivity zone content in the productivity slot while other slots stay as empty placeholders', () => {
    mocks.projects = [
      { id: 'p1', name: 'demo', createdAt: '', updatedAt: '' },
    ]
    mocks.issues = [
      { id: 'i1', number: 1, title: 't', status: 'done', health: 'active', projectId: 'p1', labels: {}, createdAt: new Date().toISOString(), updatedAt: new Date().toISOString(), isDraft: false, canStart: true, blocker: null },
    ]
    mocks.epics = [
      { id: 'e1', number: 1, title: 'epic one', status: 'active', priority: 'p0', progress: { deliveredCount: 1, totalIssueCount: 2 }, createdAt: '', updatedAt: '' },
      { id: 'e2', number: 2, title: 'epic two', status: 'active', priority: 'p1', progress: { deliveredCount: 0, totalIssueCount: 3 }, createdAt: '', updatedAt: '' },
    ]
    mocks.completionTrend = {
      bucket: 'week',
      window: { from: '2026-01-01T00:00:00Z', to: '2026-06-22T00:00:00Z' },
      buckets: [
        { boundary: '2026-04-01T00:00:00Z', completed: 1, failed: 0 },
        { boundary: '2026-04-08T00:00:00Z', completed: 2, failed: 0 },
        { boundary: '2026-04-15T00:00:00Z', completed: 3, failed: 0 },
      ],
    }

    renderPage()

    const attention = screen.getByTestId('dashboard-zone-attention')
    const pulse = screen.getByTestId('dashboard-zone-pulse')
    const productivity = screen.getByTestId('dashboard-zone-productivity')
    const digest = screen.getByTestId('dashboard-zone-digest')

    expect(attention).toHaveAttribute('data-zone', 'attention')
    expect(pulse).toHaveAttribute('data-zone', 'pulse')
    expect(productivity).toHaveAttribute('data-zone', 'productivity')
    expect(digest).toHaveAttribute('data-zone', 'digest')

    expect(attention.className).toMatch(/border-dashed/)
    expect(pulse.className).toMatch(/border-dashed/)
    expect(digest.className).toMatch(/border-dashed/)

    expect(productivity.className).not.toMatch(/border-dashed/)
    expect(attention.querySelector('[data-testid="productivity-zone"]')).toBeNull()
    expect(pulse.querySelector('[data-testid="productivity-zone"]')).toBeNull()
    expect(digest.querySelector('[data-testid="productivity-zone"]')).toBeNull()

    const zone = productivity.querySelector('[data-testid="productivity-zone"]')
    expect(zone).not.toBeNull()
    expect(zone).toBe(screen.getByTestId('productivity-zone'))

    expect(zone).toContainElement(screen.getByTestId('productivity-snapshot-row'))
    expect(zone).toContainElement(screen.getByTestId('productivity-epic-list'))
    expect(zone).toContainElement(screen.getByTestId('productivity-trend'))
    expect(zone).toContainElement(screen.getByTestId('productivity-investment'))
  })

  it('renders four labeled empty states in the productivity zone when all data sources are empty', () => {
    mocks.projects = [
      { id: 'p1', name: 'demo', createdAt: '', updatedAt: '' },
    ]

    renderPage()

    const productivity = screen.getByTestId('dashboard-zone-productivity')
    expect(productivity).toHaveAttribute('data-zone', 'productivity')

    const zone = screen.getByTestId('productivity-zone')
    expect(productivity).toContainElement(zone)

    const snapshotEmpty = screen.getByTestId('productivity-snapshot-empty')
    expect(snapshotEmpty).toBeInTheDocument()
    expect(snapshotEmpty.textContent ?? '').toMatch(/no issues/i)
    expect(snapshotEmpty.parentElement).toHaveAttribute('data-state', 'empty')

    const epicEmpty = screen.getByTestId('productivity-epic-list-empty')
    expect(epicEmpty).toBeInTheDocument()
    expect(epicEmpty.textContent ?? '').toMatch(/active epics/i)
    expect(epicEmpty.parentElement).toHaveAttribute('data-state', 'empty')

    const trendEmpty = screen.getByTestId('productivity-trend-empty')
    expect(trendEmpty).toBeInTheDocument()
    expect(trendEmpty.textContent ?? '').toMatch(/no completion data/i)
    expect(trendEmpty.parentElement).toHaveAttribute('data-state', 'empty')

    fireEvent.click(screen.getByTestId('productivity-investment-toggle'))

    const investmentEmpty = screen.getByTestId('productivity-investment-empty')
    expect(investmentEmpty).toBeInTheDocument()
    expect(investmentEmpty).toHaveAttribute('data-state', 'empty')
    expect(investmentEmpty.textContent ?? '').toMatch(/data unavailable/i)
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
})
