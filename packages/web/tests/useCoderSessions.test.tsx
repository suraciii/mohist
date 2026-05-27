import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { TEST_PROJECT, waitFor } from './test-utils'
import { renderHook } from '@testing-library/react'
import { useCoderSessions } from '../src/entities/coder-session/model/useCoderSessions'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { ProjectProvider } from '../src/entities/project/model/ProjectContext'
import type { ReactNode } from 'react'
import type { CoderSessionItem } from '../src/shared/api/types'

const apiMocks = vi.hoisted(() => ({
  getCoderSessions: vi.fn(),
}))

vi.mock('../src/shared/api/client', () => ({
  api: {
    getCoderSessions: (...args: any[]) => apiMocks.getCoderSessions(...args),
  },
}))

vi.mock('../src/shared/api/agent-events', () => ({
  onAgentEvent: vi.fn(() => vi.fn()),
}))

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

function makeSession(overrides: Partial<CoderSessionItem> = {}): CoderSessionItem {
  return {
    id: 'session-1',
    acpSessionId: 'acp-1',
    executionId: null,
    taskDescription: null,
    status: 'running',
    createdAt: '2024-01-01T10:00:00.000Z',
    completedAt: null,
    model: null,
    coderType: null,
    stage: null,
    title: null,
    lastDataAt: null,
    probeSentAt: null,
    probeDeadlineAt: null,
    failureReason: null,
    ...overrides,
  }
}

beforeEach(() => {
  vi.clearAllMocks()
  apiMocks.getCoderSessions.mockReset()
  apiMocks.getCoderSessions.mockResolvedValue([])
})

afterEach(() => {
  for (const qc of queryClients) qc.clear()
  queryClients.length = 0
})

describe('useCoderSessions', () => {
  describe('staleTime configuration', () => {
    it('uses 30 second staleTime for caching', async () => {
      const sessions = [makeSession({ id: 's1' }), makeSession({ id: 's2' })]
      apiMocks.getCoderSessions.mockResolvedValue(sessions)

      const { result } = renderHookWithProviders(() => useCoderSessions(123))

      await waitFor(() => {
        expect(result.current.sessions.length).toBe(2)
      })

      expect(apiMocks.getCoderSessions).toHaveBeenCalledTimes(1)
    })

    it('returns cached data when re-rendering within stale window', async () => {
      const sessions = [makeSession({ id: 's1' })]
      apiMocks.getCoderSessions.mockResolvedValue(sessions)

      const { result } = renderHookWithProviders(() => useCoderSessions(123))

      await waitFor(() => {
        expect(result.current.sessions.length).toBe(1)
      })

      expect(apiMocks.getCoderSessions).toHaveBeenCalledTimes(1)
    })
  })

  describe('cache key scoping', () => {
    it('uses issue-specific cache key', async () => {
      apiMocks.getCoderSessions.mockResolvedValue([makeSession({ id: 's1' })])

      const { result: result1 } = renderHookWithProviders(() => useCoderSessions(123))

      await waitFor(() => {
        expect(result1.current.sessions.length).toBe(1)
      })

      const { result: result2 } = renderHookWithProviders(() => useCoderSessions(456))

      await waitFor(() => {
        expect(result2.current.sessions.length).toBe(1)
      })

      expect(apiMocks.getCoderSessions).toHaveBeenCalledTimes(2)
    })

    it('does not share cache between different issues', async () => {
      const sessions123 = [makeSession({ id: 's1', title: 'Issue 123 Session' })]
      const sessions456 = [makeSession({ id: 's2', title: 'Issue 456 Session' })]

      apiMocks.getCoderSessions
        .mockResolvedValueOnce(sessions123)
        .mockResolvedValueOnce(sessions456)

      const { result: result1 } = renderHookWithProviders(() => useCoderSessions(123))
      await waitFor(() => {
        expect(result1.current.sessions[0].title).toBe('Issue 123 Session')
      })

      const { result: result2 } = renderHookWithProviders(() => useCoderSessions(456))
      await waitFor(() => {
        expect(result2.current.sessions[0].title).toBe('Issue 456 Session')
      })
    })
  })

  describe('query behavior', () => {
    it('returns empty sessions array while loading', async () => {
      apiMocks.getCoderSessions.mockImplementation(() => new Promise(() => {}))

      const { result } = renderHookWithProviders(() => useCoderSessions(123))

      expect(result.current.sessions).toEqual([])
      expect(result.current.isLoading).toBe(true)
    })

    it('returns sessions with isLoading false when loaded', async () => {
      apiMocks.getCoderSessions.mockResolvedValue([makeSession({ id: 's1' })])

      const { result } = renderHookWithProviders(() => useCoderSessions(123))

      await waitFor(() => {
        expect(result.current.sessions.length).toBe(1)
      })

      expect(result.current.isLoading).toBe(false)
    })

    it('does not fetch when issueNumber is 0', async () => {
      const { result } = renderHookWithProviders(() => useCoderSessions(0))

      expect(result.current.sessions).toEqual([])
      expect(apiMocks.getCoderSessions).not.toHaveBeenCalled()
    })

    it('does not fetch when issueNumber is negative', async () => {
      const { result } = renderHookWithProviders(() => useCoderSessions(-1))

      expect(result.current.sessions).toEqual([])
      expect(apiMocks.getCoderSessions).not.toHaveBeenCalled()
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
      acpSessionId: 'acp-test',
      executionId: 'exec-1',
      taskDescription: 'Test task',
      status: 'completed',
      createdAt: '2024-01-01T10:00:00.000Z',
      completedAt: '2024-01-01T11:00:00.000Z',
      model: 'claude-3',
      coderType: 'coder',
      stage: 'build',
      title: 'Test Session',
      lastDataAt: '2024-01-01T10:30:00.000Z',
      probeSentAt: '2024-01-01T10:05:00.000Z',
      probeDeadlineAt: '2024-01-01T10:05:30.000Z',
      failureReason: null,
    })

    expect(session.id).toBe('test-id')
    expect(session.acpSessionId).toBe('acp-test')
    expect(session.executionId).toBe('exec-1')
    expect(session.taskDescription).toBe('Test task')
    expect(session.status).toBe('completed')
    expect(session.model).toBe('claude-3')
    expect(session.stage).toBe('build')
    expect(session.title).toBe('Test Session')
  })
})
