// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'

const mocks = vi.hoisted(() => ({
  projects: [] as any[],
  isLoading: false,
  agentStatus: {
    running: false,
    issueId: null,
    issueNumber: null,
    activeAgents: [],
    capacity: { active: 0, max: 8 },
    runnerAvailable: true,
    runnerMessage: null,
  } as any,
  issues: [] as any[],
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

vi.mock('../../../entities/issue', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/issue')>()
  return {
    ...actual,
    useIssues: () => ({ data: mocks.issues, isLoading: false }),
  }
})

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

const demoProject = {
  id: 'p1',
  name: 'demo',
  createdAt: '',
  updatedAt: '',
  repositories: [],
}

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId="p1" initialProjects={[demoProject]}>
        <MemoryRouter initialEntries={['/demo']}>
          <DashboardPage />
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

describe('DashboardPage', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.projects = [demoProject]
    mocks.isLoading = false
    mocks.issues = []
    mocks.agentStatus = {
      running: false,
      issueId: null,
      issueNumber: null,
      activeAgents: [],
      capacity: { active: 0, max: 8 },
      runnerAvailable: true,
      runnerMessage: null,
    }
  })

  afterEach(() => {
    cleanup()
  })

  it('renders the Attention Hero in the attention slot and placeholders in the other three slots', () => {
    renderPage()

    const attention = screen.getByTestId('dashboard-zone-attention')
    expect(attention).toHaveAttribute('data-zone', 'attention')

    const pulse = screen.getByTestId('dashboard-zone-pulse')
    const productivity = screen.getByTestId('dashboard-zone-productivity')
    const digest = screen.getByTestId('dashboard-zone-digest')

    expect(pulse).toHaveAttribute('data-zone', 'pulse')
    expect(productivity).toHaveAttribute('data-zone', 'productivity')
    expect(digest).toHaveAttribute('data-zone', 'digest')
  })

  it('does not render the generic placeholder for the attention slot', () => {
    renderPage()

    const attentionNodes = screen.getAllByTestId('dashboard-zone-attention')
    expect(attentionNodes).toHaveLength(1)
  })

  it('keeps the placeholder testids stable for the non-attention slots', () => {
    renderPage()

    const pulse = screen.getByTestId('dashboard-zone-pulse')
    const productivity = screen.getByTestId('dashboard-zone-productivity')
    const digest = screen.getByTestId('dashboard-zone-digest')

    expect(pulse.querySelector('[data-testid="attention-items"]')).toBeNull()
    expect(productivity.querySelector('[data-testid="attention-items"]')).toBeNull()
    expect(digest.querySelector('[data-testid="attention-items"]')).toBeNull()
  })

  it('does not render the Kanban board on the dashboard', () => {
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
