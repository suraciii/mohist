import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  archiveAgentSubscription,
  createAgentSubscription,
  deleteAgentSubscription,
  formatAgentSubscriptionFilter,
  listAgentSubscriptions,
  restoreAgentSubscription,
} from './subscriptions'
import { ApiError } from '../../../shared/api/client'

afterEach(() => {
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

function envelope<T>(data: T, status: number = 200): Response {
  return new Response(JSON.stringify({ success: true, data }), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })
}

describe('listAgentSubscriptions', () => {
  it('GETs /api/projects/{ref}/agents/{agentRef}/subscriptions and returns an array', async () => {
    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(
      envelope([
        {
          id: 'subs_1',
          projectId: 'proj-1',
          agentId: 'agent-1',
          name: 'default',
          filter: { type: 'com.mohist.workflow.stage.*', source: null, subject: null },
          responsePrompt: 'approve if clear',
          priority: 0,
          status: 'active',
          createdAt: '2026-06-01T00:00:00.000Z',
          updatedAt: '2026-06-01T00:00:00.000Z',
        },
      ]),
    )
    vi.stubGlobal('fetch', fetchMock)

    const result = await listAgentSubscriptions('proj-1', 'agent-1')

    expect(result).toHaveLength(1)
    expect(result[0].name).toBe('default')
    expect(result[0].status).toBe('active')
    expect(fetchMock).toHaveBeenCalledTimes(1)
    const [calledPath, calledInit] = fetchMock.mock.calls[0]
    expect(calledPath).toBe('/api/projects/proj-1/agents/agent-1/subscriptions')
    expect(calledInit?.method ?? 'GET').toBe('GET')
  })

  it('encodes agent refs with special characters', async () => {
    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(envelope([]))
    vi.stubGlobal('fetch', fetchMock)

    await listAgentSubscriptions('proj-1', 'a/b')

    const [calledPath] = fetchMock.mock.calls[0]
    expect(calledPath).toBe('/api/projects/proj-1/agents/a%2Fb/subscriptions')
  })
})

describe('createAgentSubscription', () => {
  it('POSTs the subscription payload and returns the dto', async () => {
    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(
      envelope(
        {
          id: 'subs_new',
          projectId: 'proj-1',
          agentId: 'agent-1',
          name: 'fallback',
          filter: { type: 'com.mohist.workflow.stage.*', source: null, subject: null },
          responsePrompt: 'approve if clear',
          priority: 1,
          status: 'active',
          createdAt: '2026-06-01T00:00:00.000Z',
          updatedAt: '2026-06-01T00:00:00.000Z',
        },
        201,
      ),
    )
    vi.stubGlobal('fetch', fetchMock)

    const result = await createAgentSubscription('proj-1', 'agent-1', {
      name: 'fallback',
      filter: { type: 'com.mohist.workflow.stage.*' },
      responsePrompt: 'approve if clear',
      priority: 1,
    })

    expect(result.id).toBe('subs_new')
    expect(result.status).toBe('active')
    const [calledPath, calledInit] = fetchMock.mock.calls[0]
    expect(calledPath).toBe('/api/projects/proj-1/agents/agent-1/subscriptions')
    expect(calledInit?.method).toBe('POST')
    expect(calledInit?.body).toBe(
      JSON.stringify({
        name: 'fallback',
        filter: { type: 'com.mohist.workflow.stage.*' },
        responsePrompt: 'approve if clear',
        priority: 1,
      }),
    )
  })

  it('surfaces server-side error messages (e.g. agent_archived)', async () => {
    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(
      new Response(
        JSON.stringify({
          success: false,
          error: 'Archived agents cannot receive new subscriptions',
          code: 'agent_archived',
        }),
        { status: 409, headers: { 'Content-Type': 'application/json' } },
      ),
    )
    vi.stubGlobal('fetch', fetchMock)

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
    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(
      envelope({
        id: 'subs_x',
        projectId: 'proj-1',
        agentId: 'agent-1',
        name: 'fallback',
        filter: { type: 'com.mohist.workflow.stage.*', source: null, subject: null },
        responsePrompt: 'approve if clear',
        priority: 0,
        status: 'archived',
        createdAt: '2026-06-01T00:00:00.000Z',
        updatedAt: '2026-06-02T00:00:00.000Z',
      }),
    )
    vi.stubGlobal('fetch', fetchMock)

    const result = await archiveAgentSubscription('proj-1', 'agent-1', 'subs_x')

    expect(result.status).toBe('archived')
    const [calledPath, calledInit] = fetchMock.mock.calls[0]
    expect(calledPath).toBe(
      '/api/projects/proj-1/agents/agent-1/subscriptions/subs_x/archive',
    )
    expect(calledInit?.method).toBe('POST')
  })

  it('encodes subscription ids with special characters', async () => {
    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(
      envelope({
        id: 'subs/a',
        projectId: 'proj-1',
        agentId: 'agent-1',
        name: 'fallback',
        filter: { type: 'com.mohist.workflow.stage.*', source: null, subject: null },
        responsePrompt: 'approve if clear',
        priority: 0,
        status: 'archived',
        createdAt: '2026-06-01T00:00:00.000Z',
        updatedAt: '2026-06-02T00:00:00.000Z',
      }),
    )
    vi.stubGlobal('fetch', fetchMock)

    await archiveAgentSubscription('proj-1', 'agent-1', 'subs/a')

    const [calledPath] = fetchMock.mock.calls[0]
    expect(calledPath).toBe(
      '/api/projects/proj-1/agents/agent-1/subscriptions/subs%2Fa/archive',
    )
  })
})

describe('restoreAgentSubscription', () => {
  it('POSTs the restore endpoint and returns the dto', async () => {
    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(
      envelope({
        id: 'subs_x',
        projectId: 'proj-1',
        agentId: 'agent-1',
        name: 'fallback',
        filter: { type: 'com.mohist.workflow.stage.*', source: null, subject: null },
        responsePrompt: 'approve if clear',
        priority: 0,
        status: 'active',
        createdAt: '2026-06-01T00:00:00.000Z',
        updatedAt: '2026-06-02T00:00:00.000Z',
      }),
    )
    vi.stubGlobal('fetch', fetchMock)

    const result = await restoreAgentSubscription('proj-1', 'agent-1', 'subs_x')

    expect(result.status).toBe('active')
    const [calledPath, calledInit] = fetchMock.mock.calls[0]
    expect(calledPath).toBe(
      '/api/projects/proj-1/agents/agent-1/subscriptions/subs_x/restore',
    )
    expect(calledInit?.method).toBe('POST')
  })
})

describe('deleteAgentSubscription', () => {
  it('DELETEs the subscription endpoint', async () => {
    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(envelope(null))
    vi.stubGlobal('fetch', fetchMock)

    await deleteAgentSubscription('proj-1', 'agent-1', 'subs_x')

    const [calledPath, calledInit] = fetchMock.mock.calls[0]
    expect(calledPath).toBe('/api/projects/proj-1/agents/agent-1/subscriptions/subs_x')
    expect(calledInit?.method).toBe('DELETE')
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
