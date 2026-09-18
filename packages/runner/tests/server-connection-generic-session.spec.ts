import { describe, expect, it as vitestIt } from 'vitest'
import { RunnerTransportError, ServerConnection } from '../src/server/connection.js'
import { transportFetch, withFakeTransport } from './support/fake-transport.js'

const fetchMock = transportFetch
const it = (name: string, body: () => unknown) => vitestIt(name, () => withFakeTransport(async () => await body()))

function mockResponse({
  status,
  contentType = 'application/json',
  body = '{}',
}: {
  status: number
  contentType?: string
  body?: string | Buffer
}): Response {
  return new Response(typeof body === 'string' ? body : new Uint8Array(body), {
    status,
    headers: { 'content-type': contentType },
  })
}

function options() {
  return {
    serverUrl: 'https://runner.test',
    runnerId: 'runner-1',
    runnerRoot: '/virtual/runner',
    pollIntervalMs: 100,
    heartbeatIntervalMs: 60_000,
    dispatchLivenessProbeIntervalMs: 60_000,
  }
}

describe('ServerConnection.getAgentSession (generic)', () => {
  it('GetAgentSession_HitsGenericSessionUrl', async () => {
    fetchMock.mockResolvedValueOnce(
      mockResponse({
        status: 200,
        body: JSON.stringify({ runtimeSessionId: 'runtime-1', runtime: 'opencode', workDir: 'D:/work' }),
      }),
    )
    const connection = new ServerConnection(options())

    const result = await connection.getAgentSession('project-1', 'session-abc', new AbortController().signal)

    expect(result).toEqual({ runtimeSessionId: 'runtime-1', runtime: 'opencode', workDir: 'D:/work' })
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(url).toMatch(/\/api\/runner\/runner-1\/agent-sessions\/project-1\/session-abc$/)
    expect(init.method).toBe('GET')
    expect(url).not.toMatch(/\/api\/runner\/runner-1\/sessions\/project-1\//)
  })

  it('GetAgentSession_ReturnsNull_On404', async () => {
    fetchMock.mockResolvedValueOnce(mockResponse({ status: 404, body: 'not found' }))
    const connection = new ServerConnection(options())

    const result = await connection.getAgentSession('project-1', 'missing-session', new AbortController().signal)

    expect(result).toBeNull()
  })

  it('GetAgentSession_ThrowsOnOtherErrors', async () => {
    fetchMock.mockResolvedValueOnce(mockResponse({ status: 500, body: 'boom' }))
    const connection = new ServerConnection(options())

    await expect(
      connection.getAgentSession('project-1', 'session-1', new AbortController().signal),
    ).rejects.toMatchObject({
      operation: 'getAgentSession',
      kind: 'http',
      httpStatus: 500,
    } satisfies Partial<RunnerTransportError>)
  })

  it('GetAgentSession_ClassifiesNetworkFailures', async () => {
    fetchMock.mockRejectedValueOnce(new Error('connection refused'))
    const connection = new ServerConnection(options())

    await expect(
      connection.getAgentSession('project-1', 'session-1', new AbortController().signal),
    ).rejects.toMatchObject({
      operation: 'getAgentSession',
      kind: 'network',
    } satisfies Partial<RunnerTransportError>)
  })
})

describe('ServerConnection.openAgentSession (generic)', () => {
  it('OpenAgentSession_PostsToGenericOpenUrl_AndReturnsSession', async () => {
    fetchMock.mockResolvedValueOnce(
      mockResponse({
        status: 200,
        body: JSON.stringify({
          runtimeSessionId: 'runtime-new',
          runtime: 'opencode',
          workDir: 'D:/work',
          model: 'openai/gpt-4.1',
        }),
      }),
    )
    const connection = new ServerConnection(options())

    const result = await connection.openAgentSession(
      'project-1',
      'session-abc',
      { workId: 'work-1', workType: 'agent-job' },
      new AbortController().signal,
    )

    expect(result).toEqual({
      runtimeSessionId: 'runtime-new',
      runtime: 'opencode',
      workDir: 'D:/work',
      model: 'openai/gpt-4.1',
    })
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(url).toMatch(/\/api\/runner\/runner-1\/agent-sessions\/project-1\/session-abc\/open$/)
    expect(init.method).toBe('POST')
    expect(JSON.parse(init.body as string)).toEqual({ workId: 'work-1', workType: 'agent-job' })
    expect(url).not.toMatch(/\/api\/runner\/runner-1\/sessions\/project-1\//)
  })

  it('OpenAgentSession_ThrowsOnServerError', async () => {
    fetchMock.mockResolvedValueOnce(mockResponse({ status: 500, body: 'boom' }))
    const connection = new ServerConnection(options())

    await expect(
      connection.openAgentSession('project-1', 'session-abc', {}, new AbortController().signal),
    ).rejects.toMatchObject({
      operation: 'openAgentSession',
      kind: 'http',
      httpStatus: 500,
    } satisfies Partial<RunnerTransportError>)
  })

  it('OpenAgentSession_ClassifiesMalformedSuccessPayloads', async () => {
    fetchMock.mockResolvedValueOnce(mockResponse({ status: 200, body: 'not-json' }))
    const connection = new ServerConnection(options())

    await expect(
      connection.openAgentSession('project-1', 'session-abc', {}, new AbortController().signal),
    ).rejects.toMatchObject({
      operation: 'openAgentSession',
      kind: 'protocol',
    } satisfies Partial<RunnerTransportError>)
  })

  it('OpenAgentSession_RejectsAnEmptySuccessPayload', async () => {
    fetchMock.mockResolvedValueOnce(mockResponse({ status: 200, body: '{}' }))
    const connection = new ServerConnection(options())

    await expect(
      connection.openAgentSession('project-1', 'session-abc', {}, new AbortController().signal),
    ).rejects.toMatchObject({
      operation: 'openAgentSession',
      kind: 'protocol',
    } satisfies Partial<RunnerTransportError>)
  })
})

describe('ServerConnection.attachAgentSession (generic)', () => {
  it('AttachAgentSession_PostsToGenericAttachUrl', async () => {
    fetchMock.mockResolvedValueOnce(mockResponse({ status: 200, body: '' }))
    const connection = new ServerConnection(options())

    await connection.attachAgentSession(
      'project-1',
      'session-abc',
      { runtimeSessionId: 'runtime-1', workDir: 'D:/work' },
      new AbortController().signal,
    )

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(url).toMatch(/\/api\/runner\/runner-1\/agent-sessions\/project-1\/session-abc\/attach$/)
    expect(init.method).toBe('POST')
    expect(JSON.parse(init.body as string)).toEqual({ runtimeSessionId: 'runtime-1', workDir: 'D:/work' })
    expect(url).not.toMatch(/\/api\/runner\/runner-1\/sessions\/project-1\//)
  })

  it('AttachAgentSession_ClassifiesAbortBeforeFetching', async () => {
    const controller = new AbortController()
    controller.abort('timeout')
    const connection = new ServerConnection(options())

    await expect(
      connection.attachAgentSession('project-1', 'session-abc', {}, controller.signal),
    ).rejects.toMatchObject({
      operation: 'attachAgentSession',
      kind: 'cancelled',
    } satisfies Partial<RunnerTransportError>)
    expect(fetchMock).not.toHaveBeenCalled()
  })
})

describe('ServerConnection.recoverMissingAgentSession (canonical)', () => {
  it('RecoverMissingAgentSession_PostsToCanonicalAgentSessionUrl', async () => {
    fetchMock.mockResolvedValueOnce(
      mockResponse({
        status: 200,
        body: JSON.stringify({ runtimeSessionId: 'runtime-new', runtime: 'pi', workDir: '/work' }),
      }),
    )
    const connection = new ServerConnection(options())
    const body = {
      expectedRunnerId: 'runner-1',
      expectedRuntime: 'opencode',
      expectedRuntimeSessionId: 'runtime-old',
      replacementRuntimeSessionId: 'runtime-new',
      replacementRuntime: 'pi',
      expectedQueuedTurnId: 'turn-1',
    }

    await expect(
      connection.recoverMissingAgentSession('project-1', 'session-abc', body, new AbortController().signal),
    ).resolves.toEqual({ runtimeSessionId: 'runtime-new', runtime: 'pi', workDir: '/work' })

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(url).toMatch(/\/api\/runner\/runner-1\/agent-sessions\/project-1\/session-abc\/recover-missing$/)
    expect(init.method).toBe('POST')
    expect(JSON.parse(init.body as string)).toEqual(body)
  })
})

describe('ServerConnection.resetWorkflowAgentSession', () => {
  it('ResetWorkflowAgentSession_RejectsAResponseWithoutSessionId', async () => {
    fetchMock.mockResolvedValueOnce(
      mockResponse({ status: 200, body: JSON.stringify({ runtimeSessionId: 'runtime-1' }) }),
    )
    const connection = new ServerConnection(options())

    await expect(
      connection.resetWorkflowAgentSession('project-1', 'wf-1', 'plan', {}, new AbortController().signal),
    ).rejects.toMatchObject({
      operation: 'resetWorkflowAgentSession',
      kind: 'protocol',
    } satisfies Partial<RunnerTransportError>)
  })

  it('ResetWorkflowAgentSession_PostsTheExpectedBindingCasToTheWorkflowResetUrl', async () => {
    fetchMock.mockResolvedValueOnce(
      mockResponse({
        status: 200,
        body: JSON.stringify({
          sessionId: 'session-1',
          runtimeSessionId: 'runtime-new',
          runtime: 'opencode',
          workDir: '/workspace',
        }),
      }),
    )
    const connection = new ServerConnection(options())
    const body = {
      expectedRunnerId: 'runner-1',
      expectedRuntime: 'opencode',
      expectedRuntimeSessionId: 'runtime-old',
      replacementRuntimeSessionId: 'runtime-new',
      replacementRuntime: 'opencode',
    }

    const result = await connection.resetWorkflowAgentSession(
      'project-1',
      'wf-1',
      'plan',
      body,
      new AbortController().signal,
    )

    expect(result).toEqual({
      sessionId: 'session-1',
      runtimeSessionId: 'runtime-new',
      runtime: 'opencode',
      workDir: '/workspace',
    })
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(url).toMatch(/\/api\/runner\/runner-1\/sessions\/project-1\/wf-1\/plan\/reset$/)
    expect(init.method).toBe('POST')
    expect(JSON.parse(init.body as string)).toEqual(body)
  })
})

describe('ServerConnection.workflowAgentSessionCleanupTurn', () => {
  const body = {
    cleanupOperationId: 'cleanup-1',
    prompt: 'clean the worktree',
    actionAttemptId: 'task-1.1',
    workId: 'work-1',
    agentSessionId: 'agent-session-1',
    runtime: 'pi',
    runtimeSessionId: 'runtime-1',
  }

  it('preserves a new cleanup receipt array', async () => {
    fetchMock.mockResolvedValueOnce(
      mockResponse({
        status: 200,
        body: JSON.stringify([
          {
            type: 'session.cleanup',
            cleanupOperationId: 'cleanup-1',
            inputDeliveryId: 'cleanup-input-1',
            agentTurnId: 'cleanup-turn-1',
            agentSessionId: 'agent-session-1',
          },
        ]),
      }),
    )
    const connection = new ServerConnection(options())

    await expect(
      connection.workflowAgentSessionCleanupTurn('project-1', 'wf-1', 'build', body, new AbortController().signal),
    ).resolves.toEqual([
      {
        type: 'session.cleanup',
        cleanupOperationId: 'cleanup-1',
        inputDeliveryId: 'cleanup-input-1',
        agentTurnId: 'cleanup-turn-1',
        agentSessionId: 'agent-session-1',
      },
    ])
  })

  it('preserves an idempotent empty cleanup receipt array', async () => {
    fetchMock.mockResolvedValueOnce(mockResponse({ status: 200, body: '[]' }))
    const connection = new ServerConnection(options())

    await expect(
      connection.workflowAgentSessionCleanupTurn('project-1', 'wf-1', 'build', body, new AbortController().signal),
    ).resolves.toEqual([])
  })
})

describe('ServerConnection.agentSessionRuntimeEvents (generic)', () => {
  it('AgentSessionRuntimeEvents_PostsToGenericRuntimeEventsUrl', async () => {
    fetchMock.mockResolvedValueOnce(mockResponse({ status: 200, body: '[]' }))
    const connection = new ServerConnection(options())

    const receipts = await connection.agentSessionRuntimeEvents(
      'project-1',
      'session-abc',
      {
        workId: 'work-1',
        workType: 'agent-job',
        stage: 'agent',
        runtimeSessionId: 'runtime-1',
        runtimeEvents: [{ type: 'session.input', payload: { text: 'hi' } }],
      },
      new AbortController().signal,
    )

    expect(receipts).toEqual([])
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(url).toMatch(/\/api\/runner\/runner-1\/agent-sessions\/project-1\/session-abc\/runtime-events$/)
    expect(init.method).toBe('POST')
    const body = JSON.parse(init.body as string)
    expect(body).toMatchObject({
      runtimeSessionId: 'runtime-1',
      runtimeEvents: [{ type: 'session.input', payload: { text: 'hi' } }],
    })
    expect(url).not.toMatch(/\/api\/runner\/runner-1\/sessions\/project-1\//)
  })

  it('AgentSessionRuntimeEvents_ReturnsParsedReceipts', async () => {
    fetchMock.mockResolvedValueOnce(
      mockResponse({ status: 200, body: JSON.stringify([{ type: 'session.input' }, { type: 'message.delta' }]) }),
    )
    const connection = new ServerConnection(options())

    const receipts = await connection.agentSessionRuntimeEvents(
      'project-1',
      'session-abc',
      {
        workId: null,
        workType: null,
        stage: null,
        runtimeSessionId: 'runtime-1',
        runtimeEvents: [{ type: 'session.input', payload: {} }],
      },
      new AbortController().signal,
    )

    expect(receipts).toEqual([{ type: 'session.input' }, { type: 'message.delta' }])
  })

  it('AgentSessionRuntimeEvents_ClassifiesMalformedReceiptsAsProtocol', async () => {
    fetchMock.mockResolvedValueOnce(mockResponse({ status: 200, body: '[{}]' }))
    const connection = new ServerConnection(options())

    await expect(
      connection.agentSessionRuntimeEvents(
        'project-1',
        'session-abc',
        { runtimeSessionId: 'runtime-1', runtimeEvents: [{ type: 'session.input', payload: {} }] },
        new AbortController().signal,
      ),
    ).rejects.toMatchObject({
      operation: 'agentSessionRuntimeEvents',
      kind: 'protocol',
    } satisfies Partial<RunnerTransportError>)
  })
})

describe('ServerConnection runtime-event failure metadata', () => {
  it('preserves HTTP status and the Server ApiResponse code without parsing the message', async () => {
    fetchMock.mockResolvedValueOnce(
      mockResponse({
        status: 409,
        body: JSON.stringify({ success: false, error: 'binding changed', code: 'agent_session_changed' }),
      }),
    )
    const connection = new ServerConnection(options())

    await expect(
      connection.reconcileAgentSessionRuntimeEvents(
        'session-abc',
        { runtimeSessionId: 'runtime-1', runtimeEvents: [] },
        new AbortController().signal,
      ),
    ).rejects.toMatchObject({
      name: 'RunnerTransportError',
      operation: 'reconcileAgentSessionRuntimeEvents',
      kind: 'http',
      httpStatus: 409,
      serverCode: 'agent_session_changed',
    } satisfies Partial<RunnerTransportError>)
  })
})

describe('ServerConnection generic vs workflow URL segregation', () => {
  it('GenericUrls_AreDistinctFrom_WorkflowUrls', async () => {
    fetchMock.mockResolvedValueOnce(
      mockResponse({ status: 200, body: JSON.stringify({ status: 'idle', runtimeSessionId: null }) }),
    )
    await new ServerConnection(options()).getAgentSession('project-1', 'session-abc', new AbortController().signal)
    const genericGet = (fetchMock.mock.calls[0] as [string, RequestInit])[0]

    fetchMock.mockResolvedValueOnce(
      mockResponse({ status: 200, body: JSON.stringify({ sessionId: 'agent-session-1' }) }),
    )
    await new ServerConnection(options()).getWorkflowAgentSession(
      'project-1',
      'wf-1',
      'build',
      new AbortController().signal,
    )
    const workflowGet = (fetchMock.mock.calls[1] as [string, RequestInit])[0]

    expect(genericGet).toMatch(/\/api\/runner\/runner-1\/agent-sessions\/project-1\/session-abc$/)
    expect(workflowGet).toMatch(/\/api\/runner\/runner-1\/sessions\/project-1\/wf-1\/build$/)
    expect(genericGet).not.toBe(workflowGet)
  })

  it('GenericOpenUrl_ContainsSlashOpen_WorkflowOpenUrl_AlsoContainsSlashOpen', async () => {
    fetchMock.mockResolvedValueOnce(
      mockResponse({ status: 200, body: JSON.stringify({ status: 'idle', runtimeSessionId: 'runtime-1' }) }),
    )
    await new ServerConnection(options()).openAgentSession('project-1', 'session-abc', {}, new AbortController().signal)
    const genericOpen = (fetchMock.mock.calls[0] as [string, RequestInit])[0]

    fetchMock.mockResolvedValueOnce(
      mockResponse({ status: 200, body: JSON.stringify({ sessionId: 'agent-session-1' }) }),
    )
    await new ServerConnection(options()).openWorkflowAgentSession(
      'project-1',
      'wf-1',
      'build',
      {},
      new AbortController().signal,
    )
    const workflowOpen = (fetchMock.mock.calls[1] as [string, RequestInit])[0]

    expect(genericOpen).toMatch(/\/api\/runner\/runner-1\/agent-sessions\/project-1\/session-abc\/open$/)
    expect(workflowOpen).toMatch(/\/api\/runner\/runner-1\/sessions\/project-1\/wf-1\/build\/open$/)
    expect(genericOpen).not.toBe(workflowOpen)
  })

  it('GenericAttachUrl_ContainsAgentSessionsPrefix', async () => {
    fetchMock.mockResolvedValueOnce(mockResponse({ status: 200, body: '' }))
    await new ServerConnection(options()).attachAgentSession(
      'project-1',
      'session-abc',
      {},
      new AbortController().signal,
    )
    const genericAttach = (fetchMock.mock.calls[0] as [string, RequestInit])[0]

    expect(genericAttach).toMatch(/\/api\/runner\/runner-1\/agent-sessions\/project-1\/session-abc\/attach$/)
  })

  it('GenericRuntimeEventsUrl_ContainsAgentSessionsPrefix', async () => {
    fetchMock.mockResolvedValueOnce(mockResponse({ status: 200, body: '[]' }))
    await new ServerConnection(options()).agentSessionRuntimeEvents(
      'project-1',
      'session-abc',
      { runtimeEvents: [] },
      new AbortController().signal,
    )
    const genericRuntime = (fetchMock.mock.calls[0] as [string, RequestInit])[0]

    expect(genericRuntime).toMatch(/\/api\/runner\/runner-1\/agent-sessions\/project-1\/session-abc\/runtime-events$/)
  })
})
