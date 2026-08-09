import { render } from '@testing-library/react'
import { QueryClient, QueryClientProvider, useMutation } from '@tanstack/react-query'
import { MemoryRouter, Routes, Route } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import type {
  AgentSessionLaunchContext,
  AgentSessionLaunchResponse,
  AgentInfo,
  AgentSessionListItemDto,
  AgentStatusDetailResponse,
} from '../../../entities/agent'
import {
  AgentSessionComposerPage,
  type AgentSessionComposerDataHook,
  type AgentSessionComposerPageComponents,
} from '../../agent-session-composer'
import {
  AgentDetailPage,
  type AgentDetailPageComponents,
  type AgentDetailPageDataHook,
} from './AgentDetailPage'
import { makeWorkspace } from '../../../../tests/support/agent-session-composer-test-support'

export const state: {
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
  archiveCalls: [],
  unarchiveCalls: [],
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
  return {
    agents: state.agent ? [state.agent] : [],
    agentsLoading: false,
    availability: [],
    availabilityLoading: false,
    launchMutation,
    repositories: [{ name: 'main', gitUrl: 'file://main', baseBranch: 'main', isDefault: true }],
    workspaces: [makeWorkspace('workspace-1')],
  }
}

export function resetState() {
  state.agent = undefined
  state.agentState = 'loading'
  state.sessions = []
  state.archiveCalls.length = 0
  state.unarchiveCalls.length = 0
  state.detailStatus = undefined
  state.detailStatusLoading = false
}

function createQueryClient() {
  return new QueryClient({ defaultOptions: { queries: { retry: false } } })
}

export function renderPage() {
  const queryClient = createQueryClient()
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId="proj-1" initialProjects={[{
        id: 'proj-1', name: 'Test',
        createdAt: '2026-01-01T00:00:00.000Z', updatedAt: '2026-01-01T00:00:00.000Z',
        repositories: [{ name: 'main', gitUrl: 'file://main', baseBranch: 'main', isDefault: true }],
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

export function renderJourneyPage() {
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

export function makeAgent(overrides: Partial<AgentInfo> = {}): AgentInfo {
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

export function makeSession(overrides: Partial<AgentSessionListItemDto> = {}): AgentSessionListItemDto {
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

export function mockAgent(agent: AgentInfo) {
  state.agent = agent
  state.agentState = 'ready'
}

export function mockAgentError() {
  state.agent = undefined
  state.agentState = 'error'
}

export function mockSessions(sessions: AgentSessionListItemDto[]) {
  state.sessions = sessions
}
