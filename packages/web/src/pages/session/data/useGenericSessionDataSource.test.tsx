import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Routes, Route } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import { GenericSessionPage, type GenericSessionPageDependencies } from '../ui/GenericSessionPage'

let _summaryData: unknown = null
let _summaryLoading = false
let _summaryError = false
let _transcriptData: unknown = null

const capturedTranscriptOptions: Array<{
  sessionId: string
  runtimeSessionId: string
  runtime: string | null | undefined
  isHistoricalRuntimeView: boolean | undefined
  isRunning: boolean
}> = []

function resetCapturedOptions() {
  capturedTranscriptOptions.length = 0
}

function baseSummary(overrides: Record<string, unknown> = {}) {
  return {
    sessionId: 'sess-abc', agentId: 'agent-1', agentName: 'Test Agent',
    runtimeSessionId: 'rt-abc', runtime: 'opencode', status: 'active',
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

  it('sets isRunning to true during summary loading', async () => {
    _summaryData = null
    _summaryLoading = true

    renderPage('/agent-sessions/sess-abc')

    await waitFor(() => {
      expect(capturedTranscriptOptions.length).toBeGreaterThan(0)
    })

    expect(capturedTranscriptOptions[0].isRunning).toBe(true)
  })

  it('sets isRunning to false after summary loads with non-running status', async () => {
    _summaryData = baseSummary({ status: 'completed' })

    renderPage('/agent-sessions/sess-abc')

    await waitFor(() => {
      const hasFinal = capturedTranscriptOptions.some((o) => o.isRunning === false)
      expect(hasFinal).toBe(true)
    })
  })

  it('passes isHistoricalRuntimeView = true when ?rt= is present', async () => {
    renderPage('/agent-sessions/sess-abc?rt=rt-old')

    await waitFor(() => {
      expect(capturedTranscriptOptions.length).toBeGreaterThan(0)
    })

    const last = capturedTranscriptOptions[capturedTranscriptOptions.length - 1]
    expect(last.isHistoricalRuntimeView).toBe(true)
    expect(last.runtimeSessionId).toBe('rt-old')
  })

  it('passes isHistoricalRuntimeView = false when no ?rt= is present', async () => {
    renderPage('/agent-sessions/sess-abc')

    await waitFor(() => {
      expect(capturedTranscriptOptions.length).toBeGreaterThan(0)
    })

    const last = capturedTranscriptOptions[capturedTranscriptOptions.length - 1]
    expect(last.isHistoricalRuntimeView).toBe(false)
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
})
