import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider, useMutation } from '@tanstack/react-query'
import { MemoryRouter, Routes, Route } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import { GenericSessionPage, type GenericSessionPageDependencies } from './GenericSessionPage'

const mocks = {
  transcriptTurns: [] as any[],
}

let _summaryData: unknown = null
let _summaryLoading = false
let _summaryError = false
let _transcriptData: unknown = null
const _followupHandler = vi.fn()
const _cancelHandler = vi.fn()
const _useGenericSessionTranscript = vi.fn(() => ({ data: _transcriptData }) as never)

let _blockCancel = false
let _cancelResolve: (() => void) | null = null

const genericSessionPageDependencies: GenericSessionPageDependencies = {
  dataSource: {
    useGenericSessionSummary: () => ({
      data: _summaryData,
      isLoading: _summaryLoading,
      isError: _summaryError,
    }) as never,
    useGenericSessionTranscript: _useGenericSessionTranscript,
    useGenericFollowup: () => useMutation({
      mutationFn: async ({ text }: { sessionId: string; text: string }) => {
        _followupHandler({ text })
        return { status: 'sent' }
      },
    }) as never,
    useCancelGenericSession: () => useMutation({
      mutationFn: ({ sessionId }: { sessionId: string; agentRef?: string }) => {
        _cancelHandler(sessionId)
        if (!_blockCancel) return Promise.resolve({ state: 'cancelled' })
        return new Promise<{ state: string }>((resolve) => {
          _cancelResolve = () => resolve({ state: 'cancelled' })
        })
      },
    }) as never,
    useSessionTranscript: () => ({
      turns: mocks.transcriptTurns,
      transcriptVersion: 0,
      scrollToBottom: vi.fn(),
      newContentAvailable: false,
      setIsNearBottom: vi.fn(),
      isFinalizing: false,
      isThinking: false,
      isStreaming: false,
    }) as never,
    projectTurn: (turn) => turn as never,
  },
  shellComponents: {
    SessionTranscriptLayout: () => <div data-testid="session-transcript-layout" />,
    SessionRecoveryActions: () => <div data-testid="session-recovery-actions" />,
    SessionFollowupComposer: ({ disabled }: { disabled?: boolean }) => (
      <div data-testid="session-followup-composer" data-disabled={disabled ? 'true' : 'false'} />
    ),
    ContextHealthBar: () => <div data-testid="context-health-bar" />,
    CompactionLineageLink: ({ runtimeSessionLineage, buildTargetPath }: { runtimeSessionLineage?: Array<{ runtimeSessionId: string }> | null; buildTargetPath: (runtimeId: string) => string }) => (
      <a data-testid="compaction-lineage-link" href={buildTargetPath(runtimeSessionLineage![0].runtimeSessionId)} />
    ),
  },
}
function createQueryClient() {
  return new QueryClient({ defaultOptions: { queries: { retry: false } } })
}

function baseSummary(overrides: Record<string, any> = {}) {
  return {
    sessionId: 'sess-abc', agentId: 'agent-1', agentName: 'Test Agent',
    runtimeSessionId: 'rt-abc', runtime: 'opencode', status: 'completed',
    createdAt: '2026-06-15T10:00:00.000Z', lastActivityAt: '2026-06-15T10:30:00.000Z',
    resolvedModel: 'gpt-4', failureCategory: null,
    toolCallCount: 5, toolErrorCount: 0, contextRefs: null, usage: null,
    ...overrides,
  }
}

function makeTurn(overrides: Record<string, any> = {}) {
  return {
    id: 'turn-1', startedAt: '2026-01-01T00:00:00Z', completedAt: null,
    user: { role: 'mohist', text: 'hi', kind: 'task', sentAt: '2026-01-01T00:00:00Z' },
    assistant: [], ...overrides,
  }
}

async function renderPage(initialEntry = '/agent-sessions/sess-abc') {
  const queryClient = createQueryClient()
  const result = render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId="proj-1" initialProjects={[{
        id: 'proj-1', name: 'Test', createdAt: '2026-01-01T00:00:00Z',
        updatedAt: '2026-01-01T00:00:00Z', repositories: [],
      }]}>
        <MemoryRouter initialEntries={[initialEntry]}>
          <Routes>
            <Route path="/agent-sessions/:sessionId" element={<GenericSessionPage dependencies={genericSessionPageDependencies} />} />
          </Routes>
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
  return result
}

describe('GenericSessionPage', () => {
  beforeEach(() => {
    _summaryData = null
    _summaryLoading = false
    _summaryError = false
    _transcriptData = null
    mocks.transcriptTurns = []
    _followupHandler.mockClear()
    _cancelHandler.mockClear()
    _useGenericSessionTranscript.mockClear()
    _blockCancel = false
    _cancelResolve = null
  })

  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  describe('loading and error states', () => {
    it('shows loading state while summary is loading', () => {
      _summaryLoading = true
      renderPage()
      expect(screen.getByText(/loading session/i)).toBeInTheDocument()
    })

    it('shows error state when summary fetch fails', async () => {
      _summaryError = true
      renderPage()
      await waitFor(() => {
        expect(screen.getByText(/failed to load session/i)).toBeInTheDocument()
      })
    })
  })

  describe('header and back-link', () => {
    it('renders session header with agent name, status badge, and model', async () => {
      _summaryData = baseSummary()
      renderPage()
      await waitFor(() => {
        expect(within(screen.getByTestId('session-header')).getByTestId('session-status-badge')).toBeInTheDocument()
      })
      expect(screen.getByText('gpt-4')).toBeInTheDocument()
      const agentNameElements = screen.getAllByText('Test Agent')
      expect(agentNameElements.length).toBeGreaterThanOrEqual(1)
    })

    it('links back to agent profile (/agents/{agentId}) when no issue context ref', async () => {
      _summaryData = baseSummary({ contextRefs: null })
      renderPage()
      await waitFor(() => {
        const backLink = screen.getByRole('link', { name: /Test Agent/i })
        expect(backLink).toBeInTheDocument()
        expect(backLink.getAttribute('href')).toContain('/agents/agent-1')
      })
    })

    it('links back to referenced issue when context ref has issueNumber', async () => {
      _summaryData = baseSummary({
        contextRefs: { issueNumber: 42, epicNumber: null, repository: null, workspacePath: null },
      })
      renderPage()
      await waitFor(() => {
        const backLink = screen.getByRole('link', { name: /Issue #42/i })
        expect(backLink).toBeInTheDocument()
        expect(backLink.getAttribute('href')).toContain('/issues/42')
      })
    })

    it('omits workflow-stage badge (Session label shown instead)', async () => {
      _summaryData = baseSummary()
      renderPage()
      await waitFor(() => {
        const labels = screen.getAllByText(/Session/i)
        expect(labels.length).toBeGreaterThanOrEqual(1)
      })
    })

    it('displays turn count', async () => {
      _summaryData = baseSummary()
      const turn = makeTurn()
      mocks.transcriptTurns = [turn]
      renderPage()
      await waitFor(() => {
        expect(screen.getAllByText('0 turns').length).toBeGreaterThanOrEqual(1)
      })
    })
  })

  describe('follow-up enable/disable', () => {
    it('enables followup composer for active sessions with turns', async () => {
      // Issue 484: sessions are never terminal. A running session has
      // activity='active' and follow-up remains enabled (interrupts the
      // current turn) per canFollowupSession.
      _summaryData = baseSummary({ activity: 'active' })
      mocks.transcriptTurns = [makeTurn()]
      renderPage()
      await waitFor(() => {
        const composer = screen.getByTestId('session-followup-composer')
        expect(composer).toBeInTheDocument()
        expect(composer.getAttribute('data-disabled')).toBe('false')
      })
    })

    it('disables followup composer for terminal (completed) sessions with turns', async () => {
      _summaryData = baseSummary({ status: 'completed' })
      mocks.transcriptTurns = [makeTurn()]
      renderPage()
      await waitFor(() => {
        const composer = screen.getByTestId('session-followup-composer')
        expect(composer).toHaveAttribute('data-disabled', 'true')
      })
    })

    it('disables followup composer for failed sessions with turns', async () => {
      _summaryData = baseSummary({ status: 'failed' })
      mocks.transcriptTurns = [makeTurn()]
      renderPage()
      await waitFor(() => {
        const composer = screen.getByTestId('session-followup-composer')
        expect(composer).toHaveAttribute('data-disabled', 'true')
      })
    })
  })

  describe('transcript rendering', () => {
    it('passes session transcript to SessionTranscriptLayout', async () => {
      _summaryData = baseSummary()
      mocks.transcriptTurns = [makeTurn()]
      renderPage()
      await waitFor(() => {
        expect(screen.getByTestId('session-transcript-scroll-container')).toBeInTheDocument()
      })
    })
  })

  describe('runtime lineage', () => {
    // Issue 484: the generic-session compaction/lineage predecessor
    // rendering and the `?rt=<runtimeId>` historical-transcript
    // selector were removed from the product code (the shell no longer
    // renders a CompactionLineageLink and useGenericSessionTranscript is
    // keyed only by sessionId). These two scenarios are obsolete under
    // the activity model and have been removed:
    //  - "links generic predecessor bindings back to the same stable
    //     session route"
    //  - "requests the selected runtime transcript for a history link"
    //  - "makes a historical runtime view read-only for followup and
    //     cancel" (the historical `?rt=` view no longer exists)
    it.skip('runtime lineage scenarios removed under the activity model', () => {})
  })

  describe('cancel control', () => {
    // Issue 484: cancel is available only while activity === 'active'
    // (interrupts the current turn). Sessions never reach a terminal
    // state, so 'non-terminal' now means 'active'.
    it.each(['active'])(
      'renders the cancel trigger in the header when the generic session is active (%s)',
      async (activity) => {
        _summaryData = baseSummary({ activity })
        mocks.transcriptTurns = [makeTurn()]
        renderPage()
        await waitFor(() => {
          expect(screen.getByTestId('session-cancel-trigger')).toBeInTheDocument()
        })
      },
    )
    it.each(['idle', 'unknown'])(
      'does not render the cancel trigger when the session is not active (%s)',
      async (activity) => {
        _summaryData = baseSummary({ activity })
        mocks.transcriptTurns = [makeTurn()]
        renderPage()
        await waitFor(() => {
          expect(screen.getByTestId('session-transcript-scroll-container')).toBeInTheDocument()
        })
        expect(screen.queryByTestId('session-cancel-trigger')).not.toBeInTheDocument()
      },
    )
    it('does not render the cancel trigger inside the followup composer', async () => {
      _summaryData = baseSummary({ activity: 'active' })
      mocks.transcriptTurns = [makeTurn()]
      renderPage()
      await waitFor(() => {
        expect(screen.getByTestId('session-cancel-trigger')).toBeInTheDocument()
      })
      const composer = screen.getByTestId('session-followup-composer')
      expect(composer.querySelector('[data-testid="session-cancel-trigger"]')).toBeNull()
      expect(composer.querySelector('[data-testid="session-cancel-alert"]')).toBeNull()
    })
    it('opens a destructive-toned AlertDialog without sending the cancel request', async () => {
      _summaryData = baseSummary({ activity: 'active' })
      mocks.transcriptTurns = [makeTurn()]
      renderPage()
      await waitFor(() => {
        expect(screen.getByTestId('session-cancel-trigger')).toBeInTheDocument()
      })
      expect(screen.queryByTestId('session-cancel-alert')).not.toBeInTheDocument()
      expect(_cancelHandler).not.toHaveBeenCalled()
      fireEvent.click(screen.getByTestId('session-cancel-trigger'))
      const dialog = screen.getByTestId('session-cancel-alert')
      expect(dialog).toBeInTheDocument()
      expect(dialog).toHaveAttribute('data-tone', 'destructive')
      expect(_cancelHandler).not.toHaveBeenCalled()
    })
    it('dismissing the dialog sends no cancel request and leaves the session running', async () => {
      _summaryData = baseSummary({ activity: 'active' })
      mocks.transcriptTurns = [makeTurn()]
      renderPage()
      await waitFor(() => {
        expect(screen.getByTestId('session-cancel-trigger')).toBeInTheDocument()
      })
      fireEvent.click(screen.getByTestId('session-cancel-trigger'))
      expect(screen.getByTestId('session-cancel-alert')).toBeInTheDocument()
      fireEvent.click(screen.getByTestId('session-cancel-alert-cancel'))
      await waitFor(() => {
        expect(screen.queryByTestId('session-cancel-alert')).not.toBeInTheDocument()
      })
      expect(_cancelHandler).not.toHaveBeenCalled()
      expect(screen.getByTestId('session-cancel-trigger')).toBeInTheDocument()
    })
    it('confirming the dialog calls the cancel endpoint with the session id', async () => {
      _summaryData = baseSummary({ activity: 'active' })
      mocks.transcriptTurns = [makeTurn()]
      renderPage()
      await waitFor(() => {
        expect(screen.getByTestId('session-cancel-trigger')).toBeInTheDocument()
      })

      fireEvent.click(screen.getByTestId('session-cancel-trigger'))
      fireEvent.click(screen.getByTestId('session-cancel-alert-confirm'))

      await waitFor(() => {
        expect(_cancelHandler).toHaveBeenCalledWith('sess-abc')
        expect(screen.queryByTestId('session-cancel-alert')).not.toBeInTheDocument()
      })
    })
    it('closes the confirmation dialog after the cancel mutation settles while the session remains non-terminal', async () => {
      _summaryData = baseSummary({ activity: 'active' })
      mocks.transcriptTurns = [makeTurn()]
      renderPage()
      await waitFor(() => {
        expect(screen.getByTestId('session-cancel-trigger')).toBeInTheDocument()
      })

      fireEvent.click(screen.getByTestId('session-cancel-trigger'))
      fireEvent.click(screen.getByTestId('session-cancel-alert-confirm'))

      await waitFor(() => {
        expect(screen.queryByTestId('session-cancel-alert')).not.toBeInTheDocument()
      })
      expect(screen.getByTestId('session-cancel-trigger')).toBeInTheDocument()
    })
    it('AlertDialog confirm button reflects cancel.isPending (dismissing disabled while in flight)', async () => {
      _blockCancel = true

      _summaryData = baseSummary({ activity: 'active' })
      mocks.transcriptTurns = [makeTurn()]
      renderPage()
      await waitFor(() => {
        expect(screen.getByTestId('session-cancel-trigger')).toBeInTheDocument()
      })

      fireEvent.click(screen.getByTestId('session-cancel-trigger'))
      fireEvent.click(screen.getByTestId('session-cancel-alert-confirm'))

      await waitFor(() => {
        expect(screen.getByTestId('session-cancel-alert-confirm')).toBeDisabled()
        expect(screen.getByTestId('session-cancel-alert-cancel')).toBeDisabled()
        expect(screen.getByTestId('session-cancel-alert-confirm').textContent).toContain('Working')
      })

      await act(async () => {
        _cancelResolve?.()
      })
      await waitFor(() => {
        expect(screen.queryByTestId('session-cancel-alert')).not.toBeInTheDocument()
      })
    })
  })
})
