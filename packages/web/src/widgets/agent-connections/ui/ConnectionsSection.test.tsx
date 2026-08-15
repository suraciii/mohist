import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen, fireEvent, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { ProjectProvider } from '../../../entities/project'
import { ConnectionsSection, type ConnectionOperationsHook } from './ConnectionsSection'
import type { AgentInfo } from '../../../entities/agent'
import type { AgentConnectionDto } from '../../../entities/agent-connection'

const mocks = {
  connections: [] as AgentConnectionDto[],
  connectionsLoading: false,
  createMutateCalls: [] as Array<{ data: unknown; options?: unknown }>,
  createPending: false,
}

function makeConnection(overrides: Partial<AgentConnectionDto> = {}): AgentConnectionDto {
  return {
    id: 'conn_default',
    projectId: 'proj-1',
    agentId: 'agent-1',
    providerKind: 'slack',
    workspaceTeamId: '',
    appId: '',
    botUserId: '',
    botName: 'preview-bot',
    avatarHash: null,
    verifiedBotName: null,
    verifiedBotIconUrl: null,
    setupProgress: 'create_app_credentials',
    desiredState: 'enabled',
    connectionHealth: 'healthy',
    healthReason: null,
    agentReadiness: 'unknown',
    ownerSlackUserId: null,
    accessPolicy: 'owner_only',
    lastHeartbeatAt: null,
    createdAt: '2026-06-01T00:00:00.000Z',
    updatedAt: '2026-06-01T00:00:00.000Z',
    deletedAt: null,
    ...overrides,
  }
}

function makeAgent(overrides: Partial<AgentInfo> = {}): AgentInfo {
  return {
    id: 'agent-1',
    projectId: 'proj-1',
    name: 'Test Agent',
    purpose: null,
    description: '',
    instructions: '...',
    agentConfig: null,
    skills: [],
    permissions: [],
    maxConcurrentRuns: null,
    status: 'active',
    createdAt: '2026-06-01T00:00:00.000Z',
    updatedAt: '2026-06-01T00:00:00.000Z',
    ...overrides,
  }
}

function createQueryClient() {
  return new QueryClient({ defaultOptions: { queries: { retry: false } } })
}

const operationsHook: ConnectionOperationsHook = () => ({
  connectionsQuery: {
    data: mocks.connections,
    isLoading: mocks.connectionsLoading,
  },
  createMutation: {
    mutate: (data, options) => {
      mocks.createMutateCalls.push({ data, options })
      const onSuccess = options?.onSuccess as ((created: { connection: AgentConnectionDto }) => void) | undefined
      onSuccess?.({ connection: makeConnection({ id: 'conn_new', agentId: (data as { agentId: string }).agentId }) })
    },
    isPending: mocks.createPending,
  },
})

function renderSection(agent: AgentInfo = makeAgent(), { withRoutes = false }: { withRoutes?: boolean } = {}) {
  const queryClient = createQueryClient()
  if (withRoutes) {
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
                element={<ConnectionsSection agent={agent} operationsHook={operationsHook} />}
              />
              <Route
                path="/:projectName/connections/:connectionId"
                element={<div data-testid="connection-page" data-connection-id=":connectionId" />}
              />
            </Routes>
          </MemoryRouter>
        </ProjectProvider>
      </QueryClientProvider>,
    )
  }
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
        <MemoryRouter>
          <ConnectionsSection agent={agent} operationsHook={operationsHook} />
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

describe('ConnectionsSection', () => {
  beforeEach(() => {
    mocks.connections = []
    mocks.connectionsLoading = false
    mocks.createMutateCalls.length = 0
    mocks.createPending = false
  })

  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  describe('rendering', () => {
    it('renders the section heading and Add Slack button', () => {
      renderSection()
      expect(screen.getByText('Connections')).toBeInTheDocument()
      expect(screen.getByTestId('agent-connections-add-slack')).toBeInTheDocument()
    })

    it('shows the empty state when there are no connections', () => {
      renderSection()
      expect(screen.getByTestId('agent-connections-empty')).toBeInTheDocument()
    })

    it('shows the loading state when the query is loading', () => {
      mocks.connectionsLoading = true
      renderSection()
      expect(screen.getByTestId('agent-connections-loading')).toBeInTheDocument()
    })

    it('filters list to the current agent only', () => {
      mocks.connections = [
        makeConnection({ id: 'conn_mine', agentId: 'agent-1', botName: 'mine' }),
        makeConnection({ id: 'conn_other', agentId: 'agent-2', botName: 'other' }),
      ]
      renderSection()
      expect(screen.getByTestId('agent-connection-row-conn_mine')).toBeInTheDocument()
      expect(screen.queryByTestId('agent-connection-row-conn_other')).not.toBeInTheDocument()
    })

    it('renders setup-incomplete state for create_app_credentials', () => {
      mocks.connections = [makeConnection({ id: 'conn_a', setupProgress: 'create_app_credentials' })]
      renderSection()
      const row = screen.getByTestId('agent-connection-row-conn_a')
      expect(row).toHaveAttribute('data-connection-state', 'amber')
      expect(screen.getByTestId('agent-connection-row-conn_a-setup')).toHaveTextContent(/setup/i)
    })

    it('renders unhealthy state when connectionHealth is unhealthy', () => {
      mocks.connections = [
        makeConnection({
          id: 'conn_u',
          setupProgress: 'complete',
          connectionHealth: 'unhealthy',
          healthReason: 'invalid_auth',
        }),
      ]
      renderSection()
      const row = screen.getByTestId('agent-connection-row-conn_u')
      expect(row).toHaveAttribute('data-connection-state', 'amber')
    })

    it('renders disabled state for desiredState=disabled', () => {
      mocks.connections = [
        makeConnection({
          id: 'conn_d',
          setupProgress: 'complete',
          connectionHealth: 'healthy',
          desiredState: 'disabled',
        }),
      ]
      renderSection()
      const row = screen.getByTestId('agent-connection-row-conn_d')
      expect(row).toHaveAttribute('data-connection-state', 'muted')
    })

    it('renders ready state for a complete, healthy, enabled connection', () => {
      mocks.connections = [
        makeConnection({
          id: 'conn_r',
          setupProgress: 'complete',
          connectionHealth: 'healthy',
          desiredState: 'enabled',
        }),
      ]
      renderSection()
      const row = screen.getByTestId('agent-connection-row-conn_r')
      expect(row).toHaveAttribute('data-connection-state', 'emerald')
    })

    it('links each row to its connection page', () => {
      mocks.connections = [makeConnection({ id: 'conn_link' })]
      renderSection()
      const link = screen.getByTestId('agent-connection-row-conn_link-link')
      expect(link).toHaveAttribute('href', '/Test/connections/conn_link')
    })
  })

  describe('Add Slack', () => {
    it('calls the create mutation with the agent id', () => {
      renderSection(makeAgent({ id: 'agent-42' }))
      fireEvent.click(screen.getByTestId('agent-connections-add-slack'))
      expect(mocks.createMutateCalls).toHaveLength(1)
      expect(mocks.createMutateCalls[0].data).toEqual({ agentId: 'agent-42' })
    })

    it('navigates to the new connection page on success', async () => {
      renderSection(makeAgent(), { withRoutes: true })
      fireEvent.click(screen.getByTestId('agent-connections-add-slack'))
      await waitFor(() => {
        expect(screen.getByTestId('connection-page')).toBeInTheDocument()
      })
    })
  })

  describe('archived-agent gating', () => {
    it('disables Add Slack and shows the archived notice on archived agents', () => {
      renderSection(makeAgent({ status: 'archived' }))
      expect(screen.getByTestId('agent-connections-add-slack')).toBeDisabled()
      expect(screen.getByTestId('agent-connections-archived-notice')).toBeInTheDocument()
    })
  })
})
