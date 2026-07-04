// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, useLocation } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import type { AgentInfo } from '../../../entities/agent'
import { AgentListPage } from './AgentListPage'

const mocks = vi.hoisted(() => ({
  agents: [] as AgentInfo[],
  agentsLoading: false,
}))

vi.mock('../../../entities/agent', () => ({
  useAgents: () => ({
    data: mocks.agents,
    isLoading: mocks.agentsLoading,
  }),
  readAgentModelAndVariant: (agent: any) => {
    if (!agent?.agentConfig) return { model: null, variant: null }
    const cfg = agent.agentConfig as Record<string, unknown>
    return { model: cfg.model as string ?? null, variant: cfg.variant as string ?? null }
  },
  useAgentStatus: () => ({ data: { running: false, capacity: { active: 0, max: 8 } } }),
}))

vi.mock('../../../shared/lib/useDocumentTitle', () => ({
  useDocumentTitle: () => {},
}))

vi.mock('../../../widgets/agent-profile-editor/ui/AgentProfileEditor', () => ({
  AgentProfileEditor: ({ agent, open }: { agent?: AgentInfo | null; open: boolean }) =>
    open ? <div data-testid="agent-profile-editor" data-mode={agent === null ? 'create' : 'edit'} /> : null,
}))

function LocationProbe() {
  const location = useLocation()
  return <div data-testid="current-path">{location.pathname}</div>
}

function createQueryClient() {
  return new QueryClient({ defaultOptions: { queries: { retry: false } } })
}

function renderPage() {
  const queryClient = createQueryClient()
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId="proj-1" initialProjects={[{
        id: 'proj-1', name: 'Test',
        createdAt: '2026-01-01T00:00:00.000Z', updatedAt: '2026-01-01T00:00:00.000Z',
        repositories: [],
      }]}>
        <MemoryRouter initialEntries={['/agents']}>
          <AgentListPage />
          <LocationProbe />
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

function makeAgent(overrides: Partial<AgentInfo> = {}): AgentInfo {
  return {
    id: 'agent-1',
    projectId: 'proj-1',
    name: 'Test Agent',
    description: 'A test agent',
    instructions: 'Do stuff',
    agentConfig: null,
    skills: [],
    maxConcurrentRuns: null,
    status: 'active',
    createdAt: '2026-06-01T00:00:00.000Z',
    updatedAt: '2026-06-01T00:00:00.000Z',
    ...overrides,
  }
}

describe('AgentListPage', () => {
  beforeEach(() => {
    mocks.agents = []
    mocks.agentsLoading = false
  })

  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  describe('list rendering', () => {
    it('shows loading state while agents are loading', () => {
      mocks.agentsLoading = true
      renderPage()
      expect(screen.getByText(/loading agents/i)).toBeInTheDocument()
    })

    it('renders empty state when no profiles exist', () => {
      renderPage()
      expect(screen.getByTestId('agents-empty-state')).toBeInTheDocument()
      expect(screen.getByText(/no agents defined/i)).toBeInTheDocument()
      expect(screen.getByTestId('agents-empty-create')).toBeInTheDocument()
    })

    it('renders active agents in the list', () => {
      mocks.agents = [makeAgent({ name: 'Alpha', id: 'a1' })]
      renderPage()
      expect(screen.getByTestId('agent-list')).toBeInTheDocument()
      expect(screen.getByTestId('agent-row-a1')).toBeInTheDocument()
      expect(screen.getByText('Alpha')).toBeInTheDocument()
    })

    it('renders agent type, model, and variant for each row', () => {
      mocks.agents = [makeAgent({
        name: 'Beta',
        id: 'b1',
        agentConfig: { model: 'gpt-4', variant: 'high' },
      })]
      renderPage()
      expect(screen.getByText('gpt-4')).toBeInTheDocument()
      expect(screen.getByText('high')).toBeInTheDocument()
    })

    it('distinguishes archived agents with opacity and badge', () => {
      mocks.agents = [
        makeAgent({ name: 'Active One', id: 'a1', status: 'active' }),
        makeAgent({ name: 'Archived One', id: 'a2', status: 'archived' }),
      ]
      renderPage()
      const archivedRow = screen.getByTestId('agent-row-a2')
      expect(archivedRow).toBeInTheDocument()
      expect(archivedRow.getAttribute('data-status')).toBe('archived')
      const archivedLabels = screen.getAllByText('Archived')
      expect(archivedLabels.length).toBeGreaterThanOrEqual(1)
    })

    it('displays availability status (Active / Archived)', () => {
      mocks.agents = [
        makeAgent({ id: 'a1', name: 'Active A', status: 'active' }),
        makeAgent({ id: 'a2', name: 'Archived B', status: 'archived' }),
      ]
      renderPage()
      // Both "Active" (for active status) and "Archived" should be present
      const activeStatuses = screen.getAllByText('Active')
      const archivedStatuses = screen.getAllByText('Archived')
      expect(activeStatuses.length).toBeGreaterThanOrEqual(1)
      expect(archivedStatuses.length).toBeGreaterThanOrEqual(1)
    })
  })

  describe('create entry points', () => {
    it('does not render the editor before any entry point is clicked', () => {
      mocks.agents = [makeAgent({ id: 'a1', name: 'Alpha' })]
      renderPage()
      expect(screen.queryByTestId('agent-profile-editor')).not.toBeInTheDocument()
    })

    it('opens the profile editor in create mode when the header "New Agent" button is clicked (no route change)', () => {
      mocks.agents = [makeAgent({ id: 'a1', name: 'Alpha' })]
      renderPage()
      fireEvent.click(screen.getByTestId('agent-list-create'))
      expect(screen.getByTestId('agent-profile-editor')).toHaveAttribute('data-mode', 'create')
      expect(screen.getByTestId('current-path')).toHaveTextContent('/agents')
    })

    it('opens the profile editor in create mode when the empty-state "Create Agent" button is clicked (no route change)', () => {
      renderPage()
      fireEvent.click(screen.getByTestId('agents-empty-create'))
      expect(screen.getByTestId('agent-profile-editor')).toHaveAttribute('data-mode', 'create')
      expect(screen.getByTestId('current-path')).toHaveTextContent('/agents')
    })
  })
})
