import { describe, expect, it } from 'vitest'
import { http, HttpResponse } from 'msw'
import { server, useMswServer } from '../../../../tests/support/msw'
import {
  agentRuntimeToConfigKey,
  configToAgentRuntime,
  getActionCatalog,
  getAgentRuntime,
  getLogLevel,
  patchWorkflowProfileAgentAction,
  setLogLevel,
  updateAgentRuntime,
} from './client'

useMswServer()

interface CapturedRequest {
  path: string
  method: string
  contentType: string | null
  body?: unknown
}

const baselineConfig = {
  maxConcurrentAgents: 3,
  agentTimeout: 600,
  taskTimeout: 600,
  stageTimeout: 3600,
  pollInterval: 5000,
  maxGracePeriods: 3,
}

async function captureRequest(request: Request, requests: CapturedRequest[]) {
  const url = new URL(request.url)
  const captured: CapturedRequest = {
    path: `${url.pathname}${url.search}`,
    method: request.method,
    contentType: request.headers.get('content-type'),
  }
  if (request.method !== 'GET') captured.body = await request.json()
  requests.push(captured)
}

function configResponse(overrides: Record<string, unknown> = {}) {
  return HttpResponse.json({ success: true, data: { ...baselineConfig, ...overrides } })
}

function errorResponse(error: string, status: number, code: string) {
  return HttpResponse.json({ success: false, error, code }, { status })
}

describe('settings client agent runtime adapter', () => {
  it('reads runtime config from /api/config instead of the missing /api/agent-runtime endpoint', async () => {
    const requests: CapturedRequest[] = []
    server.use(
      http.get('*/api/config', async ({ request }) => {
        await captureRequest(request, requests)
        return configResponse({
          maxConcurrentAgents: 4,
          agentTimeout: 900,
          taskTimeout: 300,
          stageTimeout: 1800,
          pollInterval: 10000,
          maxGracePeriods: 5,
        })
      }),
    )

    const result = await getAgentRuntime()

    expect(result).toEqual({
      maxConcurrent: 4,
      timeout: 900000,
      taskTimeout: 300000,
      stageTimeout: 1800000,
      pollInterval: 10000,
      maxGracePeriods: 5,
    })
    expect(requests).toEqual([{
      path: '/api/config',
      method: 'GET',
      contentType: 'application/json',
    }])
  })

  it('propagates a load failure from /api/config without falling back to defaults', async () => {
    server.use(
      http.get('*/api/config', () => errorResponse('server unavailable', 500, 'server_error')),
    )

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
    const requests: CapturedRequest[] = []
    server.use(
      http.put('*/api/config/:key', async ({ request }) => {
        await captureRequest(request, requests)
        return configResponse({ agentTimeout: 1200 })
      }),
    )

    const result = await updateAgentRuntime({ timeout: 1200000 })

    expect(result).toEqual({
      maxConcurrent: 3,
      timeout: 1200000,
      taskTimeout: 600000,
      stageTimeout: 3600000,
      pollInterval: 5000,
      maxGracePeriods: 3,
    })
    expect(requests).toEqual([{
      path: '/api/config/agentTimeout',
      method: 'PUT',
      contentType: 'application/json',
      body: { value: 1200 },
    }])
  })

  it('sends only changed supported fields and skips unaffected ones', async () => {
    const requests: CapturedRequest[] = []
    server.use(
      http.put('*/api/config/:key', async ({ request }) => {
        await captureRequest(request, requests)
        const path = new URL(request.url).pathname
        return configResponse(path.endsWith('/maxConcurrentAgents')
          ? { maxConcurrentAgents: 7 }
          : { maxGracePeriods: 2 })
      }),
    )

    await updateAgentRuntime({ maxConcurrent: 7, maxGracePeriods: 2 })

    expect(requests).toEqual(expect.arrayContaining([
      {
        path: '/api/config/maxConcurrentAgents',
        method: 'PUT',
        contentType: 'application/json',
        body: { value: 7 },
      },
      {
        path: '/api/config/maxGracePeriods',
        method: 'PUT',
        contentType: 'application/json',
        body: { value: 2 },
      },
    ]))
    expect(requests).toHaveLength(2)
  })

  it('surfaces server validation failures from the config API', async () => {
    server.use(
      http.put('*/api/config/agentTimeout', () => errorResponse('agentTimeout must be a number', 400, 'bad_request')),
    )

    await expect(updateAgentRuntime({ timeout: 1200000 })).rejects.toMatchObject({
      name: 'ApiError',
      message: 'agentTimeout must be a number',
      status: 400,
    })
  })

  it('refetches confirmed state after a successful save by re-reading the latest response', async () => {
    const requests: CapturedRequest[] = []
    server.use(
      http.put('*/api/config/pollInterval', async ({ request }) => {
        await captureRequest(request, requests)
        return configResponse({ pollInterval: 15000 })
      }),
    )

    const result = await updateAgentRuntime({ pollInterval: 15000 })

    expect(result.pollInterval).toBe(15000)
    expect(requests).toEqual([{
      path: '/api/config/pollInterval',
      method: 'PUT',
      contentType: 'application/json',
      body: { value: 15000 },
    }])
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
    const requests: CapturedRequest[] = []
    server.use(
      http.get('*/api/config', async ({ request }) => {
        await captureRequest(request, requests)
        return configResponse({ logLevel: 'WARN' })
      }),
    )

    const result = await getLogLevel()

    expect(result).toEqual({ level: 'WARN' })
    expect(requests).toEqual([{
      path: '/api/config',
      method: 'GET',
      contentType: 'application/json',
    }])
  })

  it('persists log level changes through PUT /api/config/logLevel', async () => {
    const requests: CapturedRequest[] = []
    server.use(
      http.put('*/api/config/logLevel', async ({ request }) => {
        await captureRequest(request, requests)
        return configResponse({ logLevel: 'ERROR' })
      }),
    )

    const result = await setLogLevel('ERROR')

    expect(result).toEqual({ level: 'ERROR' })
    expect(requests).toEqual([{
      path: '/api/config/logLevel',
      method: 'PUT',
      contentType: 'application/json',
      body: { value: 'ERROR' },
    }])
  })

  it('surfaces server-side validation failures from the config API', async () => {
    server.use(
      http.put('*/api/config/logLevel', () => errorResponse('logLevel must be one of DEBUG, INFO, WARN, ERROR', 400, 'bad_request')),
    )

    await expect(setLogLevel('TRACE')).rejects.toMatchObject({
      name: 'ApiError',
      message: 'logLevel must be one of DEBUG, INFO, WARN, ERROR',
      status: 400,
    })
  })
})

describe('settings client workflow Profile Agent Actions', () => {
  it('loads the project Action catalog without deriving candidates from Action names', async () => {
    const requests: CapturedRequest[] = []
    server.use(
      http.get('*/api/projects/:projectId/actions', async ({ request }) => {
        await captureRequest(request, requests)
        return HttpResponse.json({
          success: true,
          data: { actions: [{ name: 'team/agent', capabilities: ['agent-turn'] }] },
        })
      }),
    )

    await expect(getActionCatalog('proj-1')).resolves.toEqual({
      actions: [{ name: 'team/agent', capabilities: ['agent-turn'] }],
    })
    expect(requests).toEqual([{
      path: '/api/projects/proj-1/actions',
      method: 'GET',
      contentType: 'application/json',
    }])
  })

  it('PATCHes the selected Agent Action on the Profile resource', async () => {
    const requests: CapturedRequest[] = []
    server.use(
      http.patch('*/api/projects/:projectId/workflow-profiles/*', async ({ request }) => {
        await captureRequest(request, requests)
        return HttpResponse.json({
          success: true,
          data: {
            projectId: 'proj-1',
            profileId: 'mohist/github-pr',
            name: 'GitHub PR',
            description: '',
            sourceProvenance: 'BuiltIn',
            isBuiltIn: true,
            definitionSource: null,
            agentAction: 'mohist/pi',
            agentRuntime: 'pi',
          },
        })
      }),
    )

    const result = await patchWorkflowProfileAgentAction('proj-1', 'mohist/github-pr', 'mohist/pi')

    expect(result).toEqual(expect.objectContaining({ id: 'mohist/github-pr', agentAction: 'mohist/pi', agentRuntime: 'pi' }))
    expect(requests).toEqual([{
      path: '/api/projects/proj-1/workflow-profiles/mohist/github-pr',
      method: 'PATCH',
      contentType: 'application/json',
      body: { agentAction: 'mohist/pi' },
    }])
  })
})
