import { describe, expect, it, vi } from 'vitest'
import {
  RunnerTransport,
  RunnerTransportError,
  ServerConnection,
  runnerTransportDiagnostics,
} from '../src/server/connection.js'
import { createRunnerLogger } from '../src/system/logger.js'
import { transportFetch, withFakeTransport } from './support/fake-transport.js'

type Fetcher = (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>

function response(status: number, body: string, contentType = 'application/json'): Response {
  return new Response(body, { status, headers: { 'content-type': contentType } })
}

function transport(fetcher: Fetcher, options: { credential?: string; enrollmentToken?: string } = {}): RunnerTransport {
  return new RunnerTransport({ ...options, fetcher })
}

const serverConnectionOptions = {
  serverUrl: 'https://runner.test',
  runnerId: 'runner-1',
  runnerRoot: '/virtual/runner',
  pollIntervalMs: 100,
  heartbeatIntervalMs: 60_000,
  dispatchLivenessProbeIntervalMs: 60_000,
}

function serverResponse(status: number, body = '', contentType = 'application/json'): Response {
  return new Response(status === 204 ? null : body, {
    status,
    headers: { 'content-type': contentType },
  })
}

function serverConnection(credential?: string): ServerConnection {
  return new ServerConnection({
    ...serverConnectionOptions,
    ...(credential === undefined ? {} : { credential }),
  })
}

function serverTest(name: string, body: () => Promise<void>): void {
  it(name, async () => await withFakeTransport(async () => await body()))
}

async function rejected<T>(promise: Promise<T>): Promise<RunnerTransportError> {
  try {
    await promise
    throw new Error('expected promise to reject')
  } catch (error) {
    expect(error).toBeInstanceOf(RunnerTransportError)
    return error as RunnerTransportError
  }
}

describe('RunnerTransport', () => {
  it('preserves an immutable canonical error value without status/code aliases', () => {
    const error = new RunnerTransportError({
      operation: 'poll',
      kind: 'http',
      httpStatus: 401,
      serverCode: 'runner_unauthorized',
      safeMessage: 'poll failed safely',
    })

    expect(error).toMatchObject({
      operation: 'poll',
      kind: 'http',
      httpStatus: 401,
      serverCode: 'runner_unauthorized',
      safeMessage: 'poll failed safely',
      message: 'poll failed safely',
    })
    expect(Object.isFrozen(error)).toBe(true)
    expect(Object.hasOwn(error, 'status')).toBe(false)
    expect(Object.hasOwn(error, 'code')).toBe(false)
  })

  it('classifies an already-aborted request as cancelled without fetching', async () => {
    const fetcher = vi.fn(async () => response(200, '{}'))
    const controller = new AbortController()
    controller.abort('timeout')
    const error = await rejected(
      transport(fetcher).request('poll', 'https://runner.test/poll', { signal: controller.signal }),
    )

    expect(error.kind).toBe('cancelled')
    expect(error.operation).toBe('poll')
    expect(error.cause).toBe('timeout')
    expect(fetcher).not.toHaveBeenCalled()
  })

  it('gives cancellation precedence over a racing fetch rejection', async () => {
    const controller = new AbortController()
    const rejection = new Error('fetch failed')
    const fetcher = vi.fn(async () => {
      controller.abort('deadline')
      throw rejection
    })
    const error = await rejected(
      transport(fetcher).request('heartbeat', 'https://runner.test/heartbeat', { signal: controller.signal }),
    )

    expect(error.kind).toBe('cancelled')
    expect(error.cause).toBe('deadline')
  })

  it('classifies an ordinary fetch rejection as network', async () => {
    const rejection = new Error('connect ECONNREFUSED https://runner.test')
    const fetcher = vi.fn(async () => {
      throw rejection
    })
    const error = await rejected(transport(fetcher).request('connect', 'https://runner.test/register', {}))

    expect(error.kind).toBe('network')
    expect(error.cause).toBe(rejection)
    expect(error.safeMessage).toBe('connect failed due to a network error')
    expect(error.safeMessage).not.toContain('runner.test')
  })

  it('classifies structured HTTP failures and redacts transport secrets and known token patterns', async () => {
    const credential = 'moh_runner_secret_123'
    const enrollmentToken = 'moh_enroll_secret_456'
    const body = JSON.stringify({
      code: 'runner_unauthorized',
      data: {
        message: `Authorization: Bearer ${credential}; token=${enrollmentToken}; Basic QWxhZGRpbjpvcGVuIHNlc2FtZQ==; ghp_123456789012345678901234567890`,
      },
      details: 'unsafe-response-body-marker',
    })
    const error = await rejected(
      transport(async () => response(401, body), { credential, enrollmentToken }).request(
        'heartbeat',
        'https://runner.test/heartbeat',
        {},
      ),
    )

    expect(error.kind).toBe('http')
    expect(error.httpStatus).toBe(401)
    expect(error.serverCode).toBe('runner_unauthorized')
    expect(error.safeMessage).toContain('heartbeat failed with HTTP status 401')
    expect(error.safeMessage).toContain('Bearer ***')
    expect(error.safeMessage).toContain('token=***')
    expect(error.safeMessage).toContain('Basic ***')
    expect(error.safeMessage).toContain('ghp_***')
    expect(error.safeMessage).not.toContain(credential)
    expect(error.safeMessage).not.toContain(enrollmentToken)
    expect(error.safeMessage).not.toContain('unsafe-response-body-marker')
    expect(error.cause).toBeUndefined()
  })

  it('keeps an invalid HTTP body as an HTTP failure', async () => {
    const error = await rejected(
      transport(async () => response(502, 'raw-response-body-marker', 'text/plain')).request(
        'poll',
        'https://runner.test/poll',
        {},
      ),
    )

    expect(error.kind).toBe('http')
    expect(error.httpStatus).toBe(502)
    expect(error.safeMessage).toBe('poll failed with HTTP status 502')
    expect(error.safeMessage).not.toContain('raw-response-body-marker')
  })

  it('projects only safe transport facts into captured log output', async () => {
    const credential = 'moh_runner_log_secret'
    const enrollmentToken = 'moh_enroll_log_secret'
    const rawBody = 'unsafe-response-body-marker'
    const error = await rejected(
      transport(
        async () =>
          response(
            401,
            JSON.stringify({
              code: 'runner_rejected',
              data: {
                message: `Bearer ${credential}; enrollment_token=${enrollmentToken}`,
              },
              raw: rawBody,
            }),
          ),
        { credential, enrollmentToken },
      ).request('heartbeat', 'https://runner.test/heartbeat', {}),
    )
    const output: string[] = []
    const logger = createRunnerLogger({
      logsPath: '/virtual/logs',
      fileWriter: {
        ensureDirectory: async () => {},
        size: async () => 0,
        append: async (_path, content) => {
          output.push(content)
        },
        rename: async () => false,
      },
      terminal: { write: (line) => output.push(line) },
    })

    logger.error('runner transport failed', runnerTransportDiagnostics(error, { includeCredentialGuidance: true }))
    await logger.flush()

    const captured = output.join('')
    expect(captured).toContain('operation=heartbeat')
    expect(captured).toContain('kind=http')
    expect(captured).toContain('httpStatus=401')
    expect(captured).toContain('serverCode=runner_rejected')
    expect(captured).toContain('safeMessage=')
    expect(captured).toContain('nextAction="re-run \'mo install runner\'"')
    expect(captured).not.toContain(credential)
    expect(captured).not.toContain(enrollmentToken)
    expect(captured).not.toContain(rawBody)
  })

  it('classifies malformed successful JSON as protocol without retaining the body', async () => {
    const body = '{"unsafe":"raw-success-body-marker"'
    const runner = transport(async () => response(200, body))
    const successfulResponse = await runner.request('fetchConfig', 'https://runner.test/config', {})
    const error = await rejected(runner.readJson(successfulResponse, 'fetchConfig'))

    expect(error.kind).toBe('protocol')
    expect(error.operation).toBe('fetchConfig')
    expect(error.safeMessage).toBe('fetchConfig returned malformed JSON')
    expect(error.safeMessage).not.toContain('raw-success-body-marker')
    expect(error.cause).toBeInstanceOf(SyntaxError)
  })

  it('reads valid success JSON through the shared seam and attaches authentication', async () => {
    let calls = 0
    let requestInit: RequestInit | undefined
    const fetcher: Fetcher = async (_input, init) => {
      calls += 1
      requestInit = init
      return response(200, JSON.stringify({ data: { ready: true } }))
    }
    const runner = transport(fetcher, { credential: 'moh_runner_abc' })
    const result = await runner.readJson<{ data: { ready: boolean } }>(
      await runner.request('fetchConfig', 'https://runner.test/config', { method: 'GET' }),
      'fetchConfig',
    )

    expect(result).toEqual({ data: { ready: true } })
    expect(calls).toBe(1)
    expect(new Headers(requestInit?.headers).get('authorization')).toBe('Bearer moh_runner_abc')
  })
})

describe('ServerConnection transport contract', () => {
  it('classifies a pre-aborted poll through the canonical value', async () => {
    await withFakeTransport(async () => {
      const controller = new AbortController()
      controller.abort('timeout')

      await expect(
        serverConnection().poll(controller.signal, {
          processGeneration: 'generation-1',
          inFlight: [],
          awaitingAck: [],
          admissionReady: true,
        }),
      ).rejects.toMatchObject({ operation: 'poll', kind: 'cancelled' } satisfies Partial<RunnerTransportError>)
      expect(transportFetch).not.toHaveBeenCalled()
    })
  })

  serverTest('classifies registration network failures without exposing the fetch error', async () => {
    transportFetch.mockRejectedValueOnce(new Error('ECONNREFUSED https://runner.test'))

    const error = await serverConnection()
      .connect(
        {
          processGeneration: 'generation-1',
          capabilities: [],
          actionCatalog: { actions: [], tombstones: [] },
          projectId: 'project-1',
        },
        new AbortController().signal,
      )
      .catch((value: unknown) => value)

    expect(error).toBeInstanceOf(RunnerTransportError)
    expect(error).toMatchObject({ operation: 'register', kind: 'network' })
    expect((error as RunnerTransportError).safeMessage).not.toContain('ECONNREFUSED')
    expect((error as RunnerTransportError).safeMessage).not.toContain('runner.test')
  })

  serverTest('classifies session mutation HTTP failures from status and structured code', async () => {
    const credential = 'moh_runner_contract_secret'
    const rawBody = 'unsafe-session-response-body'
    transportFetch.mockResolvedValueOnce(
      serverResponse(
        503,
        JSON.stringify({
          code: 'session_unavailable',
          data: { message: `Session unavailable; Bearer ${credential}` },
          raw: rawBody,
        }),
      ),
    )

    const error = await serverConnection(credential)
      .openAgentSession('project-1', 'session-1', {}, new AbortController().signal)
      .catch((value: unknown) => value)

    expect(error).toBeInstanceOf(RunnerTransportError)
    expect(error).toMatchObject({
      operation: 'openAgentSession',
      kind: 'http',
      httpStatus: 503,
      serverCode: 'session_unavailable',
    } satisfies Partial<RunnerTransportError>)
    expect((error as RunnerTransportError).safeMessage).not.toContain(credential)
    expect((error as RunnerTransportError).safeMessage).not.toContain(rawBody)
  })

  serverTest('classifies malformed runtime-event success as protocol', async () => {
    const rawBody = '{"unsafe":"runtime-event-body"'
    transportFetch.mockResolvedValueOnce(serverResponse(200, rawBody))

    await expect(
      serverConnection().agentSessionRuntimeEvents(
        'project-1',
        'session-1',
        { runtimeSessionId: 'runtime-1', runtimeEvents: [] },
        new AbortController().signal,
      ),
    ).rejects.toMatchObject({
      operation: 'agentSessionRuntimeEvents',
      kind: 'protocol',
    } satisfies Partial<RunnerTransportError>)
  })

  serverTest('accepts valid poll dispatches through ServerConnection', async () => {
    transportFetch.mockResolvedValueOnce(
      serverResponse(
        200,
        JSON.stringify({
          dispatches: [
            {
              workflowRunId: 'workflow-1',
              workId: 'work-1',
              workType: 'task',
              projectId: 'project-1',
              ownerKind: 'workflow',
              uses: 'mohist/rebase',
              variables: JSON.stringify({ workspace: { path: '/virtual/workspace' } }),
            },
          ],
        }),
      ),
    )

    const result = await serverConnection().poll(new AbortController().signal, {
      processGeneration: 'generation-1',
      inFlight: [],
      awaitingAck: [],
      admissionReady: true,
    })

    expect(result).toHaveLength(1)
    expect(result[0]?.work.workId).toBe('work-1')
  })

  serverTest('accepts registration and an empty successful session attachment', async () => {
    transportFetch.mockResolvedValueOnce(serverResponse(204))
    transportFetch.mockResolvedValueOnce(serverResponse(200))
    const connection = serverConnection()

    await connection.connect(
      {
        processGeneration: 'generation-1',
        capabilities: ['spec/*'],
        actionCatalog: { actions: [], tombstones: [] },
        projectId: 'project-1',
      },
      new AbortController().signal,
    )
    await expect(
      connection.attachAgentSession('project-1', 'session-1', {}, new AbortController().signal),
    ).resolves.toBeNull()
  })

  serverTest('accepts valid Workspace materialization reports', async () => {
    transportFetch.mockResolvedValueOnce(
      serverResponse(200, JSON.stringify({ runnerId: 'runner-1', path: '/virtual/workspace' })),
    )

    await expect(
      serverConnection().reportWorkspaceMaterialized(
        'project-1',
        'workspace-1',
        '/virtual/workspace',
        new AbortController().signal,
      ),
    ).resolves.toEqual({ runnerId: 'runner-1', path: '/virtual/workspace' })
  })

  serverTest('accepts valid artifact and task-log acknowledgements', async () => {
    transportFetch.mockResolvedValueOnce(
      serverResponse(200, JSON.stringify({ data: { uploadId: 'upload-1', path: 'artifact.txt', size: 3 } })),
    )
    transportFetch.mockResolvedValueOnce(
      serverResponse(200, JSON.stringify({ data: { status: 'changed', accepted: 1, truncated: false } })),
    )
    const connection = serverConnection()

    await expect(
      connection.uploadArtifact(
        'workflow-1',
        'work-1',
        { path: 'artifact.txt', size: 3, content: new TextEncoder().encode('abc') },
        new AbortController().signal,
      ),
    ).resolves.toMatchObject({ uploadId: 'upload-1', path: 'artifact.txt' })
    await expect(
      connection.uploadTaskLog(
        'workflow-1',
        'work-1',
        {
          entries: [{ seq: 1, timestamp: new Date('2026-07-01T00:00:00.000Z'), source: 'action', text: 'ok' }],
          truncated: false,
        },
        new AbortController().signal,
      ),
    ).resolves.toEqual({ status: 'changed', accepted: 1, truncated: false })
  })

  serverTest('accepts valid runtime-event acknowledgements', async () => {
    transportFetch.mockResolvedValueOnce(serverResponse(200, JSON.stringify([{ type: 'message.delta' }])))

    await expect(
      serverConnection().agentSessionRuntimeEvents(
        'project-1',
        'session-1',
        { runtimeSessionId: 'runtime-1', runtimeEvents: [{ type: 'message.delta', payload: {} }] },
        new AbortController().signal,
      ),
    ).resolves.toEqual([{ type: 'message.delta' }])
  })
})
