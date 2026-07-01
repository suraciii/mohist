import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import {
  cancelGenericSession,
  getAgentSessions,
  getGenericSessionSummary,
  getGenericSessionTranscript,
  launchAgentSession,
  postGenericFollowup,
  useCancelGenericSession,
  useGenericFollowup,
  useGenericSessionSummary,
  useGenericSessionTranscript,
  useLaunchAgentSession,
} from './agent-sessions'

const useQueryMock = vi.fn()
const useMutationMock = vi.fn()
const useQueryClientMock = vi.fn()
const useProjectMock = vi.fn()
const invalidateQueriesMock = vi.fn()
const toastSuccessMock = vi.fn()
const toastErrorMock = vi.fn()

vi.mock('@tanstack/react-query', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@tanstack/react-query')>()),
  useQuery: (...args: unknown[]) => useQueryMock(...args),
  useMutation: (...args: unknown[]) => useMutationMock(...args),
  useQueryClient: () => useQueryClientMock(),
}))

vi.mock('../../project/@x/project-context', () => ({
  useProject: () => useProjectMock(),
}))

vi.mock('sonner', () => ({
  toast: {
    success: (...args: unknown[]) => toastSuccessMock(...args),
    error: (...args: unknown[]) => toastErrorMock(...args),
  },
}))

beforeEach(() => {
  useQueryMock.mockReset()
  useMutationMock.mockReset()
  useQueryClientMock.mockReset()
  useProjectMock.mockReset()
  invalidateQueriesMock.mockReset()
  toastSuccessMock.mockReset()
  toastErrorMock.mockReset()
  useProjectMock.mockReturnValue({ projectId: 'proj-1' })
  useQueryClientMock.mockReturnValue({ invalidateQueries: invalidateQueriesMock })
  useQueryMock.mockReturnValue({ data: null, isLoading: false })
})

afterEach(() => {
  vi.restoreAllMocks()
})

function getLastQueryOptions() {
  const calls = useQueryMock.mock.calls
  return calls[calls.length - 1][0] as {
    queryKey: unknown[]
    queryFn: () => unknown
    enabled: boolean
    refetchInterval?: number | ((q: { state: { data: unknown } }) => number | false)
  }
}

function getLastMutationOptions(): {
  mutationFn: (...a: unknown[]) => unknown
  onSuccess: (...a: unknown[]) => void
  onError: (...a: unknown[]) => void
} {
  const calls = useMutationMock.mock.calls
  const last = calls[calls.length - 1][0] as {
    mutationFn: (...a: unknown[]) => unknown
    onSuccess: (...a: unknown[]) => void
    onError: (...a: unknown[]) => void
  }
  return last
}

function mockFetchResponse(data: unknown) {
  return vi.spyOn(globalThis, 'fetch').mockResolvedValue({
    text: () => Promise.resolve(JSON.stringify({ success: true, data })),
  } as Response)
}

/* ── Client functions ───────────────────────────────────── */
describe('getAgentSessions (client fn)', () => {
  it('builds the correct agent-scoped URL', () => {
    const fetchMock = mockFetchResponse([])

    void getAgentSessions('proj-1', 'agent-foo', { status: 'running', limit: 5 })

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/projects/proj-1/agents/agent-foo/sessions?status=running&limit=5',
      expect.any(Object),
    )
  })
})

describe('getGenericSessionSummary (client fn)', () => {
  it('builds the correct URL for a session summary', () => {
    const fetchMock = mockFetchResponse({ sessionId: 's1' })

    void getGenericSessionSummary('proj-1', 'sess-abc')

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/projects/proj-1/agent-sessions/sess-abc',
      expect.any(Object),
    )
  })
})

describe('getGenericSessionTranscript (client fn)', () => {
  it('builds the correct URL for a session transcript', () => {
    const fetchMock = mockFetchResponse({ turns: [], partCount: 0 })

    void getGenericSessionTranscript('proj-1', 'sess-abc')

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/projects/proj-1/agent-sessions/sess-abc/transcript',
      expect.any(Object),
    )
  })
})

describe('launchAgentSession (client fn)', () => {
  it('POSTs to the launch endpoint with prompt and context', () => {
    const fetchMock = mockFetchResponse({ sessionId: 's1' })

    void launchAgentSession('proj-1', 'agent-foo', {
      prompt: 'Hello',
      context: { issueNumber: 42 },
    })

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/projects/proj-1/agents/agent-foo/sessions',
      expect.objectContaining({
        method: 'POST',
        body: JSON.stringify({ prompt: 'Hello', context: { issueNumber: 42 } }),
      }),
    )
  })
})

describe('postGenericFollowup (client fn)', () => {
  it('POSTs to the followup endpoint with text', () => {
    const fetchMock = mockFetchResponse({ status: 'sent' })

    void postGenericFollowup('proj-1', 'sess-abc', { text: 'Continue' })

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/projects/proj-1/agent-sessions/sess-abc/followup',
      expect.objectContaining({
        method: 'POST',
        body: JSON.stringify({ text: 'Continue' }),
      }),
    )
  })
})

describe('cancelGenericSession (client fn)', () => {
  it('POSTs to the cancel endpoint', () => {
    const fetchMock = mockFetchResponse({ state: 'cancelled' })

    void cancelGenericSession('proj-1', 'sess-abc')

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/projects/proj-1/agent-sessions/sess-abc/cancel',
      expect.objectContaining({ method: 'POST' }),
    )
  })
})

/* ── useGenericSessionSummary ───────────────────────────── */
describe('useGenericSessionSummary', () => {
  it('uses query key ["agent-session", projectId, sessionId]', () => {
    useGenericSessionSummary('sess-abc')
    const opts = getLastQueryOptions()
    expect(opts.queryKey).toEqual(['agent-session', 'proj-1', 'sess-abc'])
  })

  it('is enabled only when projectId and sessionId are present', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })
    useGenericSessionSummary('sess-abc')
    expect(getLastQueryOptions().enabled).toBe(true)

    useProjectMock.mockReturnValue({ projectId: null })
    useGenericSessionSummary('sess-abc')
    expect(getLastQueryOptions().enabled).toBe(false)

    useProjectMock.mockReturnValue({ projectId: 'proj-1' })
    useGenericSessionSummary('')
    expect(getLastQueryOptions().enabled).toBe(false)
  })

  it('polls every 5s when session is not terminal', () => {
    useQueryMock.mockReturnValue({ data: { status: 'running' } })
    useGenericSessionSummary('sess-abc')
    const opts = getLastQueryOptions()
    const interval = typeof opts.refetchInterval === 'function'
      ? opts.refetchInterval({ state: { data: { status: 'running' } } })
      : opts.refetchInterval
    expect(interval).toBe(5000)
  })

  it('stops polling when session is terminal', () => {
    useQueryMock.mockReturnValue({ data: { status: 'completed' } })
    useGenericSessionSummary('sess-abc')
    const opts = getLastQueryOptions()
    const interval = typeof opts.refetchInterval === 'function'
      ? opts.refetchInterval({ state: { data: { status: 'completed' } } })
      : opts.refetchInterval
    expect(interval).toBe(false)
  })
})

/* ── useGenericSessionTranscript ────────────────────────── */
describe('useGenericSessionTranscript', () => {
  it('uses query key ["agent-session", projectId, sessionId, "transcript"]', () => {
    useGenericSessionTranscript('sess-abc')
    const opts = getLastQueryOptions()
    expect(opts.queryKey).toEqual(['agent-session', 'proj-1', 'sess-abc', 'transcript'])
  })

  it('is enabled only when projectId and sessionId are present', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })
    useGenericSessionTranscript('sess-abc')
    expect(getLastQueryOptions().enabled).toBe(true)

    useProjectMock.mockReturnValue({ projectId: null })
    useGenericSessionTranscript('sess-abc')
    expect(getLastQueryOptions().enabled).toBe(false)
  })

  it('polls when there are incomplete turns', () => {
    useQueryMock.mockReturnValue({ data: { turns: [{ incomplete: true }] } })
    useGenericSessionTranscript('sess-abc')
    const opts = getLastQueryOptions()
    const interval = typeof opts.refetchInterval === 'function'
      ? opts.refetchInterval({ state: { data: { turns: [{ incomplete: true }] } } })
      : opts.refetchInterval
    expect(interval).toBe(5000)
  })

  it('stops polling when no incomplete turns remain', () => {
    useQueryMock.mockReturnValue({ data: { turns: [{ incomplete: false }] } })
    useGenericSessionTranscript('sess-abc')
    const opts = getLastQueryOptions()
    const interval = typeof opts.refetchInterval === 'function'
      ? opts.refetchInterval({ state: { data: { turns: [{ incomplete: false }] } } })
      : opts.refetchInterval
    expect(interval).toBe(false)
  })
})

/* ── useLaunchAgentSession ──────────────────────────────── */
describe('useLaunchAgentSession', () => {
  beforeEach(() => {
    useMutationMock.mockImplementation((options: unknown) => ({ mutate: vi.fn(), options }))
  })

  it('invalidates agent-status, agent-activity, and owning session list on success', () => {
    useLaunchAgentSession()
    getLastMutationOptions().onSuccess({ sessionId: 's1' } as never, { agentRef: 'agent-foo', prompt: 'Hi' })
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['agent-status'] })
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['agent-activity'] })
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['agents', 'proj-1', 'agent-foo', 'sessions'] })
  })

  it('shows success toast on launch', () => {
    useLaunchAgentSession()
    getLastMutationOptions().onSuccess({} as never, { agentRef: 'a', prompt: 'p' })
    expect(toastSuccessMock).toHaveBeenCalledWith('Session launched')
  })

  it('shows error toast on failure', () => {
    useLaunchAgentSession()
    getLastMutationOptions().onError(new Error('NO_RUNNER'))
    expect(toastErrorMock).toHaveBeenCalledWith('NO_RUNNER')
  })
})

/* ── useGenericFollowup ─────────────────────────────────── */
describe('useGenericFollowup', () => {
  beforeEach(() => {
    useMutationMock.mockImplementation((options: unknown) => ({ mutate: vi.fn(), options }))
  })

  it('invalidates agent-status, agent-activity, and session queries on success', () => {
    useGenericFollowup()
    getLastMutationOptions().onSuccess({}, { sessionId: 'sess-abc', text: 'go on', agentRef: 'agent-foo' })
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['agent-status'] })
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['agent-activity'] })
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['agent-session', 'proj-1', 'sess-abc'] })
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['agent-session', 'proj-1', 'sess-abc', 'transcript'] })
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['agents', 'proj-1', 'agent-foo', 'sessions'] })
  })

  it('skips agent sessions invalidation when agentRef is omitted', () => {
    useGenericFollowup()
    getLastMutationOptions().onSuccess({}, { sessionId: 'sess-abc', text: 'go on' })
    const agentSessionsCalls = invalidateQueriesMock.mock.calls.filter(
      (c: unknown[]) => (c[0] as { queryKey: string[] }).queryKey[3] === 'sessions',
    )
    expect(agentSessionsCalls).toHaveLength(0)
  })

  it('shows success toast', () => {
    useGenericFollowup()
    getLastMutationOptions().onSuccess({}, { sessionId: 's1', text: 't' })
    expect(toastSuccessMock).toHaveBeenCalledWith('Follow-up sent')
  })

  it('shows error toast on failure', () => {
    useGenericFollowup()
    getLastMutationOptions().onError(new Error('SESSION_INACTIVE'))
    expect(toastErrorMock).toHaveBeenCalledWith('SESSION_INACTIVE')
  })
})

/* ── useCancelGenericSession ────────────────────────────── */
describe('useCancelGenericSession', () => {
  beforeEach(() => {
    useMutationMock.mockImplementation((options: unknown) => ({ mutate: vi.fn(), options }))
  })

  it('invalidates agent-status, agent-activity, and session query on success', () => {
    useCancelGenericSession()
    getLastMutationOptions().onSuccess({}, { sessionId: 'sess-abc', agentRef: 'agent-foo' })
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['agent-status'] })
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['agent-activity'] })
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['agent-session', 'proj-1', 'sess-abc'] })
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['agents', 'proj-1', 'agent-foo', 'sessions'] })
  })

  it('skips agent sessions invalidation when agentRef is omitted', () => {
    useCancelGenericSession()
    getLastMutationOptions().onSuccess({}, { sessionId: 'sess-abc' })
    const agentSessionsCalls = invalidateQueriesMock.mock.calls.filter(
      (c: unknown[]) => (c[0] as { queryKey: string[] }).queryKey[3] === 'sessions',
    )
    expect(agentSessionsCalls).toHaveLength(0)
  })

  it('shows success toast', () => {
    useCancelGenericSession()
    getLastMutationOptions().onSuccess({}, { sessionId: 's1', agentRef: 'a' })
    expect(toastSuccessMock).toHaveBeenCalledWith('Session cancelled')
  })

  it('shows error toast on failure', () => {
    useCancelGenericSession()
    getLastMutationOptions().onError(new Error('NOT_CANCELLABLE'))
    expect(toastErrorMock).toHaveBeenCalledWith('NOT_CANCELLABLE')
  })
})
