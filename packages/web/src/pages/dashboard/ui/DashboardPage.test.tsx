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
  })

  afterEach(() => {
    cleanup()
  })

  it('renders the four zone placeholders with stable identities when projects exist', () => {
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
