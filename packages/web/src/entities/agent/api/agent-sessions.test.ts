import { describe, expect, it, vi } from 'vitest'
import { http, HttpResponse } from 'msw'
import { server, useMswServer } from '../../../../tests/support/msw'
import { toast } from 'sonner'
import {
  cancelGenericSession,
  cancelGenericSessionMutationOptions,
  genericFollowupMutationOptions,
  genericSessionSummaryQueryOptions,
  genericSessionTranscriptQueryOptions,
  getAgentSessions,
  getGenericSessionSummary,
  getGenericSessionTranscript,
  launchAgentSession,
  launchAgentSessionMutationOptions,
  postGenericFollowup,
} from './agent-sessions'
useMswServer()

function createInvalidationClient() {
  return { invalidateQueries: vi.fn() }
}

/* ── Client functions ───────────────────────────────────── */
describe('getAgentSessions (client fn)', () => {
  it('builds the correct agent-scoped URL', async () => {
    const urls: string[] = []
    server.use(
      http.get('*/api/projects/:projectId/agents/:agentRef/sessions', ({ request }) => {
        urls.push(new URL(request.url).pathname + new URL(request.url).search)
        return HttpResponse.json({ success: true, data: [] })
      }),
    )

    await getAgentSessions('proj-1', 'agent-foo', { status: 'running', limit: 5 })

    expect(urls).toEqual(['/api/projects/proj-1/agents/agent-foo/sessions?status=running&limit=5'])
  })
})

describe('getGenericSessionSummary (client fn)', () => {
  it('builds the correct URL for a session summary', async () => {
    const urls: string[] = []
    server.use(
      http.get('*/api/projects/:projectId/agent-sessions/:sessionId', ({ request }) => {
        urls.push(new URL(request.url).pathname)
        return HttpResponse.json({ success: true, data: { sessionId: 's1' } })
      }),
    )

    await getGenericSessionSummary('proj-1', 'sess-abc')

    expect(urls).toEqual(['/api/projects/proj-1/agent-sessions/sess-abc'])
  })
})

describe('getGenericSessionTranscript (client fn)', () => {
  it('builds the correct URL for a selected runtime transcript', async () => {
    const urls: string[] = []
    server.use(
      http.get('*/api/projects/:projectId/agent-sessions/:sessionId/transcript', ({ request }) => {
        urls.push(new URL(request.url).pathname + new URL(request.url).search)
        return HttpResponse.json({ success: true, data: { turns: [], partCount: 0 } })
      }),
    )

    await getGenericSessionTranscript('proj-1', 'sess-abc', 'runtime-old')

    expect(urls).toEqual(['/api/projects/proj-1/agent-sessions/sess-abc/transcript?runtimeSessionId=runtime-old'])
  })
})

describe('launchAgentSession (client fn)', () => {
  it('POSTs to the launch endpoint with prompt and context', async () => {
    const captured: { url: string; method: string; body: unknown }[] = []
    server.use(
      http.post('*/api/projects/:projectId/agents/:agentRef/sessions', async ({ request }) => {
        captured.push({
          url: new URL(request.url).pathname,
          method: request.method,
          body: await request.json(),
        })
        return HttpResponse.json({ success: true, data: { sessionId: 's1' } })
      }),
    )

    await launchAgentSession('proj-1', 'agent-foo', {
      prompt: 'Hello',
      context: { issueNumber: 42 },
    })

    expect(captured).toEqual([
      {
        url: '/api/projects/proj-1/agents/agent-foo/sessions',
        method: 'POST',
        body: { prompt: 'Hello', context: { issueNumber: 42 } },
      },
    ])
  })
})

describe('postGenericFollowup (client fn)', () => {
  it('POSTs to the followup endpoint with text', async () => {
    const captured: { url: string; method: string; body: unknown }[] = []
    server.use(
      http.post('*/api/projects/:projectId/agent-sessions/:sessionId/followup', async ({ request }) => {
        captured.push({
          url: new URL(request.url).pathname,
          method: request.method,
          body: await request.json(),
        })
        return HttpResponse.json({ success: true, data: { status: 'sent' } })
      }),
    )

    await postGenericFollowup('proj-1', 'sess-abc', { text: 'Continue' })

    expect(captured).toEqual([
      {
        url: '/api/projects/proj-1/agent-sessions/sess-abc/followup',
        method: 'POST',
        body: { text: 'Continue' },
      },
    ])
  })
})

describe('cancelGenericSession (client fn)', () => {
  it('POSTs to the cancel endpoint', async () => {
    const captured: { url: string; method: string }[] = []
    server.use(
      http.post('*/api/projects/:projectId/agent-sessions/:sessionId/cancel', ({ request }) => {
        captured.push({ url: new URL(request.url).pathname, method: request.method })
        return HttpResponse.json({ success: true, data: { state: 'cancelled' } })
      }),
    )

    await cancelGenericSession('proj-1', 'sess-abc')

    expect(captured).toEqual([
      { url: '/api/projects/proj-1/agent-sessions/sess-abc/cancel', method: 'POST' },
    ])
  })
})

/* ── genericSessionSummaryQueryOptions ─────────────────── */
describe('genericSessionSummaryQueryOptions', () => {
  it('uses query key ["agent-session", projectId, sessionId]', () => {
    expect(genericSessionSummaryQueryOptions('proj-1', 'sess-abc').queryKey).toEqual([
      'agent-session',
      'proj-1',
      'sess-abc',
    ])
  })

  it('is enabled only when projectId and sessionId are present', () => {
    expect(genericSessionSummaryQueryOptions('proj-1', 'sess-abc').enabled).toBe(true)
    expect(genericSessionSummaryQueryOptions(null, 'sess-abc').enabled).toBe(false)
    expect(genericSessionSummaryQueryOptions('proj-1', '').enabled).toBe(false)
  })

  it('polls every 5s when session is not terminal', () => {
    const opts = genericSessionSummaryQueryOptions('proj-1', 'sess-abc')
    const interval = typeof opts.refetchInterval === 'function'
      ? opts.refetchInterval({ state: { data: { status: 'running' } as never } })
      : opts.refetchInterval
    expect(interval).toBe(5000)
  })

  it.each(['completed', 'failed', 'stopped', 'cancelled'])(
    'stops polling when session is terminal (%s)',
    (status) => {
      const opts = genericSessionSummaryQueryOptions('proj-1', 'sess-abc')
      const interval = typeof opts.refetchInterval === 'function'
        ? opts.refetchInterval({ state: { data: { status } as never } })
        : opts.refetchInterval
      expect(interval).toBe(false)
    },
  )
})

/* ── genericSessionTranscriptQueryOptions ──────────────── */
describe('genericSessionTranscriptQueryOptions', () => {
  it('includes the selected runtime in the transcript query key', () => {
    expect(genericSessionTranscriptQueryOptions('proj-1', 'sess-abc', 'runtime-old').queryKey).toEqual([
      'agent-session',
      'proj-1',
      'sess-abc',
      'transcript',
      'runtime-old',
    ])
  })

  it('is enabled only when projectId and sessionId are present', () => {
    expect(genericSessionTranscriptQueryOptions('proj-1', 'sess-abc').enabled).toBe(true)
    expect(genericSessionTranscriptQueryOptions(null, 'sess-abc').enabled).toBe(false)
  })

  it('polls when there are incomplete turns', () => {
    const opts = genericSessionTranscriptQueryOptions('proj-1', 'sess-abc')
    const interval = typeof opts.refetchInterval === 'function'
      ? opts.refetchInterval({ state: { data: { turns: [{ incomplete: true }] } as never } })
      : opts.refetchInterval
    expect(interval).toBe(5000)
  })

  it('stops polling when no incomplete turns remain', () => {
    const opts = genericSessionTranscriptQueryOptions('proj-1', 'sess-abc')
    const interval = typeof opts.refetchInterval === 'function'
      ? opts.refetchInterval({ state: { data: { turns: [{ incomplete: false }] } as never } })
      : opts.refetchInterval
    expect(interval).toBe(false)
  })
})

/* ── launchAgentSessionMutationOptions ─────────────────── */
describe('launchAgentSessionMutationOptions', () => {
  it('invalidates agent-status, agent-activity, and owning session list on success', () => {
    const qc = createInvalidationClient()
    launchAgentSessionMutationOptions('proj-1', qc).onSuccess(
      { sessionId: 's1' } as never,
      { agentRef: 'agent-foo', prompt: 'Hi' },
    )
    expect(qc.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['agent-status'] })
    expect(qc.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['agent-activity'] })
    expect(qc.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['agents', 'proj-1', 'agent-foo', 'sessions'] })
  })

  it('shows success toast on launch', () => {
    launchAgentSessionMutationOptions('proj-1', createInvalidationClient()).onSuccess(
      {} as never,
      { agentRef: 'a', prompt: 'p' },
    )
    expect(toast.success).toHaveBeenCalledWith('Session launched')
  })

  it('shows error toast on failure', () => {
    launchAgentSessionMutationOptions('proj-1', createInvalidationClient()).onError(new Error('NO_RUNNER'))
    expect(toast.error).toHaveBeenCalledWith('NO_RUNNER')
  })
})

/* ── genericFollowupMutationOptions ────────────────────── */
describe('genericFollowupMutationOptions', () => {
  it('invalidates agent-status, agent-activity, and session queries on success', () => {
    const qc = createInvalidationClient()
    genericFollowupMutationOptions('proj-1', qc).onSuccess({} as never, { sessionId: 'sess-abc', text: 'go on', agentRef: 'agent-foo' })
    expect(qc.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['agent-status'] })
    expect(qc.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['agent-activity'] })
    expect(qc.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['agent-session', 'proj-1', 'sess-abc'] })
    expect(qc.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['agent-session', 'proj-1', 'sess-abc', 'transcript'] })
    expect(qc.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['agents', 'proj-1', 'agent-foo', 'sessions'] })
  })

  it('skips agent sessions invalidation when agentRef is omitted', () => {
    const qc = createInvalidationClient()
    genericFollowupMutationOptions('proj-1', qc).onSuccess({} as never, { sessionId: 'sess-abc', text: 'go on' })
    const agentSessionsCalls = qc.invalidateQueries.mock.calls.filter(
      (c) => (c[0] as { queryKey: string[] }).queryKey[3] === 'sessions',
    )
    expect(agentSessionsCalls).toHaveLength(0)
  })

  it('shows success toast', () => {
    genericFollowupMutationOptions('proj-1', createInvalidationClient()).onSuccess({} as never, { sessionId: 's1', text: 't' })
    expect(toast.success).toHaveBeenCalledWith('Follow-up sent')
  })

  it('shows error toast on failure', () => {
    genericFollowupMutationOptions('proj-1', createInvalidationClient()).onError(new Error('SESSION_INACTIVE'))
    expect(toast.error).toHaveBeenCalledWith('SESSION_INACTIVE')
  })
})

/* ── cancelGenericSessionMutationOptions ───────────────── */
describe('cancelGenericSessionMutationOptions', () => {
  it('emits a success toast and invalidates session queries on cancelled', () => {
    const qc = createInvalidationClient()
    cancelGenericSessionMutationOptions('proj-1', qc).onSuccess(
      { state: 'cancelled' },
      { sessionId: 'sess-abc', agentRef: 'agent-foo' },
    )
    expect(toast.success).toHaveBeenCalledWith('Session cancelled')
    expect(toast.warning).not.toHaveBeenCalled()
    expect(qc.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['agent-session', 'proj-1', 'sess-abc'] })
    expect(qc.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['agent-status'] })
    expect(qc.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['agent-activity'] })
    expect(qc.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['agents', 'proj-1', 'agent-foo', 'sessions'] })
  })

  it.each(['completed', 'failed', 'stopped'])(
    'emits a success toast when the runner reports a terminal state (%s)',
    (terminalState) => {
      cancelGenericSessionMutationOptions('proj-1', createInvalidationClient()).onSuccess(
        { state: terminalState },
        { sessionId: 'sess-abc', agentRef: 'agent-foo' },
      )
      expect(toast.success).toHaveBeenCalledWith('Session cancelled')
      expect(toast.warning).not.toHaveBeenCalled()
    },
  )

  it('emits a warning toast and still invalidates session queries on not-cancellable', () => {
    const qc = createInvalidationClient()
    cancelGenericSessionMutationOptions('proj-1', qc).onSuccess(
      { state: 'not-cancellable' },
      { sessionId: 'sess-abc', agentRef: 'agent-foo' },
    )
    expect(toast.warning).toHaveBeenCalledWith('Session could not be cancelled')
    expect(toast.success).not.toHaveBeenCalled()
    expect(qc.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['agent-session', 'proj-1', 'sess-abc'] })
    expect(qc.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['agent-status'] })
    expect(qc.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['agent-activity'] })
    expect(qc.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['agents', 'proj-1', 'agent-foo', 'sessions'] })
  })

  it('skips agent sessions invalidation when agentRef is omitted', () => {
    const qc = createInvalidationClient()
    cancelGenericSessionMutationOptions('proj-1', qc).onSuccess({ state: 'cancelled' }, { sessionId: 'sess-abc' })
    const agentSessionsCalls = qc.invalidateQueries.mock.calls.filter(
      (c) => (c[0] as { queryKey: string[] }).queryKey[3] === 'sessions',
    )
    expect(agentSessionsCalls).toHaveLength(0)
  })

  it('shows error toast on failure', () => {
    cancelGenericSessionMutationOptions('proj-1', createInvalidationClient()).onError(new Error('NOT_CANCELLABLE'))
    expect(toast.error).toHaveBeenCalledWith('NOT_CANCELLABLE')
    expect(toast.success).not.toHaveBeenCalled()
    expect(toast.warning).not.toHaveBeenCalled()
  })
})
