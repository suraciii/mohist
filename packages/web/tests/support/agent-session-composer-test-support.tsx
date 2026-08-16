import { QueryClient, QueryClientProvider, useMutation } from '@tanstack/react-query'
import { render } from '@testing-library/react'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom'
import { ProjectProvider } from '../../src/entities/project'
import type { ProjectDefaultExecutionConfig } from '../../src/entities/project'
import type {
  AgentAvailabilitySummaryEntry,
  AgentInfo,
  AgentSessionLaunchContext,
  AgentSessionLaunchResponse,
  AgentTaskLaunchInput,
  AgentTaskPreflightResponse,
} from '../../src/entities/agent'
import {
  AgentSessionComposerPage,
  type AgentSessionComposerDataHook,
  type AgentSessionComposerPageComponents,
} from '../../src/pages/agent-session-composer/ui/AgentSessionComposerPage'

export const state = {
  agentsData: [] as AgentInfo[],
  availabilityData: [] as AgentAvailabilitySummaryEntry[],
  launchCalls: [] as Array<{ agentRef: string; body: unknown; idempotencyKey?: string }>,
  taskCalls: [] as Array<{ body: AgentTaskLaunchInput; idempotencyKey?: string }>,
  preflightCalls: [] as Array<{ body: AgentTaskLaunchInput; idempotencyKey?: string }>,
  enablePreflight: false,
  launchError: null as { error: string; code?: string } | null,
  launchFailuresRemaining: -1,
  launchResponse: null as Partial<AgentSessionLaunchResponse> | null,
  defaultExecutionConfig: {
    runtime: 'opencode' as const,
    model: 'openai/gpt-4o',
    variant: null,
  } as ProjectDefaultExecutionConfig | null,
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
  const launchMutation = useMutation<
    AgentSessionLaunchResponse,
    Error,
    {
      agentRef: string
      prompt: string
      context?: AgentSessionLaunchContext | null
      attachments?: string[]
      idempotencyKey?: string
    }
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
  const preflightTaskMutation = useMutation<
    AgentTaskPreflightResponse,
    Error,
    AgentTaskLaunchInput & { idempotencyKey: string }
  >({
    mutationFn: async ({ idempotencyKey, ...input }) => {
      state.preflightCalls.push({ body: input, idempotencyKey })
      return {
        scopeFingerprint: 'scope-test',
        agentName: 'Task Agent',
        execution: { runtime: 'pi', model: 'provider/model', variant: 'balanced' },
        repository: 'org/repo',
        workspace: 'review-workspace',
        workspaceRepositories: ['org/repo'],
        issueNumber: 42,
        epicNumber: null,
        permissionScope: 'project-workspace-write',
        expectedImpact: 'Creates one Agent and starts one AgentJob and AgentSession.',
      }
    },
  })
  const startTaskMutation = useMutation<
    AgentSessionLaunchResponse,
    Error,
    AgentTaskLaunchInput & { idempotencyKey?: string }
  >({
    mutationFn: async ({ idempotencyKey, preflightFingerprint: _ignored, ...input }) => {
      state.taskCalls.push({ body: input, idempotencyKey })
      if (state.launchError && (state.launchFailuresRemaining < 0 || state.launchFailuresRemaining-- > 0)) {
        throw Object.assign(new Error(state.launchError.error), { code: state.launchError.code })
      }
      return {
        sessionId: 'sess-123',
        agentId: 'agent-task-1',
        agentName: 'Task Agent',
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
    preflightTaskMutation: state.enablePreflight ? preflightTaskMutation : undefined,
    startTaskMutation,
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
    purpose: null,
    description: '',
    instructions: '',
    agentConfig: null,
    skills: [],
    permissions: [],
    maxConcurrentRuns: null,
    status: 'active',
    createdAt: '2026-01-01T00:00:00.000Z',
    updatedAt: '2026-01-01T00:00:00.000Z',
    ...overrides,
  }
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
        initialProjects={[
          {
            id: 'proj-1',
            name: 'Test',
            createdAt: '2026-01-01T00:00:00.000Z',
            updatedAt: '2026-01-01T00:00:00.000Z',
            repositories: [],
            defaultExecutionConfig: state.defaultExecutionConfig,
          },
        ]}
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
    </QueryClientProvider>,
  )
}
