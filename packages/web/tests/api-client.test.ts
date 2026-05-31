import { afterEach, describe, expect, it, vi } from 'vitest'
import { ApiError, request, withProject } from '../src/shared/api/client'

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('api client', () => {
  it('surfaces empty responses as ApiError instead of JSON parse errors', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response('', { status: 400 })))

    await expect(request('/agent/status')).rejects.toMatchObject({
      name: 'ApiError',
      message: 'Empty response from /agent/status',
      status: 400,
    })
  })

  it('preserves api error details from JSON responses', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => Response.json({
        success: false,
        error: 'No active project',
        code: 'bad_request',
      }, { status: 400 })),
    )

    await expect(request('/agent/status')).rejects.toMatchObject({
      name: 'ApiError',
      message: 'No active project',
      status: 400,
      code: 'bad_request',
    })
  })

  it('uses ApiError for invalid JSON responses', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response('{', { status: 500 })))

    await expect(request('/opencode/models')).rejects.toBeInstanceOf(ApiError)
    await expect(request('/opencode/models')).rejects.toMatchObject({
      message: 'Invalid JSON response from /opencode/models',
      status: 500,
    })
  })

  it('passes project context through a header without dropping existing headers', async () => {
    const init = withProject({
      method: 'POST',
      headers: { 'X-Existing': 'keep' },
      body: JSON.stringify({ title: 'Header scoped issue' }),
    }, 'project-1')

    const headers = new Headers(init?.headers)

    expect(headers.get('X-Mohist-Project-Id')).toBe('project-1')
    expect(headers.get('X-Existing')).toBe('keep')
    expect(init?.body).toBe(JSON.stringify({ title: 'Header scoped issue' }))
  })
})
