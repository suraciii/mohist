import { describe, expect, it as vitestIt } from 'vitest'
import { ServerConnection } from './connection.js'
import { WorkspaceHomeClaimedError } from '../runtime/workspace-entity.js'
import { transportFetch, withFakeTransport } from '../../tests/support/fake-transport.js'

const options = {
  serverUrl: 'https://runner.test',
  runnerId: 'runner-1',
  runnerRoot: '/virtual/runner',
  pollIntervalMs: 1,
  heartbeatIntervalMs: 1,
  dispatchLivenessProbeIntervalMs: 1,
}

const signal = new AbortController().signal
const fetchSpy = transportFetch

function it(name: string, body: () => Promise<void>): void {
  vitestIt(name, async () => await withFakeTransport(async () => await body()))
}

function itEach(cases: readonly unknown[]) {
  return (name: string, body: (...args: any[]) => Promise<void>) => {
    vitestIt.each(cases)(name, async (...args: any[]) => await withFakeTransport(async () => await body(...args)))
  }
}

describe('ServerConnection machine credential', () => {
  it('attaches the credential as a Bearer header on every request', async () => {
    fetchSpy.mockResolvedValue(
      new Response(JSON.stringify({ dispatches: [] }), {
        status: 200,
        headers: { 'content-type': 'application/json' },
      }),
    )

    const connection = new ServerConnection({ ...options, credential: 'moh_runner_abc' })
    await connection.poll(signal)

    const [, init] = fetchSpy.mock.calls[0]!
    const headers = new Headers(init?.headers)
    expect(headers.get('authorization')).toBe('Bearer moh_runner_abc')
  })

  it('leaves requests headerless without a credential', async () => {
    fetchSpy.mockResolvedValue(
      new Response(JSON.stringify({ dispatches: [] }), {
        status: 200,
        headers: { 'content-type': 'application/json' },
      }),
    )

    await new ServerConnection(options).poll(signal)

    const [, init] = fetchSpy.mock.calls[0]!
    const headers = new Headers(init?.headers)
    expect(headers.get('authorization')).toBeNull()
  })
})

describe('ServerConnection AgentSession reconciliation', () => {
  it('reads the runner-scoped binding list', async () => {
    fetchSpy.mockResolvedValue(
      new Response(
        JSON.stringify([
          {
            sessionId: 'session-1',
            runtime: 'opencode',
            runtimeSessionId: 'runtime-1',
            workDir: '/work',
          },
        ]),
        { status: 200, headers: { 'content-type': 'application/json' } },
      ),
    )

    const bindings = await new ServerConnection(options).listAgentSessionsForReconcile(signal)

    expect(bindings).toEqual([
      {
        sessionId: 'session-1',
        runtime: 'opencode',
        runtimeSessionId: 'runtime-1',
        workDir: '/work',
      },
    ])
    expect(fetchSpy.mock.calls[0]?.[0]).toBe('https://runner.test/api/runner/runner-1/agent-sessions/reconcile')
  })

  itEach([
    {},
    [{ sessionId: 'session-1', runtime: 'unknown', runtimeSessionId: 'runtime-1', workDir: '/work' }],
    [{ sessionId: 'session-1', runtime: 'opencode', runtimeSessionId: '', workDir: '/work' }],
  ])('rejects corrupt reconcile responses', async (payload) => {
    fetchSpy.mockResolvedValue(new Response(JSON.stringify(payload), { status: 200 }))

    await expect(new ServerConnection(options).listAgentSessionsForReconcile(signal)).rejects.toThrow('malformed')
  })
})

describe('ServerConnection workflow runtime events', () => {
  it('returns accepted entries when every submitted fact is accepted', async () => {
    fetchSpy.mockResolvedValue(
      new Response(
        JSON.stringify([
          { id: '1', type: 'session.input' },
          { id: '2', type: 'message.delta' },
        ]),
        { status: 200, headers: { 'content-type': 'application/json' } },
      ),
    )

    const accepted = await new ServerConnection(options).workflowAgentSessionRuntimeEvents(
      'project',
      'run',
      'session',
      { runtimeSessionId: 'runtime', runtimeEvents: [{ type: 'session.input' }, { type: 'message.delta' }] },
      signal,
    )

    expect(accepted).toHaveLength(2)
  })

  it('surfaces malformed and count-mismatched acceptance responses', async () => {
    fetchSpy.mockResolvedValueOnce(new Response('not-json', { status: 200 }))
    await expect(
      new ServerConnection(options).workflowAgentSessionRuntimeEvents(
        'project',
        'run',
        'session',
        { runtimeEvents: [{ type: 'session.input' }] },
        signal,
      ),
    ).rejects.toThrow('malformed JSON')

    fetchSpy.mockResolvedValueOnce(new Response('[]', { status: 200 }))
    await expect(
      new ServerConnection(options).workflowAgentSessionRuntimeEvents(
        'project',
        'run',
        'session',
        { runtimeEvents: [{ type: 'session.input' }] },
        signal,
      ),
    ).rejects.toThrow('acceptance mismatch')
  })
})

describe('ServerConnection agent-input attachments', () => {
  it('fetches bytes only through the owning input scoped route', async () => {
    fetchSpy.mockResolvedValue(
      new Response('content', {
        status: 200,
        headers: {
          'content-type': 'text/plain',
          'content-disposition': 'attachment; filename=notes.txt',
        },
      }),
    )

    const content = await new ServerConnection(options).openAgentInputAttachment(
      'project/1',
      'session/1',
      'input/1',
      'attachment/1',
      signal,
    )

    expect(new TextDecoder().decode(content?.bytes)).toBe('content')
    expect(fetchSpy.mock.calls[0]?.[0]).toBe(
      'https://runner.test/api/projects/project%2F1/agent-sessions/session%2F1/inputs/input%2F1/attachments/attachment%2F1/content',
    )
    expect(JSON.stringify(fetchSpy.mock.calls[0]?.[1])).not.toContain('temp')
    expect(JSON.stringify(fetchSpy.mock.calls[0]?.[1])).not.toContain('token')
  })
})

describe('ServerConnection named workspace materialization report', () => {
  it('posts the materialized path and parses the recorded home', async () => {
    fetchSpy.mockResolvedValue(
      new Response(JSON.stringify({ runnerId: 'runner-1', path: '/virtual/ws/pay' }), {
        status: 200,
        headers: { 'content-type': 'application/json' },
      }),
    )

    const report = await new ServerConnection(options).reportWorkspaceMaterialized(
      'project-1',
      'pay',
      '/virtual/ws/pay',
      signal,
    )

    expect(report).toEqual({ runnerId: 'runner-1', path: '/virtual/ws/pay' })
    expect(fetchSpy.mock.calls[0]?.[0]).toBe(
      'https://runner.test/api/runner/runner-1/workspaces/project-1/pay/materialized',
    )
    const init = fetchSpy.mock.calls[0]?.[1] as RequestInit | undefined
    expect(init?.method).toBe('POST')
    expect(JSON.parse(String(init?.body))).toEqual({ path: '/virtual/ws/pay' })
  })

  it('throws WorkspaceHomeClaimedError on a 409 workspace_home_claimed answer', async () => {
    fetchSpy.mockResolvedValue(
      new Response(JSON.stringify({ ok: false, code: 'workspace_home_claimed', error: 'already materialized' }), {
        status: 409,
        headers: { 'content-type': 'application/json' },
      }),
    )

    await expect(
      new ServerConnection(options).reportWorkspaceMaterialized('project-1', 'pay', '/virtual/ws/pay', signal),
    ).rejects.toBeInstanceOf(WorkspaceHomeClaimedError)
  })

  it('throws a plain error on other non-2xx answers', async () => {
    fetchSpy.mockResolvedValue(new Response('bad', { status: 400 }))
    await expect(
      new ServerConnection(options).reportWorkspaceMaterialized('project-1', 'pay', '/virtual/ws/pay', signal),
    ).rejects.toThrow('workspace materialization failed: 400')
  })
})

describe('ServerConnection workspace reclaimability', () => {
  itEach([
    [
      'active with no bound sessions',
      { status: 'active', activeBoundSessions: 0 },
      { status: 'active', activeBoundSessions: 0 },
    ],
    [
      'active with bound sessions',
      { status: 'active', activeBoundSessions: 2 },
      { status: 'active', activeBoundSessions: 2 },
    ],
    ['archived', { status: 'archived', activeBoundSessions: 0 }, { status: 'archived', activeBoundSessions: 0 }],
  ] as const)('parses the wrapped %s answer', async (_label, data, expected) => {
    fetchSpy.mockResolvedValue(
      new Response(JSON.stringify({ data }), { status: 200, headers: { 'content-type': 'application/json' } }),
    )

    const info = await new ServerConnection(options).getWorkspaceReclaimability('project-1', 'pay', signal)

    expect(info).toEqual(expected)
    expect(fetchSpy.mock.calls[0]?.[0]).toBe(
      'https://runner.test/api/runner/runner-1/workspaces/project-1/pay/reclaimable',
    )
    const init = fetchSpy.mock.calls[0]?.[1] as RequestInit | undefined
    expect(init?.method).toBe('GET')
  })

  it('throws on non-2xx', async () => {
    fetchSpy.mockResolvedValue(new Response('gone', { status: 404 }))
    await expect(new ServerConnection(options).getWorkspaceReclaimability('project-1', 'pay', signal)).rejects.toThrow(
      'workspace reclaimability failed: 404',
    )
  })

  itEach([
    ['an unknown status', JSON.stringify({ data: { status: 'suspended', activeBoundSessions: 0 } }), 'unknown status'],
    [
      'a malformed count',
      JSON.stringify({ data: { status: 'active', activeBoundSessions: -1 } }),
      'invalid session count',
    ],
    ['a missing data envelope', JSON.stringify({ status: 'active', activeBoundSessions: 0 }), 'malformed response'],
    ['a non-object data envelope', JSON.stringify({ data: null }), 'malformed response'],
    ['malformed JSON', 'not-json', 'malformed JSON'],
  ])('rejects %s', async (_label, body, message) => {
    fetchSpy.mockResolvedValue(new Response(body, { status: 200, headers: { 'content-type': 'application/json' } }))
    await expect(new ServerConnection(options).getWorkspaceReclaimability('project-1', 'pay', signal)).rejects.toThrow(
      message,
    )
  })
})
