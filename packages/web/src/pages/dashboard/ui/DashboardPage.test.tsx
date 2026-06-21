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
  agentActivity: null as any,
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
  useAgentActivity: () => ({ data: mocks.agentActivity }),
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
    mocks.agentActivity = null
  })

  afterEach(() => {
    cleanup()
  })

  it('renders the four zone mount-point slots with stable identities when projects exist', () => {
    mocks.projects = [
      { id: 'p1', name: 'demo', createdAt: '', updatedAt: '' },
    ]

    renderPage()

    const attention = screen.getByTestId('dashboard-zone-attention')
    const pulse = screen.getByTestId('dashboard-zone-pulse')
    const productivity = screen.getByTestId('dashboard-zone-productivity')
    const digest = screen.getByTestId('dashboard-zone-digest')

    expect(attention).toHaveAttribute('data-zone', 'attention')
    expect(attention).toHaveAttribute('aria-label', 'Attention')
    expect(pulse).toHaveAttribute('data-zone', 'pulse')
    expect(pulse).toHaveAttribute('aria-label', 'Pulse')
    expect(productivity).toHaveAttribute('data-zone', 'productivity')
    expect(productivity).toHaveAttribute('aria-label', 'Productivity')
    expect(digest).toHaveAttribute('data-zone', 'digest')
    expect(digest).toHaveAttribute('aria-label', 'Digest')
  })

  it('mounts dashboard-pulse content inside the pulse slot', () => {
    mocks.projects = [
      { id: 'p1', name: 'demo', createdAt: '', updatedAt: '' },
    ]

    renderPage()

    const pulse = screen.getByTestId('dashboard-zone-pulse')
    expect(pulse).toContainElement(screen.getByTestId('pulse-zone'))
  })

  it('renders the attention, productivity, and digest slots as empty placeholders', () => {
    mocks.projects = [
      { id: 'p1', name: 'demo', createdAt: '', updatedAt: '' },
    ]

    renderPage()

    const attention = screen.getByTestId('dashboard-zone-attention')
    const productivity = screen.getByTestId('dashboard-zone-productivity')
    const digest = screen.getByTestId('dashboard-zone-digest')

    expect(attention).toBeEmptyDOMElement()
    expect(productivity).toBeEmptyDOMElement()
    expect(digest).toBeEmptyDOMElement()

    expect(attention.querySelector('[data-testid="pulse-zone"]')).toBeNull()
    expect(productivity.querySelector('[data-testid="pulse-zone"]')).toBeNull()
    expect(digest.querySelector('[data-testid="pulse-zone"]')).toBeNull()
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
