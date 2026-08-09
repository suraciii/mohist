import { describe, expect, it, vi } from 'vitest'
import { http, HttpResponse } from 'msw'
import { server, useMswServer } from '../../../../tests/support/msw'
import { toast } from 'sonner'
import {
  agentSubscriptionsQueryKey,
  agentSubscriptionsQueryOptions,
  createAgentSubscriptionMutationOptions,
  deleteAgentSubscriptionMutationOptions,
  updateAgentSubscriptionMutationOptions,
} from './subscription-queries'

useMswServer()

function client() { return { invalidateQueries: vi.fn() } }
function resource() { return { id: 'rule_x', name: 'fallback' } }

describe('agent subscription query contract', () => {
  it('uses the canonical scoped query key and enabled guard', () => {
    expect(agentSubscriptionsQueryKey('proj-1', 'agent-1')).toEqual(['agents', 'proj-1', 'agent-1', 'subscriptions'])
    expect(agentSubscriptionsQueryOptions('proj-1', 'agent-1').enabled).toBe(true)
    expect(agentSubscriptionsQueryOptions(null, 'agent-1').enabled).toBe(false)
  })

  it('invalidates the list and agent after a create', async () => {
    server.use(http.post('*/api/projects/:projectId/agents/:agentRef/subscriptions', () =>
      HttpResponse.json({ success: true, data: resource() })))
    const qc = client()
    const options = createAgentSubscriptionMutationOptions('proj-1', 'agent-1', qc)
    await options.mutationFn({ name: 'fallback', match: 'event.type == "x"', responsePrompt: 'inspect' })
    options.onSuccess(resource() as never)
    expect(qc.invalidateQueries).toHaveBeenCalledTimes(2)
    expect(toast.success).toHaveBeenCalled()
  })

  it('invalidates after update and delete', async () => {
    server.use(
      http.patch('*/api/projects/:projectId/agents/:agentRef/subscriptions/:id', () =>
        HttpResponse.json({ success: true, data: resource() })),
      http.delete('*/api/projects/:projectId/agents/:agentRef/subscriptions/:id', () =>
        HttpResponse.json({ success: true, data: { id: 'rule_x', status: 'deleted' } })),
    )
    const qc = client()
    const update = updateAgentSubscriptionMutationOptions('proj-1', 'agent-1', qc)
    await update.mutationFn({ subscriptionId: 'rule_x', data: { name: 'updated' } })
    update.onSuccess(resource() as never)
    const remove = deleteAgentSubscriptionMutationOptions('proj-1', 'agent-1', qc)
    await remove.mutationFn({ subscriptionId: 'rule_x' })
    remove.onSuccess({ id: 'rule_x', status: 'deleted' }, { subscriptionId: 'rule_x' })
    expect(qc.invalidateQueries).toHaveBeenCalledTimes(4)
  })
})
