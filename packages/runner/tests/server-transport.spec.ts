import { describe, expect, it, vi } from 'vitest'
import { RunnerTransport, RunnerTransportError, runnerTransportDiagnostics } from '../src/server/connection.js'
import { createRunnerLogger } from '../src/system/logger.js'

type Fetcher = (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>

function response(status: number, body: string, contentType = 'application/json'): Response {
  return new Response(body, { status, headers: { 'content-type': contentType } })
}

function transport(fetcher: Fetcher, options: { credential?: string; enrollmentToken?: string } = {}): RunnerTransport {
  return new RunnerTransport({ ...options, fetcher })
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
