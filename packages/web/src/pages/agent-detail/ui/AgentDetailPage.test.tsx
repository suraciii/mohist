// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen, fireEvent } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Routes, Route } from 'react-router-dom'
import { http, HttpResponse } from 'msw'
import { ProjectProvider } from '../../../entities/project'
import type { AgentInfo, AgentSessionListItemDto } from '../../../entities/agent'
import { server, useMswServer } from '../../../../tests/support/msw'
import { AgentDetailPage } from './AgentDetailPage'

const state = vi.hoisted(() => ({
  archiveCalls: [] as string[],
  unarchiveCalls: [] as string[],
}))

vi.mock('../../../widgets/agent-profile-editor/ui/AgentProfileEditor', () => ({
  AgentProfileEditor: ({ open }: { open: boolean }) =>
    open ? <div data-testid="agent-profile-editor" /> : null,
}))

vi.mock('../../../widgets/agent-subscriptions/ui/SubscriptionsSection', () => ({
  SubscriptionsSection: ({ agent }: { agent: { id: string; status: string } }) => (
    <div
      data-testid="agent-subscriptions-section"
      data-agent-id={agent.id}
      data-agent-status={agent.status}
    />
  ),
}))

useMswServer(
  http.get('*/api/projects/:projectId/agents/:agentRef', () => new Promise(() => {})),
  http.get('*/api/projects/:projectId/agents/:agentRef/sessions', () =>
    HttpResponse.json({ success: true, data: [] }),
  ),
  http.delete('*/api/projects/:projectId/agents/:agentRef', ({ params }) => {
    state.archiveCalls.push(params.agentRef as string)
    return HttpResponse.json({ success: true, data: { id: params.agentRef, status: 'archived' } })
  }),
  http.post('*/api/projects/:projectId/agents/:agentRef/unarchive', ({ params }) => {
    state.unarchiveCalls.push(params.agentRef as string)
    return HttpResponse.json({ success: true, data: { id: params.agentRef, status: 'active' } })
  }),
)

function mockAgent(agent: AgentInfo) {
  server.use(
    http.get('*/api/projects/:projectId/agents/:agentRef', () =>
      HttpResponse.json({ success: true, data: agent }),
    ),
  )
}

function mockAgentError() {
  server.use(
    http.get('*/api/projects/:projectId/agents/:agentRef', () =>
      HttpResponse.json({ success: false, error: 'fail' }, { status: 500 }),
    ),
  )
}

function mockSessions(sessions: AgentSessionListItemDto[]) {
  server.use(
    http.get('*/api/projects/:projectId/agents/:agentRef/sessions', () =>
      HttpResponse.json({ success: true, data: sessions }),
    ),
  )
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
    state.archiveCalls.length = 0
    state.unarchiveCalls.length = 0
  })

  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  describe('loading and error states', () => {
    it('shows loading state while agent is loading', () => {
      renderPage()
      expect(screen.getByText(/loading agent/i)).toBeInTheDocument()
    })

    it('shows error state when agent fetch fails', async () => {
      mockAgentError()
      renderPage()
      expect(await screen.findByText(/failed to load agent/i)).toBeInTheDocument()
    })
  })

  describe('profile summary', () => {
    it('renders agent name, instructions, and config', async () => {
      mockAgent(makeAgent())
      renderPage()
      await screen.findByTestId('agent-detail-page')
      expect(screen.getByText('Test Agent')).toBeInTheDocument()
      expect(screen.getByTestId('agent-detail-instructions')).toHaveTextContent('You are a helpful assistant.')
      expect(screen.getByTestId('agent-detail-config')).toBeInTheDocument()
      expect(screen.getByText('gpt-4')).toBeInTheDocument()
      expect(screen.getByText('high')).toBeInTheDocument()
    })

    it('renders skills metadata', async () => {
      mockAgent(makeAgent())
      renderPage()
      await screen.findByTestId('agent-detail-skills')
      const skillsContainer = screen.getByTestId('agent-detail-skills')
      expect(skillsContainer).toBeInTheDocument()
      expect(skillsContainer).toHaveTextContent('code')
      expect(skillsContainer).toHaveTextContent('debug')
    })
  })

  describe('session history grouping', () => {
    it('renders sessions in running, failed, and ended sections', async () => {
      mockAgent(makeAgent())
      mockSessions([
        makeSession({ sessionId: 's1', status: 'running' }),
        makeSession({ sessionId: 's2', status: 'failed' }),
        makeSession({ sessionId: 's3', status: 'completed' }),
      ])
      renderPage()
      await screen.findByTestId('agent-detail-sessions')
      expect(screen.getByText('Running')).toBeInTheDocument()
      expect(screen.getByText('Failed')).toBeInTheDocument()
      expect(screen.getByText('Ended')).toBeInTheDocument()
    })

    it('shows empty sessions message when no sessions exist', async () => {
      mockAgent(makeAgent())
      renderPage()
      expect(await screen.findByText(/no sessions yet/i)).toBeInTheDocument()
    })
  })

  describe('new-session and edit entry points', () => {
    it('offers a new-session button for active profiles', async () => {
      mockAgent(makeAgent())
      renderPage()
      const newSessionBtn = await screen.findByTestId('agent-detail-new-session')
      expect(newSessionBtn).toBeInTheDocument()
      expect(newSessionBtn).not.toBeDisabled()
    })

    it('disables new-session button for archived profiles', async () => {
      mockAgent(makeAgent({ status: 'archived' }))
      renderPage()
      const newSessionBtn = await screen.findByTestId('agent-detail-new-session')
      expect(newSessionBtn).toBeInTheDocument()
      expect(newSessionBtn).toBeDisabled()
    })

    it('shows edit button', async () => {
      mockAgent(makeAgent())
      renderPage()
      expect(await screen.findByTestId('agent-detail-edit')).toBeInTheDocument()
    })

    it('opens the profile editor when edit is clicked', async () => {
      mockAgent(makeAgent())
      renderPage()
      const editBtn = await screen.findByTestId('agent-detail-edit')
      fireEvent.click(editBtn)
      expect(screen.getByTestId('agent-profile-editor')).toBeInTheDocument()
    })
  })

  describe('Actions card (agent-archive + agent-unarchive specs)', () => {
    it('for an active agent, the Archive button does not open the Edit dialog on click', async () => {
      mockAgent(makeAgent({ status: 'active' }))
      renderPage()
      const archiveBtn = await screen.findByTestId('agent-detail-archive-btn')
      fireEvent.click(archiveBtn)
      expect(screen.queryByTestId('agent-profile-editor')).not.toBeInTheDocument()
    })

    it('for an active agent, clicking the Archive button opens a confirm dialog (not a direct archive)', async () => {
      mockAgent(makeAgent({ status: 'active' }))
      renderPage()
      fireEvent.click(await screen.findByTestId('agent-detail-archive-btn'))
      expect(screen.getByTestId('agent-detail-archive-confirm-dialog')).toBeInTheDocument()
      expect(screen.getByTestId('agent-detail-archive-confirm')).toBeInTheDocument()
      expect(screen.getByTestId('agent-detail-archive-cancel')).toBeInTheDocument()
    })

    it('cancelling the archive confirm does NOT archive', async () => {
      mockAgent(makeAgent({ status: 'active' }))
      renderPage()
      fireEvent.click(await screen.findByTestId('agent-detail-archive-btn'))
      fireEvent.click(screen.getByTestId('agent-detail-archive-cancel'))
      expect(screen.queryByTestId('agent-detail-archive-confirm-dialog')).not.toBeInTheDocument()
      expect(state.archiveCalls).toHaveLength(0)
    })

    it('confirming the archive invokes useArchiveAgent.mutate with the agent id and closes the confirm dialog', async () => {
      mockAgent(makeAgent({ status: 'active' }))
      renderPage()
      fireEvent.click(await screen.findByTestId('agent-detail-archive-btn'))
      fireEvent.click(screen.getByTestId('agent-detail-archive-confirm'))
      await vi.waitFor(() => {
        expect(state.archiveCalls).toHaveLength(1)
      })
      expect(state.archiveCalls[0]).toBe('agent-1')
      expect(screen.queryByTestId('agent-detail-archive-confirm-dialog')).not.toBeInTheDocument()
    })

    it('for an archived agent, the static archived notice is replaced by an Unarchive control', async () => {
      mockAgent(makeAgent({ status: 'archived' }))
      renderPage()
      await screen.findByTestId('agent-detail-page')
      expect(screen.queryByText(/this agent is archived and cannot be launched/i)).not.toBeInTheDocument()
      expect(screen.getByTestId('agent-detail-unarchive-btn')).toBeInTheDocument()
      expect(screen.queryByTestId('agent-detail-archive-btn')).not.toBeInTheDocument()
    })

    it('for an archived agent, clicking the Unarchive control invokes useUnarchiveAgent with the agent id', async () => {
      mockAgent(makeAgent({ status: 'archived' }))
      renderPage()
      fireEvent.click(await screen.findByTestId('agent-detail-unarchive-btn'))
      await vi.waitFor(() => {
        expect(state.unarchiveCalls).toEqual(['agent-1'])
      })
    })

    it('for an active agent, the Unarchive control is NOT rendered (no mismatch)', async () => {
      mockAgent(makeAgent({ status: 'active' }))
      renderPage()
      await screen.findByTestId('agent-detail-page')
      expect(screen.queryByTestId('agent-detail-unarchive-btn')).not.toBeInTheDocument()
    })

    it('for an archived agent, the New Session control remains disabled (archived-cannot-launch invariant)', async () => {
      mockAgent(makeAgent({ status: 'archived' }))
      renderPage()
      const newSessionBtn = await screen.findByTestId('agent-detail-new-session')
      expect(newSessionBtn).toBeDisabled()
    })
  })

  describe('Subscriptions section wiring (T-004)', () => {
    it('mounts the SubscriptionsSection for an active agent with its own data-agent-id', async () => {
      mockAgent(makeAgent({ id: 'agent-42', status: 'active' }))
      renderPage()
      const section = await screen.findByTestId('agent-subscriptions-section')
      expect(section).toBeInTheDocument()
      expect(section).toHaveAttribute('data-agent-id', 'agent-42')
      expect(section).toHaveAttribute('data-agent-status', 'active')
    })

    it('mounts the SubscriptionsSection for an archived agent and forwards the archived status', async () => {
      mockAgent(makeAgent({ status: 'archived' }))
      renderPage()
      const section = await screen.findByTestId('agent-subscriptions-section')
      expect(section).toBeInTheDocument()
      expect(section).toHaveAttribute('data-agent-status', 'archived')
    })
  })
})
