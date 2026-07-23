import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider, useMutation } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import { SessionRecoveryActions } from '../../../widgets/coder-session'
import { GenericSessionPage, type GenericSessionPageDependencies } from './GenericSessionPage'

let summary: unknown = null
const compact = vi.fn(async () => ({ id: 'sess-abc', status: 'completed', wasCompacted: true }))
const reset = vi.fn(async () => ({ id: 'sess-abc', status: 'completed', wasCompacted: false }))

const dependencies: GenericSessionPageDependencies = {
  dataSource: {
    useGenericSessionSummary: () => ({ data: summary, isLoading: false, isError: false }) as never,
    useGenericSessionTranscript: () => ({ data: { turns: [] } }) as never,
    useGenericFollowup: () => useMutation({ mutationFn: async () => ({ status: 'sent' }) }) as never,
    useCancelGenericSession: () => useMutation({ mutationFn: async () => ({ state: 'cancelled' }) }) as never,
    useSessionTranscript: () => ({
      turns: [], transcriptVersion: 0, scrollToBottom: vi.fn(), newContentAvailable: false,
      setIsNearBottom: vi.fn(), isFinalizing: false, isThinking: false, isStreaming: false,
    }) as never,
    projectTurn: (turn) => turn as never,
  },
  shellComponents: {
    SessionTranscriptLayout: () => <div data-testid="session-transcript-layout" />,
    SessionFollowupComposer: ({ disabled }: { disabled?: boolean }) => (
      <div data-testid="session-followup-composer" data-disabled={disabled ? 'true' : 'false'} />
    ),
    ContextHealthBar: () => <div data-testid="context-health-bar" />,
    CompactionLineageLink: () => null,
    SessionRecoveryActions: (props) => (
      <SessionRecoveryActions
        {...props}
        genericClients={{ compact, reset }}
      />
    ),
  },
}

function makeSummary(
  activity: 'idle' | 'active' | 'unknown',
  usage: unknown = null,
  overrides: Record<string, unknown> = {},
) {
  return {
    sessionId: 'sess-abc', agentId: 'agent-1', agentName: 'Test Agent',
    runtimeSessionId: 'rt-abc', runtime: 'opencode', activity,
    createdAt: '2026-06-15T10:00:00.000Z', lastActivityAt: '2026-06-15T10:30:00.000Z',
    resolvedModel: 'gpt-4', failureCategory: null, toolCallCount: 0, toolErrorCount: 0,
    contextRefs: null, usage, runtimeSessionLineage: null, ...overrides,
  }
}

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return { client, ...render(
    <QueryClientProvider client={client}>
      <ProjectProvider initialProjectId="proj-1" initialProjects={[{
        id: 'proj-1', name: 'Test', createdAt: '2026-01-01T00:00:00Z',
        updatedAt: '2026-01-01T00:00:00Z', repositories: [],
      }]}>
        <MemoryRouter initialEntries={['/agent-sessions/sess-abc']}>
          <Routes><Route path="/agent-sessions/:sessionId" element={<GenericSessionPage dependencies={dependencies} />} /></Routes>
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  ) }
}

describe('GenericSessionPage recovery', () => {
  beforeEach(() => {
    summary = null
    compact.mockClear()
    reset.mockClear()
  })

  afterEach(() => vi.clearAllMocks())

  it('renders recovery context and controls for the stable session id', async () => {
    summary = makeSummary('idle', { contextWindowUsed: 12000, contextWindowSize: 32000, contextUsagePercent: 37.5, healthStatus: 'green' })
    renderPage()

    await waitFor(() => expect(screen.getByTestId('context-health-bar')).toBeInTheDocument())
    expect(screen.getByTestId('session-recovery-compact')).not.toBeDisabled()
    expect(screen.getByTestId('session-recovery-reset')).not.toBeDisabled()
  })

  it('allows Compact and Reset when the server reports an idle generic session', async () => {
    summary = makeSummary('idle')
    const { client } = renderPage()
    const invalidate = vi.spyOn(client, 'invalidateQueries')

    await waitFor(() => expect(screen.getByTestId('session-recovery-compact')).not.toBeDisabled())
    fireEvent.click(screen.getByTestId('session-recovery-compact'))
    await waitFor(() => expect(compact).toHaveBeenCalledWith('sess-abc', 'proj-1', expect.any(String)))
    await waitFor(() => expect(invalidate).toHaveBeenCalledWith({ queryKey: ['agent-sessions'] }))

    fireEvent.click(screen.getByTestId('session-recovery-reset'))
    fireEvent.click(screen.getByTestId('session-recovery-reset-confirm'))
    await waitFor(() => expect(reset).toHaveBeenCalledWith('sess-abc', 'proj-1', expect.any(String)))
  })

  it('disables recovery when the server reports an active generic session', async () => {
    summary = makeSummary('active')
    renderPage()

    await waitFor(() => expect(screen.getByTestId('session-recovery-compact')).toBeDisabled())
    expect(screen.getByTestId('session-recovery-reset')).toBeDisabled()
    fireEvent.click(screen.getByTestId('session-recovery-compact'))
    fireEvent.click(screen.getByTestId('session-recovery-reset'))
    expect(compact).not.toHaveBeenCalled()
    expect(reset).not.toHaveBeenCalled()
  })

  it('keeps Reset available when a runtime binding is missing and followup is disabled', async () => {
    // Issue 484: with no runtime binding canFollowup is false, so the
    // composer renders disabled (not absent). Recovery depends only on
    // activity, so Reset stays enabled while activity is idle.
    summary = makeSummary('idle', null, { runtimeSessionId: null, runtime: null })
    renderPage()

    await waitFor(() => expect(screen.getByTestId('session-recovery-reset')).not.toBeDisabled())
    expect(screen.getByTestId('session-followup-composer')).toHaveAttribute('data-disabled', 'true')
    expect(screen.queryByTestId('session-cancel-trigger')).not.toBeInTheDocument()
  })
})
