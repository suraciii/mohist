import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { cleanup, render, screen, fireEvent, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider, useMutation } from '@tanstack/react-query'
import { MemoryRouter, Routes, Route, useLocation } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import type {
  AgentInfo,
  AgentSessionLaunchContext,
  AgentSessionLaunchResponse,
} from '../../../entities/agent'
import {
  AgentSessionComposerPage,
  type AgentSessionComposerDataHook,
  type AgentSessionComposerPageComponents,
} from './AgentSessionComposerPage'

const state = {
  agentsData: [] as AgentInfo[],
  launchCalls: [] as Array<{ agentRef: string; body: unknown }>,
  launchError: null as { error: string; code?: string } | null,
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
    { agentRef: string; prompt: string; context?: AgentSessionLaunchContext | null }
  >({
    mutationFn: async ({ agentRef, prompt, context }) => {
      state.launchCalls.push({ agentRef, body: { prompt, context } })
      if (state.launchError) {
        throw Object.assign(new Error(state.launchError.error), { code: state.launchError.code })
      }
      return {
        sessionId: 'sess-123',
        agentId: agentRef,
        agentName: 'Agent 1',
        status: 'running',
        transcriptUrl: '',
      } as AgentSessionLaunchResponse
    },
  })
  return {
    agents: state.agentsData,
    agentsLoading: false,
    launchMutation,
  }
}

function createQueryClient() {
  return new QueryClient({ defaultOptions: { queries: { retry: false } } })
}

function makeAgent(id: string, overrides: Partial<AgentInfo> = {}): AgentInfo {
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

function LocationProbe() {
  const location = useLocation()
  return <div data-testid="current-path">{location.pathname}</div>
}

function renderPage(initialEntries = ['/agent-sessions/new']) {
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
            <Route path="/:projectName/agent-sessions/:sessionId" element={<div>Agent Session</div>} />
          </Routes>
          <LocationProbe />
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

describe('AgentSessionComposerPage', () => {
  beforeEach(() => {
    state.agentsData = []
    state.launchCalls.length = 0
    state.launchError = null
  })

  afterEach(() => {
    cleanup()
  })

  /* ── Query-param parsing and pre-fill ─────────────────── */

  it('reads ?agent= to pre-select an agent', async () => {
    state.agentsData = [makeAgent('agent-1', { name: 'Agent One' })]
    renderPage(['/agent-sessions/new?agent=agent-1'])
    expect(await screen.findByTestId('agent-selector-trigger')).toHaveTextContent('Agent One')
  })

  it('reads ?issue= to pre-fill an issue context ref', async () => {
    state.agentsData = [makeAgent('agent-1')]
    renderPage(['/agent-sessions/new?issue=42'])
    expect(await screen.findByTestId('context-ref-chip-issue')).toHaveTextContent('Issue #42')
  })

  it('reads ?epic= to pre-fill an epic context ref', async () => {
    state.agentsData = [makeAgent('agent-1')]
    renderPage(['/agent-sessions/new?epic=7'])
    expect(await screen.findByTestId('context-ref-chip-epic')).toHaveTextContent('Epic: 7')
  })

  it('reads ?repo= to pre-fill a repo context ref', async () => {
    state.agentsData = [makeAgent('agent-1')]
    renderPage(['/agent-sessions/new?repo=org/repo'])
    expect(await screen.findByTestId('context-ref-chip-repository')).toHaveTextContent('Repository: org/repo')
  })

  it('reads ?ws= to pre-fill a workspace path context ref', async () => {
    state.agentsData = [makeAgent('agent-1')]
    renderPage(['/agent-sessions/new?ws=/home/project'])
    expect(await screen.findByTestId('context-ref-chip-workspace')).toHaveTextContent('Workspace: /home/project')
  })

  it('pre-fills multiple context refs simultaneously', async () => {
    state.agentsData = [makeAgent('agent-1')]
    renderPage(['/agent-sessions/new?issue=7&epic=3&repo=my/repo'])
    await screen.findByTestId('context-ref-chip-repository')
    expect(screen.getByTestId('context-ref-chip-issue')).toHaveTextContent('Issue #7')
    expect(screen.getByTestId('context-ref-chip-epic')).toHaveTextContent('Epic: 3')
    expect(screen.getByTestId('context-ref-chip-repository')).toHaveTextContent('Repository: my/repo')
  })

  /* ── Agent selection ──────────────────────────────────── */

  it('lists agents in the selector dropdown', async () => {
    state.agentsData = [makeAgent('agent-1'), makeAgent('agent-2')]
    renderPage()
    fireEvent.click(await screen.findByTestId('agent-selector-trigger'))
    expect(screen.getByTestId('agent-option-agent-1')).toBeInTheDocument()
    expect(screen.getByTestId('agent-option-agent-2')).toBeInTheDocument()
  })

  it('selects agent from dropdown', async () => {
    state.agentsData = [makeAgent('agent-1')]
    renderPage()
    fireEvent.click(await screen.findByTestId('agent-selector-trigger'))
    fireEvent.click(screen.getByTestId('agent-option-agent-1'))
    expect(screen.getByTestId('agent-selector-trigger')).toHaveTextContent('Agent agent-1')
  })

  /* ── Prompt validation ────────────────────────────────── */

  it('disables launch when prompt is empty', async () => {
    state.agentsData = [makeAgent('agent-1')]
    renderPage(['/agent-sessions/new?agent=agent-1'])
    const button = await screen.findByTestId('launch-button')
    expect(button).toBeDisabled()
  })

  it('shows prompt error when textarea is blurred with empty value', async () => {
    state.agentsData = [makeAgent('agent-1')]
    renderPage(['/agent-sessions/new?agent=agent-1'])
    const textarea = await screen.findByTestId('prompt-textarea')
    fireEvent.focus(textarea)
    fireEvent.blur(textarea)
    expect(screen.getByTestId('prompt-error')).toBeInTheDocument()
    expect(screen.getByTestId('prompt-error')).toHaveTextContent('Prompt is required')
  })

  it('enables launch when prompt is filled and agent selected', async () => {
    state.agentsData = [makeAgent('agent-1')]
    renderPage(['/agent-sessions/new?agent=agent-1'])
    const textarea = await screen.findByTestId('prompt-textarea')
    fireEvent.change(textarea, { target: { value: 'Hello agent' } })
    const button = screen.getByTestId('launch-button')
    expect(button).not.toBeDisabled()
  })

  /* ── Launch call + navigation ─────────────────────────── */

  it('calls mutate with correct args on launch', async () => {
    state.agentsData = [makeAgent('agent-1')]
    renderPage(['/agent-sessions/new?agent=agent-1'])
    const textarea = await screen.findByTestId('prompt-textarea')
    fireEvent.change(textarea, { target: { value: 'Hello agent' } })
    fireEvent.click(screen.getByTestId('launch-button'))
    await waitFor(() => {
      expect(state.launchCalls).toHaveLength(1)
      expect(screen.getByTestId('current-path')).toHaveTextContent('/Test/agent-sessions/sess-123')
    })
    expect(state.launchCalls[0]).toMatchObject({
      agentRef: 'agent-1',
      body: expect.objectContaining({ prompt: 'Hello agent' }),
    })
  })

  it('passes context refs in launch body', async () => {
    state.agentsData = [makeAgent('agent-1')]
    renderPage(['/agent-sessions/new?agent=agent-1&issue=42&epic=7'])
    const textarea = await screen.findByTestId('prompt-textarea')
    fireEvent.change(textarea, { target: { value: 'Analyze this' } })
    fireEvent.click(screen.getByTestId('launch-button'))
    await waitFor(() => {
      expect(state.launchCalls).toHaveLength(1)
      expect(screen.getByTestId('current-path')).toHaveTextContent('/Test/agent-sessions/sess-123')
    })
    expect(state.launchCalls[0]).toMatchObject({
      agentRef: 'agent-1',
      body: { prompt: 'Analyze this', context: { issueNumber: 42, epicNumber: 7 } },
    })
  })

  it('navigates to session detail on success', async () => {
    state.agentsData = [makeAgent('agent-1')]
    renderPage(['/agent-sessions/new?agent=agent-1'])
    const textarea = await screen.findByTestId('prompt-textarea')
    fireEvent.change(textarea, { target: { value: 'Hello' } })
    fireEvent.click(screen.getByTestId('launch-button'))
    await waitFor(() => {
      expect(screen.getByTestId('current-path')).toHaveTextContent('/Test/agent-sessions/sess-123')
    })
  })

  /* ── Context-ref chip remove ──────────────────────────── */

  it('removes context ref chip when X is clicked', async () => {
    state.agentsData = [makeAgent('agent-1')]
    renderPage(['/agent-sessions/new?issue=42'])
    expect(await screen.findByTestId('context-ref-chip-issue')).toBeInTheDocument()
    fireEvent.click(screen.getByTestId('remove-ref-issue'))
    expect(screen.queryByTestId('context-ref-chip-issue')).not.toBeInTheDocument()
  })

  /* ── Archived-agent launch disabling ──────────────────── */

  it('disables launch for archived agents', async () => {
    state.agentsData = [makeAgent('agent-1', { status: 'archived' })]
    renderPage(['/agent-sessions/new?agent=agent-1'])
    await screen.findByTestId('archived-warning')
    expect(screen.getByTestId('archived-warning')).toBeInTheDocument()
    const button = screen.getByTestId('launch-button')
    expect(button).toBeDisabled()
  })

  it('excludes archived agents from the launcher picker', async () => {
    state.agentsData = [
      makeAgent('agent-archived', { name: 'Archived One', status: 'archived' }),
      makeAgent('agent-active', { name: 'Active One', status: 'active' }),
    ]
    renderPage()
    fireEvent.click(await screen.findByTestId('agent-selector-trigger'))
    expect(screen.queryByTestId('agent-option-agent-archived')).not.toBeInTheDocument()
    expect(screen.getByTestId('agent-option-agent-active')).toBeInTheDocument()
  })

  it('shows the archived warning when ?agent= points at an archived agent even though it is not in the picker', async () => {
    state.agentsData = [makeAgent('agent-1', { status: 'archived' })]
    renderPage(['/agent-sessions/new?agent=agent-1'])
    await screen.findByTestId('archived-warning')
    expect(screen.getByTestId('archived-warning')).toBeInTheDocument()
    expect(screen.getByTestId('launch-button')).toBeDisabled()
  })

  /* ── Error states ─────────────────────────────────────── */

  it('surfaces no-available-runner error state', async () => {
    state.agentsData = [makeAgent('agent-1')]
    state.launchError = { error: 'No available runner for selected agent', code: 'NO_AVAILABLE_RUNNER' }
    renderPage(['/agent-sessions/new?agent=agent-1'])
    const textarea = await screen.findByTestId('prompt-textarea')
    fireEvent.change(textarea, { target: { value: 'Hello' } })
    fireEvent.click(screen.getByTestId('launch-button'))
    await waitFor(() => {
      expect(screen.getByTestId('error-no-runner')).toBeInTheDocument()
    })
    expect(screen.getByTestId('error-no-runner')).toHaveTextContent(/no available runner/i)
  })

  it('surfaces external-agent-unavailable error state', async () => {
    state.agentsData = [makeAgent('agent-1')]
    state.launchError = { error: 'External agent is unavailable', code: 'EXTERNAL_AGENT_UNAVAILABLE' }
    renderPage(['/agent-sessions/new?agent=agent-1'])
    const textarea = await screen.findByTestId('prompt-textarea')
    fireEvent.change(textarea, { target: { value: 'Hello' } })
    fireEvent.click(screen.getByTestId('launch-button'))
    await waitFor(() => {
      expect(screen.getByTestId('error-external-agent')).toBeInTheDocument()
    })
    expect(screen.getByTestId('error-external-agent')).toHaveTextContent(/external agent/i)
  })

  it('matches no-runner error by message text fallback', async () => {
    state.agentsData = [makeAgent('agent-1')]
    state.launchError = { error: 'No available runner for opencode' }
    renderPage(['/agent-sessions/new?agent=agent-1'])
    const textarea = await screen.findByTestId('prompt-textarea')
    fireEvent.change(textarea, { target: { value: 'Hello' } })
    fireEvent.click(screen.getByTestId('launch-button'))
    await waitFor(() => {
      expect(screen.getByTestId('error-no-runner')).toBeInTheDocument()
    })
  })

  it('prevents launch when error is present', async () => {
    state.agentsData = [makeAgent('agent-1')]
    state.launchError = { error: 'No available runner' }
    renderPage(['/agent-sessions/new?agent=agent-1'])
    const textarea = await screen.findByTestId('prompt-textarea')
    fireEvent.change(textarea, { target: { value: 'Hello' } })
    fireEvent.click(screen.getByTestId('launch-button'))
    await waitFor(() => {
      expect(screen.getByTestId('error-no-runner')).toBeInTheDocument()
    })
    fireEvent.change(screen.getByTestId('prompt-textarea'), { target: { value: 'Hello' } })
    expect(screen.getByTestId('error-no-runner')).toBeInTheDocument()
  })
})
