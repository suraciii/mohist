import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { TEST_PROJECT } from '../../../../tests/test-utils'
import { act, renderHook } from '@testing-library/react'
import { useCoderSessions } from './useCoderSessions'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { ProjectProvider } from '../../project/model/ProjectContext'
import type { ReactNode } from 'react'
import type { CoderSessionItem } from '..'
import { dispatchAgentEvent } from '../../agent/model/events'

// react-query resolves via notifyManager's scheduled timers. Under fake timers
// we advance the clock ourselves instead of polling wall-clock time, so the
// suite is CPU-speed-independent (no waitFor, no default 1000ms timeout).
async function flush() {
  await act(async () => {
    await vi.advanceTimersByTimeAsync(1000)
  })
}

let _coderSessionsData: CoderSessionItem[] = []
let _coderSessionsResponses: CoderSessionItem[][] = []
let _neverResolve = false

const coderSessionsFetcher = vi.fn(async () => {
  if (_neverResolve) return new Promise<never>(() => {})
  return _coderSessionsResponses.length > 0
    ? _coderSessionsResponses.shift()!
    : _coderSessionsData
})

const queryClients: QueryClient[] = []

function createQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      mutations: { retry: false },
    },
  })
}

function renderHookWithProviders<T>(callback: () => T, options?: { initialProps?: T }) {
  const queryClient = createQueryClient()
  queryClients.push(queryClient)
  return renderHook(callback, {
    wrapper: ({ children }: { children: ReactNode }) => (
      <QueryClientProvider client={queryClient}>
        <ProjectProvider initialProjectId={TEST_PROJECT.id} initialProjects={[TEST_PROJECT]}>
          {children}
        </ProjectProvider>
      </QueryClientProvider>
    ),
    ...options,
  })
}

type SessionOverrides = Partial<CoderSessionItem> & {
  inputTokens?: number | null
  outputTokens?: number | null
  costAmount?: number | null
  costCurrency?: string | null
}

function makeSession(overrides: SessionOverrides = {}): CoderSessionItem {
  const { inputTokens, outputTokens, costAmount, costCurrency, ...rest } = overrides
  return {
    id: 'session-1',
    runtimeSessionId: 'acp-1',
    executionId: null,
    taskDescription: null,
    status: 'running',
    createdAt: '2024-01-01T10:00:00.000Z',
    completedAt: null,
    model: null,
    runtime: null,
    stage: null,
    title: null,
    lastDataAt: null,
    probeSentAt: null,
    probeDeadlineAt: null,
    failureReason: null,
    ...(inputTokens !== undefined || outputTokens !== undefined || costAmount !== undefined || costCurrency !== undefined
      ? {
          usage: {
            inputTokens: inputTokens ?? null,
            outputTokens: outputTokens ?? null,
            costAmount: costAmount ?? null,
            costCurrency: costCurrency ?? null,
          },
        }
      : {}),
    ...rest,
  }
}

beforeEach(() => {
  vi.clearAllMocks()
  _coderSessionsData = []
  _coderSessionsResponses = []
  _neverResolve = false
  vi.useFakeTimers()
})

afterEach(() => {
  vi.useRealTimers()
  for (const qc of queryClients) qc.clear()
  queryClients.length = 0
})

describe('useCoderSessions', () => {
  describe('staleTime configuration', () => {
    it('uses 30 second staleTime for caching', async () => {
      _coderSessionsData = [makeSession({ id: 's1' }), makeSession({ id: 's2' })]

      const { result } = renderHookWithProviders(() => useCoderSessions(123, coderSessionsFetcher))

      await flush()

      expect(result.current.sessions.length).toBe(2)
      expect(coderSessionsFetcher).toHaveBeenCalledTimes(1)
    })

    it('returns cached data when re-rendering within stale window', async () => {
      _coderSessionsData = [makeSession({ id: 's1' })]

      const { result } = renderHookWithProviders(() => useCoderSessions(123, coderSessionsFetcher))

      await flush()

      expect(result.current.sessions.length).toBe(1)
      expect(coderSessionsFetcher).toHaveBeenCalledTimes(1)
    })
  })

  describe('cache key scoping', () => {
    it('uses issue-specific cache key', async () => {
      _coderSessionsData = [makeSession({ id: 's1' })]

      const { result: result1 } = renderHookWithProviders(() => useCoderSessions(123, coderSessionsFetcher))
      await flush()
      expect(result1.current.sessions.length).toBe(1)

      const { result: result2 } = renderHookWithProviders(() => useCoderSessions(456, coderSessionsFetcher))
      await flush()
      expect(result2.current.sessions.length).toBe(1)

      expect(coderSessionsFetcher).toHaveBeenCalledTimes(2)
    })

    it('does not share cache between different issues', async () => {
      const sessions123 = [makeSession({ id: 's1', title: 'Issue 123 Session' })]
      const sessions456 = [makeSession({ id: 's2', title: 'Issue 456 Session' })]

      _coderSessionsResponses = [sessions123, sessions456]

      const { result: result1 } = renderHookWithProviders(() => useCoderSessions(123, coderSessionsFetcher))
      await flush()
      expect(result1.current.sessions[0].title).toBe('Issue 123 Session')

      const { result: result2 } = renderHookWithProviders(() => useCoderSessions(456, coderSessionsFetcher))
      await flush()
      expect(result2.current.sessions[0].title).toBe('Issue 456 Session')
    })
  })

  describe('query behavior', () => {
    it('returns empty sessions array while loading', async () => {
      _neverResolve = true

      const { result } = renderHookWithProviders(() => useCoderSessions(123, coderSessionsFetcher))

      expect(result.current.sessions).toEqual([])
      expect(result.current.isLoading).toBe(true)
    })

    it('returns sessions with isLoading false when loaded', async () => {
      _coderSessionsData = [makeSession({ id: 's1' })]

      const { result } = renderHookWithProviders(() => useCoderSessions(123, coderSessionsFetcher))

      await flush()

      expect(result.current.sessions.length).toBe(1)
      expect(result.current.isLoading).toBe(false)
    })

    it('does not fetch when issueNumber is 0', async () => {
      const { result } = renderHookWithProviders(() => useCoderSessions(0, coderSessionsFetcher))

      expect(result.current.sessions).toEqual([])
      expect(coderSessionsFetcher).not.toHaveBeenCalled()
    })

    it('does not fetch when issueNumber is negative', async () => {
      const { result } = renderHookWithProviders(() => useCoderSessions(-1, coderSessionsFetcher))

      expect(result.current.sessions).toEqual([])
      expect(coderSessionsFetcher).not.toHaveBeenCalled()
    })
  })
})

describe('CoderSessionItem type contract', () => {
  it('CoderSessionItem does not include workflowLogs field', () => {
    const session = makeSession()

    expect(session).not.toHaveProperty('workflowLogs')
    expect(session).not.toHaveProperty('turns')
    expect(session).not.toHaveProperty('metadata')
  })

  it('CoderSessionItem includes all summary fields', () => {
    const session = makeSession({
      id: 'test-id',
      runtimeSessionId: 'acp-test',
      executionId: 'exec-1',
      taskDescription: 'Test task',
      status: 'completed',
      createdAt: '2024-01-01T10:00:00.000Z',
      completedAt: '2024-01-01T11:00:00.000Z',
      model: 'claude-3',
      runtime: 'coder',
      stage: 'build',
      title: 'Test Session',
      lastDataAt: '2024-01-01T10:30:00.000Z',
      probeSentAt: '2024-01-01T10:05:00.000Z',
      probeDeadlineAt: '2024-01-01T10:05:30.000Z',
      failureReason: null,
    })

    expect(session.id).toBe('test-id')
    expect(session.runtimeSessionId).toBe('acp-test')
    expect(session.executionId).toBe('exec-1')
    expect(session.taskDescription).toBe('Test task')
    expect(session.status).toBe('completed')
    expect(session.model).toBe('claude-3')
    expect(session.stage).toBe('build')
    expect(session.title).toBe('Test Session')
  })
})

describe('useCoderSessions live event handling', () => {
  it('applies usage update to matching session', async () => {
    _coderSessionsData = [makeSession({ id: 'session-1', status: 'running' })]

    const { result } = renderHookWithProviders(() => useCoderSessions(1, coderSessionsFetcher))
    await flush()
    expect(result.current.sessions.length).toBe(1)

    act(() => {
      dispatchAgentEvent('usage.updated', {
        sessionId: 'session-1',
        inputTokens: 100,
        outputTokens: 50,
        totalTokens: 150,
        cachedReadTokens: 10,
        thoughtTokens: 5,
        costAmount: 0.01,
        costCurrency: 'USD',
        contextWindowSize: 200000,
        contextWindowUsed: 150,
      } as any)
    })

    await flush()

    const session = result.current.sessions[0]
    expect(session.usage?.inputTokens).toBe(100)
    expect(session.usage?.outputTokens).toBe(50)
    expect(session.usage?.totalTokens).toBe(150)
    expect(session.usage?.cachedReadTokens).toBe(10)
    expect(session.usage?.thoughtTokens).toBe(5)
    expect(session.usage?.costAmount).toBe(0.01)
    expect(session.usage?.costCurrency).toBe('USD')
    expect(session.usage?.contextWindowSize).toBe(200000)
    expect(session.usage?.contextWindowUsed).toBe(150)
  })

  it('ignores usage update for unknown session', async () => {
    _coderSessionsData = [makeSession({ id: 'session-1', status: 'running' })]

    const { result } = renderHookWithProviders(() => useCoderSessions(1, coderSessionsFetcher))
    await flush()
    expect(result.current.sessions.length).toBe(1)

    act(() => {
      dispatchAgentEvent('usage.updated', {
        sessionId: 'session-unknown',
        inputTokens: 100,
      } as any)
    })

    await flush()

    expect(result.current.sessions[0].usage?.inputTokens).toBeUndefined()
  })

  it('preserves existing fields when usage update is partial', async () => {
    _coderSessionsData = [makeSession({
      id: 'session-1',
      status: 'running',
      inputTokens: 50,
      costAmount: 0.005,
      costCurrency: 'USD',
    })]

    const { result } = renderHookWithProviders(() => useCoderSessions(1, coderSessionsFetcher))
    await flush()
    expect(result.current.sessions.length).toBe(1)

    act(() => {
      dispatchAgentEvent('usage.updated', {
        sessionId: 'session-1',
        outputTokens: 25,
      } as any)
    })

    await flush()

    const session = result.current.sessions[0]
    expect(session.usage?.inputTokens).toBe(50)
    expect(session.usage?.outputTokens).toBe(25)
    expect(session.usage?.costAmount).toBe(0.005)
    expect(session.usage?.costCurrency).toBe('USD')
  })
})
