// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Routes, Route } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import { GenericSessionPage } from './GenericSessionPage'

const mocks = vi.hoisted(() => ({
  summaryData: null as any,
  summaryLoading: false,
  summaryError: false,
  transcriptData: null as any,
  transcriptTurns: [] as any[],
  transcriptIsRunning: false,
  followupMutation: { mutate: vi.fn(), isPending: false },
  cancelMutation: { mutate: vi.fn(), isPending: false },
  routeParams: { sessionId: 'sess-abc' },
}))

vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>()
  return {
    ...actual,
    useParams: () => mocks.routeParams,
  }
})

vi.mock('../../../entities/agent', () => ({
  useGenericSessionSummary: () => ({
    data: mocks.summaryData,
    isLoading: mocks.summaryLoading,
    isError: mocks.summaryError,
  }),
  useGenericSessionTranscript: () => ({
    data: mocks.transcriptData,
  }),
  useGenericFollowup: () => mocks.followupMutation,
  useCancelGenericSession: () => mocks.cancelMutation,
  useAgentSessions: () => ({ data: [], isLoading: false }),
}))

vi.mock('../../../widgets/session-transcript', () => ({
  useSessionTranscript: () => ({
    turns: mocks.transcriptTurns,
    transcriptVersion: 0,
    scrollToBottom: vi.fn(),
    newContentAvailable: false,
    setIsNearBottom: vi.fn(),
    isFinalizing: false,
    isThinking: false,
    isStreaming: false,
  }),
  projectTurn: (turn: any) => turn,
  SessionTranscriptLayout: () => <div data-testid="session-transcript-layout" />,
}))

vi.mock('../../../widgets/coder-session', () => ({
  SessionRecoveryActions: () => <div data-testid="session-recovery-actions" />,
  SessionFollowupComposer: ({ disabled }: { disabled: boolean }) => (
    <div data-testid="session-followup-composer" data-disabled={disabled ? 'true' : 'false'} />
  ),
}))

vi.mock('../../../widgets/session-health', () => ({
  ContextHealthBar: () => <div data-testid="context-health-bar" />,
  ContextHealthIndicator: () => <div data-testid="context-health-indicator" />,
  CompactionLineageLink: () => <div data-testid="compaction-lineage-link" />,
}))

vi.mock('../../../shared/lib/useDocumentTitle', () => ({
  useDocumentTitle: () => {},
}))

function createQueryClient() {
  return new QueryClient({ defaultOptions: { queries: { retry: false } } })
}

function baseSummary(overrides: Record<string, any> = {}) {
  return {
    sessionId: 'sess-abc',
    agentId: 'agent-1',
    agentName: 'Test Agent',
    status: 'completed',
    createdAt: '2026-06-15T10:00:00.000Z',
    lastActivityAt: '2026-06-15T10:30:00.000Z',
    resolvedModel: 'gpt-4',
    failureCategory: null,
    toolCallCount: 5,
    toolErrorCount: 0,
    contextRefs: null,
    usage: null,
    ...overrides,
  }
}

function makeTurn(overrides: Record<string, any> = {}) {
  return {
    id: 'turn-1',
    startedAt: '2026-01-01T00:00:00Z',
    completedAt: null,
    user: { role: 'mohist', text: 'hi', kind: 'task', sentAt: '2026-01-01T00:00:00Z' },
    assistant: [],
    ...overrides,
  }
}

async function renderPage() {
  const queryClient = createQueryClient()
  const result = render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId="proj-1" initialProjects={[{
        id: 'proj-1',
        name: 'Test',
        createdAt: '2026-01-01T00:00:00Z',
        updatedAt: '2026-01-01T00:00:00Z',
        repositories: [],
      }]}>
        <MemoryRouter initialEntries={['/agent-sessions/sess-abc']}>
          <Routes>
            <Route path="/agent-sessions/:sessionId" element={<GenericSessionPage />} />
          </Routes>
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
  return result
}

describe('GenericSessionPage', () => {
  beforeEach(() => {
    mocks.summaryData = null
    mocks.summaryLoading = false
    mocks.summaryError = false
    mocks.transcriptData = null
    mocks.transcriptTurns = []
    mocks.followupMutation = { mutate: vi.fn(), isPending: false }
    mocks.cancelMutation = { mutate: vi.fn(), isPending: false }
    mocks.routeParams = { sessionId: 'sess-abc' }
  })

  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  describe('loading and error states', () => {
    it('shows loading state while summary is loading', () => {
      mocks.summaryLoading = true
      renderPage()
      expect(screen.getByText(/loading session/i)).toBeInTheDocument()
    })

    it('shows error state when summary fetch fails', async () => {
      mocks.summaryError = true
      renderPage()
      await waitFor(() => {
        expect(screen.getByText(/failed to load session/i)).toBeInTheDocument()
      })
    })
  })

  describe('header and back-link', () => {
    it('renders session header with agent name, status badge, and model', async () => {
      mocks.summaryData = baseSummary()
      renderPage()
      await waitFor(() => {
        expect(screen.getByText('Completed')).toBeInTheDocument()
      })
      expect(screen.getByText('gpt-4')).toBeInTheDocument()
      // Agent name appears as both the session title and the back-label link text
      const agentNameElements = screen.getAllByText('Test Agent')
      expect(agentNameElements.length).toBeGreaterThanOrEqual(1)
    })

    it('links back to agent profile (/agents/{agentId}) when no issue context ref', async () => {
      mocks.summaryData = baseSummary({ contextRefs: null })
      renderPage()
      await waitFor(() => {
        const backLink = screen.getByRole('link', { name: /Test Agent/i })
        expect(backLink).toBeInTheDocument()
        expect(backLink.getAttribute('href')).toContain('/agents/agent-1')
      })
    })

    it('links back to referenced issue when context ref has issueNumber', async () => {
      mocks.summaryData = baseSummary({
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
      mocks.summaryData = baseSummary()
      renderPage()
      await waitFor(() => {
        const labels = screen.getAllByText(/Session/i)
        expect(labels.length).toBeGreaterThanOrEqual(1)
      })
    })

    it('displays turn count', () => {
      mocks.summaryData = baseSummary()
      const turn = makeTurn()
      mocks.transcriptTurns = [turn]
      renderPage()
      expect(screen.getAllByText('0 turns').length).toBeGreaterThanOrEqual(1)
    })
  })

  describe('follow-up enable/disable', () => {
    it('enables followup composer for non-terminal (running) sessions with turns', async () => {
      mocks.summaryData = baseSummary({ status: 'running' })
      mocks.transcriptTurns = [makeTurn()]
      renderPage()
      await waitFor(() => {
        const composer = screen.getByTestId('session-followup-composer')
        expect(composer).toBeInTheDocument()
        expect(composer.getAttribute('data-disabled')).toBe('false')
      })
    })

    it('disables followup composer for terminal (completed) sessions with turns', async () => {
      mocks.summaryData = baseSummary({ status: 'completed' })
      mocks.transcriptTurns = [makeTurn()]
      renderPage()
      await waitFor(() => {
        const composer = screen.getByTestId('session-followup-composer')
        expect(composer).toHaveAttribute('data-disabled', 'true')
      })
    })

    it('disables followup composer for failed sessions with turns', async () => {
      mocks.summaryData = baseSummary({ status: 'failed' })
      mocks.transcriptTurns = [makeTurn()]
      renderPage()
      await waitFor(() => {
        const composer = screen.getByTestId('session-followup-composer')
        expect(composer).toHaveAttribute('data-disabled', 'true')
      })
    })
  })

  describe('recovery region', () => {
    it('renders ContextHealthBar when usage data is present and turns exist', async () => {
      mocks.summaryData = baseSummary({
        usage: { contextWindowUsed: 12000, contextWindowSize: 32000, contextUsagePercent: 37.5, healthStatus: 'green' },
      })
      mocks.transcriptTurns = [makeTurn()]
      renderPage()
      await waitFor(() => {
        const bars = screen.getAllByTestId('context-health-bar')
        expect(bars.length).toBeGreaterThanOrEqual(1)
      })
    })

    it('omits ContextHealthBar when no usage data', async () => {
      mocks.summaryData = baseSummary({ usage: null })
      renderPage()
      await waitFor(() => {
        expect(screen.queryByTestId('context-health-bar')).not.toBeInTheDocument()
      })
    })

    it('omits Compact/Reset recovery actions', async () => {
      mocks.summaryData = baseSummary()
      mocks.transcriptTurns = [makeTurn()]
      renderPage()
      await waitFor(() => {
        expect(screen.queryByTestId('session-recovery-actions')).not.toBeInTheDocument()
      })
    })

    it('renders no sibling sidebar for generic sessions', async () => {
      mocks.summaryData = baseSummary()
      mocks.transcriptTurns = [makeTurn()]
      renderPage()
      await waitFor(() => {
        expect(screen.queryByTestId('session-sibling-sidebar')).not.toBeInTheDocument()
      })
    })
  })

  describe('transcript rendering', () => {
    it('passes session transcript to SessionTranscriptLayout', async () => {
      mocks.summaryData = baseSummary()
      mocks.transcriptTurns = [makeTurn()]
      renderPage()
      await waitFor(() => {
        expect(screen.getByTestId('session-transcript-scroll-container')).toBeInTheDocument()
      })
    })
  })

  describe('cancel control (issue-349 T-002)', () => {
    it.each(['active', 'running', 'probing'])(
      'renders the cancel trigger in the header when the generic session is non-terminal (%s)',
      async (status) => {
        mocks.summaryData = baseSummary({ status })
        mocks.transcriptTurns = [makeTurn()]
        renderPage()
        await waitFor(() => {
          expect(screen.getByTestId('session-cancel-trigger')).toBeInTheDocument()
        })
      },
    )

    it.each(['completed', 'failed', 'cancelled', 'stopped'])(
      'does not render the cancel trigger when the session is terminal (%s)',
      async (status) => {
        mocks.summaryData = baseSummary({ status })
        mocks.transcriptTurns = [makeTurn()]
        renderPage()
        await waitFor(() => {
          expect(screen.getByTestId('session-transcript-scroll-container')).toBeInTheDocument()
        })
        expect(screen.queryByTestId('session-cancel-trigger')).not.toBeInTheDocument()
      },
    )

    it('does not render the cancel trigger inside the followup composer (issue-242 composer constraint)', async () => {
      mocks.summaryData = baseSummary({ status: 'running' })
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
      mocks.summaryData = baseSummary({ status: 'running' })
      mocks.transcriptTurns = [makeTurn()]
      renderPage()
      await waitFor(() => {
        expect(screen.getByTestId('session-cancel-trigger')).toBeInTheDocument()
      })

      expect(screen.queryByTestId('session-cancel-alert')).not.toBeInTheDocument()
      expect(mocks.cancelMutation.mutate).not.toHaveBeenCalled()

      fireEvent.click(screen.getByTestId('session-cancel-trigger'))

      const dialog = screen.getByTestId('session-cancel-alert')
      expect(dialog).toBeInTheDocument()
      expect(dialog).toHaveAttribute('data-tone', 'destructive')

      // Mutate must not fire before explicit confirmation
      expect(mocks.cancelMutation.mutate).not.toHaveBeenCalled()
    })

    it('dismissing the dialog sends no cancel request and leaves the session running', async () => {
      mocks.summaryData = baseSummary({ status: 'running' })
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
      expect(mocks.cancelMutation.mutate).not.toHaveBeenCalled()

      // Session is still running — cancel control is still present
      expect(screen.getByTestId('session-cancel-trigger')).toBeInTheDocument()
    })

    it('confirming the dialog calls cancel.mutate() with the owning agent reference', async () => {
      mocks.summaryData = baseSummary({ status: 'running' })
      mocks.transcriptTurns = [makeTurn()]
      renderPage()
      await waitFor(() => {
        expect(screen.getByTestId('session-cancel-trigger')).toBeInTheDocument()
      })

      fireEvent.click(screen.getByTestId('session-cancel-trigger'))
      fireEvent.click(screen.getByTestId('session-cancel-alert-confirm'))

      expect(mocks.cancelMutation.mutate).toHaveBeenCalledTimes(1)
      expect(mocks.cancelMutation.mutate).toHaveBeenCalledWith(
        { sessionId: 'sess-abc', agentRef: 'agent-1' },
        expect.objectContaining({ onSettled: expect.any(Function) }),
      )
    })

    it('closes the confirmation dialog after the cancel mutation settles while the session remains non-terminal', async () => {
      mocks.summaryData = baseSummary({ status: 'running' })
      mocks.transcriptTurns = [makeTurn()]
      renderPage()
      await waitFor(() => {
        expect(screen.getByTestId('session-cancel-trigger')).toBeInTheDocument()
      })

      fireEvent.click(screen.getByTestId('session-cancel-trigger'))
      fireEvent.click(screen.getByTestId('session-cancel-alert-confirm'))

      const mutationOptions = mocks.cancelMutation.mutate.mock.calls[0][1]
      act(() => {
        mutationOptions.onSettled()
      })

      await waitFor(() => {
        expect(screen.queryByTestId('session-cancel-alert')).not.toBeInTheDocument()
      })
      expect(screen.getByTestId('session-cancel-trigger')).toBeInTheDocument()
    })

    it('AlertDialog confirm button reflects cancel.isPending (dismissing disabled while in flight)', async () => {
      mocks.cancelMutation = { mutate: vi.fn(), isPending: true }
      mocks.summaryData = baseSummary({ status: 'running' })
      mocks.transcriptTurns = [makeTurn()]
      renderPage()
      await waitFor(() => {
        expect(screen.getByTestId('session-cancel-trigger')).toBeInTheDocument()
      })

      fireEvent.click(screen.getByTestId('session-cancel-trigger'))

      const confirmButton = screen.getByTestId('session-cancel-alert-confirm') as HTMLButtonElement
      const cancelButton = screen.getByTestId('session-cancel-alert-cancel') as HTMLButtonElement
      expect(confirmButton).toBeDisabled()
      expect(cancelButton).toBeDisabled()
      expect(confirmButton.textContent).toContain('Working')
    })
  })
})
