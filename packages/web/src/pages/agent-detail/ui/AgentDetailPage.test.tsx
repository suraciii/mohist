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
  subscriptions: [] as Array<Record<string, unknown>>,
  subscriptionsLoading: false,
  subscriptionsSectionRendered: true,
  archiveMutateCalls: [] as Array<{ id: string; options: { onSuccess?: () => void } | undefined }>,
  unarchiveMutateCalls: [] as Array<string>,
  archiveSubscriptionCalls: [] as Array<{ subscriptionId: string }>,
  restoreSubscriptionCalls: [] as Array<{ subscriptionId: string }>,
  deleteSubscriptionCalls: [] as Array<{ subscriptionId: string }>,
  archivePending: false,
  unarchivePending: false,
}))


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
  useArchiveAgent: () => ({
    mutate: (id: string, options?: { onSuccess?: () => void }) => {
      mocks.archiveMutateCalls.push({ id, options: options ?? undefined })
      options?.onSuccess?.()
    },
    isPending: mocks.archivePending,
  }),
  useUnarchiveAgent: () => ({
    mutate: (id: string) => {
      mocks.unarchiveMutateCalls.push(id)
    },
    isPending: mocks.unarchivePending,
  }),
  useAgentSubscriptions: () => ({
    data: mocks.subscriptions,
    isLoading: mocks.subscriptionsLoading,
  }),
  useCreateAgentSubscription: () => ({
    mutate: () => {},
    isPending: false,
  }),
  useArchiveAgentSubscription: () => ({
    mutate: mocks.archiveSubscriptionCalls.push.bind(mocks.archiveSubscriptionCalls),
    isPending: false,
  }),
  useRestoreAgentSubscription: () => ({
    mutate: mocks.restoreSubscriptionCalls.push.bind(mocks.restoreSubscriptionCalls),
    isPending: false,
  }),
  useDeleteAgentSubscription: () => ({
    mutate: mocks.deleteSubscriptionCalls.push.bind(mocks.deleteSubscriptionCalls),
    isPending: false,
  }),
  formatAgentSubscriptionFilter: (filter: { type: string; source: string | null; subject: string | null }) => {
    const parts: string[] = [filter.type]
    if (filter.source) parts.push(`source=${filter.source}`)
    if (filter.subject) parts.push(`subject=${filter.subject}`)
    return parts.join(', ')
  },
  readAgentModelAndVariant: (agent: any) => {
    if (!agent?.agentConfig) return { model: null, variant: null }
    const cfg = agent.agentConfig as Record<string, unknown>
    return { model: cfg.model as string ?? null, variant: cfg.variant as string ?? null }
  },
}))


vi.mock('../../../widgets/agent-profile-editor/ui/AgentProfileEditor', () => ({
  AgentProfileEditor: ({ open }: { open: boolean }) =>
    open ? <div data-testid="agent-profile-editor" /> : null,
}))

vi.mock('../../../widgets/agent-subscriptions/ui/SubscriptionsSection', () => ({
  SubscriptionsSection: ({ agent }: { agent: { id: string; status: string } }) =>
    mocks.subscriptionsSectionRendered ? (
      <div
        data-testid="agent-subscriptions-section"
        data-agent-id={agent.id}
        data-agent-status={agent.status}
      />
    ) : null,
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
    mocks.subscriptions = []
    mocks.subscriptionsLoading = false
    mocks.archiveMutateCalls.length = 0
    mocks.unarchiveMutateCalls.length = 0
    mocks.archiveSubscriptionCalls.length = 0
    mocks.restoreSubscriptionCalls.length = 0
    mocks.deleteSubscriptionCalls.length = 0
    mocks.archivePending = false
    mocks.unarchivePending = false
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

  describe('Actions card (agent-archive + agent-unarchive specs)', () => {
    it('for an active agent, the Archive button does not open the Edit dialog on click', () => {
      mocks.agent = makeAgent({ status: 'active' })
      renderPage()
      const archiveBtn = screen.getByTestId('agent-detail-archive-btn')
      fireEvent.click(archiveBtn)
      expect(screen.queryByTestId('agent-profile-editor')).not.toBeInTheDocument()
    })

    it('for an active agent, clicking the Archive button opens a confirm dialog (not a direct archive)', () => {
      mocks.agent = makeAgent({ status: 'active' })
      renderPage()
      fireEvent.click(screen.getByTestId('agent-detail-archive-btn'))
      expect(screen.getByTestId('agent-detail-archive-confirm-dialog')).toBeInTheDocument()
      expect(screen.getByTestId('agent-detail-archive-confirm')).toBeInTheDocument()
      expect(screen.getByTestId('agent-detail-archive-cancel')).toBeInTheDocument()
    })

    it('cancelling the archive confirm does NOT archive', () => {
      mocks.agent = makeAgent({ status: 'active' })
      renderPage()
      fireEvent.click(screen.getByTestId('agent-detail-archive-btn'))
      fireEvent.click(screen.getByTestId('agent-detail-archive-cancel'))
      expect(screen.queryByTestId('agent-detail-archive-confirm-dialog')).not.toBeInTheDocument()
      expect(mocks.archiveMutateCalls).toHaveLength(0)
    })

    it('confirming the archive invokes useArchiveAgent.mutate with the agent id and closes the confirm dialog', () => {
      mocks.agent = makeAgent({ status: 'active' })
      renderPage()
      fireEvent.click(screen.getByTestId('agent-detail-archive-btn'))
      fireEvent.click(screen.getByTestId('agent-detail-archive-confirm'))
      expect(mocks.archiveMutateCalls).toHaveLength(1)
      expect(mocks.archiveMutateCalls[0].id).toBe('agent-1')
      expect(screen.queryByTestId('agent-detail-archive-confirm-dialog')).not.toBeInTheDocument()
    })

    it('for an archived agent, the static archived notice is replaced by an Unarchive control', () => {
      mocks.agent = makeAgent({ status: 'archived' })
      renderPage()
      expect(screen.queryByText(/this agent is archived and cannot be launched/i)).not.toBeInTheDocument()
      expect(screen.getByTestId('agent-detail-unarchive-btn')).toBeInTheDocument()
      expect(screen.queryByTestId('agent-detail-archive-btn')).not.toBeInTheDocument()
    })

    it('for an archived agent, clicking the Unarchive control invokes useUnarchiveAgent with the agent id', () => {
      mocks.agent = makeAgent({ status: 'archived' })
      renderPage()
      fireEvent.click(screen.getByTestId('agent-detail-unarchive-btn'))
      expect(mocks.unarchiveMutateCalls).toEqual(['agent-1'])
    })

    it('for an active agent, the Unarchive control is NOT rendered (no mismatch)', () => {
      mocks.agent = makeAgent({ status: 'active' })
      renderPage()
      expect(screen.queryByTestId('agent-detail-unarchive-btn')).not.toBeInTheDocument()
    })

    it('for an archived agent, the New Session control remains disabled (archived-cannot-launch invariant)', () => {
      mocks.agent = makeAgent({ status: 'archived' })
      renderPage()
      const newSessionBtn = screen.getByTestId('agent-detail-new-session')
      expect(newSessionBtn).toBeDisabled()
    })
  })

  describe('Subscriptions section wiring (T-004)', () => {
    it('mounts the SubscriptionsSection for an active agent with its own data-agent-id', () => {
      mocks.agent = makeAgent({ id: 'agent-42', status: 'active' })
      renderPage()
      const section = screen.getByTestId('agent-subscriptions-section')
      expect(section).toBeInTheDocument()
      expect(section).toHaveAttribute('data-agent-id', 'agent-42')
      expect(section).toHaveAttribute('data-agent-status', 'active')
    })

    it('mounts the SubscriptionsSection for an archived agent and forwards the archived status', () => {
      mocks.agent = makeAgent({ status: 'archived' })
      renderPage()
      const section = screen.getByTestId('agent-subscriptions-section')
      expect(section).toBeInTheDocument()
      expect(section).toHaveAttribute('data-agent-status', 'archived')
    })
  })
})
