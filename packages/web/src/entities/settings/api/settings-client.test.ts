import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  agentRuntimeToConfigKey,
  configToAgentRuntime,
  getAgentRuntime,
  getLogLevel,
  setLogLevel,
  updateAgentRuntime,
} from './client'

afterEach(() => {
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

describe('settings client agent runtime adapter', () => {
  it('reads runtime config from /api/config instead of the missing /api/agent-runtime endpoint', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = typeof input === 'string' ? input : input.toString()
      if (url.endsWith('/api/config')) {
        return Response.json({
          success: true,
          data: {
            maxConcurrentAgents: 4,
            agentTimeout: 900,
            taskTimeout: 300,
            stageTimeout: 1800,
            pollInterval: 10000,
            maxGracePeriods: 5,
          },
        })
      }
      return Response.json({ success: false, error: 'not found' }, { status: 404 })
    })
    vi.stubGlobal('fetch', fetchMock)

    const result = await getAgentRuntime()

    expect(result).toEqual({
      maxConcurrent: 4,
      timeout: 900000,
      taskTimeout: 300000,
      stageTimeout: 1800000,
      pollInterval: 10000,
      maxGracePeriods: 5,
    })
    expect(fetchMock).toHaveBeenCalledTimes(1)
    const calledUrl = fetchMock.mock.calls[0]?.[0]
    const calledUrlString = typeof calledUrl === 'string' ? calledUrl : (calledUrl as URL).toString()
    expect(calledUrlString).toBe('/api/config')
    expect(calledUrlString).not.toContain('/api/agent-runtime')
  })

  it('propagates a load failure from /api/config without falling back to defaults', async () => {
    const fetchMock = vi.fn(async () => Response.json({
      success: false,
      error: 'server unavailable',
      code: 'server_error',
    }, { status: 500 }))
    vi.stubGlobal('fetch', fetchMock)

    await expect(getAgentRuntime()).rejects.toMatchObject({
      name: 'ApiError',
      message: 'server unavailable',
      status: 500,
    })
  })

  it('converts server seconds to millisecond UI values and vice versa', () => {
    const config = {
      agentTimeout: 600,
      taskTimeout: 600,
      stageTimeout: 3600,
      maxConcurrentAgents: 3,
      maxGracePeriods: 3,
      pollInterval: 5000,
      logLevel: 'INFO',
    } as Parameters<typeof configToAgentRuntime>[0]

    const runtime = configToAgentRuntime(config)

    expect(runtime).toEqual({
      timeout: 600000,
      taskTimeout: 600000,
      stageTimeout: 3600000,
      maxConcurrent: 3,
      maxGracePeriods: 3,
      pollInterval: 5000,
    })
  })

  it('treats missing config keys as zero without throwing', () => {
    const runtime = configToAgentRuntime(undefined)

    expect(runtime).toEqual({
      timeout: 0,
      taskTimeout: 0,
      stageTimeout: 0,
      maxConcurrent: 0,
      maxGracePeriods: 0,
      pollInterval: 0,
    })
  })

  it('persists supported runtime fields through PUT /api/config/{key} and reuses the latest response', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === 'string' ? input : input.toString()
      const method = init?.method ?? 'GET'

      if (method === 'GET' && url.endsWith('/api/config')) {
        return Response.json({
          success: true,
          data: {
            maxConcurrentAgents: 3,
            agentTimeout: 600,
            taskTimeout: 600,
            stageTimeout: 3600,
            pollInterval: 5000,
            maxGracePeriods: 3,
          },
        })
      }

      if (method === 'PUT' && url.endsWith('/api/config/agentTimeout')) {
        return Response.json({
          success: true,
          data: {
            maxConcurrentAgents: 3,
            agentTimeout: 1200,
            taskTimeout: 600,
            stageTimeout: 3600,
            pollInterval: 5000,
            maxGracePeriods: 3,
          },
        })
      }

      return Response.json({ success: false, error: 'unexpected' }, { status: 500 })
    })
    vi.stubGlobal('fetch', fetchMock)

    const result = await updateAgentRuntime({ timeout: 1200000 })

    expect(result).toEqual({
      maxConcurrent: 3,
      timeout: 1200000,
      taskTimeout: 600000,
      stageTimeout: 3600000,
      pollInterval: 5000,
      maxGracePeriods: 3,
    })

    const putCall = fetchMock.mock.calls.find(([, init]) => init?.method === 'PUT')
    expect(putCall).toBeDefined()
    const putUrl = putCall?.[0]
    const putUrlString = typeof putUrl === 'string' ? putUrl : (putUrl as URL).toString()
    expect(putUrlString).toBe('/api/config/agentTimeout')
    expect(putUrlString).not.toContain('/api/agent-runtime')
    const body = JSON.parse(putCall?.[1]?.body as string)
    expect(body).toEqual({ value: 1200 })
  })

  it('sends only changed supported fields and skips unaffected ones', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === 'string' ? input : input.toString()
      const method = init?.method ?? 'GET'

      if (method === 'GET' && url.endsWith('/api/config')) {
        return Response.json({
          success: true,
          data: {
            maxConcurrentAgents: 3,
            agentTimeout: 600,
            taskTimeout: 600,
            stageTimeout: 3600,
            pollInterval: 5000,
            maxGracePeriods: 3,
          },
        })
      }

      if (method === 'PUT' && (url.endsWith('/api/config/maxConcurrentAgents') || url.endsWith('/api/config/maxGracePeriods'))) {
        const body = JSON.parse(init?.body as string) as { value: number }
        return Response.json({
          success: true,
          data: {
            maxConcurrentAgents: body.value,
            agentTimeout: 600,
            taskTimeout: 600,
            stageTimeout: 3600,
            pollInterval: 5000,
            maxGracePeriods: url.endsWith('/api/config/maxGracePeriods') ? body.value : 3,
          },
        })
      }

      return Response.json({ success: false, error: 'unexpected' }, { status: 500 })
    })
    vi.stubGlobal('fetch', fetchMock)

    await updateAgentRuntime({ maxConcurrent: 7, maxGracePeriods: 2 })

    const putCalls = fetchMock.mock.calls.filter(([, init]) => init?.method === 'PUT')
    const putUrls = putCalls.map(([u]) => (typeof u === 'string' ? u : (u as URL).toString()))
    expect(putUrls).toContain('/api/config/maxConcurrentAgents')
    expect(putUrls).toContain('/api/config/maxGracePeriods')
    expect(putUrls).not.toContain('/api/config/agentTimeout')
    expect(putUrls).not.toContain('/api/config/taskTimeout')
    expect(putUrls).not.toContain('/api/config/stageTimeout')
    expect(putUrls).not.toContain('/api/config/pollInterval')
  })

  it('surfaces server validation failures from the config API', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === 'string' ? input : input.toString()
      if (init?.method === 'PUT' && url.endsWith('/api/config/agentTimeout')) {
        return Response.json({
          success: false,
          error: 'agentTimeout must be a number',
          code: 'bad_request',
        }, { status: 400 })
      }
      return Response.json({ success: false, error: 'unexpected' }, { status: 500 })
    })
    vi.stubGlobal('fetch', fetchMock)

    await expect(updateAgentRuntime({ timeout: 1200000 })).rejects.toMatchObject({
      name: 'ApiError',
      message: 'agentTimeout must be a number',
      status: 400,
    })
  })

  it('refetches confirmed state after a successful save by re-reading the latest response', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === 'string' ? input : input.toString()
      const method = init?.method ?? 'GET'

      if (method === 'GET' && url.endsWith('/api/config')) {
        return Response.json({
          success: true,
          data: {
            maxConcurrentAgents: 3,
            agentTimeout: 600,
            taskTimeout: 600,
            stageTimeout: 3600,
            pollInterval: 5000,
            maxGracePeriods: 3,
          },
        })
      }

      if (method === 'PUT' && url.endsWith('/api/config/pollInterval')) {
        return Response.json({
          success: true,
          data: {
            maxConcurrentAgents: 3,
            agentTimeout: 600,
            taskTimeout: 600,
            stageTimeout: 3600,
            pollInterval: 15000,
            maxGracePeriods: 3,
          },
        })
      }

      return Response.json({ success: false, error: 'unexpected' }, { status: 500 })
    })
    vi.stubGlobal('fetch', fetchMock)

    const result = await updateAgentRuntime({ pollInterval: 15000 })

    expect(result.pollInterval).toBe(15000)
    const putCall = fetchMock.mock.calls.find(([, init]) => init?.method === 'PUT')
    const body = JSON.parse(putCall?.[1]?.body as string)
    expect(body).toEqual({ value: 15000 })
    expect(result.pollInterval).not.toBe(5000)
  })

  it('exposes the supported runtime key mapping for callers', () => {
    expect(agentRuntimeToConfigKey('timeout')).toBe('agentTimeout')
    expect(agentRuntimeToConfigKey('taskTimeout')).toBe('taskTimeout')
    expect(agentRuntimeToConfigKey('stageTimeout')).toBe('stageTimeout')
    expect(agentRuntimeToConfigKey('maxConcurrent')).toBe('maxConcurrentAgents')
    expect(agentRuntimeToConfigKey('maxGracePeriods')).toBe('maxGracePeriods')
    expect(agentRuntimeToConfigKey('pollInterval')).toBe('pollInterval')
  })
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
