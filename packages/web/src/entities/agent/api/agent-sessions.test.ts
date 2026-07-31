import { describe, expect, it, vi } from 'vitest'
import { http, HttpResponse } from 'msw'
import { server, useMswServer } from '../../../../tests/support/msw'
import { toast } from 'sonner'
import {
  cancelGenericSession,
  stopGenericSession,
  cancelGenericSessionMutationOptions,
  genericFollowupMutationOptions,
  genericSessionSummaryQueryOptions,
  genericSessionTranscriptQueryOptions,
  getAgentSessions,
  getGenericSessionSummary,
  getGenericSessionTranscript,
  launchAgentSession,
  launchAgentSessionMutationOptions,
  getAgentLaunchObservationMeaning,
  postGenericFollowup,
  agentInputAttachmentContentPath,
} from './agent-sessions'
useMswServer()

describe('getAgentLaunchObservationMeaning', () => {
  it.each([
    ['accepted', 'observe'],
    ['queued', 'observe'],
    ['executing', 'observe'],
    ['completed', 'result'],
    ['failed', 'result'],
    ['Unknown', 'reconcile'],
  ] as const)('maps %s from the DTO without using Session activity', (turnStatus, meaning) => {
    expect(getAgentLaunchObservationMeaning({ turnStatus })).toBe(meaning)
  })
})

describe('agentInputAttachmentContentPath', () => {
  it('keeps attachment content scoped to the session input', () => {
    expect(agentInputAttachmentContentPath('proj-1', 'session/ignored', 'input-1', 'att-1')).toBe(
      '/projects/proj-1/agent-sessions/session%2Fignored/inputs/input-1/attachments/att-1/content',
    )
  })
})

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
  it('POSTs to the launch endpoint with prompt, context, and explicit attachments', async () => {
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
      context: { issueNumber: 42, },
      attachments: ['att-1'],
    })

    expect(captured).toEqual([
      {
        url: '/api/projects/proj-1/agents/agent-foo/sessions',
        method: 'POST',
        body: { prompt: 'Hello', context: { issueNumber: 42, }, attachments: ['att-1'] },
      },
    ])
  })

  it('forwards the idempotency key as a request header', async () => {
    let key = ''
    server.use(
      http.post('*/api/projects/:projectId/agents/:agentRef/sessions', ({ request }) => {
        key = request.headers.get('Idempotency-Key') ?? ''
        return HttpResponse.json({ success: true, data: { sessionId: 's1' } })
      }),
    )

    await launchAgentSession('proj-1', 'agent-foo', { prompt: 'Hello' }, 'retry-key')

    expect(key).toBe('retry-key')
  })
})

describe('postGenericFollowup (client fn)', () => {
  it('POSTs to the followup endpoint with text and explicit attachments', async () => {
    const captured: { url: string; method: string; body: unknown; key: string }[] = []
    server.use(
      http.post('*/api/projects/:projectId/agent-sessions/:sessionId/followup', async ({ request }) => {
        captured.push({
          url: new URL(request.url).pathname,
          method: request.method,
          body: await request.json(),
          key: request.headers.get('Idempotency-Key') ?? '',
        })
        return HttpResponse.json({ success: true, data: { status: 'accepted' } })
      }),
    )

    await postGenericFollowup('proj-1', 'sess-abc', { text: 'Continue', attachments: ['att-1'] }, 'followup-key')

    expect(captured).toEqual([
      {
        url: '/api/projects/proj-1/agent-sessions/sess-abc/followup',
        method: 'POST',
        body: { text: 'Continue', attachments: ['att-1'] },
        key: 'followup-key',
      },
    ])
  })
})

describe('cancelGenericSession (client fn)', () => {
  it('POSTs to the cancel endpoint', async () => {
    const captured: { url: string; method: string; body: unknown }[] = []
    server.use(
      http.post('*/api/projects/:projectId/agent-sessions/:sessionId/cancel', async ({ request }) => {
        captured.push({ url: new URL(request.url).pathname, method: request.method, body: await request.json() })
        return HttpResponse.json({ success: true, data: { state: 'cancelled' } })
      }),
    )

    await cancelGenericSession('proj-1', 'sess-abc', 'turn-1')

    expect(captured).toEqual([
      { url: '/api/projects/proj-1/agent-sessions/sess-abc/cancel', method: 'POST', body: { turnId: 'turn-1' } },
    ])
  })
})

describe('stopGenericSession (client fn)', () => {
  it('POSTs the targeted Turn to the stop endpoint', async () => {
    let body: unknown
    server.use(
      http.post('*/api/projects/:projectId/agent-sessions/:sessionId/stop', async ({ request }) => {
        body = await request.json()
        return HttpResponse.json({ success: true, data: { state: 'stop-requested' } })
      }),
    )

    await stopGenericSession('proj-1', 'sess-abc', 'turn-2')

    expect(body).toEqual({ turnId: 'turn-2' })
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

  // Issue 484: refetchInterval is now driven by `activity`, not `status`.
  // Sessions never enter a terminal status (completed/failed/stopped/
  // cancelled) — execution finishing brings activity back to `idle`.
  // Polling continues while activity is `active` or `unknown`, and stops
  // only when activity resolves to `idle` (the follow-up-able quiescent
  // state). `status` is no longer consulted.
  it('polls every 5s while activity is active', () => {
    const opts = genericSessionSummaryQueryOptions('proj-1', 'sess-abc')
    const interval = typeof opts.refetchInterval === 'function'
      ? opts.refetchInterval({ state: { data: { activity: 'active' } as never } })
      : opts.refetchInterval
    expect(interval).toBe(5000)
  })

  it('polls every 5s while activity is unknown (awaiting resolution)', () => {
    const opts = genericSessionSummaryQueryOptions('proj-1', 'sess-abc')
    const interval = typeof opts.refetchInterval === 'function'
      ? opts.refetchInterval({ state: { data: { activity: 'unknown' } as never } })
      : opts.refetchInterval
    expect(interval).toBe(5000)
  })

  it('stops polling when activity returns to idle', () => {
    const opts = genericSessionSummaryQueryOptions('proj-1', 'sess-abc')
    const interval = typeof opts.refetchInterval === 'function'
      ? opts.refetchInterval({ state: { data: { activity: 'idle' } as never } })
      : opts.refetchInterval
    expect(interval).toBe(false)
  })

  // Issue 484: legacy `status` values (running/completed/failed/stopped/
  // cancelled) are no longer read by refetchInterval, so they must NOT stop
  // polling on their own. Sessions carrying a legacy status but no resolved
  // activity keep polling until activity resolves to idle. This guards
  // against a regression that re-introduces status-based gating.
  it.each(['completed', 'failed', 'stopped', 'cancelled'])(
    'does not stop polling based on a legacy status value (%s) — only activity=idle stops polling',
    (status) => {
      const opts = genericSessionSummaryQueryOptions('proj-1', 'sess-abc')
      const interval = typeof opts.refetchInterval === 'function'
        ? opts.refetchInterval({ state: { data: { status, activity: 'active' } as never } })
        : opts.refetchInterval
      expect(interval).toBe(5000)
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
  it('invalidates agent status, list Availability, and owning session list on success', () => {
    const qc = createInvalidationClient()
    launchAgentSessionMutationOptions('proj-1', qc).onSuccess(
      { sessionId: 's1' } as never,
      { agentRef: 'agent-foo', prompt: 'Hi' },
    )
    expect(qc.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['agent-status'] })
    expect(qc.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['agent-activity'] })
    expect(qc.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['agent-availability', 'proj-1'] })
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
    genericFollowupMutationOptions('proj-1', createInvalidationClient()).onSuccess(
      { status: 'accepted' },
      { sessionId: 's1', text: 't' },
    )
    expect(toast.success).toHaveBeenCalledWith('Follow-up sent')
  })

  it('shows the confirmed rejection', () => {
    genericFollowupMutationOptions('proj-1', createInvalidationClient()).onSuccess(
      { status: 'rejected', error: 'Queue is full' },
      { sessionId: 's1', text: 't' },
    )
    expect(toast.error).toHaveBeenCalledWith('Queue is full')
    expect(toast.success).not.toHaveBeenCalled()
  })

  it('shows unknown outcome without claiming delivery', () => {
    genericFollowupMutationOptions('proj-1', createInvalidationClient()).onSuccess(
      { status: 'unknown' },
      { sessionId: 's1', text: 't' },
    )
    expect(toast.warning).toHaveBeenCalledWith('Follow-up outcome is unknown. Retry with the same key.')
    expect(toast.success).not.toHaveBeenCalled()
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
      { sessionId: 'sess-abc', turnId: 'turn-1', operation: 'cancel', agentRef: 'agent-foo' },
    )
    expect(toast.success).toHaveBeenCalledWith('cancelled')
    expect(toast.warning).not.toHaveBeenCalled()
    expect(qc.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['agent-session', 'proj-1', 'sess-abc'] })
    expect(qc.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['agent-status'] })
    expect(qc.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['agent-activity'] })
    expect(qc.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['agents', 'proj-1', 'agent-foo', 'sessions'] })
  })

  it.each(['stop-requested', 'stopped', 'unknown'])(
    'emits a success toast for the shared Turn state (%s)',
    (terminalState) => {
      cancelGenericSessionMutationOptions('proj-1', createInvalidationClient()).onSuccess(
        { state: terminalState },
        { sessionId: 'sess-abc', turnId: 'turn-1', operation: 'stop', agentRef: 'agent-foo' },
      )
      expect(toast.success).toHaveBeenCalledWith(terminalState)
      expect(toast.warning).not.toHaveBeenCalled()
    },
  )

  it('emits a warning toast and still invalidates session queries on not-cancellable', () => {
    const qc = createInvalidationClient()
    cancelGenericSessionMutationOptions('proj-1', qc).onSuccess(
      { state: 'not-cancellable' },
      { sessionId: 'sess-abc', turnId: 'turn-1', operation: 'cancel', agentRef: 'agent-foo' },
    )
    expect(toast.warning).toHaveBeenCalledWith('Turn cancel was not applied')
    expect(toast.success).not.toHaveBeenCalled()
    expect(qc.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['agent-session', 'proj-1', 'sess-abc'] })
    expect(qc.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['agent-status'] })
    expect(qc.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['agent-activity'] })
    expect(qc.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['agents', 'proj-1', 'agent-foo', 'sessions'] })
  })

  it('skips agent sessions invalidation when agentRef is omitted', () => {
    const qc = createInvalidationClient()
    cancelGenericSessionMutationOptions('proj-1', qc).onSuccess({ state: 'cancelled' }, { sessionId: 'sess-abc', turnId: 'turn-1', operation: 'cancel' })
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
