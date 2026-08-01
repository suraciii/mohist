import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { act, cleanup, renderHook, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import type { ReactNode } from 'react'
import { ProjectProvider } from '../../../entities/project'
import { useUnifiedSessionDataSource, type UnifiedSessionDataSourceDependencies } from './useUnifiedSessionDataSource'
import type { AgentSessionTranscriptResponse, SessionFollowupResult, UnifiedSessionSummaryDto } from '../../../entities/coder-session'
import type { TurnControlResult } from '../../../entities/agent'
import { ApiError } from '../../../shared/api/client'

const TEST_PROJECT = {
  id: 'proj-1',
  name: 'Test',
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-01T00:00:00Z',
  repositories: [],
}

function makeSummary(overrides: Partial<UnifiedSessionSummaryDto> = {}): UnifiedSessionSummaryDto {
  return {
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
    toolCallCount: 0,
    toolErrorCount: 0,
    agentId: 'agent-1',
    agentName: 'Reviewer',
    contextRefs: null,
    usage: { contextWindowUsed: 100, contextWindowSize: 1000, contextUsagePercent: 10, healthStatus: 'healthy' },
    recoveryAvailable: true,
    currentTurnId: null,
    inputs: null,
    turns: null,
    ...overrides,
  }
}

interface CapturedFollowup { sessionId: string; text: string; attachments: string[] | undefined; idempotencyKey: string }
interface CapturedTurnControl { sessionId: string; turnId: string; operation: 'cancel' | 'stop' }

let followupSequence: SessionFollowupResult[] = []
const followupCalls: CapturedFollowup[] = []
let turnControlSequence: TurnControlResult[] = []
const turnControlCalls: CapturedTurnControl[] = []

const followupMock = {
  mutateAsync: vi.fn(async (input: CapturedFollowup): Promise<SessionFollowupResult> => {
    followupCalls.push(input)
    const next = followupSequence.length > 0
      ? followupSequence.shift()!
      : { status: 'accepted' as const, inputId: 'input-1', turnId: 'turn-1' }
    return next
  }),
  isPending: false,
}

const turnControlMock = {
  mutate: vi.fn((input: CapturedTurnControl, options?: { onSuccess?: (result: TurnControlResult) => void }) => {
    turnControlCalls.push(input)
    const next = turnControlSequence.length > 0 ? turnControlSequence.shift()! : { state: 'cancelled' }
    options?.onSuccess?.(next)
  }),
  isPending: false,
}

function makeDependencies(overrides: Partial<UnifiedSessionDataSourceDependencies> = {}): UnifiedSessionDataSourceDependencies {
  return {
    useSessionTranscript: (() => ({
      turns: [],
      transcriptVersion: 0,
      scrollToBottom: vi.fn(),
      newContentAvailable: false,
      setIsNearBottom: vi.fn(),
      isFinalizing: false,
      isThinking: false,
      isStreaming: false,
    })) as never,
    projectTurn: ((turn: unknown) => turn) as never,
    useUnifiedSessionSummary: (() => ({ data: makeSummary(), isLoading: false, isError: false })) as never,
    useUnifiedSessionTranscript: (() => ({ data: { turns: [], partCount: 0, lastActivityAt: null } as AgentSessionTranscriptResponse })) as never,
    useGenericFollowup: (() => followupMock) as never,
    useGenericTurnControl: (() => turnControlMock) as never,
    ...overrides,
  }
}

const queryClients: QueryClient[] = []

function createQueryClient() {
  return new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 }, mutations: { retry: false } },
  })
}

function renderUnifiedHook(
  dependencies: UnifiedSessionDataSourceDependencies,
  initialEntry = '/sessions/session-1',
  qc?: QueryClient,
) {
  const queryClient = qc ?? createQueryClient()
  queryClients.push(queryClient)
  return {
    queryClient,
    ...renderHook(() => useUnifiedSessionDataSource(dependencies), {
      wrapper: ({ children }: { children: ReactNode }) => (
        <QueryClientProvider client={queryClient}>
          <ProjectProvider initialProjectId={TEST_PROJECT.id} initialProjects={[TEST_PROJECT]}>
            <MemoryRouter initialEntries={[initialEntry]}>
              <Routes>
                <Route path="/sessions/:sessionId" element={children} />
              </Routes>
            </MemoryRouter>
          </ProjectProvider>
        </QueryClientProvider>
      ),
    }),
  }
}

beforeEach(() => {
  followupSequence = []
  followupCalls.length = 0
  followupMock.mutateAsync.mockClear()
  turnControlSequence = []
  turnControlCalls.length = 0
  turnControlMock.mutate.mockClear()
})

afterEach(() => {
  cleanup()
  for (const qc of queryClients) qc.clear()
  queryClients.length = 0
})

describe('useUnifiedSessionDataSource — follow-up commands', () => {
  it('accepts a follow-up on an idle Session and adds the input to the existing Session', async () => {
    const deps = makeDependencies()
    const { result } = renderUnifiedHook(deps)

    await act(async () => {
      const response = await result.current.sendFollowup('Continue')
      expect(response?.status).toBe('accepted')
    })

    expect(followupCalls).toHaveLength(1)
    const captured = followupCalls[0]
    expect(captured.sessionId).toBe('session-1')
    expect(captured.text).toBe('Continue')
    expect(captured.attachments?.length ?? 0).toBe(0)
    expect(captured.idempotencyKey).toMatch(/^[0-9a-f-]{36}$/i)
  })

  it('accepts a follow-up on an active Session without spawning another Session', async () => {
    const deps = makeDependencies({
      useUnifiedSessionSummary: (() => ({
        data: makeSummary({
          activity: 'active',
          currentTurnId: 'turn-active',
          turns: [{ id: 'turn-active', sequence: 1, inputIds: ['input-0'], status: 'executing' }],
          inputs: [{ id: 'input-0', sequence: 1, source: 'web', acceptance: 'accepted' }],
        }),
        isLoading: false,
        isError: false,
      })) as never,
    })
    const { result } = renderUnifiedHook(deps)

    await act(async () => {
      await result.current.sendFollowup('Next step')
    })

    expect(followupCalls).toHaveLength(1)
    expect(followupCalls[0].sessionId).toBe('session-1')
    expect(followupCalls[0].text).toBe('Next step')
  })

  it('retains the original idempotency key when the follow-up result is unknown', async () => {
    followupSequence = [
      { status: 'unknown', inputId: 'input-x', turnId: 'turn-x' },
      { status: 'accepted', inputId: 'input-y', turnId: 'turn-y' },
    ]
    const deps = makeDependencies()
    const { result } = renderUnifiedHook(deps)

    await act(async () => {
      await expect(result.current.sendFollowup('Retry me')).rejects.toThrow(/unknown/i)
    })
    expect(followupCalls).toHaveLength(1)
    const originalKey = followupCalls[0].idempotencyKey

    await act(async () => {
      await result.current.sendFollowup('Retry me')
    })
    expect(followupCalls).toHaveLength(2)
    expect(followupCalls[1].idempotencyKey).toBe(originalKey)
  })

  it('discards the idempotency key after a known 4xx rejection so retry uses a fresh key', async () => {
    const mutateAsync = vi.fn()
      .mockRejectedValueOnce(new ApiError('Conflict', 409))
      .mockResolvedValueOnce({ status: 'accepted', inputId: 'input-z', turnId: 'turn-z' })
    const deps = makeDependencies({
      useGenericFollowup: (() => ({ mutateAsync, isPending: false })) as never,
    })
    const { result } = renderUnifiedHook(deps)

    await act(async () => {
      await expect(result.current.sendFollowup('Retry after conflict')).rejects.toThrow('Conflict')
    })
    expect(mutateAsync).toHaveBeenCalledTimes(1)
    const rejectedKey = (mutateAsync.mock.calls[0][0] as { idempotencyKey: string }).idempotencyKey

    await act(async () => {
      await result.current.sendFollowup('Retry after conflict')
    })
    expect(mutateAsync).toHaveBeenCalledTimes(2)
    expect((mutateAsync.mock.calls[1][0] as { idempotencyKey: string }).idempotencyKey).not.toBe(rejectedKey)
  })

  it('retains the idempotency key after a network error with an ambiguous outcome', async () => {
    const mutateAsync = vi.fn()
      .mockRejectedValueOnce(new ApiError('Internal error', 503))
      .mockResolvedValueOnce({ status: 'accepted', inputId: 'input-z', turnId: 'turn-z' })
    const deps = makeDependencies({
      useGenericFollowup: (() => ({ mutateAsync, isPending: false })) as never,
    })
    const { result } = renderUnifiedHook(deps)

    await act(async () => {
      await expect(result.current.sendFollowup('Retry after 503')).rejects.toThrow('Internal error')
    })
    expect(mutateAsync).toHaveBeenCalledTimes(1)
    const originalKey = (mutateAsync.mock.calls[0][0] as { idempotencyKey: string }).idempotencyKey

    await act(async () => {
      await result.current.sendFollowup('Retry after 503')
    })
    expect(mutateAsync).toHaveBeenCalledTimes(2)
    expect((mutateAsync.mock.calls[1][0] as { idempotencyKey: string }).idempotencyKey).toBe(originalKey)
  })

  it('reconciles authoritative reads when a follow-up request is rejected', async () => {
    const mutateAsync = vi.fn().mockRejectedValue(new Error('Session is active'))
    const deps = makeDependencies({
      useGenericFollowup: (() => ({ mutateAsync, isPending: false })) as never,
    })
    const { result, queryClient } = renderUnifiedHook(deps)
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries')

    await act(async () => {
      await expect(result.current.sendFollowup('Retry after rejection')).rejects.toThrow('Session is active')
    })

    const invalidatedKeys = invalidateSpy.mock.calls.map((call) => JSON.stringify((call[0] as { queryKey: unknown[] }).queryKey))
    expect(invalidatedKeys.some((key) => key.includes('"unified-session","proj-1","session-1"'))).toBe(true)
    expect(invalidatedKeys.some((key) => key.includes('"agent-sessions"'))).toBe(true)
  })

  it('invalidates the unified summary, transcript prefix, and Session-list queries after a successful follow-up', async () => {
    const deps = makeDependencies()
    const { result, queryClient } = renderUnifiedHook(deps)
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries')

    await act(async () => {
      await result.current.sendFollowup('Continue')
    })

    const invalidatedKeys = invalidateSpy.mock.calls.map((call) => JSON.stringify((call[0] as { queryKey: unknown[] }).queryKey))
    expect(invalidatedKeys.some((key) => key.includes('"unified-session","proj-1","session-1"'))).toBe(true)
    expect(invalidatedKeys.some((key) => key.includes('"unified-session","proj-1","session-1","transcript"'))).toBe(true)
    expect(invalidatedKeys.some((key) => key.includes('"agent-sessions"'))).toBe(true)
    expect(invalidatedKeys.some((key) => key.includes('"workflow-runs"'))).toBe(true)
  })
})

describe('useUnifiedSessionDataSource — turn control availability', () => {
  it('exposes cancel only when the current turn is queued and suppresses stop', () => {
    const deps = makeDependencies({
      useUnifiedSessionSummary: (() => ({
        data: makeSummary({
          activity: 'active',
          currentTurnId: 'turn-queued',
          turns: [{ id: 'turn-queued', sequence: 1, inputIds: [], status: 'queued' }],
        }),
        isLoading: false,
        isError: false,
      })) as never,
    })
    const { result } = renderUnifiedHook(deps)

    expect(result.current.cancel).not.toBeNull()
    expect(result.current.cancel?.state).toBe('queued')
    expect(result.current.cancel?.turnId).toBe('turn-queued')
    expect(result.current.stop).toBeNull()
  })

  it('exposes cancel for a queued turn even when the activity field is idle', () => {
    const deps = makeDependencies({
      useUnifiedSessionSummary: (() => ({
        data: makeSummary({
          activity: 'idle',
          recoveryAvailable: false,
          currentTurnId: 'turn-queued',
          turns: [{ id: 'turn-queued', sequence: 1, inputIds: [], status: 'queued' }],
        }),
        isLoading: false,
        isError: false,
      })) as never,
    })
    const { result } = renderUnifiedHook(deps)

    expect(result.current.cancel?.turnId).toBe('turn-queued')
    expect(result.current.stop).toBeNull()
    expect(result.current.recoveryAvailable).toBe(false)
  })

  it('exposes stop only when the current turn is executing and suppresses cancel', () => {
    const deps = makeDependencies({
      useUnifiedSessionSummary: (() => ({
        data: makeSummary({
          activity: 'active',
          currentTurnId: 'turn-running',
          turns: [{ id: 'turn-running', sequence: 1, inputIds: ['input-1'], status: 'executing' }],
          inputs: [{ id: 'input-1', sequence: 1, source: 'web', acceptance: 'accepted' }],
        }),
        isLoading: false,
        isError: false,
      })) as never,
    })
    const { result } = renderUnifiedHook(deps)

    expect(result.current.stop).not.toBeNull()
    expect(result.current.stop?.state).toBe('executing')
    expect(result.current.stop?.turnId).toBe('turn-running')
    expect(result.current.cancel).toBeNull()
  })

  it('keeps cancel and stop null while the Session is idle or in an unknown state', () => {
    const idleDeps = makeDependencies({
      useUnifiedSessionSummary: (() => ({
        data: makeSummary({ activity: 'idle', currentTurnId: null }),
        isLoading: false,
        isError: false,
      })) as never,
    })
    const { result: idleResult } = renderUnifiedHook(idleDeps)
    expect(idleResult.current.cancel).toBeNull()
    expect(idleResult.current.stop).toBeNull()

    const unknownDeps = makeDependencies({
      useUnifiedSessionSummary: (() => ({
        data: makeSummary({ activity: 'unknown', currentTurnId: 'turn-stale' }),
        isLoading: false,
        isError: false,
      })) as never,
    })
    const { result: unknownResult } = renderUnifiedHook(unknownDeps)
    expect(unknownResult.current.cancel).toBeNull()
    expect(unknownResult.current.stop).toBeNull()
  })

  it('keeps recovery actions gated off when the Session has a queued or executing turn', () => {
    const deps = makeDependencies({
      useUnifiedSessionSummary: (() => ({
        data: makeSummary({
          activity: 'active',
          currentTurnId: 'turn-running',
          recoveryAvailable: false,
          turns: [{ id: 'turn-running', sequence: 1, inputIds: [], status: 'executing' }],
        }),
        isLoading: false,
        isError: false,
      })) as never,
    })
    const { result } = renderUnifiedHook(deps)

    expect(result.current.recoveryAvailable).toBe(false)
    expect(result.current.stop).not.toBeNull()
    expect(result.current.cancel).toBeNull()
  })

  it('dispatches the cancel command with the queued operation and reconciles the unified queries', async () => {
    turnControlSequence = [{ state: 'cancelled' }]
    const deps = makeDependencies({
      useUnifiedSessionSummary: (() => ({
        data: makeSummary({
          activity: 'active',
          currentTurnId: 'turn-queued',
          turns: [{ id: 'turn-queued', sequence: 1, inputIds: [], status: 'queued' }],
        }),
        isLoading: false,
        isError: false,
      })) as never,
    })
    const { result, queryClient } = renderUnifiedHook(deps)
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries')

    act(() => {
      result.current.cancel?.mutate({ onSuccess: ({ state }) => { expect(state).toBe('cancelled') } })
    })

    expect(turnControlCalls).toHaveLength(1)
    expect(turnControlCalls[0]).toEqual({ sessionId: 'session-1', turnId: 'turn-queued', operation: 'cancel' })
    await waitFor(() => {
      const keys = invalidateSpy.mock.calls.map((call) => JSON.stringify((call[0] as { queryKey: unknown[] }).queryKey))
      expect(keys.some((key) => key.includes('"unified-session","proj-1","session-1"'))).toBe(true)
      expect(keys.some((key) => key.includes('"agent-sessions"'))).toBe(true)
      expect(keys.some((key) => key.includes('"workflow-runs"'))).toBe(true)
    })
  })

  it('reports the stop command result as an authoritative Server observation until the Session turns terminal', async () => {
    turnControlSequence = [{ state: 'stop-requested', interruptUnconfirmed: true }]
    const deps = makeDependencies({
      useUnifiedSessionSummary: (() => ({
        data: makeSummary({
          activity: 'active',
          currentTurnId: 'turn-running',
          turns: [{ id: 'turn-running', sequence: 1, inputIds: [], status: 'executing' }],
        }),
        isLoading: false,
        isError: false,
      })) as never,
    })
    const { result } = renderUnifiedHook(deps)
    let observedState: string | undefined

    act(() => {
      result.current.stop?.mutate({ onSuccess: ({ state }) => { observedState = state } })
    })

    expect(observedState).toBe('stop-requested')
    expect(turnControlCalls).toHaveLength(1)
    expect(turnControlCalls[0].operation).toBe('stop')
  })
})

describe('useUnifiedSessionDataSource — recovery command reconciliation', () => {
  it('reconciles the unified summary, transcript, and Session lists when a recovery command succeeds', () => {
    const deps = makeDependencies()
    const { result, queryClient } = renderUnifiedHook(deps)
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries')

    act(() => {
      result.current.handleRecoverySuccess()
    })

    const keys = invalidateSpy.mock.calls.map((call) => JSON.stringify((call[0] as { queryKey: unknown[] }).queryKey))
    expect(keys.some((key) => key.includes('"unified-session","proj-1","session-1"'))).toBe(true)
    expect(keys.some((key) => key.includes('"unified-session","proj-1","session-1","transcript"'))).toBe(true)
    expect(keys.some((key) => key.includes('"agent-sessions"'))).toBe(true)
    expect(keys.some((key) => key.includes('"workflow-runs"'))).toBe(true)
  })
})

describe('useUnifiedSessionDataSource — both Session sources', () => {
  it('drives follow-up and turn control for an agent-launch Session through the same canonical APIs', async () => {
    const deps = makeDependencies({
      useUnifiedSessionSummary: (() => ({
        data: makeSummary({
          activity: 'active',
          currentTurnId: 'turn-agent',
          turns: [{ id: 'turn-agent', sequence: 1, inputIds: [], status: 'queued' }],
        }),
        isLoading: false,
        isError: false,
      })) as never,
    })
    const { result } = renderUnifiedHook(deps)

    await act(async () => {
      await result.current.sendFollowup('hello')
    })
    expect(followupCalls[0].sessionId).toBe('session-1')

    act(() => {
      result.current.cancel?.mutate()
    })
    expect(turnControlCalls[0].sessionId).toBe('session-1')
    expect(turnControlCalls[0].operation).toBe('cancel')
  })

  it('drives follow-up and turn control for a workflow Session through the same canonical APIs', async () => {
    const deps = makeDependencies({
      useUnifiedSessionSummary: (() => ({
        data: makeSummary({
          id: 'workflow-session-1',
          source: 'workflow',
          activity: 'active',
          currentTurnId: 'turn-workflow',
          agentId: null,
          agentName: null,
          workflowRunId: 'run-1',
          sessionName: 'build',
          contextRefs: { issueNumber: 42 },
          turns: [{ id: 'turn-workflow', sequence: 1, inputIds: [], status: 'executing' }],
        }),
        isLoading: false,
        isError: false,
      })) as never,
    })
    const { result } = renderUnifiedHook(deps, '/sessions/workflow-session-1')

    await act(async () => {
      await result.current.sendFollowup('continue workflow')
    })
    expect(followupCalls[0].sessionId).toBe('workflow-session-1')

    act(() => {
      result.current.stop?.mutate()
    })
    expect(turnControlCalls[0].sessionId).toBe('workflow-session-1')
    expect(turnControlCalls[0].operation).toBe('stop')
    expect(result.current.cancel).toBeNull()
  })
})
