import { describe, expect, it, vi } from 'vitest'
import { http, HttpResponse } from 'msw'
import { server, useMswServer } from '../../../../tests/support/msw'
import { toast } from 'sonner'
import {
  agentSubscriptionsQueryKey,
  agentSubscriptionsQueryOptions,
  archiveAgentSubscriptionMutationOptions,
  createAgentSubscriptionMutationOptions,
  deleteAgentSubscriptionMutationOptions,
  restoreAgentSubscriptionMutationOptions,
} from './subscription-queries'

useMswServer()

function createInvalidationClient() {
  return { invalidateQueries: vi.fn() }
}

/* ── query keys ──────────────────────────────────────────── */

describe('agentSubscriptionsQueryKey', () => {
  it('uses the segments required for per-agent scoping', () => {
    expect(agentSubscriptionsQueryKey('proj-1', 'agent-1')).toEqual([
      'agents',
      'proj-1',
      'agent-1',
      'subscriptions',
    ])
  })
})

/* ── agentSubscriptionsQueryOptions ─────────────────────── */

describe('agentSubscriptionsQueryOptions', () => {
  it('uses the canonical subscription query key', () => {
    expect(agentSubscriptionsQueryOptions('proj-1', 'agent-1').queryKey).toEqual([
      'agents',
      'proj-1',
      'agent-1',
      'subscriptions',
    ])
  })

  it('is enabled only when both projectId and agentRef are present', () => {
    expect(agentSubscriptionsQueryOptions('proj-1', 'agent-1').enabled).toBe(true)
    expect(agentSubscriptionsQueryOptions(null, 'agent-1').enabled).toBe(false)
    expect(agentSubscriptionsQueryOptions('proj-1', '').enabled).toBe(false)
  })

  it('calls listAgentSubscriptions(projectId, agentRef) inside the queryFn', async () => {
    const urls: string[] = []
    server.use(
      http.get('*/api/projects/:projectId/agents/:agentRef/subscriptions', ({ request }) => {
        urls.push(new URL(request.url).pathname)
        return HttpResponse.json({ success: true, data: [] })
      }),
    )

    await agentSubscriptionsQueryOptions('proj-1', 'agent-1').queryFn()

    expect(urls).toEqual(['/api/projects/proj-1/agents/agent-1/subscriptions'])
  })
})

/* ── createAgentSubscriptionMutationOptions ─────────────── */

describe('createAgentSubscriptionMutationOptions', () => {
  it('mutationFn calls createAgentSubscription(projectId, agentRef, data)', async () => {
    const captured: { url: string; method: string; body: unknown }[] = []
    server.use(
      http.post('*/api/projects/:projectId/agents/:agentRef/subscriptions', async ({ request }) => {
        captured.push({ url: new URL(request.url).pathname, method: request.method, body: await request.json() })
        return HttpResponse.json({ success: true, data: { id: 'subs_new', name: 'fallback' } })
      }),
    )

    await createAgentSubscriptionMutationOptions('proj-1', 'agent-1', createInvalidationClient()).mutationFn({
      name: 'fallback',
      filter: { type: 'com.mohist.workflow.stage.*' },
      responsePrompt: 'approve',
    })

    expect(captured).toEqual([
      {
        url: '/api/projects/proj-1/agents/agent-1/subscriptions',
        method: 'POST',
        body: {
          name: 'fallback',
          filter: { type: 'com.mohist.workflow.stage.*' },
          responsePrompt: 'approve',
        },
      },
    ])
  })

  it('invalidates the subscriptions query on success', () => {
    const qc = createInvalidationClient()
    createAgentSubscriptionMutationOptions('proj-1', 'agent-1', qc).onSuccess({ id: 'subs_new', name: 'fallback' } as never)
    expect(qc.invalidateQueries).toHaveBeenCalledWith({
      queryKey: ['agents', 'proj-1', 'agent-1', 'subscriptions'],
    })
    expect(qc.invalidateQueries).toHaveBeenCalledWith({
      queryKey: ['agents', 'proj-1', 'agent-1'],
    })
  })

  it('shows a success toast with the created subscription name', () => {
    createAgentSubscriptionMutationOptions('proj-1', 'agent-1', createInvalidationClient()).onSuccess({
      id: 'subs_new',
      name: 'fallback',
    } as never)
    expect(toast.success).toHaveBeenCalledWith('Subscription "fallback" created')
  })

  it('shows an error toast on failure (preserving the server message)', () => {
    createAgentSubscriptionMutationOptions('proj-1', 'agent-1', createInvalidationClient()).onError(
      new Error('Archived agents cannot receive new subscriptions'),
    )
    expect(toast.error).toHaveBeenCalledWith('Archived agents cannot receive new subscriptions')
  })

  it('falls back to "Failed to create subscription" on empty error message', () => {
    createAgentSubscriptionMutationOptions('proj-1', 'agent-1', createInvalidationClient()).onError(new Error(''))
    expect(toast.error).toHaveBeenCalledWith('Failed to create subscription')
  })
})

/* ── archiveAgentSubscriptionMutationOptions ────────────── */

describe('archiveAgentSubscriptionMutationOptions', () => {
  it('mutationFn calls archiveAgentSubscription(projectId, agentRef, subscriptionId)', async () => {
    const captured: { url: string; method: string }[] = []
    server.use(
      http.post('*/api/projects/:projectId/agents/:agentRef/subscriptions/:subscriptionId/archive', ({ request }) => {
        captured.push({ url: new URL(request.url).pathname, method: request.method })
        return HttpResponse.json({ success: true, data: { id: 'subs_x', name: 'fallback', status: 'archived' } })
      }),
    )

    await archiveAgentSubscriptionMutationOptions('proj-1', 'agent-1', createInvalidationClient()).mutationFn({
      subscriptionId: 'subs_x',
    })

    expect(captured).toEqual([
      { url: '/api/projects/proj-1/agents/agent-1/subscriptions/subs_x/archive', method: 'POST' },
    ])
  })

  it('invalidates the subscriptions query on success', () => {
    const qc = createInvalidationClient()
    archiveAgentSubscriptionMutationOptions('proj-1', 'agent-1', qc).onSuccess({
      id: 'subs_x',
      name: 'fallback',
      status: 'archived',
    } as never)
    expect(qc.invalidateQueries).toHaveBeenCalledWith({
      queryKey: ['agents', 'proj-1', 'agent-1', 'subscriptions'],
    })
  })

  it('shows a success toast with the subscription name', () => {
    archiveAgentSubscriptionMutationOptions('proj-1', 'agent-1', createInvalidationClient()).onSuccess({
      id: 'subs_x',
      name: 'fallback',
      status: 'archived',
    } as never)
    expect(toast.success).toHaveBeenCalledWith('Subscription "fallback" archived')
  })

  it('shows an error toast on failure', () => {
    archiveAgentSubscriptionMutationOptions('proj-1', 'agent-1', createInvalidationClient()).onError(
      new Error('NOT_FOUND'),
    )
    expect(toast.error).toHaveBeenCalledWith('NOT_FOUND')
  })
})

/* ── restoreAgentSubscriptionMutationOptions ────────────── */

describe('restoreAgentSubscriptionMutationOptions', () => {
  it('mutationFn calls restoreAgentSubscription(projectId, agentRef, subscriptionId)', async () => {
    const captured: { url: string; method: string }[] = []
    server.use(
      http.post('*/api/projects/:projectId/agents/:agentRef/subscriptions/:subscriptionId/restore', ({ request }) => {
        captured.push({ url: new URL(request.url).pathname, method: request.method })
        return HttpResponse.json({ success: true, data: { id: 'subs_x', name: 'fallback', status: 'active' } })
      }),
    )

    await restoreAgentSubscriptionMutationOptions('proj-1', 'agent-1', createInvalidationClient()).mutationFn({
      subscriptionId: 'subs_x',
    })

    expect(captured).toEqual([
      { url: '/api/projects/proj-1/agents/agent-1/subscriptions/subs_x/restore', method: 'POST' },
    ])
  })

  it('invalidates the subscriptions query on success', () => {
    const qc = createInvalidationClient()
    restoreAgentSubscriptionMutationOptions('proj-1', 'agent-1', qc).onSuccess({
      id: 'subs_x',
      name: 'fallback',
      status: 'active',
    } as never)
    expect(qc.invalidateQueries).toHaveBeenCalledWith({
      queryKey: ['agents', 'proj-1', 'agent-1', 'subscriptions'],
    })
  })

  it('shows a success toast with the subscription name', () => {
    restoreAgentSubscriptionMutationOptions('proj-1', 'agent-1', createInvalidationClient()).onSuccess({
      id: 'subs_x',
      name: 'fallback',
      status: 'active',
    } as never)
    expect(toast.success).toHaveBeenCalledWith('Subscription "fallback" restored')
  })

  it('shows an error toast on failure', () => {
    restoreAgentSubscriptionMutationOptions('proj-1', 'agent-1', createInvalidationClient()).onError(
      new Error('NOT_FOUND'),
    )
    expect(toast.error).toHaveBeenCalledWith('NOT_FOUND')
  })
})

/* ── deleteAgentSubscriptionMutationOptions ─────────────── */

describe('deleteAgentSubscriptionMutationOptions', () => {
  it('mutationFn calls deleteAgentSubscription(projectId, agentRef, subscriptionId)', async () => {
    const captured: { url: string; method: string }[] = []
    server.use(
      http.delete('*/api/projects/:projectId/agents/:agentRef/subscriptions/:subscriptionId', ({ request }) => {
        captured.push({ url: new URL(request.url).pathname, method: request.method })
        return HttpResponse.json({ success: true, data: null })
      }),
    )

    await deleteAgentSubscriptionMutationOptions('proj-1', 'agent-1', createInvalidationClient()).mutationFn({
      subscriptionId: 'subs_x',
    })

    expect(captured).toEqual([
      { url: '/api/projects/proj-1/agents/agent-1/subscriptions/subs_x', method: 'DELETE' },
    ])
  })

  it('invalidates the subscriptions query on success', () => {
    const qc = createInvalidationClient()
    deleteAgentSubscriptionMutationOptions('proj-1', 'agent-1', qc).onSuccess(null, { subscriptionId: 'subs_x' })
    expect(qc.invalidateQueries).toHaveBeenCalledWith({
      queryKey: ['agents', 'proj-1', 'agent-1', 'subscriptions'],
    })
  })

  it('shows a success toast mentioning the deleted subscription id', () => {
    deleteAgentSubscriptionMutationOptions('proj-1', 'agent-1', createInvalidationClient()).onSuccess(null, {
      subscriptionId: 'subs_x',
    })
    expect(toast.success).toHaveBeenCalledWith('Subscription subs_x deleted')
  })

  it('shows an error toast on failure', () => {
    deleteAgentSubscriptionMutationOptions('proj-1', 'agent-1', createInvalidationClient()).onError(
      new Error('NOT_FOUND'),
    )
    expect(toast.error).toHaveBeenCalledWith('NOT_FOUND')
  })
})
