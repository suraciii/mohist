import { describe, expect, it } from 'vitest'
import { http, HttpResponse } from 'msw'
import { server, useMswServer } from '../../../../tests/support/msw'
import { getUnifiedSessionSummary, getUnifiedSessionTranscript, postFollowup, unifiedSessionTranscriptQueryOptions } from './client'

useMswServer()

describe('unified session reads', () => {
  it('reads summary and transcript by stable session id', async () => {
    const urls: string[] = []
    server.use(
      http.get('*/api/projects/:projectId/sessions/:sessionId', ({ request }) => {
        urls.push(new URL(request.url).pathname)
        return HttpResponse.json({ success: true, data: { id: 'session-1', source: 'workflow' } })
      }),
      http.get('*/api/projects/:projectId/sessions/:sessionId/transcript', ({ request }) => {
        urls.push(new URL(request.url).pathname + new URL(request.url).search)
        return HttpResponse.json({ success: true, data: { turns: [], partCount: 0, lastActivityAt: null } })
      }),
    )

    await getUnifiedSessionSummary('proj-1', 'session/1')
    await getUnifiedSessionTranscript('proj-1', 'session/1', 'runtime-1')

    expect(urls).toEqual([
      '/api/projects/proj-1/sessions/session%2F1',
      '/api/projects/proj-1/sessions/session%2F1/transcript?runtimeSessionId=runtime-1',
    ])
  })

  it('enables the transcript query by stable session id before a runtime binding exists', () => {
    expect(unifiedSessionTranscriptQueryOptions('proj-1', 'session-1').enabled).toBe(true)
    expect(unifiedSessionTranscriptQueryOptions('proj-1', 'session-1', 'runtime-1').enabled).toBe(true)
  })
})

describe('postFollowup', () => {
  it('generates and sends an idempotency key when one is not supplied', async () => {
    const keys: string[] = []
    server.use(
      http.post('*/api/projects/:projectId/issues/:number/sessions/:name/followup', ({ request }) => {
        keys.push(request.headers.get('Idempotency-Key') ?? '')
        return HttpResponse.json({ success: true, data: { status: 'accepted' } })
      }),
    )

    await postFollowup(42, 'session-a', 'Continue', 'proj-1')

    expect(keys).toHaveLength(1)
    expect(keys[0]).not.toBe('')
  })

  it('sends the same supplied key on a retry', async () => {
    const keys: string[] = []
    server.use(
      http.post('*/api/projects/:projectId/issues/:number/sessions/:name/followup', ({ request }) => {
        keys.push(request.headers.get('Idempotency-Key') ?? '')
        return HttpResponse.json({ success: true, data: { status: 'accepted' } })
      }),
    )

    await postFollowup(42, 'session-a', 'Continue', 'proj-1', 'retry-key')
    await postFollowup(42, 'session-a', 'Continue', 'proj-1', 'retry-key')

    expect(keys).toEqual(['retry-key', 'retry-key'])
  })
})
