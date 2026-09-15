import { describe, expect, it } from 'vitest'
import { http, HttpResponse } from 'msw'
import { server, useMswServer } from '../../../../tests/support/msw'
import { getRunner, getRunners } from './client'

useMswServer()

function successResponse(data: unknown) {
  return HttpResponse.json({ success: true, data })
}

describe('global Runner client', () => {
  it('requests the application-scoped list without a Project reference', async () => {
    const requests: Request[] = []
    const snapshot = {
      observedAt: '2026-01-01T00:00:00Z',
      inventory: { state: 'first-install', nextActions: [] },
      runners: [],
    }
    server.use(
      http.get('*/api/runners', ({ request }) => {
        requests.push(request)
        return successResponse(snapshot)
      }),
    )

    await expect(getRunners()).resolves.toEqual(snapshot)
    expect(requests).toHaveLength(1)
    expect(new URL(requests[0].url).pathname).toBe('/api/runners')
  })

  it('requests detail by Runner ID on the global route', async () => {
    const requests: Request[] = []
    const detail = { observedAt: '2026-01-01T00:00:00Z', runner: { identity: { id: 'runner/a' } } }
    server.use(
      http.get('*/api/runners/:runnerId', ({ request }) => {
        requests.push(request)
        return successResponse(detail)
      }),
    )

    await expect(getRunner('runner/a')).resolves.toEqual(detail)
    expect(new URL(requests[0].url).pathname).toBe('/api/runners/runner%2Fa')
  })
})
