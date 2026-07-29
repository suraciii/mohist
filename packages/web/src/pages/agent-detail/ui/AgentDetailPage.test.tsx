import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen, fireEvent, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider, useMutation } from '@tanstack/react-query'
import { MemoryRouter, Routes, Route } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import type {
  AgentInfo,
  AgentSessionListItemDto,
  AgentStatusDetailResponse,
} from '../../../entities/agent'
import {
  AgentDetailPage,
  type AgentDetailPageComponents,
  type AgentDetailPageDataHook,
} from './AgentDetailPage'

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
  AgentProfileEditor: ({ open }) => (
    open ? <div data-testid="agent-profile-editor" /> : null
  ),
  SubscriptionsSection: ({ agent }) => (
    <div
      data-testid="agent-subscriptions-section"
      data-agent-id={agent.id}
      data-agent-status={agent.status}
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
      <ProjectProvider initialProjectId="proj-1" initialProjects={[{
        id: 'proj-1', name: 'Test',
        createdAt: '2026-01-01T00:00:00.000Z', updatedAt: '2026-01-01T00:00:00.000Z',
        repositories: [],
      }]}>
        <MemoryRouter initialEntries={['/agents/agent-1']}>
          <Routes>
            <Route
              path="/agents/:agentId"
              element={<AgentDetailPage components={components} dataHook={dataHook} />}
            />
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
    // Issue 484: sessions are grouped by `activity` (active/unknown/idle),
    // not `status`. Default to idle (an ended/awaiting-followup session).
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

    it('does not render an agent-type field (no "opencode" string anywhere on the surface)', async () => {
      // Per #410 T-002 design D5: the agent-detail page must not read or
      // display the legacy `type` key from agentConfig. Earlier behaviour
      // rendered `<type> · <model> · <variant>` on the title row; the
      // converged surface shows model/variant only.
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
      // The agent-type chip lives on the subtitle row inside the page
      // header; assert the page-level text never contains the legacy
      // "opencode" value, while model/variant remain visible.
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
    // Issue 484: sessions are grouped by `activity`, not `status`.
    // - Running section  <- activity === 'active'
    // - Failed section   <- activity === 'unknown' (unconfirmed activity)
    // - Ended section    <- activity === 'idle'    (finished, follow-up-able)
    // Sessions never carry a terminal status anymore.
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

  /* ── Readiness card (server-conclusion rendering, no client synthesis) ── */

  describe('Readiness card (server-conclusion rendering)', () => {
    it('renders the Server-provided Readiness conclusion and gaps for Needs setup', async () => {
      mockAgent(makeAgent({
        readiness: {
          conclusion: 'Needs setup',
          gaps: [
            { code: 'instructions-missing', message: 'Instructions are missing.', action: 'Add instructions in Agent settings.' },
          ],
          setup: { label: 'Agent settings', path: '/agents/agent-1/settings' },
        },
      }))
      renderPage()
      const card = await screen.findByTestId('agent-detail-readiness')
      expect(card).toHaveAttribute('data-conclusion', 'Needs setup')
      const gap = screen.getByTestId('agent-detail-readiness-gap-instructions-missing')
      expect(gap).toHaveTextContent(/Instructions are missing/i)
      expect(gap).toHaveTextContent(/Add instructions/i)
      expect(screen.getByTestId('agent-detail-readiness-setup')).toHaveTextContent(/Agent settings/i)
    })

    it('renders the Server-provided Readiness conclusion for Ready without synthesizing an extra verdict', async () => {
      mockAgent(makeAgent({ readiness: { conclusion: 'Ready', gaps: [], setup: null } }))
      renderPage()
      const card = await screen.findByTestId('agent-detail-readiness')
      expect(card).toHaveAttribute('data-conclusion', 'Ready')
      expect(screen.queryByTestId('agent-detail-readiness-gaps')).not.toBeInTheDocument()
    })

    it('renders Unknown as Unknown (does not invent a verdict) and shows the will-wait-for-validation hint on the New Session path', async () => {
      mockAgent(makeAgent({ readiness: { conclusion: 'Unknown', gaps: [], setup: null } }))
      renderPage()
      const card = await screen.findByTestId('agent-detail-readiness')
      expect(card).toHaveAttribute('data-conclusion', 'Unknown')
      expect(screen.getByTestId('agent-detail-unknown-launch-hint')).toHaveTextContent(/Readiness is Unknown/i)
      expect(screen.getByTestId('agent-detail-unknown-launch-hint')).toHaveTextContent(/wait/i)
      expect(screen.getByTestId('agent-detail-new-session')).not.toBeDisabled()
    })

    it('blocks the New Session launch control when Readiness is Needs setup', async () => {
      mockAgent(makeAgent({
        readiness: {
          conclusion: 'Needs setup',
          gaps: [{ code: 'instructions-missing', message: 'Instructions are missing.', action: 'Add instructions.' }],
          setup: { label: 'Agent settings', path: '/agents/agent-1/settings' },
        },
      }))
      renderPage()
      const btn = await screen.findByTestId('agent-detail-new-session')
      expect(btn).toBeDisabled()
    })
  })

  /* ── Availability card (server-conclusion rendering) ── */

  describe('Availability card (server-conclusion rendering)', () => {
    it('shows Can start now when the Server reports canStartNow=true', async () => {
      mockAgent(makeAgent())
      state.detailStatus = {
        agentId: 'agent-1',
        agentName: 'Test Agent',
        availability: {
          canStartNow: true,
          waitingReason: null,
          activeRuns: 0,
          maxConcurrentRuns: 2,
          capacity: { usedSlots: 0, totalSlots: 2 },
          observedAt: '2026-07-29T00:00:00.000Z',
        },
        waitingWork: [],
      }
      renderPage()
      const card = await screen.findByTestId('agent-detail-availability')
      expect(card).toHaveAttribute('data-state', 'ready')
      expect(screen.getByTestId('agent-detail-availability-conclusion')).toHaveTextContent(/Can start now/i)
    })

    it('renders the Server-provided waiting reason and lists waiting work items with their reasons', async () => {
      mockAgent(makeAgent())
      state.detailStatus = {
        agentId: 'agent-1',
        agentName: 'Test Agent',
        availability: {
          canStartNow: false,
          waitingReason: 'capacity-full',
          activeRuns: 2,
          maxConcurrentRuns: 2,
          capacity: { usedSlots: 2, totalSlots: 2 },
          observedAt: '2026-07-29T00:00:00.000Z',
        },
        waitingWork: [
          { jobId: 'job-1', status: 'waiting', waitingReason: 'capacity-full', submittedAt: '2026-07-29T00:00:00.000Z' },
          { jobId: 'job-2', status: 'waiting', waitingReason: 'concurrency-limit', submittedAt: '2026-07-29T00:00:00.000Z' },
        ],
      }
      renderPage()
      const card = await screen.findByTestId('agent-detail-availability')
      expect(card).toHaveAttribute('data-state', 'waiting')
      expect(card).toHaveAttribute('data-waiting-reason', 'capacity-full')
      expect(screen.getByTestId('agent-detail-availability-conclusion')).toHaveTextContent(/Runner slots are full/i)
      expect(screen.getByTestId('agent-detail-waiting-work-job-1')).toHaveAttribute('data-waiting-reason', 'capacity-full')
      expect(screen.getByTestId('agent-detail-waiting-work-job-2')).toHaveAttribute('data-waiting-reason', 'concurrency-limit')
    })

    it('does NOT synthesize a Runner-at-capacity verdict when the Server provides its own conclusion', async () => {
      // The Server can report canStartNow=true with usedSlots==totalSlots in transient states
      // (the Server's per-agent Availability conclusion is the sole authority).
      mockAgent(makeAgent())
      state.detailStatus = {
        agentId: 'agent-1',
        agentName: 'Test Agent',
        availability: {
          canStartNow: true,
          waitingReason: null,
          activeRuns: 1,
          maxConcurrentRuns: 2,
          capacity: { usedSlots: 2, totalSlots: 2 },
          observedAt: '2026-07-29T00:00:00.000Z',
        },
        waitingWork: [],
      }
      renderPage()
      const card = await screen.findByTestId('agent-detail-availability')
      expect(card).toHaveAttribute('data-state', 'ready')
      // Surface the Server-provided slots as a numeric detail, not a client-synthesized
      // "Runner at capacity" verdict.
      expect(screen.getByTestId('agent-detail-availability-detail')).toHaveTextContent('2/2')
      expect(card.textContent ?? '').not.toMatch(/Runner at capacity/i)
    })
  })
})
