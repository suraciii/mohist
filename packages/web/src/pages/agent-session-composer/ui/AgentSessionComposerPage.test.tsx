// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen, fireEvent } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Routes, Route, useLocation } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import type { AgentInfo, AgentSessionLaunchResponse } from '../../../entities/agent'
import { AgentSessionComposerPage } from './AgentSessionComposerPage'

const mocks = vi.hoisted(() => ({
  agents: [] as AgentInfo[],
  agentsLoading: false,
  launchError: null as Error | null,
  launchIsPending: false,
  launchMutateArgs: null as { args: unknown; onSuccess?: (data: AgentSessionLaunchResponse) => void; onError?: (err: Error) => void } | null,
  launchMutate: vi.fn().mockImplementation(function (
    this: unknown,
    args: unknown,
    options?: { onSuccess?: (data: AgentSessionLaunchResponse) => void; onError?: (err: Error) => void },
  ) {
    mocks.launchMutateArgs = { args, ...options }
  }),
  toProjectPath: vi.fn((path: string) => `/Test${path}`),
}))

vi.mock('../../../entities/agent', () => ({
  useAgents: () => ({
    data: mocks.agents,
    isLoading: mocks.agentsLoading,
  }),
  useLaunchAgentSession: () => ({
    mutate: mocks.launchMutate,
    isPending: mocks.launchIsPending,
    error: mocks.launchError,
  }),
}))


vi.mock('../../../shared/ui/attachment-composer', () => ({
  AttachmentComposer: ({ value, onChange, ...props }: { value: string; onChange: (v: string) => void; [key: string]: unknown }) =>
    <textarea data-testid="prompt-textarea" value={value} onChange={(e) => onChange(e.target.value)} {...props} />,
}))

vi.mock('../../../entities/project', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/project')>()
  return {
    ...actual,
    useProjectPath: () => mocks.toProjectPath,
  }
})

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
            <Route path="/agent-sessions/new" element={<AgentSessionComposerPage />} />
          </Routes>
          <LocationProbe />
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

describe('AgentSessionComposerPage', () => {
  beforeEach(() => {
    mocks.agents = []
    mocks.agentsLoading = false
    mocks.launchError = null
    mocks.launchIsPending = false
    mocks.launchMutateArgs = null
    mocks.launchMutate.mockClear()
    mocks.toProjectPath.mockClear()
  })

  afterEach(() => {
    cleanup()
  })

  /* ── Query-param parsing and pre-fill ─────────────────── */

  it('reads ?agent= to pre-select an agent', () => {
    mocks.agents = [makeAgent('agent-1', { name: 'Agent One' })]
    renderPage(['/agent-sessions/new?agent=agent-1'])
    expect(screen.getByTestId('agent-session-composer-page')).toBeInTheDocument()
    expect(screen.getByTestId('agent-selector-trigger')).toHaveTextContent('Agent One')
  })

  it('reads ?issue= to pre-fill an issue context ref', () => {
    mocks.agents = [makeAgent('agent-1')]
    renderPage(['/agent-sessions/new?issue=42'])
    expect(screen.getByTestId('context-ref-chip-issue')).toHaveTextContent('Issue #42')
  })

  it('reads ?epic= to pre-fill an epic context ref', () => {
    mocks.agents = [makeAgent('agent-1')]
    renderPage(['/agent-sessions/new?epic=epic-1'])
    expect(screen.getByTestId('context-ref-chip-epic')).toHaveTextContent('Epic: epic-1')
  })

  it('reads ?repo= to pre-fill a repo context ref', () => {
    mocks.agents = [makeAgent('agent-1')]
    renderPage(['/agent-sessions/new?repo=org/repo'])
    expect(screen.getByTestId('context-ref-chip-repository')).toHaveTextContent('Repository: org/repo')
  })

  it('reads ?ws= to pre-fill a workspace path context ref', () => {
    mocks.agents = [makeAgent('agent-1')]
    renderPage(['/agent-sessions/new?ws=/home/project'])
    expect(screen.getByTestId('context-ref-chip-workspace')).toHaveTextContent('Workspace: /home/project')
  })

  it('pre-fills multiple context refs simultaneously', () => {
    mocks.agents = [makeAgent('agent-1')]
    renderPage(['/agent-sessions/new?issue=7&epic=epic-3&repo=my/repo'])
    expect(screen.getByTestId('context-ref-chip-issue')).toHaveTextContent('Issue #7')
    expect(screen.getByTestId('context-ref-chip-epic')).toHaveTextContent('Epic: epic-3')
    expect(screen.getByTestId('context-ref-chip-repository')).toHaveTextContent('Repository: my/repo')
  })

  /* ── Agent selection ──────────────────────────────────── */

  it('lists agents in the selector dropdown', () => {
    mocks.agents = [makeAgent('agent-1'), makeAgent('agent-2')]
    renderPage()
    fireEvent.click(screen.getByTestId('agent-selector-trigger'))
    expect(screen.getByTestId('agent-option-agent-1')).toBeInTheDocument()
    expect(screen.getByTestId('agent-option-agent-2')).toBeInTheDocument()
  })

  it('selects agent from dropdown', () => {
    mocks.agents = [makeAgent('agent-1')]
    renderPage()
    fireEvent.click(screen.getByTestId('agent-selector-trigger'))
    fireEvent.click(screen.getByTestId('agent-option-agent-1'))
    expect(screen.getByTestId('agent-selector-trigger')).toHaveTextContent('Agent agent-1')
  })

  /* ── Prompt validation ────────────────────────────────── */

  it('disables launch when prompt is empty', () => {
    mocks.agents = [makeAgent('agent-1')]
    renderPage(['/agent-sessions/new?agent=agent-1'])
    const button = screen.getByTestId('launch-button')
    expect(button).toBeDisabled()
  })

  it('shows prompt error when textarea is blurred with empty value', () => {
    mocks.agents = [makeAgent('agent-1')]
    renderPage(['/agent-sessions/new?agent=agent-1'])
    const textarea = screen.getByTestId('prompt-textarea')
    fireEvent.focus(textarea)
    fireEvent.blur(textarea)
    expect(screen.getByTestId('prompt-error')).toBeInTheDocument()
    expect(screen.getByTestId('prompt-error')).toHaveTextContent('Prompt is required')
  })

  it('enables launch when prompt is filled and agent selected', () => {
    mocks.agents = [makeAgent('agent-1')]
    renderPage(['/agent-sessions/new?agent=agent-1'])
    const textarea = screen.getByTestId('prompt-textarea')
    fireEvent.change(textarea, { target: { value: 'Hello agent' } })
    const button = screen.getByTestId('launch-button')
    expect(button).not.toBeDisabled()
  })

  /* ── Launch call + navigation ─────────────────────────── */

  it('calls mutate with correct args on launch', () => {
    mocks.agents = [makeAgent('agent-1')]
    renderPage(['/agent-sessions/new?agent=agent-1'])
    const textarea = screen.getByTestId('prompt-textarea')
    fireEvent.change(textarea, { target: { value: 'Hello agent' } })
    fireEvent.click(screen.getByTestId('launch-button'))
    expect(mocks.launchMutate).toHaveBeenCalled()
    expect(mocks.launchMutateArgs).not.toBeNull()
    const { args } = mocks.launchMutateArgs!
    expect(args).toMatchObject({
      agentRef: 'agent-1',
      prompt: 'Hello agent',
    })
  })

  it('passes context refs in launch body', () => {
    mocks.agents = [makeAgent('agent-1')]
    renderPage(['/agent-sessions/new?agent=agent-1&issue=42&epic=epic-1'])
    const textarea = screen.getByTestId('prompt-textarea')
    fireEvent.change(textarea, { target: { value: 'Analyze this' } })
    fireEvent.click(screen.getByTestId('launch-button'))
    const { args } = mocks.launchMutateArgs!
    expect(args).toMatchObject({
      agentRef: 'agent-1',
      prompt: 'Analyze this',
      context: { issueNumber: 42, epicNumber: 'epic-1' },
    })
  })

  it('navigates to session detail on success', () => {
    mocks.agents = [makeAgent('agent-1')]
    renderPage(['/agent-sessions/new?agent=agent-1'])
    const textarea = screen.getByTestId('prompt-textarea')
    fireEvent.change(textarea, { target: { value: 'Hello' } })
    mocks.launchMutate.mockImplementation(function (
      _args: unknown,
      options?: { onSuccess?: (data: AgentSessionLaunchResponse) => void },
    ) {
      options?.onSuccess?.({ sessionId: 'sess-123', agentId: 'agent-1', agentName: 'Agent 1', status: 'running', transcriptUrl: '' })
    })
    fireEvent.click(screen.getByTestId('launch-button'))
    expect(mocks.toProjectPath).toHaveBeenCalledWith('/agent-sessions/sess-123')
    expect(screen.getByTestId('current-path')).toHaveTextContent('/Test/agent-sessions/sess-123')
  })

  /* ── Context-ref chip remove ──────────────────────────── */

  it('removes context ref chip when X is clicked', () => {
    mocks.agents = [makeAgent('agent-1')]
    renderPage(['/agent-sessions/new?issue=42'])
    expect(screen.getByTestId('context-ref-chip-issue')).toBeInTheDocument()
    fireEvent.click(screen.getByTestId('remove-ref-issue'))
    expect(screen.queryByTestId('context-ref-chip-issue')).not.toBeInTheDocument()
  })

  /* ── Archived-agent launch disabling ──────────────────── */

  it('disables launch for archived agents', () => {
    mocks.agents = [makeAgent('agent-1', { status: 'archived' })]
    renderPage(['/agent-sessions/new?agent=agent-1'])
    expect(screen.getByTestId('archived-warning')).toBeInTheDocument()
    const button = screen.getByTestId('launch-button')
    expect(button).toBeDisabled()
  })

  it('excludes archived agents from the launcher picker', () => {
    mocks.agents = [
      makeAgent('agent-archived', { name: 'Archived One', status: 'archived' }),
      makeAgent('agent-active', { name: 'Active One', status: 'active' }),
    ]
    renderPage()
    fireEvent.click(screen.getByTestId('agent-selector-trigger'))
    expect(screen.queryByTestId('agent-option-agent-archived')).not.toBeInTheDocument()
    expect(screen.getByTestId('agent-option-agent-active')).toBeInTheDocument()
  })

  it('shows the archived warning when ?agent= points at an archived agent even though it is not in the picker', () => {
    mocks.agents = [makeAgent('agent-1', { status: 'archived' })]
    renderPage(['/agent-sessions/new?agent=agent-1'])
    expect(screen.getByTestId('archived-warning')).toBeInTheDocument()
    expect(screen.getByTestId('launch-button')).toBeDisabled()
  })

  /* ── Error states ─────────────────────────────────────── */

  it('surfaces no-available-runner error state', () => {
    mocks.agents = [makeAgent('agent-1')]
    mocks.launchError = Object.assign(new Error('No available runner for selected agent'), { code: 'NO_AVAILABLE_RUNNER' })
    renderPage(['/agent-sessions/new?agent=agent-1'])
    expect(screen.getByTestId('error-no-runner')).toBeInTheDocument()
    expect(screen.getByTestId('error-no-runner')).toHaveTextContent(/no available runner/i)
  })

  it('surfaces external-agent-unavailable error state', () => {
    mocks.agents = [makeAgent('agent-1')]
    mocks.launchError = Object.assign(new Error('External agent is unavailable'), { code: 'EXTERNAL_AGENT_UNAVAILABLE' })
    renderPage(['/agent-sessions/new?agent=agent-1'])
    expect(screen.getByTestId('error-external-agent')).toBeInTheDocument()
    expect(screen.getByTestId('error-external-agent')).toHaveTextContent(/external agent/i)
  })

  it('matches no-runner error by message text fallback', () => {
    mocks.agents = [makeAgent('agent-1')]
    mocks.launchError = new Error('No available runner for opencode')
    renderPage(['/agent-sessions/new?agent=agent-1'])
    expect(screen.getByTestId('error-no-runner')).toBeInTheDocument()
  })

  it('prevents launch when error is present', () => {
    mocks.agents = [makeAgent('agent-1')]
    mocks.launchError = new Error('No available runner')
    renderPage(['/agent-sessions/new?agent=agent-1'])
    const textarea = screen.getByTestId('prompt-textarea')
    fireEvent.change(textarea, { target: { value: 'Hello' } })
    expect(screen.getByTestId('error-no-runner')).toBeInTheDocument()
  })
})
