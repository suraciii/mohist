import { describe, expect, it, vi } from 'vitest'
import { http, HttpResponse } from 'msw'
import { server, useMswServer } from '../../../../tests/support/msw'
import { toast } from 'sonner'
import {
  agentQueryOptions,
  agentListAvailabilityQueryKey,
  agentListAvailabilityQueryOptions,
  agentSessionsQueryOptions,
  agentsQueryOptions,
  archiveAgentMutationOptions,
  createAgentMutationOptions,
  unarchiveAgentMutationOptions,
  updateAgentMutationOptions,
} from './queries'

useMswServer()

function createInvalidationClient() {
  return { invalidateQueries: vi.fn() }
}

describe('agentListAvailabilityQueryOptions', () => {
  it('uses a project-scoped query key and polls every five seconds', () => {
    expect(agentListAvailabilityQueryKey('proj-1')).toEqual(['agent-availability', 'proj-1'])
    expect(agentListAvailabilityQueryOptions('proj-1')).toMatchObject({
      queryKey: ['agent-availability', 'proj-1'],
      enabled: true,
      refetchInterval: 5000,
    })
    expect(agentListAvailabilityQueryOptions(null).enabled).toBe(false)
  })

  it('requests the list summary endpoint once and returns all entries', async () => {
    const requests: Request[] = []
    server.use(
      http.get('*/api/projects/:projectId/agents/availability', ({ request }) => {
        requests.push(request)
        return HttpResponse.json({ success: true, data: [{ agentId: 'a1', queuedCount: 2 }] })
      }),
    )

    await expect(agentListAvailabilityQueryOptions('proj-1').queryFn()).resolves.toEqual([{ agentId: 'a1', queuedCount: 2 }])
    expect(requests).toHaveLength(1)
    expect(new URL(requests[0].url).pathname).toBe('/api/projects/proj-1/agents/availability')
  })
})

/* ── agentsQueryOptions ─────────────────────────────────── */
describe('agentsQueryOptions', () => {
  it('uses query key ["agents", projectId]', () => {
    expect(agentsQueryOptions('proj-1').queryKey).toEqual(['agents', 'proj-1'])
  })

  it('is enabled only when projectId is present', () => {
    expect(agentsQueryOptions('proj-1').enabled).toBe(true)
    expect(agentsQueryOptions(null).enabled).toBe(false)
  })

  it('calls listAgents(projectId, { all: true }) so archived agents are included', async () => {
    const urls: string[] = []
    server.use(
      http.get('*/api/projects/:projectId/agents', ({ request }) => {
        urls.push(new URL(request.url).pathname + new URL(request.url).search)
        return HttpResponse.json({ success: true, data: [{ id: 'a1' }, { id: 'a2', status: 'archived' }] })
      }),
    )

    await agentsQueryOptions('proj-1').queryFn()

    expect(urls).toEqual(['/api/projects/proj-1/agents?all=true'])
  })
})

/* ── agentQueryOptions ──────────────────────────────────── */
describe('agentQueryOptions', () => {
  it('uses query key ["agents", projectId, agentRef]', () => {
    expect(agentQueryOptions('proj-1', 'agent-alpha').queryKey).toEqual(['agents', 'proj-1', 'agent-alpha'])
  })

  it('is enabled only when projectId and agentRef are present', () => {
    expect(agentQueryOptions('proj-1', 'alpha').enabled).toBe(true)
    expect(agentQueryOptions(null, 'alpha').enabled).toBe(false)
    expect(agentQueryOptions('proj-1', '').enabled).toBe(false)
  })

  it('calls getAgent(projectId, agentRef) as the query function', async () => {
    const urls: string[] = []
    server.use(
      http.get('*/api/projects/:projectId/agents/:agentRef', ({ request }) => {
        urls.push(new URL(request.url).pathname)
        return HttpResponse.json({ success: true, data: { id: 'a1' } })
      }),
    )

    await agentQueryOptions('proj-1', 'agent-beta').queryFn()

    expect(urls).toEqual(['/api/projects/proj-1/agents/agent-beta'])
  })
})

/* ── agentSessionsQueryOptions ──────────────────────────── */
describe('agentSessionsQueryOptions', () => {
  it('uses query key ["agents", projectId, agentRef, "sessions"]', () => {
    expect(agentSessionsQueryOptions('proj-1', 'agent-gamma').queryKey).toEqual([
      'agents',
      'proj-1',
      'agent-gamma',
      'sessions',
    ])
  })

  it('is enabled only when projectId and agentRef are present', () => {
    expect(agentSessionsQueryOptions('proj-1', 'gamma').enabled).toBe(true)
    expect(agentSessionsQueryOptions(null, 'gamma').enabled).toBe(false)
    expect(agentSessionsQueryOptions('proj-1', '').enabled).toBe(false)
  })

  it('calls getAgentScopedSessions(projectId, agentRef)', async () => {
    const urls: string[] = []
    server.use(
      http.get('*/api/projects/:projectId/agents/:agentRef/sessions', ({ request }) => {
        urls.push(new URL(request.url).pathname)
        return HttpResponse.json({ success: true, data: [{ sessionId: 's1' }] })
      }),
    )

    await agentSessionsQueryOptions('proj-1', 'gamma').queryFn()

    expect(urls).toEqual(['/api/projects/proj-1/agents/gamma/sessions'])
  })
})

/* ── createAgentMutationOptions ─────────────────────────── */
describe('createAgentMutationOptions', () => {
  it('calls createAgent(projectId, data) in mutationFn', async () => {
    const captured: { url: string; method: string; body: unknown }[] = []
    server.use(
      http.post('*/api/projects/:projectId/agents', async ({ request }) => {
        captured.push({ url: new URL(request.url).pathname, method: request.method, body: await request.json() })
        return HttpResponse.json({ success: true, data: { id: 'new-1' } })
      }),
    )

    await createAgentMutationOptions('proj-1', createInvalidationClient()).mutationFn({
      name: 'New Agent',
      instructions: 'Do things',
    } as never)

    expect(captured).toEqual([
      {
        url: '/api/projects/proj-1/agents',
        method: 'POST',
        body: { name: 'New Agent', instructions: 'Do things' },
      },
    ])
  })

  it('invalidates ["agents"] on success', () => {
    const qc = createInvalidationClient()
    createAgentMutationOptions('proj-1', qc).onSuccess()
    expect(qc.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['agents'] })
  })

  it('shows success toast on success', () => {
    createAgentMutationOptions('proj-1', createInvalidationClient()).onSuccess()
    expect(toast.success).toHaveBeenCalledWith('Agent created')
  })

  it('shows error toast on failure', () => {
    createAgentMutationOptions('proj-1', createInvalidationClient()).onError(new Error('NAME_REQUIRED'))
    expect(toast.error).toHaveBeenCalledWith('NAME_REQUIRED')
  })

  it('falls back to "Request failed" on empty error message', () => {
    createAgentMutationOptions('proj-1', createInvalidationClient()).onError(new Error(''))
    expect(toast.error).toHaveBeenCalledWith('Request failed')
  })
})

/* ── updateAgentMutationOptions ─────────────────────────── */
describe('updateAgentMutationOptions', () => {
  it('calls updateAgent(projectId, agentRef, data) in mutationFn', async () => {
    const captured: { url: string; method: string; body: unknown }[] = []
    server.use(
      http.patch('*/api/projects/:projectId/agents/:agentRef', async ({ request }) => {
        captured.push({ url: new URL(request.url).pathname, method: request.method, body: await request.json() })
        return HttpResponse.json({ success: true, data: { id: 'a1' } })
      }),
    )

    await updateAgentMutationOptions('proj-1', createInvalidationClient()).mutationFn({
      agentRef: 'agent-delta',
      data: { instructions: 'New instructions' },
    })

    expect(captured).toEqual([
      {
        url: '/api/projects/proj-1/agents/agent-delta',
        method: 'PATCH',
        body: { instructions: 'New instructions' },
      },
    ])
  })

  it('invalidates ["agents"] on success', () => {
    const qc = createInvalidationClient()
    updateAgentMutationOptions('proj-1', qc).onSuccess()
    expect(qc.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['agents'] })
  })

  it('shows success toast on success', () => {
    updateAgentMutationOptions('proj-1', createInvalidationClient()).onSuccess()
    expect(toast.success).toHaveBeenCalledWith('Agent updated')
  })

  it('shows error toast on failure', () => {
    updateAgentMutationOptions('proj-1', createInvalidationClient()).onError(new Error('NOT_FOUND'))
    expect(toast.error).toHaveBeenCalledWith('NOT_FOUND')
  })
})

/* ── archiveAgentMutationOptions ────────────────────────── */
describe('archiveAgentMutationOptions', () => {
  it('calls archiveAgent(projectId, agentRef) in mutationFn', async () => {
    const captured: { url: string; method: string }[] = []
    server.use(
      http.delete('*/api/projects/:projectId/agents/:agentRef', ({ request }) => {
        captured.push({ url: new URL(request.url).pathname, method: request.method })
        return HttpResponse.json({ success: true, data: { id: 'a1', status: 'archived' } })
      }),
    )

    await archiveAgentMutationOptions('proj-1', createInvalidationClient()).mutationFn('agent-epsilon')

    expect(captured).toEqual([
      { url: '/api/projects/proj-1/agents/agent-epsilon', method: 'DELETE' },
    ])
  })

  it('invalidates ["agents"] and ["agent-status"] on success', () => {
    const qc = createInvalidationClient()
    archiveAgentMutationOptions('proj-1', qc).onSuccess()
    expect(qc.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['agents'] })
    expect(qc.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['agent-status'] })
  })

  it('shows success toast on success', () => {
    archiveAgentMutationOptions('proj-1', createInvalidationClient()).onSuccess()
    expect(toast.success).toHaveBeenCalledWith('Agent archived')
  })

  it('shows error toast on failure', () => {
    archiveAgentMutationOptions('proj-1', createInvalidationClient()).onError(new Error('ALREADY_ARCHIVED'))
    expect(toast.error).toHaveBeenCalledWith('ALREADY_ARCHIVED')
  })
})

/* ── unarchiveAgentMutationOptions ──────────────────────── */
describe('unarchiveAgentMutationOptions', () => {
  it('calls unarchiveAgent(projectId, agentRef) in mutationFn', async () => {
    const captured: { url: string; method: string }[] = []
    server.use(
      http.post('*/api/projects/:projectId/agents/:agentRef/unarchive', ({ request }) => {
        captured.push({ url: new URL(request.url).pathname, method: request.method })
        return HttpResponse.json({ success: true, data: { id: 'a1', status: 'active' } })
      }),
    )

    await unarchiveAgentMutationOptions('proj-1', createInvalidationClient()).mutationFn('agent-zeta')

    expect(captured).toEqual([
      { url: '/api/projects/proj-1/agents/agent-zeta/unarchive', method: 'POST' },
    ])
  })

  it('invalidates ["agents"] and ["agent-status"] on success (mirroring useArchiveAgent)', () => {
    const qc = createInvalidationClient()
    unarchiveAgentMutationOptions('proj-1', qc).onSuccess()
    expect(qc.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['agents'] })
    expect(qc.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['agent-status'] })
  })

  it('shows success toast on success', () => {
    unarchiveAgentMutationOptions('proj-1', createInvalidationClient()).onSuccess()
    expect(toast.success).toHaveBeenCalledWith('Agent restored')
  })

  it('shows error toast on failure', () => {
    unarchiveAgentMutationOptions('proj-1', createInvalidationClient()).onError(new Error('NOT_FOUND'))
    expect(toast.error).toHaveBeenCalledWith('NOT_FOUND')
  })
})
