import { QueryClient, QueryClientProvider, useMutation } from '@tanstack/react-query'
import { useState } from 'react'
import { render } from '@testing-library/react'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom'
import { ProjectProvider } from '../../src/entities/project'
import type {
  AgentAvailabilitySummaryEntry,
  AgentInfo,
  AgentSessionLaunchContext,
  AgentSessionLaunchResponse,
} from '../../src/entities/agent'
import type { IssueListItem } from '../../src/entities/issue'
import type { EpicWithProgress } from '../../src/entities/epic'
import type { Repository } from '../../src/entities/project'
import type { Workspace } from '../../src/entities/workspace'
import {
  AgentSessionComposerPage,
  type AgentSessionComposerDataHook,
  type AgentSessionComposerPageComponents,
} from '../../src/pages/agent-session-composer/ui/AgentSessionComposerPage'

export const state = {
  agentsData: [] as AgentInfo[],
  availabilityData: [] as AgentAvailabilitySummaryEntry[],
  launchCalls: [] as Array<{ agentRef: string; body: unknown; idempotencyKey?: string }>,
  launchError: null as { error: string; code?: string } | null,
  launchFailuresRemaining: -1,
  launchResponse: null as Partial<AgentSessionLaunchResponse> | null,
  repositoriesData: [] as Repository[],
  workspacesData: [] as Workspace[],
  issuesData: [] as IssueListItem[],
  epicsData: [] as EpicWithProgress[],
  repositoriesError: false,
  repositoryRetryCalls: 0,
  workspacesError: false,
  workspaceRetryCalls: 0,
}

const components: AgentSessionComposerPageComponents = {
  AttachmentComposer: ({ value, onChange, onBlur, placeholder }) => (
    <textarea
      data-testid="prompt-textarea"
      value={value}
      onChange={(event) => onChange(event.target.value)}
      onBlur={onBlur}
      placeholder={placeholder}
    />
  ),
}

const dataHook: AgentSessionComposerDataHook = () => {
  const [, setRepositoryRetryVersion] = useState(0)
  const [, setWorkspaceRetryVersion] = useState(0)
  const launchMutation = useMutation<
    AgentSessionLaunchResponse,
    Error,
    { agentRef: string; prompt: string; context?: AgentSessionLaunchContext | null; attachments?: string[]; idempotencyKey?: string }
  >({
    mutationFn: async ({ agentRef, prompt, context, attachments, idempotencyKey }) => {
      state.launchCalls.push({ agentRef, body: { prompt, context, attachments }, idempotencyKey })
      if (state.launchError && (state.launchFailuresRemaining < 0 || state.launchFailuresRemaining-- > 0)) {
        throw Object.assign(new Error(state.launchError.error), { code: state.launchError.code })
      }
      return {
        sessionId: 'sess-123',
        agentId: agentRef,
        agentName: 'Agent 1',
        status: 'running',
        transcriptUrl: '',
        sessionUrl: '/Test/sessions/sess-123',
        ...state.launchResponse,
      } as AgentSessionLaunchResponse
    },
  })
  return {
    agents: state.agentsData,
    agentsLoading: false,
    availability: state.availabilityData,
    availabilityLoading: false,
    launchMutation,
    repositories: state.repositoriesData,
    repositoriesError: state.repositoriesError,
    retryRepositories: () => {
      state.repositoryRetryCalls += 1
      state.repositoriesError = false
      setRepositoryRetryVersion((version) => version + 1)
    },
    workspaces: state.workspacesData,
    workspacesError: state.workspacesError,
    retryWorkspaces: () => {
      state.workspaceRetryCalls += 1
      state.workspacesError = false
      setWorkspaceRetryVersion((version) => version + 1)
    },
    issues: state.issuesData,
    epics: state.epicsData,
    contextLoading: false,
  }
}

function createQueryClient() {
  return new QueryClient({ defaultOptions: { queries: { retry: false } } })
}

export function makeAgent(id: string, overrides: Partial<AgentInfo> = {}): AgentInfo {
  return {
    id,
    projectId: 'proj-1',
    name: `Agent ${id}`,
    description: '',
    instructions: '',
    agentConfig: null,
    skills: [],
    maxConcurrentRuns: null,
    status: 'active',
    createdAt: '2026-01-01T00:00:00.000Z',
    updatedAt: '2026-01-01T00:00:00.000Z',
    ...overrides,
  }
}

export function makeWorkspace(name: string, repositories: string[] = ['main']): Workspace {
  return {
    projectId: 'proj-1',
    name,
    origin: { kind: 'manual' },
    repositories,
    status: 'active',
    home: null,
    createdAt: '2026-01-01T00:00:00.000Z',
    boundSessionCount: 0,
  }
}

export function resetState() {
  state.agentsData = []
  state.availabilityData = []
  state.launchCalls.length = 0
  state.launchError = null
  state.launchFailuresRemaining = -1
  state.launchResponse = null
  state.repositoriesData = []
  state.repositoriesError = false
  state.repositoryRetryCalls = 0
  state.workspacesError = false
  state.workspaceRetryCalls = 0
  state.workspacesData = [makeWorkspace('workspace-1')]
  state.issuesData = []
  state.epicsData = []
}

function LocationProbe() {
  const location = useLocation()
  return <div data-testid="current-path">{location.pathname}</div>
}

export function renderPage(initialEntries = ['/agent-sessions/new']) {
  const queryClient = createQueryClient()
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider
        initialProjectId="proj-1"
        initialProjects={[{
          id: 'proj-1', name: 'Test',
          createdAt: '2026-01-01T00:00:00.000Z', updatedAt: '2026-01-01T00:00:00.000Z',
          repositories: [],
        }]}
      >
        <MemoryRouter initialEntries={initialEntries}>
          <Routes>
            <Route
              path="/agent-sessions/new"
              element={<AgentSessionComposerPage components={components} dataHook={dataHook} />}
            />
            <Route path="/:projectName/sessions/:sessionId" element={<div>Agent Session</div>} />
          </Routes>
          <LocationProbe />
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>
  )
}
