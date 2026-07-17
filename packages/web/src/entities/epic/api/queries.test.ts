import { describe, expect, it, vi } from 'vitest'
import { http, HttpResponse } from 'msw'
import { server, useMswServer } from '../../../../tests/support/msw'
import { toast } from 'sonner'
import {
  epicEventsQueryOptions,
  pauseEpicMutationOptions,
  reopenEpicMutationOptions,
  resumeEpicMutationOptions,
  startEpicMutationOptions,
  startIssueMutationOptions,
} from './queries'

useMswServer()

function createInvalidationClient() {
  return { invalidateQueries: vi.fn() }
}

/* ── startIssueMutationOptions ──────────────────────────── */
describe('startIssueMutationOptions', () => {
  it('invokes startIssue(number, projectId) in mutationFn', async () => {
    const captured: { url: string; method: string }[] = []
    server.use(
      http.post('*/api/projects/:projectId/issues/:number/start', ({ request }) => {
        captured.push({ url: new URL(request.url).pathname, method: request.method })
        return HttpResponse.json({ success: true, data: { issue: { number: 7 }, message: 'started' } })
      }),
    )

    await startIssueMutationOptions('proj-abc', createInvalidationClient()).mutationFn(7)

    expect(captured).toEqual([{ url: '/api/projects/proj-abc/issues/7/start', method: 'POST' }])
  })

  it('forwards the projectId resolved from useProject at call time', async () => {
    const captured: string[] = []
    server.use(
      http.post('*/api/projects/:projectId/issues/:number/start', ({ request }) => {
        captured.push(new URL(request.url).pathname)
        return HttpResponse.json({ success: true, data: { issue: { number: 11 }, message: 'started' } })
      }),
    )

    await startIssueMutationOptions('proj-xyz', createInvalidationClient()).mutationFn(11)

    expect(captured).toEqual(['/api/projects/proj-xyz/issues/11/start'])
  })

  it('throws when projectId is null (projectApiPath requires a project)', () => {
    const options = startIssueMutationOptions(null, createInvalidationClient())
    expect(() => options.mutationFn(1)).toThrow('Project is required')
  })

  it('invalidates both ["epics"] and ["issues"] query keys on success', () => {
    const qc = createInvalidationClient()
    startIssueMutationOptions('proj-1', qc).onSuccess()

    const invalidatedKeys = qc.invalidateQueries.mock.calls.map((call) => call[0].queryKey)
    expect(invalidatedKeys).toContainEqual(['epics'])
    expect(invalidatedKeys).toContainEqual(['issues'])
    expect(qc.invalidateQueries).toHaveBeenCalledTimes(2)
  })

  it('toasts "Issue started" on success', () => {
    startIssueMutationOptions('proj-1', createInvalidationClient()).onSuccess()
    expect(toast.success).toHaveBeenCalledWith('Issue started')
  })

  it('toasts the error message on failure', () => {
    startIssueMutationOptions('proj-1', createInvalidationClient()).onError(new Error('start refused'))
    expect(toast.error).toHaveBeenCalledWith('start refused')
  })

  it('falls back to "Request failed" when the error has no message', () => {
    startIssueMutationOptions('proj-1', createInvalidationClient()).onError(new Error(''))
    expect(toast.error).toHaveBeenCalledWith('Request failed')
  })

  it('does NOT invalidate issue queries with a more specific key on success (only the prefix)', () => {
    const qc = createInvalidationClient()
    startIssueMutationOptions('proj-1', qc).onSuccess()

    const keys = qc.invalidateQueries.mock.calls.map((call) => call[0].queryKey)
    expect(keys).toEqual([['epics'], ['issues']])
  })
})

/* ── pauseEpicMutationOptions ───────────────────────────── */
describe('pauseEpicMutationOptions', () => {
  it('calls pauseEpic(number, reason, projectId) in mutationFn', async () => {
    const captured: { url: string; method: string; body: unknown }[] = []
    server.use(
      http.post('*/api/projects/:projectId/epics/:number/pause', async ({ request }) => {
        captured.push({ url: new URL(request.url).pathname, method: request.method, body: await request.json() })
        return HttpResponse.json({ success: true, data: { number: 1, status: 'paused' } })
      }),
    )

    await pauseEpicMutationOptions('proj-1', createInvalidationClient()).mutationFn({ number: 1, reason: 'wait' })

    expect(captured).toEqual([
      { url: '/api/projects/proj-1/epics/1/pause', method: 'POST', body: { reason: 'wait' } },
    ])
  })

  it('invalidates the project-scoped detail query (["epics", projectId, number]) on success', () => {
    const qc = createInvalidationClient()
    pauseEpicMutationOptions('proj-1', qc).onSuccess({ number: 1, status: 'paused' } as never, { number: 1 })
    expect(qc.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['epics', 'proj-1', 1] })
    expect(qc.invalidateQueries).not.toHaveBeenCalledWith({ queryKey: ['epics', 1] })
  })

  it('toasts "Epic paused" on success', () => {
    pauseEpicMutationOptions('proj-1', createInvalidationClient()).onSuccess({ number: 1, status: 'paused' } as never, { number: 1 })
    expect(toast.success).toHaveBeenCalledWith('Epic paused')
  })
})

/* ── resumeEpicMutationOptions ──────────────────────────── */
describe('resumeEpicMutationOptions', () => {
  it('calls resumeEpic(number, projectId) in mutationFn', async () => {
    const captured: { url: string; method: string }[] = []
    server.use(
      http.post('*/api/projects/:projectId/epics/:number/resume', ({ request }) => {
        captured.push({ url: new URL(request.url).pathname, method: request.method })
        return HttpResponse.json({ success: true, data: { number: 1, status: 'running' } })
      }),
    )

    await resumeEpicMutationOptions('proj-1', createInvalidationClient()).mutationFn(1)

    expect(captured).toEqual([{ url: '/api/projects/proj-1/epics/1/resume', method: 'POST' }])
  })

  it('invalidates the project-scoped detail query on success', () => {
    const qc = createInvalidationClient()
    resumeEpicMutationOptions('proj-1', qc).onSuccess({ number: 1, status: 'running' } as never, 1)
    expect(qc.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['epics', 'proj-1', 1] })
    expect(qc.invalidateQueries).not.toHaveBeenCalledWith({ queryKey: ['epics', 1] })
  })

  it('toasts "Epic resumed" on success', () => {
    resumeEpicMutationOptions('proj-1', createInvalidationClient()).onSuccess({ number: 1, status: 'running' } as never, 1)
    expect(toast.success).toHaveBeenCalledWith('Epic resumed')
  })
})

/* ── startEpicMutationOptions ───────────────────────────── */
describe('startEpicMutationOptions', () => {
  it('calls startEpic(number, projectId) in mutationFn', async () => {
    const captured: { url: string; method: string }[] = []
    server.use(
      http.post('*/api/projects/:projectId/epics/:number/start', ({ request }) => {
        captured.push({ url: new URL(request.url).pathname, method: request.method })
        return HttpResponse.json({ success: true, data: { number: 1, status: 'running' } })
      }),
    )

    await startEpicMutationOptions('proj-1', createInvalidationClient()).mutationFn(1)

    expect(captured).toEqual([{ url: '/api/projects/proj-1/epics/1/start', method: 'POST' }])
  })

  it('throws when projectId is null', () => {
    const options = startEpicMutationOptions(null, createInvalidationClient())
    expect(() => options.mutationFn(1)).toThrow('Project is required')
  })

  it('invalidates the project-scoped epic detail query on success', () => {
    const qc = createInvalidationClient()
    startEpicMutationOptions('proj-1', qc).onSuccess({ number: 1, status: 'running' } as never, 1)
    expect(qc.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['epics'] })
    expect(qc.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['epics', 'proj-1', 1] })
    expect(qc.invalidateQueries).not.toHaveBeenCalledWith({ queryKey: ['epics', 1] })
  })

  it('toasts "Epic started" on success', () => {
    startEpicMutationOptions('proj-1', createInvalidationClient()).onSuccess({ number: 1, status: 'running' } as never, 1)
    expect(toast.success).toHaveBeenCalledWith('Epic started')
  })

  it('surfaces start failures through toast.error', () => {
    startEpicMutationOptions('proj-1', createInvalidationClient()).onError(new Error('EPIC_NOT_RUNNING'))
    expect(toast.error).toHaveBeenCalledWith('EPIC_NOT_RUNNING')
  })
})

/* ── reopenEpicMutationOptions ──────────────────────────── */
describe('reopenEpicMutationOptions', () => {
  it('calls reopenEpic(number, projectId) in mutationFn', async () => {
    const captured: { url: string; method: string }[] = []
    server.use(
      http.post('*/api/projects/:projectId/epics/:number/reopen', ({ request }) => {
        captured.push({ url: new URL(request.url).pathname, method: request.method })
        return HttpResponse.json({ success: true, data: { number: 1, status: 'idle' } })
      }),
    )

    await reopenEpicMutationOptions('proj-1', createInvalidationClient()).mutationFn(1)

    expect(captured).toEqual([{ url: '/api/projects/proj-1/epics/1/reopen', method: 'POST' }])
  })

  it('invalidates epic and issue caches after reopening an epic', () => {
    const qc = createInvalidationClient()
    reopenEpicMutationOptions('proj-1', qc).onSuccess({ number: 1, status: 'idle' } as never, 1)
    expect(qc.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['epics'] })
    expect(qc.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['epics', 'proj-1', 1] })
    expect(qc.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['issues'] })
  })

  it('toasts "Epic reopened" on success', () => {
    reopenEpicMutationOptions('proj-1', createInvalidationClient()).onSuccess({ number: 1, status: 'idle' } as never, 1)
    expect(toast.success).toHaveBeenCalledWith('Epic reopened')
  })
})

/* ── epicEventsQueryOptions ─────────────────────────────── */
describe('epicEventsQueryOptions', () => {
  it('uses queryKey ["epics", projectId, number, "events"] and disables the query when number is absent', () => {
    const opts = epicEventsQueryOptions(null, 'proj-1')
    expect(opts.queryKey).toEqual(['epics', 'proj-1', null, 'events'])
    expect(opts.enabled).toBe(false)
  })

  it('fetches via getEpicEvents(number, projectId) and exposes the resolved data', async () => {
    const events = [
      {
        id: 1,
        eventId: 'evt-1',
        source: '/mohist/projects/proj-1/epics/1',
        type: 'com.mohist.epic.created',
        specVersion: '1.0',
        subject: '1',
        time: '2026-06-30T12:00:00+00:00',
        dataContentType: 'application/json',
        data: { title: 'Auth epic', description: 'desc', priority: 'p2' },
        extensions: { projectid: 'proj-1', epic: '1' },
      },
    ]
    const captured: string[] = []
    server.use(
      http.get('*/api/projects/:projectId/epics/:number/events', ({ request }) => {
        captured.push(new URL(request.url).pathname)
        return HttpResponse.json({ success: true, data: events })
      }),
    )

    const data = await epicEventsQueryOptions(1, 'proj-1').queryFn()

    expect(captured).toEqual(['/api/projects/proj-1/epics/1/events'])
    expect(data).toEqual(events)
  })

  it('does not enable the query when useProject has no projectId', () => {
    expect(epicEventsQueryOptions(1, null).enabled).toBe(false)
  })

  it('passes enabled=false through to the underlying query', () => {
    expect(epicEventsQueryOptions(1, 'proj-1', false).enabled).toBe(false)
  })
})
