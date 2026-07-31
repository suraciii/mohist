import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Routes, Route } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import { UnifiedSessionPage, type UnifiedSessionPageDependencies } from './UnifiedSessionPage'

let summary: any = null
let transcript: any = { turns: [], partCount: 0, lastActivityAt: null }
let transcriptOptions: any[] = []
let turnControlCalls: Array<{ sessionId: string; turnId: string; operation: 'cancel' | 'stop' }> = []
let turnControlState: { state: string } = { state: 'cancelled' }

const baseSummary = (overrides: Record<string, unknown> = {}) => ({
  id: 'session-1',
  source: 'agent-launch',
  runtimeSessionId: 'runtime-1',
  runtime: 'opencode',
  activity: 'idle',
  createdAt: '2026-07-31T10:00:00.000Z',
  lastActivityAt: '2026-07-31T10:01:00.000Z',
  model: 'configured-model',
  resolvedModel: 'resolved-model',
  failureCategory: null,
  failureReason: null,
  toolCallCount: 2,
  toolErrorCount: 0,
  agentId: 'agent-1',
  agentName: 'Reviewer',
  contextRefs: null,
  usage: { contextWindowUsed: 100, contextWindowSize: 1000, contextUsagePercent: 10, healthStatus: 'healthy' },
  recoveryAvailable: true,
  inputs: null,
  turns: null,
  ...overrides,
})

const transcriptTurn = {
  id: 'turn-1',
  startedAt: '2026-07-31T10:00:00.000Z',
  completedAt: '2026-07-31T10:01:00.000Z',
  user: { role: 'mohist', text: 'Build it', kind: 'task', sentAt: '2026-07-31T10:00:00.000Z' },
  assistant: [],
}

function makeDependencies(): UnifiedSessionPageDependencies {
  return {
    dataSource: {
      useUnifiedSessionSummary: () => ({ data: summary, isLoading: false, isError: false }) as never,
      useUnifiedSessionTranscript: (_sessionId, runtimeSessionId) => {
        transcriptOptions.push({ sessionId: _sessionId, runtimeSessionId })
        return { data: transcript } as never
      },
      useSessionTranscript: (options) => {
        transcriptOptions.push(options)
        return {
          turns: transcript.turns,
          transcriptVersion: 0,
          scrollToBottom: vi.fn(),
          newContentAvailable: false,
          setIsNearBottom: vi.fn(),
          isFinalizing: false,
          isThinking: false,
          isStreaming: false,
        } as never
      },
      projectTurn: (turn) => turn as never,
      useGenericFollowup: () => ({ mutateAsync: vi.fn(), isPending: false }) as never,
      useGenericTurnControl: () => ({
        mutate: (input: { sessionId: string; turnId: string; operation: 'cancel' | 'stop' }, options?: { onSuccess?: (result: { state: string }) => void }) => {
          turnControlCalls.push(input)
          options?.onSuccess?.(turnControlState)
        },
        isPending: false,
      }) as never,
    },
    shellComponents: {
      SessionTranscriptLayout: () => <div data-testid="transcript" />,
      SessionRecoveryActions: ({ recoveryAvailable }: any) => (
        <div data-testid="recovery" data-recovery-available={String(recoveryAvailable ?? false)} />
      ),
      SessionFollowupComposer: () => <div data-testid="followup" />,
      ContextHealthBar: () => <div data-testid="context-health" />,
    },
  }
}

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId="proj-1" initialProjects={[{
        id: 'proj-1', name: 'Test', createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z', repositories: [],
      }]}>
        <MemoryRouter initialEntries={['/sessions/session-1']}>
          <Routes>
            <Route path="/sessions/:sessionId" element={<UnifiedSessionPage dependencies={makeDependencies()} />} />
          </Routes>
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

describe('UnifiedSessionPage', () => {
  beforeEach(() => {
    summary = baseSummary()
    transcript = { turns: [], partCount: 0, lastActivityAt: null }
    transcriptOptions = []
    turnControlCalls = []
    turnControlState = { state: 'cancelled' }
  })

  afterEach(() => cleanup())

  it.each([
    ['agent-launch', baseSummary(), 'Agent Session', 'Reviewer'],
    ['workflow', baseSummary({ source: 'workflow', agentId: null, agentName: null, workflowRunId: 'run-1', sessionName: 'build', contextRefs: { issueNumber: 42 } }), 'Workflow Session', 'Work: build'],
  ])('renders source context for %s sessions', (_source, value, contextLabel, detailLabel) => {
    summary = value
    renderPage()
    expect(screen.getByTestId('session-source-context')).toHaveTextContent(contextLabel)
    expect(screen.getByTestId('session-source-context')).toHaveTextContent(detailLabel)
  })

  it('groups chronological inputs under their authoritative turn observation', () => {
    summary = baseSummary({
      inputs: [
        { id: 'input-1', sequence: 1, source: 'web', acceptance: 'accepted' },
        { id: 'input-2', sequence: 2, source: 'web', acceptance: 'pending' },
      ],
      turns: [{ id: 'turn-1', sequence: 1, inputIds: ['input-1', 'input-2'], status: 'executing' }],
    })
    transcript = { turns: [transcriptTurn], partCount: 1, lastActivityAt: transcriptTurn.completedAt }
    renderPage()
    const evidence = screen.getByTestId('session-input-turn-evidence')
    expect(evidence).toHaveTextContent('accepted: accepted')
    expect(evidence).toHaveTextContent('accepted: pending')
    expect(evidence).toHaveTextContent('delivered: executing')
    expect(screen.getByTestId('session-turn-evidence-turn-1')).toBeInTheDocument()
  })

  it.each([
    ['active', 'active-no-content'],
    ['idle', 'idle-no-content'],
    ['unknown', 'unknown-no-content'],
  ])('keeps %s empty state distinct', (activity, stateKind) => {
    summary = baseSummary({ activity, recoveryAvailable: activity === 'idle' })
    renderPage()
    expect(screen.getByTestId('session-empty-state')).toHaveAttribute('data-state-kind', stateKind)
  })

  it('shows failure evidence and resolved model without inventing a terminal state', () => {
    summary = baseSummary({ activity: 'unknown', failureCategory: 'timeout', failureReason: 'runner timed out', toolErrorCount: 1 })
    renderPage()
    expect(screen.getByTestId('session-errors-region')).toHaveTextContent('Timed out')
    expect(screen.getByTestId('session-errors-region')).toHaveTextContent('runner timed out')
    expect(screen.getByTestId('session-header-model')).toHaveTextContent('configured-model')
    expect(screen.getByTestId('session-header-model')).toHaveTextContent('resolved-model')
    expect(screen.queryByTestId('session-cancel-trigger')).not.toBeInTheDocument()
  })

  it('passes the stable session and current runtime binding to transcript observation', () => {
    summary = baseSummary({ runtimeSessionId: 'runtime-current' })
    renderPage()
    expect(transcriptOptions.some((value) => value.sessionId === 'session-1' && value.runtimeSessionId === 'runtime-current')).toBe(true)
  })
})

describe('UnifiedSessionPage — turn control and recovery gating', () => {
  beforeEach(() => {
    summary = baseSummary()
    transcript = { turns: [], partCount: 0, lastActivityAt: null }
    transcriptOptions = []
    turnControlCalls = []
    turnControlState = { state: 'cancelled' }
  })

  afterEach(() => cleanup())

  it('renders only the Cancel Turn button when the current turn is queued', () => {
    summary = baseSummary({
      activity: 'active',
      currentTurnId: 'turn-queued',
      turns: [{ id: 'turn-queued', sequence: 1, inputIds: [], status: 'queued' }],
    })
    renderPage()
    expect(screen.getByTestId('session-cancel-trigger')).toBeInTheDocument()
    expect(screen.getByTestId('session-cancel-trigger')).toHaveAttribute('data-turn-state', 'queued')
    expect(screen.queryByTestId('session-stop-trigger')).not.toBeInTheDocument()
  })

  it('renders only the Stop Turn button when the current turn is executing', () => {
    summary = baseSummary({
      activity: 'active',
      currentTurnId: 'turn-running',
      turns: [{ id: 'turn-running', sequence: 1, inputIds: [], status: 'executing' }],
    })
    renderPage()
    expect(screen.getByTestId('session-stop-trigger')).toBeInTheDocument()
    expect(screen.getByTestId('session-stop-trigger')).toHaveAttribute('data-turn-state', 'executing')
    expect(screen.queryByTestId('session-cancel-trigger')).not.toBeInTheDocument()
  })

  it('keeps both Cancel and Stop hidden when no current turn is queued or executing', () => {
    summary = baseSummary({ activity: 'idle', currentTurnId: null })
    renderPage()
    expect(screen.queryByTestId('session-cancel-trigger')).not.toBeInTheDocument()
    expect(screen.queryByTestId('session-stop-trigger')).not.toBeInTheDocument()
  })

  it('opens a confirmation dialog and dispatches the cancel command on confirm', () => {
    summary = baseSummary({
      activity: 'active',
      currentTurnId: 'turn-queued',
      turns: [{ id: 'turn-queued', sequence: 1, inputIds: [], status: 'queued' }],
    })
    renderPage()
    act(() => {
      fireEvent.click(screen.getByTestId('session-cancel-trigger'))
    })
    const alert = screen.getByTestId('session-cancel-alert')
    expect(alert).toHaveTextContent('Cancel this Turn?')
    const confirmButton = screen.getByTestId('session-cancel-alert-confirm')
    expect(confirmButton).toHaveTextContent(/Cancel Turn/i)
    act(() => {
      fireEvent.click(confirmButton)
    })
    expect(turnControlCalls).toEqual([{ sessionId: 'session-1', turnId: 'turn-queued', operation: 'cancel' }])
  })

  it('shows the Stop Turn confirmation dialog with the stop-requested acknowledgement', () => {
    summary = baseSummary({
      activity: 'active',
      currentTurnId: 'turn-running',
      turns: [{ id: 'turn-running', sequence: 1, inputIds: [], status: 'executing' }],
    })
    turnControlState = { state: 'stop-requested' }
    renderPage()
    act(() => {
      fireEvent.click(screen.getByTestId('session-stop-trigger'))
    })
    const alert = screen.getByTestId('session-cancel-alert')
    expect(alert).toHaveTextContent('Stop this Turn?')
    expect(alert).toHaveTextContent(/may be unknown/i)
    act(() => {
      fireEvent.click(screen.getByTestId('session-cancel-alert-confirm'))
    })
    expect(turnControlCalls).toEqual([{ sessionId: 'session-1', turnId: 'turn-running', operation: 'stop' }])
    expect(screen.getByTestId('session-cancel-result')).toHaveTextContent(/stop-requested/i)
  })

  it('gates Compact and Reset off while a queued or executing turn is in flight', () => {
    summary = baseSummary({
      activity: 'active',
      currentTurnId: 'turn-running',
      recoveryAvailable: false,
      turns: [{ id: 'turn-running', sequence: 1, inputIds: [], status: 'executing' }],
    })
    renderPage()
    expect(screen.getByTestId('recovery')).toHaveAttribute('data-recovery-available', 'false')
  })

  it('enables Compact and Reset for an idle Session with no active turn', () => {
    summary = baseSummary({ activity: 'idle', currentTurnId: null })
    renderPage()
    expect(screen.getByTestId('recovery')).toHaveAttribute('data-recovery-available', 'true')
  })
})
