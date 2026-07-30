import { describe, expect, it } from 'vitest'
import { http, HttpResponse } from 'msw'
import { server, useMswServer } from '../../../../tests/support/msw'
import { postFollowup } from './client'

useMswServer()

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
