import { describe, expect, it } from 'vitest'
import { http, HttpResponse } from 'msw'
import { server, useMswServer } from '../../../../tests/support/msw'
import {
  archiveAgentSubscription,
  createAgentSubscription,
  deleteAgentSubscription,
  formatAgentSubscriptionFilter,
  listAgentSubscriptions,
  restoreAgentSubscription,
} from './subscriptions'
import { ApiError } from '../../../shared/api/client'

useMswServer()

function successResponse(payload: unknown, status = 200) {
  return HttpResponse.json({ success: true, data: payload }, { status })
}

function subscription(status: 'active' | 'archived', id = 'subs_x') {
  return {
    id,
    projectId: 'proj-1',
    agentId: 'agent-1',
    name: 'fallback',
    filter: { type: 'com.mohist.workflow.stage.*', source: null, subject: null },
    responsePrompt: 'approve if clear',
    priority: 0,
    status,
    createdAt: '2026-06-01T00:00:00.000Z',
    updatedAt: '2026-06-02T00:00:00.000Z',
  }
}

function requestPath(request: Request) {
  const url = new URL(request.url)
  return `${url.pathname}${url.search}`
}

describe('listAgentSubscriptions', () => {
  it('GETs /api/projects/{ref}/agents/{agentRef}/subscriptions and returns an array', async () => {
    const requests: Request[] = []
    server.use(
      http.get('*/api/projects/:projectId/agents/:agentRef/subscriptions', ({ request }) => {
        requests.push(request)
        return successResponse([{ ...subscription('active'), name: 'default', updatedAt: '2026-06-01T00:00:00.000Z' }])
      }),
    )

    const result = await listAgentSubscriptions('proj-1', 'agent-1')

    expect(result).toHaveLength(1)
    expect(result[0].name).toBe('default')
    expect(result[0].status).toBe('active')
    expect(requests).toHaveLength(1)
    expect(requestPath(requests[0])).toBe('/api/projects/proj-1/agents/agent-1/subscriptions')
    expect(requests[0].method).toBe('GET')
    expect(requests[0].headers.get('content-type')).toBe('application/json')
  })

  it('encodes agent refs with special characters', async () => {
    const paths: string[] = []
    server.use(
      http.get('*/api/projects/:projectId/agents/:agentRef/subscriptions', ({ request }) => {
        paths.push(requestPath(request))
        return successResponse([])
      }),
    )

    await listAgentSubscriptions('proj-1', 'a/b')

    expect(paths).toEqual(['/api/projects/proj-1/agents/a%2Fb/subscriptions'])
  })
})

describe('createAgentSubscription', () => {
  it('POSTs the subscription payload and returns the dto', async () => {
    const requests: Request[] = []
    server.use(
      http.post('*/api/projects/:projectId/agents/:agentRef/subscriptions', ({ request }) => {
        requests.push(request)
        return successResponse({ ...subscription('active', 'subs_new'), priority: 1 }, 201)
      }),
    )

    const result = await createAgentSubscription('proj-1', 'agent-1', {
      name: 'fallback',
      filter: { type: 'com.mohist.workflow.stage.*' },
      responsePrompt: 'approve if clear',
      priority: 1,
    })

    expect(result.id).toBe('subs_new')
    expect(result.status).toBe('active')
    expect(requests).toHaveLength(1)
    expect(requestPath(requests[0])).toBe('/api/projects/proj-1/agents/agent-1/subscriptions')
    expect(requests[0].method).toBe('POST')
    expect(requests[0].headers.get('content-type')).toBe('application/json')
    await expect(requests[0].json()).resolves.toEqual({
      name: 'fallback',
      filter: { type: 'com.mohist.workflow.stage.*' },
      responsePrompt: 'approve if clear',
      priority: 1,
    })
  })

  it('surfaces server-side error messages (e.g. agent_archived)', async () => {
    server.use(
      http.post('*/api/projects/:projectId/agents/:agentRef/subscriptions', () => HttpResponse.json({
        success: false,
        error: 'Archived agents cannot receive new subscriptions',
        code: 'agent_archived',
      }, { status: 409 })),
    )

    await expect(
      createAgentSubscription('proj-1', 'agent-1', {
        name: 'fallback',
        filter: { type: 'com.mohist.workflow.stage.*' },
        responsePrompt: 'approve if clear',
      }),
    ).rejects.toMatchObject({
      name: 'ApiError',
      status: 409,
      code: 'agent_archived',
    } satisfies Partial<ApiError>)
  })
})

describe('archiveAgentSubscription', () => {
  it('POSTs the archive endpoint and returns the dto', async () => {
    const requests: Request[] = []
    server.use(
      http.post('*/api/projects/:projectId/agents/:agentRef/subscriptions/:subscriptionId/archive', ({ request }) => {
        requests.push(request)
        return successResponse(subscription('archived'))
      }),
    )

    const result = await archiveAgentSubscription('proj-1', 'agent-1', 'subs_x')

    expect(result.status).toBe('archived')
    expect(requests).toHaveLength(1)
    expect(requestPath(requests[0])).toBe('/api/projects/proj-1/agents/agent-1/subscriptions/subs_x/archive')
    expect(requests[0].method).toBe('POST')
    expect(requests[0].headers.get('content-type')).toBe('application/json')
  })

  it('encodes subscription ids with special characters', async () => {
    const paths: string[] = []
    server.use(
      http.post('*/api/projects/:projectId/agents/:agentRef/subscriptions/:subscriptionId/archive', ({ request }) => {
        paths.push(requestPath(request))
        return successResponse(subscription('archived', 'subs/a'))
      }),
    )

    await archiveAgentSubscription('proj-1', 'agent-1', 'subs/a')

    expect(paths).toEqual(['/api/projects/proj-1/agents/agent-1/subscriptions/subs%2Fa/archive'])
  })
})

describe('restoreAgentSubscription', () => {
  it('POSTs the restore endpoint and returns the dto', async () => {
    const requests: Request[] = []
    server.use(
      http.post('*/api/projects/:projectId/agents/:agentRef/subscriptions/:subscriptionId/restore', ({ request }) => {
        requests.push(request)
        return successResponse(subscription('active'))
      }),
    )

    const result = await restoreAgentSubscription('proj-1', 'agent-1', 'subs_x')

    expect(result.status).toBe('active')
    expect(requests).toHaveLength(1)
    expect(requestPath(requests[0])).toBe('/api/projects/proj-1/agents/agent-1/subscriptions/subs_x/restore')
    expect(requests[0].method).toBe('POST')
    expect(requests[0].headers.get('content-type')).toBe('application/json')
  })
})

describe('deleteAgentSubscription', () => {
  it('DELETEs the subscription endpoint', async () => {
    const requests: Request[] = []
    server.use(
      http.delete('*/api/projects/:projectId/agents/:agentRef/subscriptions/:subscriptionId', ({ request }) => {
        requests.push(request)
        return successResponse(null)
      }),
    )

    await deleteAgentSubscription('proj-1', 'agent-1', 'subs_x')

    expect(requests).toHaveLength(1)
    expect(requestPath(requests[0])).toBe('/api/projects/proj-1/agents/agent-1/subscriptions/subs_x')
    expect(requests[0].method).toBe('DELETE')
    expect(requests[0].headers.get('content-type')).toBe('application/json')
  })
})

describe('formatAgentSubscriptionFilter', () => {
  it('renders the type alone when source/subject are absent', () => {
    expect(
      formatAgentSubscriptionFilter({
        type: 'com.mohist.workflow.stage.*',
        source: null,
        subject: null,
      }),
    ).toBe('com.mohist.workflow.stage.*')
  })

  it('includes source and subject when present', () => {
    expect(
      formatAgentSubscriptionFilter({
        type: 'com.mohist.issue.archived',
        source: '/mohist/issues/42',
        subject: null,
      }),
    ).toBe('com.mohist.issue.archived, source=/mohist/issues/42')
    expect(
      formatAgentSubscriptionFilter({
        type: 'com.mohist.issue.archived',
        source: null,
        subject: 'issue-42',
      }),
    ).toBe('com.mohist.issue.archived, subject=issue-42')
  })
})
