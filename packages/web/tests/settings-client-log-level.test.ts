import { afterEach, describe, expect, it, vi } from 'vitest'
import { getLogLevel, setLogLevel } from '../src/entities/settings/api/client'

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('settings client log level', () => {
  it('loads log level from /api/config instead of the missing /api/log-level endpoint', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = typeof input === 'string' ? input : input.toString()
      if (url.endsWith('/api/config')) {
        return Response.json({ success: true, data: { logLevel: 'WARN' } })
      }
      return Response.json({ success: false, error: 'not found' }, { status: 404 })
    })
    vi.stubGlobal('fetch', fetchMock)

    const result = await getLogLevel()

    expect(result).toEqual({ level: 'WARN' })
    expect(fetchMock).toHaveBeenCalledTimes(1)
    const calledUrl = fetchMock.mock.calls[0]?.[0]
    const calledUrlString = typeof calledUrl === 'string' ? calledUrl : (calledUrl as URL).toString()
    expect(calledUrlString).toBe('/api/config')
    expect(calledUrlString).not.toContain('/api/log-level')
  })

  it('persists log level changes through PUT /api/config/logLevel', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === 'string' ? input : input.toString()
      if (init?.method === 'PUT' && url.endsWith('/api/config/logLevel')) {
        return Response.json({ success: true, data: { logLevel: 'ERROR' } })
      }
      return Response.json({ success: false, error: 'unexpected' }, { status: 500 })
    })
    vi.stubGlobal('fetch', fetchMock)

    const result = await setLogLevel('ERROR')

    expect(result).toEqual({ level: 'ERROR' })
    const calledUrl = fetchMock.mock.calls[0]?.[0]
    const calledUrlString = typeof calledUrl === 'string' ? calledUrl : (calledUrl as URL).toString()
    expect(calledUrlString).toBe('/api/config/logLevel')
  })

  it('surfaces server-side validation failures from the config API', async () => {
    const fetchMock = vi.fn(async () => Response.json({
      success: false,
      error: 'logLevel must be one of DEBUG, INFO, WARN, ERROR',
      code: 'bad_request',
    }, { status: 400 }))
    vi.stubGlobal('fetch', fetchMock)

    await expect(setLogLevel('TRACE')).rejects.toMatchObject({
      name: 'ApiError',
      message: 'logLevel must be one of DEBUG, INFO, WARN, ERROR',
      status: 400,
    })
  })
})
