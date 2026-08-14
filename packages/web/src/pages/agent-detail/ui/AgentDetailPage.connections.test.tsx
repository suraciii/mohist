import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider, useMutation } from '@tanstack/react-query'
import { MemoryRouter, Routes, Route } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import type { AgentInfo, AgentHistoryItemDto, AgentStatusDetailResponse } from '../../../entities/agent'
import {
  AgentDetailPage,
  type AgentDetailPageComponents,
  type AgentDetailPageDataHook,
} from './AgentDetailPage'

const state: {
  agent: AgentInfo | undefined
  agentState: 'loading' | 'ready' | 'error'
  sessions: AgentHistoryItemDto[]
  detailStatus: AgentStatusDetailResponse | undefined
  detailStatusLoading: boolean
} = {
  agent: undefined,
  agentState: 'loading',
  sessions: [],
  detailStatus: undefined,
  detailStatusLoading: false,
}

const components: AgentDetailPageComponents = {
  AgentProfileEditor: () => null,
  SubscriptionsSection: () => null,
  ConnectionsSection: ({ agent }) => (
    <div
      data-testid="agent-connections-section"
      data-agent-id={agent.id}
      data-agent-status={agent.status}
    />
  ),
}

const dataHook: AgentDetailPageDataHook = () => {
  const archiveAgent = useMutation<AgentInfo, Error, string>({ mutationFn: async (id) => ({ ...state.agent!, id, status: 'archived' }) as AgentInfo })
  const unarchiveAgent = useMutation<AgentInfo, Error, string>({ mutationFn: async (id) => ({ ...state.agent!, id, status: 'active' }) as AgentInfo })

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

function makeAgent(overrides: Partial<AgentInfo> = {}): AgentInfo {
  return {
    id: 'agent-1',
    projectId: 'proj-1',
    name: 'Test Agent',
    description: 'A test agent',
    instructions: '...',
    agentConfig: null,
    skills: [],
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

function renderPage() {
  const queryClient = createQueryClient()
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId="proj-1" initialProjects={[{
        id: 'proj-1', name: 'Test',
        createdAt: '2026-01-01T00:00:00.000Z', updatedAt: '2026-01-01T00:00:00.000Z',
        repositories: [],
      }]}>
        <MemoryRouter initialEntries={['/Test/agents/agent-1']}>
          <Routes>
            <Route
              path="/:projectName/agents/:agentId"
              element={<AgentDetailPage components={components} dataHook={dataHook} />}
            />
          </Routes>
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

describe('AgentDetailPage Connections section wiring', () => {
  beforeEach(() => {
    state.agent = undefined
    state.agentState = 'loading'
    state.sessions = []
    state.detailStatus = undefined
    state.detailStatusLoading = false
  })

  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  it('mounts the ConnectionsSection for an active agent with its own data-agent-id', async () => {
    state.agent = makeAgent({ id: 'agent-42', status: 'active' })
    state.agentState = 'ready'
    renderPage()
    const section = await screen.findByTestId('agent-connections-section')
    expect(section).toBeInTheDocument()
    expect(section).toHaveAttribute('data-agent-id', 'agent-42')
    expect(section).toHaveAttribute('data-agent-status', 'active')
  })

  it('mounts the ConnectionsSection for an archived agent and forwards the archived status', async () => {
    state.agent = makeAgent({ status: 'archived' })
    state.agentState = 'ready'
    renderPage()
    const section = await screen.findByTestId('agent-connections-section')
    expect(section).toBeInTheDocument()
    expect(section).toHaveAttribute('data-agent-status', 'archived')
  })
})
