import { describe, expect, it } from 'vitest'
import { http, HttpResponse } from 'msw'
import { server, useMswServer } from '../../../../tests/support/msw'
import {
  createAgentSubscription,
  deleteAgentSubscription,
  listAgentSubscriptions,
  updateAgentSubscription,
} from './subscriptions'

useMswServer()

function successResponse(payload: unknown, status = 200) {
  return HttpResponse.json({ success: true, data: payload }, { status })
}

function subscription(id = 'rule_x') {
  return {
    id,
    projectId: 'proj-1',
    agentId: 'agent-1',
    name: 'fallback',
    match: 'event.type == "com.example.failed"',
    responsePrompt: 'inspect the failure',
    continue: false,
    position: 1,
    status: 'active' as const,
    createdAt: '2026-08-09T00:00:00.000Z',
    updatedAt: '2026-08-09T00:00:00.000Z',
  }
}

describe('Agent subscription API', () => {
  it('reads the canonical list envelope instead of treating it as an array', async () => {
    server.use(
      http.get('*/api/projects/:projectId/agents/:agentRef/subscriptions', () =>
        successResponse({
          subscriptions: [subscription()],
          state: 'configured',
          agentStatus: 'active',
          executability: 'executable',
          connection: 'connected',
        }),
      ),
    )

    const result = await listAgentSubscriptions('proj-1', 'agent-1')
    expect(result.subscriptions[0]).toMatchObject({ id: 'rule_x', match: 'event.type == "com.example.failed"' })
    expect(result.state).toBe('configured')
  })

  it('uses a stable idempotency key for create and removes it from the JSON body', async () => {
    const requests: Request[] = []
    server.use(
      http.post('*/api/projects/:projectId/agents/:agentRef/subscriptions', ({ request }) => {
        requests.push(request)
        return successResponse(subscription('rule_new'), 201)
      }),
    )

    const result = await createAgentSubscription('proj-1', 'agent-1', {
      name: 'fallback',
      match: 'event.type == "com.example.failed"',
      responsePrompt: 'inspect',
      continue: true,
      idempotencyKey: 'request-1',
    })

    expect(result.idempotencyKey).toBe('request-1')
    expect(requests[0].headers.get('Idempotency-Key')).toBe('request-1')
    await expect(requests[0].json()).resolves.toEqual({
      name: 'fallback',
      match: 'event.type == "com.example.failed"',
      responsePrompt: 'inspect',
      continue: true,
    })
  })

  it('exposes an auto-generated key after response loss so replay uses the same key', async () => {
    const keys: string[] = []
    let attempts = 0
    server.use(
      http.post('*/api/projects/:projectId/agents/:agentRef/subscriptions', ({ request }) => {
        keys.push(request.headers.get('Idempotency-Key') ?? '')
        attempts += 1
        if (attempts === 1) return HttpResponse.error()
        return successResponse(subscription('rule_replayed'), 201)
      }),
    )

    let firstError: unknown
    try {
      await createAgentSubscription('proj-1', 'agent-1', {
        name: 'fallback',
        match: 'event.type == "com.example.failed"',
        responsePrompt: 'inspect',
      })
    } catch (error) {
      firstError = error
    }

    const key = (firstError as { idempotencyKey?: string }).idempotencyKey
    expect(key).toBeTruthy()
    const replay = await createAgentSubscription('proj-1', 'agent-1', {
      name: 'fallback',
      match: 'event.type == "com.example.failed"',
      responsePrompt: 'inspect',
      idempotencyKey: key,
    })

    expect(keys).toEqual([key, key])
    expect(replay.idempotencyKey).toBe(key)
  })

  it('patches and deletes the same canonical resource path', async () => {
    const methods: string[] = []
    server.use(
      http.patch('*/api/projects/:projectId/agents/:agentRef/subscriptions/:id', ({ request }) => {
        methods.push(request.method)
        return successResponse(subscription())
      }),
      http.delete('*/api/projects/:projectId/agents/:agentRef/subscriptions/:id', ({ request }) => {
        methods.push(request.method)
        return successResponse({ id: 'rule_x', status: 'deleted' })
      }),
    )

    await updateAgentSubscription('proj-1', 'agent-1', 'rule_x', { name: 'updated' })
    await deleteAgentSubscription('proj-1', 'agent-1', 'rule_x')
    expect(methods).toEqual(['PATCH', 'DELETE'])
  })
})
