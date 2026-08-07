import { describe, expect, it, afterEach } from 'vitest'
import { http, HttpResponse } from 'msw'
import { server, useMswServer } from '../../../tests/support/msw'
import { ApiError, projectApiPath, request, setUnauthorizedListener } from './client'

useMswServer()

function requestPath(request: Request) {
  const url = new URL(request.url)
  return `${url.pathname}${url.search}`
}

describe('unauthorized listener', () => {
  afterEach(() => {
    setUnauthorizedListener(null)
  })

  it('notifies the listener on a business 401', async () => {
    const listener = vi.fn()
    setUnauthorizedListener(listener)
    server.use(
      http.get('*/api/agent/status', () =>
        HttpResponse.json({ success: false, error: 'Authentication required.', code: 'unauthorized' }, { status: 401 }),
      ),
    )

    await expect(request('/agent/status')).rejects.toMatchObject({ status: 401 })

    expect(listener).toHaveBeenCalledTimes(1)
  })

  it('does not notify the listener on auth-surface 401s', async () => {
    const listener = vi.fn()
    setUnauthorizedListener(listener)
    server.use(
      http.get('*/api/auth/session', () =>
        HttpResponse.json({ success: false, error: 'Authentication required.', code: 'unauthorized' }, { status: 401 }),
      ),
    )

    await expect(request('/auth/session')).rejects.toMatchObject({ status: 401 })

    expect(listener).not.toHaveBeenCalled()
  })
})

describe('api client', () => {
  it('surfaces empty responses as ApiError instead of JSON parse errors', async () => {
    const requests: Request[] = []
    server.use(
      http.get('*/api/agent/status', ({ request }) => {
        requests.push(request)
        return new HttpResponse(null, { status: 400 })
      }),
    )

    await expect(request('/agent/status')).rejects.toMatchObject({
      name: 'ApiError',
      message: 'Empty response from /agent/status',
      status: 400,
    })
    expect(requests).toHaveLength(1)
    expect(requestPath(requests[0])).toBe('/api/agent/status')
    expect(requests[0].method).toBe('GET')
    expect(requests[0].headers.get('content-type')).toBe('application/json')
  })

  it('preserves api error details from JSON responses', async () => {
    const requests: Request[] = []
    server.use(
      http.get('*/api/agent/status', ({ request }) => {
        requests.push(request)
        return HttpResponse.json({
          success: false,
          error: 'No active project',
          code: 'bad_request',
        }, { status: 400 })
      }),
    )

    await expect(request('/agent/status')).rejects.toMatchObject({
      name: 'ApiError',
      message: 'No active project',
      status: 400,
      code: 'bad_request',
    })
    expect(requests).toHaveLength(1)
    expect(requestPath(requests[0])).toBe('/api/agent/status')
    expect(requests[0].method).toBe('GET')
    expect(requests[0].headers.get('content-type')).toBe('application/json')
  })

  it('uses ApiError for invalid JSON responses', async () => {
    const requests: Request[] = []
    server.use(
      http.get('*/api/opencode/models', ({ request }) => {
        requests.push(request)
        return new HttpResponse('{', { status: 500 })
      }),
    )

    await expect(request('/opencode/models')).rejects.toBeInstanceOf(ApiError)
    await expect(request('/opencode/models')).rejects.toMatchObject({
      message: 'Invalid JSON response from /opencode/models',
      status: 500,
    })
    expect(requests).toHaveLength(2)
    expect(requests.map(requestPath)).toEqual(['/api/opencode/models', '/api/opencode/models'])
    expect(requests.every((request) => request.method === 'GET')).toBe(true)
    expect(requests.every((request) => request.headers.get('content-type') === 'application/json')).toBe(true)
  })

  it('builds project-scoped API paths', () => {
    expect(projectApiPath('project-1', '/issues')).toBe('/projects/project-1/issues')
    expect(projectApiPath('project-1', 'issues/12')).toBe('/projects/project-1/issues/12')
    expect(projectApiPath('my project', '/issues')).toBe('/projects/my%20project/issues')
  })
})
