// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Routes, Route } from 'react-router-dom'
import { http, HttpResponse } from 'msw'
import { useMswServer } from '../../../../tests/support/msw'
import { ProjectProvider } from '../../../entities/project'
import { GenericSessionPage } from './GenericSessionPage'

const mocks = vi.hoisted(() => ({
  transcriptTurns: [] as any[],
}))

let _summaryData: unknown = null
let _summaryLoading = false
let _summaryError = false
let _transcriptData: unknown = null
const _followupHandler = vi.fn()
const _cancelHandler = vi.fn()

let _blockCancel = false
let _cancelResolve: (() => void) | null = null

useMswServer(
  http.get('*/api/projects/:projectId/agent-sessions/:sessionId', () => {
    if (_summaryLoading) return new Promise(() => {})
    if (_summaryError) return HttpResponse.json({ success: false, error: 'Not found' }, { status: 500 })
    return HttpResponse.json({ success: true, data: _summaryData })
  }),
  http.get('*/api/projects/:projectId/agent-sessions/:sessionId/transcript', () =>
    HttpResponse.json({ success: true, data: _transcriptData }),
  ),
  http.post('*/api/projects/:projectId/agent-sessions/:sessionId/followup', async ({ request }) => {
    const body = await request.json()
    _followupHandler(body)
    return HttpResponse.json({ success: true, data: { status: 'sent' } })
  }),
  http.post('*/api/projects/:projectId/agent-sessions/:sessionId/cancel', ({ params }) => {
    _cancelHandler(params.sessionId)
    if (_blockCancel) {
      return new Promise((resolve) => {
        _cancelResolve = () => resolve(HttpResponse.json({ success: true, data: { state: 'cancelled' } }))
      })
    }
    return HttpResponse.json({ success: true, data: { state: 'cancelled' } })
  }),
  http.get('*/api/projects/:projectId/agents/:agentRef/sessions', () =>
    HttpResponse.json({ success: true, data: [] }),
  ),
)

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
    _summaryData = null
    _summaryLoading = false
    _summaryError = false
    _transcriptData = null
    mocks.transcriptTurns = []
    _followupHandler.mockClear()
    _cancelHandler.mockClear()
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
        expect(screen.getByText('Completed')).toBeInTheDocument()
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
    it('enables followup composer for non-terminal (running) sessions with turns', async () => {
      _summaryData = baseSummary({ status: 'running' })
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

  describe('recovery region', () => {
    it('renders ContextHealthBar when usage data is present and turns exist', async () => {
      _summaryData = baseSummary({
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
      _summaryData = baseSummary({ usage: null })
      renderPage()
      await waitFor(() => {
        expect(screen.queryByTestId('context-health-bar')).not.toBeInTheDocument()
      })
    })

    it('omits Compact/Reset recovery actions', async () => {
      _summaryData = baseSummary()
      mocks.transcriptTurns = [makeTurn()]
      renderPage()
      await waitFor(() => {
        expect(screen.queryByTestId('session-recovery-actions')).not.toBeInTheDocument()
      })
    })

    it('renders no sibling sidebar for generic sessions', async () => {
      _summaryData = baseSummary()
      mocks.transcriptTurns = [makeTurn()]
      renderPage()
      await waitFor(() => {
        expect(screen.queryByTestId('session-sibling-sidebar')).not.toBeInTheDocument()
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

  describe('cancel control (issue-349 T-002)', () => {
    it.each(['active', 'running', 'probing'])(
      'renders the cancel trigger in the header when the generic session is non-terminal (%s)',
      async (status) => {
        _summaryData = baseSummary({ status })
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
        _summaryData = baseSummary({ status })
        mocks.transcriptTurns = [makeTurn()]
        renderPage()
        await waitFor(() => {
          expect(screen.getByTestId('session-transcript-scroll-container')).toBeInTheDocument()
        })
        expect(screen.queryByTestId('session-cancel-trigger')).not.toBeInTheDocument()
      },
    )

    it('does not render the cancel trigger inside the followup composer (issue-242 composer constraint)', async () => {
      _summaryData = baseSummary({ status: 'running' })
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
      _summaryData = baseSummary({ status: 'running' })
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
      _summaryData = baseSummary({ status: 'running' })
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
      _summaryData = baseSummary({ status: 'running' })
      mocks.transcriptTurns = [makeTurn()]
      renderPage()
      await waitFor(() => {
        expect(screen.getByTestId('session-cancel-trigger')).toBeInTheDocument()
      })

      fireEvent.click(screen.getByTestId('session-cancel-trigger'))
      fireEvent.click(screen.getByTestId('session-cancel-alert-confirm'))

      await vi.waitFor(() => {
        expect(_cancelHandler).toHaveBeenCalledWith('sess-abc')
      })
    })

    it('closes the confirmation dialog after the cancel mutation settles while the session remains non-terminal', async () => {
      _summaryData = baseSummary({ status: 'running' })
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

      _summaryData = baseSummary({ status: 'running' })
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

      _cancelResolve?.()
    })
  })
})
