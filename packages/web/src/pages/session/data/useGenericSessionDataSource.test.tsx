import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Routes, Route } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import { GenericSessionPage, type GenericSessionPageDependencies } from '../ui/GenericSessionPage'

let _summaryData: unknown = null
let _summaryLoading = false
let _summaryError = false
let _transcriptData: unknown = null
let _unfilteredTranscriptData: unknown = null
const unfilteredTranscriptCalls: string[] = []

const capturedTranscriptOptions: Array<{
  sessionId: string
  runtimeSessionId: string
  runtime: string | null | undefined
  isHistoricalRuntimeView: boolean | undefined
  isRunning: boolean
}> = []

function resetCapturedOptions() {
  capturedTranscriptOptions.length = 0
  unfilteredTranscriptCalls.length = 0
}

function baseSummary(overrides: Record<string, unknown> = {}) {
  return {
    sessionId: 'sess-abc', agentId: 'agent-1', agentName: 'Test Agent',
    runtimeSessionId: 'rt-abc', runtime: 'opencode',
    // Issue 484: summaries carry an `activity` (idle/active/unknown) instead
    // of a `status`. Default to active so the session reads as live.
    activity: 'active',
    createdAt: '2026-06-15T10:00:00.000Z', lastActivityAt: '2026-06-15T10:30:00.000Z',
    resolvedModel: 'gpt-4', failureCategory: null,
    toolCallCount: 5, toolErrorCount: 0, contextRefs: null, usage: null,
    ...overrides,
  }
}

const mocks = {
  transcriptReturn: {
    turns: [] as any[],
    transcriptVersion: 0,
    scrollToBottom: vi.fn(),
    newContentAvailable: false,
    setIsNearBottom: vi.fn(),
    isFinalizing: false,
    isThinking: false,
    isStreaming: false,
  },
}

function makeDeps(): GenericSessionPageDependencies {
  return {
    dataSource: {
      useSessionTranscript: ((options: any) => {
        capturedTranscriptOptions.push({
          sessionId: options.sessionId,
          runtimeSessionId: options.runtimeSessionId,
          runtime: options.runtime,
          isHistoricalRuntimeView: options.isHistoricalRuntimeView,
          isRunning: options.isRunning,
        })
        return mocks.transcriptReturn
      }) as any,
      projectTurn: (turn: any) => turn,
      useGenericSessionSummary: () => ({
        data: _summaryData,
        isLoading: _summaryLoading,
        isError: _summaryError,
      }) as never,
      useGenericSessionTranscript: () => ({ data: _transcriptData }) as never,
      getGenericSessionTranscript: async (_projectId: string, sessionId: string, runtimeSessionId?: string | null) => {
        if (runtimeSessionId) return _transcriptData as never
        unfilteredTranscriptCalls.push(sessionId)
        return (_unfilteredTranscriptData ?? _transcriptData) as never
      },
      useGenericFollowup: () => ({ mutateAsync: vi.fn(), isPending: false }) as never,
      useCancelGenericSession: () => ({ mutate: vi.fn(), isPending: false }) as never,
    },
    shellComponents: {
      SessionTranscriptLayout: () => <></>,
      SessionRecoveryActions: () => <></>,
      SessionFollowupComposer: () => <></>,
    },
  }
}

const queryClients: QueryClient[] = []

function createQueryClient() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 }, mutations: { retry: false } },
  })
  queryClients.push(queryClient)
  return queryClient
}

async function renderPage(initialEntry = '/agent-sessions/sess-abc') {
  const queryClient = createQueryClient()
  const deps = makeDeps()
  return {
    deps,
    ...render(
      <QueryClientProvider client={queryClient}>
        <ProjectProvider initialProjectId="proj-1" initialProjects={[{
          id: 'proj-1', name: 'Test', createdAt: '2026-01-01T00:00:00Z',
          updatedAt: '2026-01-01T00:00:00Z', repositories: [],
        }]}>
          <MemoryRouter initialEntries={[initialEntry]}>
            <Routes>
              <Route path="/agent-sessions/:sessionId" element={<GenericSessionPage dependencies={deps} />} />
            </Routes>
          </MemoryRouter>
        </ProjectProvider>
      </QueryClientProvider>,
    ),
  }
}

describe('useGenericSessionDataSource — transcript identity wiring', () => {
  beforeEach(() => {
    _summaryData = baseSummary()
    _summaryLoading = false
    _summaryError = false
    _transcriptData = null
    _unfilteredTranscriptData = null
    resetCapturedOptions()
  })

  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  it('passes route sessionId as canonical sessionId', async () => {
    renderPage('/agent-sessions/sess-abc')

    await waitFor(() => {
      expect(capturedTranscriptOptions.length).toBeGreaterThan(0)
    })

    const last = capturedTranscriptOptions[capturedTranscriptOptions.length - 1]
    expect(last.sessionId).toBe('sess-abc')
  })

  // Issue 484: `isRunning` is derived from `activity === 'active'`. While the
  // summary is still loading there is no activity to read, so `isRunning`
  // resolves to false (the generic source has no session-list fallback). The
  // session is not considered running until activity resolves to active.
  it('reports isRunning=false during summary loading (activity unresolved)', async () => {
    _summaryData = null
    _summaryLoading = true

    renderPage('/agent-sessions/sess-abc')

    await waitFor(() => {
      expect(capturedTranscriptOptions.length).toBeGreaterThan(0)
    })

    expect(capturedTranscriptOptions[0].isRunning).toBe(false)
  })

  it('sets isRunning to false after summary loads with idle activity', async () => {
    _summaryData = baseSummary({ activity: 'idle' })

    renderPage('/agent-sessions/sess-abc')

    await waitFor(() => {
      const hasFinal = capturedTranscriptOptions.some((o) => o.isRunning === false)
      expect(hasFinal).toBe(true)
    })
  })

  // Issue 484: the historical-runtime-view (?rt=) affordance was removed
  // from this data source. It no longer forwards an `isHistoricalRuntimeView`
  // flag (always undefined) and no longer remaps the runtime session id from
  // the query string. These cases assert the new (non-)behaviour.
  it('does not forward isHistoricalRuntimeView even when ?rt= is present', async () => {
    renderPage('/agent-sessions/sess-abc?rt=rt-old')

    await waitFor(() => {
      expect(capturedTranscriptOptions.length).toBeGreaterThan(0)
    })

    const last = capturedTranscriptOptions[capturedTranscriptOptions.length - 1]
    expect(last.isHistoricalRuntimeView).toBeUndefined()
    // The summary-reported runtime session id wins; ?rt= no longer overrides.
    expect(last.runtimeSessionId).toBe('rt-abc')
  })

  it('does not forward isHistoricalRuntimeView when no ?rt= is present', async () => {
    renderPage('/agent-sessions/sess-abc')

    await waitFor(() => {
      expect(capturedTranscriptOptions.length).toBeGreaterThan(0)
    })

    const last = capturedTranscriptOptions[capturedTranscriptOptions.length - 1]
    expect(last.isHistoricalRuntimeView).toBeUndefined()
  })

  it('passes runtime from summary as runtime', async () => {
    _summaryData = baseSummary({ runtime: 'claude' })

    renderPage('/agent-sessions/sess-abc')

    await waitFor(() => {
      expect(capturedTranscriptOptions.length).toBeGreaterThan(0)
    })

    const last = capturedTranscriptOptions[capturedTranscriptOptions.length - 1]
    expect(last.runtime).toBe('claude')
  })

  // Issue 484: the `runtime-filtered` empty-state branch (and the
  // `?rt=`-driven historical-runtime-lineage diagnostics that produced it —
  // the "first eligible lineage runtime" history link, the unfiltered
  // transcript read, and the hidden-only-mismatch distinction) were removed
  // from this data source. The transcript is now scoped solely by the
  // summary-reported runtime session id. The two former cases below had no
  // equivalent under the activity model and were deleted intentionally:
  //   - "diagnoses an empty runtime view from visible content in the first
  //      eligible lineage runtime" (asserted runtime-filtered + history link)
  //   - "does not diagnose a runtime mismatch for hidden-only historical
  //      content" (asserted running-no-content fallback)

  it('does not issue an unfiltered read for an unfiltered empty view', async () => {
    _summaryData = baseSummary()
    _transcriptData = { turns: [], partCount: 0, lastActivityAt: null }

    renderPage('/agent-sessions/sess-abc')

    await screen.findByTestId('session-empty-state')
    expect(unfilteredTranscriptCalls).toEqual([])
  })
})
