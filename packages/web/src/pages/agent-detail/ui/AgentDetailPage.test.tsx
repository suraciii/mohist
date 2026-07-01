// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen, fireEvent } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Routes, Route } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import type { AgentInfo, AgentSessionListItemDto } from '../../../entities/agent'
import { AgentDetailPage } from './AgentDetailPage'

const mocks = vi.hoisted(() => ({
  agent: null as AgentInfo | null,
  agentLoading: false,
  agentError: false,
  sessions: [] as AgentSessionListItemDto[],
  sessionsLoading: false,
  routeParams: { agentId: 'agent-1' },
}))

vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>()
  return {
    ...actual,
    useParams: () => mocks.routeParams,
  }
})

vi.mock('../../../entities/agent', () => ({
  useAgent: () => ({
    data: mocks.agent,
    isLoading: mocks.agentLoading,
    isError: mocks.agentError,
  }),
  useAgentSessions: () => ({
    data: mocks.sessions,
    isLoading: mocks.sessionsLoading,
  }),
  readAgentModelAndVariant: (agent: any) => {
    if (!agent?.agentConfig) return { model: null, variant: null }
    const cfg = agent.agentConfig as Record<string, unknown>
    return { model: cfg.model as string ?? null, variant: cfg.variant as string ?? null }
  },
}))

vi.mock('../../../shared/lib/useDocumentTitle', () => ({
  useDocumentTitle: () => {},
}))

vi.mock('../../../widgets/agent-profile-editor/ui/AgentProfileEditor', () => ({
  AgentProfileEditor: ({ open }: { open: boolean }) =>
    open ? <div data-testid="agent-profile-editor" /> : null,
}))

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
        <MemoryRouter initialEntries={['/agents/agent-1']}>
          <Routes>
            <Route path="/agents/:agentId" element={<AgentDetailPage />} />
          </Routes>
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
    instructions: 'You are a helpful assistant.',
    agentConfig: { model: 'gpt-4', variant: 'high' },
    skills: ['code', 'debug'],
    maxConcurrentRuns: null,
    status: 'active',
    createdAt: '2026-06-01T00:00:00.000Z',
    updatedAt: '2026-06-01T00:00:00.000Z',
    ...overrides,
  }
}

function makeSession(overrides: Partial<AgentSessionListItemDto> = {}): AgentSessionListItemDto {
  return {
    sessionId: 'sess-1',
    agentId: 'agent-1',
    agentName: 'Test Agent',
    status: 'completed',
    createdAt: '2026-06-10T00:00:00.000Z',
    lastActivityAt: '2026-06-10T01:00:00.000Z',
    resolvedModel: 'gpt-4',
    contextRefs: null,
    ...overrides,
  }
}

describe('AgentDetailPage', () => {
  beforeEach(() => {
    mocks.agent = null
    mocks.agentLoading = false
    mocks.agentError = false
    mocks.sessions = []
    mocks.sessionsLoading = false
    mocks.routeParams = { agentId: 'agent-1' }
  })

  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  describe('loading and error states', () => {
    it('shows loading state while agent is loading', () => {
      mocks.agentLoading = true
      renderPage()
      expect(screen.getByText(/loading agent/i)).toBeInTheDocument()
    })

    it('shows error state when agent fetch fails', () => {
      mocks.agentError = true
      renderPage()
      expect(screen.getByText(/failed to load agent/i)).toBeInTheDocument()
    })
  })

  describe('profile summary', () => {
    it('renders agent name, instructions, and config', () => {
      mocks.agent = makeAgent()
      renderPage()
      expect(screen.getByText('Test Agent')).toBeInTheDocument()
      expect(screen.getByTestId('agent-detail-instructions')).toHaveTextContent('You are a helpful assistant.')
      expect(screen.getByTestId('agent-detail-config')).toBeInTheDocument()
      expect(screen.getByText('gpt-4')).toBeInTheDocument()
      expect(screen.getByText('high')).toBeInTheDocument()
    })

    it('renders skills metadata', () => {
      mocks.agent = makeAgent()
      renderPage()
      const skillsContainer = screen.getByTestId('agent-detail-skills')
      expect(skillsContainer).toBeInTheDocument()
      expect(skillsContainer).toHaveTextContent('code')
      expect(skillsContainer).toHaveTextContent('debug')
    })
  })

  describe('session history grouping', () => {
    it('renders sessions in running, failed, and ended sections', () => {
      mocks.agent = makeAgent()
      mocks.sessions = [
        makeSession({ sessionId: 's1', status: 'running' }),
        makeSession({ sessionId: 's2', status: 'failed' }),
        makeSession({ sessionId: 's3', status: 'completed' }),
      ]
      renderPage()
      expect(screen.getByTestId('agent-detail-sessions')).toBeInTheDocument()
      expect(screen.getByText('Running')).toBeInTheDocument()
      expect(screen.getByText('Failed')).toBeInTheDocument()
      expect(screen.getByText('Ended')).toBeInTheDocument()
    })

    it('shows empty sessions message when no sessions exist', () => {
      mocks.agent = makeAgent()
      mocks.sessions = []
      renderPage()
      expect(screen.getByText(/no sessions yet/i)).toBeInTheDocument()
    })
  })

  describe('new-session and edit entry points', () => {
    it('offers a new-session button for active profiles', () => {
      mocks.agent = makeAgent()
      renderPage()
      const newSessionBtn = screen.getByTestId('agent-detail-new-session')
      expect(newSessionBtn).toBeInTheDocument()
      expect(newSessionBtn).not.toBeDisabled()
    })

    it('disables new-session button for archived profiles', () => {
      mocks.agent = makeAgent({ status: 'archived' })
      renderPage()
      const newSessionBtn = screen.getByTestId('agent-detail-new-session')
      expect(newSessionBtn).toBeInTheDocument()
      expect(newSessionBtn).toBeDisabled()
    })

    it('shows edit button', () => {
      mocks.agent = makeAgent()
      renderPage()
      expect(screen.getByTestId('agent-detail-edit')).toBeInTheDocument()
    })

    it('opens the profile editor when edit is clicked', () => {
      mocks.agent = makeAgent()
      renderPage()
      const editBtn = screen.getByTestId('agent-detail-edit')
      fireEvent.click(editBtn)
      expect(screen.getByTestId('agent-profile-editor')).toBeInTheDocument()
    })
  })
})
