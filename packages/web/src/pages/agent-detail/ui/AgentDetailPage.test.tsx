import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen, fireEvent, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider, useMutation } from '@tanstack/react-query'
import { MemoryRouter, Routes, Route } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import type {
  AgentSessionLaunchContext,
  AgentSessionLaunchResponse,
  AgentTaskLaunchInput,
  AgentInfo,
  AgentSessionListItemDto,
  AgentStatusDetailResponse,
} from '../../../entities/agent'
import {
  AgentSessionComposerPage,
  type AgentSessionComposerDataHook,
  type AgentSessionComposerPageComponents,
} from '../../agent-session-composer'
import { AgentDetailPage, type AgentDetailPageComponents, type AgentDetailPageDataHook } from './AgentDetailPage'

const state: {
  agent: AgentInfo | undefined
  agentState: 'loading' | 'ready' | 'error'
  sessions: AgentSessionListItemDto[]
  archiveCalls: string[]
  unarchiveCalls: string[]
  detailStatus: AgentStatusDetailResponse | undefined
  detailStatusLoading: boolean
} = {
  agent: undefined,
  agentState: 'loading',
  sessions: [],
  archiveCalls: [] as string[],
  unarchiveCalls: [] as string[],
  detailStatus: undefined,
  detailStatusLoading: false,
}

const components: AgentDetailPageComponents = {
  AgentProfileEditor: ({ open }) => (open ? <div data-testid="agent-profile-editor" /> : null),
  SubscriptionsSection: ({ agent }) => (
    <div data-testid="agent-subscriptions-section" data-agent-id={agent.id} data-agent-status={agent.status} />
  ),
  ConnectionsSection: () => null,
}

const composerComponents: AgentSessionComposerPageComponents = {
  AttachmentComposer: ({ value, onChange, placeholder }) => (
    <textarea
      data-testid="journey-prompt"
      value={value}
      onChange={(event) => onChange(event.target.value)}
      placeholder={placeholder}
    />
  ),
}

const dataHook: AgentDetailPageDataHook = () => {
  const archiveAgent = useMutation<AgentInfo, Error, string>({
    mutationFn: async (agentId) => {
      state.archiveCalls.push(agentId)
      return { ...state.agent!, status: 'archived' }
    },
  })
  const unarchiveAgent = useMutation<AgentInfo, Error, string>({
    mutationFn: async (agentId) => {
      state.unarchiveCalls.push(agentId)
      return { ...state.agent!, status: 'active' }
    },
  })

  return {
    agent: state.agent,
    isLoading: state.agentState === 'loading',
    isError: state.agentState === 'error',
    sessions: state.sessions,
    sessionsLoading: false,
    archiveAgent,
    unarchiveAgent,
    detailStatus: state.detailStatus,
    detailStatusLoading: state.detailStatusLoading,
  }
}

const composerDataHook: AgentSessionComposerDataHook = () => {
  const launchMutation = useMutation<
    AgentSessionLaunchResponse,
    Error,
    { agentRef: string; prompt: string; context?: AgentSessionLaunchContext | null; idempotencyKey?: string }
  >({
    mutationFn: async ({ agentRef }) => ({
      sessionId: 'session-from-detail',
      agentId: agentRef,
      agentName: state.agent?.name ?? 'Test Agent',
      workspaceId: 'cli-current',
      targetId: agentRef,
      origin: 'web',
      status: 'queued',
      transcriptUrl: '',
      sessionUrl: '/Test/sessions/session-from-detail',
    }),
  })
  const startTaskMutation = useMutation<
    AgentSessionLaunchResponse,
    Error,
    AgentTaskLaunchInput & { idempotencyKey?: string }
  >({
    mutationFn: async () => ({
      sessionId: 'session-from-task',
      agentId: 'agent-task',
      agentName: 'Task Agent',
      workspaceId: 'web-current',
      targetId: 'agent-task',
      origin: 'web',
      status: 'queued',
      transcriptUrl: '',
      sessionUrl: '/Test/sessions/session-from-task',
    }),
  })
  return {
    agents: state.agent ? [state.agent] : [],
    agentsLoading: false,
    availability: [],
    availabilityLoading: false,
    launchMutation,
    startTaskMutation,
  }
}

function mockAgent(agent: AgentInfo) {
  state.agent = agent
  state.agentState = 'ready'
}

function mockAgentError() {
  state.agent = undefined
  state.agentState = 'error'
}

function mockSessions(sessions: AgentSessionListItemDto[]) {
  state.sessions = sessions
}

function createQueryClient() {
  return new QueryClient({ defaultOptions: { queries: { retry: false } } })
}

function renderPage() {
  const queryClient = createQueryClient()
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider
        initialProjectId="proj-1"
        initialProjects={[
          {
            id: 'proj-1',
            name: 'Test',
            createdAt: '2026-01-01T00:00:00.000Z',
            updatedAt: '2026-01-01T00:00:00.000Z',
            repositories: [],
          },
        ]}
      >
        <MemoryRouter initialEntries={['/agents/agent-1']}>
          <Routes>
            <Route path="/agents/:agentId" element={<AgentDetailPage components={components} dataHook={dataHook} />} />
          </Routes>
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

function renderJourneyPage() {
  const queryClient = createQueryClient()
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider
        initialProjectId="proj-1"
        initialProjects={[
          {
            id: 'proj-1',
            name: 'Test',
            createdAt: '2026-01-01T00:00:00.000Z',
            updatedAt: '2026-01-01T00:00:00.000Z',
            repositories: [],
          },
        ]}
      >
        <MemoryRouter initialEntries={['/Test/agents/agent-1']}>
          <Routes>
            <Route
              path="/:projectName/agents/:agentId"
              element={<AgentDetailPage components={components} dataHook={dataHook} />}
            />
            <Route
              path="/:projectName/agent-sessions/new"
              element={<AgentSessionComposerPage components={composerComponents} dataHook={composerDataHook} />}
            />
            <Route path="/:projectName/sessions/:sessionId" element={<div data-testid="created-session" />} />
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
    purpose: 'Review pull requests',
    description: 'A test agent',
    instructions: 'You are a helpful assistant.',
    agentConfig: { model: 'gpt-4', variant: 'high' },
    skills: ['code', 'debug'],
    permissions: ['repo:read'],
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
    activity: 'idle',
    createdAt: '2026-06-10T00:00:00Z',
    lastActivityAt: '2026-06-10T01:00:00Z',
    resolvedModel: 'gpt-4',
    contextRefs: null,
    ...overrides,
  }
}

describe('AgentDetailPage', () => {
  beforeEach(() => {
    state.agent = undefined
    state.agentState = 'loading'
    state.sessions = []
    state.archiveCalls.length = 0
    state.unarchiveCalls.length = 0
    state.detailStatus = undefined
    state.detailStatusLoading = false
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
    it('renders the active Agent definition identity, instructions, and config', async () => {
      mockAgent(makeAgent({ agentConfig: { model: 'gpt-4', reasoningEffort: 'high', variant: 'balanced' } }))
      renderPage()
      await screen.findByTestId('agent-detail-page')
      expect(screen.getByText('Test Agent')).toBeInTheDocument()
      expect(screen.getByTestId('agent-detail-purpose')).toHaveTextContent('Review pull requests')
      expect(screen.getByTestId('agent-detail-description')).toHaveTextContent('A test agent')
      expect(screen.getByTestId('agent-detail-permissions')).toHaveTextContent('repo:read')
      expect(screen.getByTestId('agent-detail-lifecycle')).toHaveTextContent('Active')
      expect(screen.getByTestId('agent-detail-instructions')).toHaveTextContent('You are a helpful assistant.')
      expect(screen.getByTestId('agent-detail-config')).toBeInTheDocument()
      expect(screen.getByText('gpt-4')).toBeInTheDocument()
      expect(screen.getByTestId('agent-detail-reasoning-effort')).toHaveTextContent('high')
      expect(screen.getByTestId('agent-detail-variant')).toHaveTextContent('balanced')
    })

    it('renders the archived Agent definition identity and lifecycle', async () => {
      mockAgent(makeAgent({ purpose: 'Retained for audit', status: 'archived' }))
      renderPage()

      await screen.findByTestId('agent-detail-page')
      expect(screen.getByTestId('agent-detail-purpose')).toHaveTextContent('Retained for audit')
      expect(screen.getByTestId('agent-detail-lifecycle')).toHaveTextContent('Archived')
    })

    it('omits the effort value when the Agent has no stored effort', async () => {
      mockAgent(makeAgent({ agentConfig: { model: 'gpt-4', variant: 'balanced' } }))
      renderPage()

      const config = await screen.findByTestId('agent-detail-config')
      expect(config.querySelector('[data-testid="agent-detail-reasoning-effort"]')).not.toBeInTheDocument()
      expect(config.textContent ?? '').not.toMatch(/Reasoning Effort.*high/i)
    })

    it('renders runtime, max concurrent runs, and edit timing in the definition summary', async () => {
      mockAgent(
        makeAgent({
          agentConfig: { runtime: 'pi', model: 'gpt-4', variant: 'high' },
          maxConcurrentRuns: 3,
        }),
      )
      renderPage()

      await screen.findByTestId('agent-detail-page')
      expect(screen.getByTestId('agent-detail-runtime')).toHaveTextContent('Pi')
      expect(screen.getByTestId('agent-detail-max-concurrent-runs')).toHaveTextContent('3')
      expect(screen.getByTestId('agent-detail-edit-timing')).toHaveTextContent(/Reasoning Effort/i)
      expect(screen.getByTestId('agent-detail-edit-timing')).toHaveTextContent(/Jobs created after saving/i)
      expect(screen.getByTestId('agent-detail-edit-timing')).toHaveTextContent(/already in progress/i)
    })

    it('does not render an agent-type field (no "opencode" string anywhere on the surface)', async () => {
      mockAgent(
        makeAgent({
          agentConfig: {
            model: 'gpt-4',
            variant: 'high',
            type: 'opencode',
          } as AgentInfo['agentConfig'],
        }),
      )
      renderPage()
      const page = await screen.findByTestId('agent-detail-page')
      const pageText = page.textContent ?? ''
      expect(pageText).toMatch(/gpt-4/)
      expect(pageText).toMatch(/high/)
      expect(pageText).not.toMatch(/opencode/)
    })

    it('surfaces only model and variant in the Agent Config card when the persisted config carries legacy keys', async () => {
      mockAgent(
        makeAgent({
          agentConfig: {
            type: 'opencode',
            livenessQuietThresholdMs: 1200000,
            probeTimeoutMs: 30000,
            model: 'gpt-4',
            variant: 'high',
          } as AgentInfo['agentConfig'],
        }),
      )
      renderPage()
      const config = await screen.findByTestId('agent-detail-config')
      expect(config).toHaveTextContent('gpt-4')
      expect(config).toHaveTextContent('high')
      // Legacy keys are not surfaced in the Agent Config card at all.
      expect(config.textContent ?? '').not.toMatch(/opencode/)
      expect(config.textContent ?? '').not.toMatch(/liveness/i)
      expect(config.textContent ?? '').not.toMatch(/probe/i)
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
        makeSession({ sessionId: 's1', activity: 'active' }),
        makeSession({ sessionId: 's2', activity: 'unknown' }),
        makeSession({ sessionId: 's3', activity: 'idle' }),
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

    it('takes an active Agent from detail through the bound composer to its created Session', async () => {
      mockAgent(makeAgent({ name: 'Detail Agent' }))
      renderJourneyPage()

      fireEvent.click(await screen.findByTestId('agent-detail-new-session'))
      expect(await screen.findByTestId('agent-selector-trigger')).toHaveTextContent('Detail Agent')

      fireEvent.change(screen.getByTestId('journey-prompt'), { target: { value: 'Check the launch path' } })
      fireEvent.click(screen.getByTestId('launch-button'))

      expect(await screen.findByTestId('created-session')).toBeInTheDocument()
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
      await waitFor(() => {
        expect(state.archiveCalls).toHaveLength(1)
        expect(screen.queryByTestId('agent-detail-archive-confirm-dialog')).not.toBeInTheDocument()
      })
      expect(state.archiveCalls[0]).toBe('agent-1')
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
      await waitFor(() => {
        expect(state.unarchiveCalls).toEqual(['agent-1'])
        expect(screen.getByTestId('agent-detail-unarchive-btn')).not.toBeDisabled()
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

  describe('Subscriptions section wiring', () => {
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
