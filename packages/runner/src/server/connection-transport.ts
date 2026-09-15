import { currentRunnerTransport } from '../system/filesystem.js'
import { RunnerTransportError, type RunnerTransportErrorOptions } from './connection-errors.js'

const MAX_SERVER_CODE_LENGTH = 128
const MAX_SAFE_MESSAGE_LENGTH = 512
const MAX_ERROR_BODY_PARSE_LENGTH = 64 * 1024
const REDACTED = '***'

type RunnerTransportFetcher = (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>

export interface RunnerRequestOptions {
  readonly allowedStatuses?: readonly number[]
}

export interface RunnerRequestTransport {
  request(operation: string, input: string, init: RequestInit, options?: RunnerRequestOptions): Promise<Response>
  readJson<T>(response: Response, operation: string, allowEmpty?: boolean): Promise<T | null>
  readBytes(response: Response, operation: string): Promise<Uint8Array>
}

export interface RunnerTransportOptions {
  credential?: string | null
  enrollmentToken?: string | null
  fetcher?: RunnerTransportFetcher
}

interface RunnerResponseContext {
  readonly signal: AbortSignal | null | undefined
}

export class RunnerTransport implements RunnerRequestTransport {
  private readonly credential: string | null
  private readonly secrets: readonly string[]
  private readonly fetcher: RunnerTransportFetcher
  private readonly responseContexts = new WeakMap<Response, RunnerResponseContext>()

  constructor(options: RunnerTransportOptions = {}) {
    this.credential = options.credential ?? null
    this.secrets = [options.credential, options.enrollmentToken].filter(
      (value): value is string => typeof value === 'string' && value.length > 0,
    )
    this.fetcher = options.fetcher ?? ((input, init) => currentRunnerTransport()(input, init))
  }

  async request(
    operation: string,
    input: string,
    init: RequestInit,
    options: RunnerRequestOptions = {},
  ): Promise<Response> {
    const signal = init.signal
    try {
      if (signal?.aborted) throw this.cancelled(operation, signal)
      const headers = new Headers(init.headers)
      if (this.credential) headers.set('authorization', `Bearer ${this.credential}`)
      const response = await this.fetcher(input, { ...init, headers })
      this.responseContexts.set(response, { signal })
      if (signal?.aborted) throw this.cancelled(operation, signal)
      if (!response.ok && !options.allowedStatuses?.includes(response.status)) {
        throw await this.httpFailure(operation, response)
      }
      return response
    } catch (cause) {
      if (cause instanceof RunnerTransportError) throw cause
      if (signal?.aborted) throw this.cancelled(operation, signal)
      throw new RunnerTransportError({
        operation: this.safeOperation(operation),
        kind: 'network',
        safeMessage: `${this.safeOperation(operation)} failed due to a network error`,
        cause,
      })
    }
  }

  async readJson<T>(response: Response, operation: string, allowEmpty = false): Promise<T | null> {
    if (!response.ok) throw await this.httpFailure(operation, response)

    let text: string
    try {
      text = await response.text()
    } catch (cause) {
      throw this.bodyReadFailure(operation, response, cause)
    }
    if (text.trim().length === 0) {
      if (allowEmpty) return null
      throw this.protocolFailure(operation)
    }

    try {
      return JSON.parse(text) as T
    } catch {
      throw this.protocolFailure(operation)
    }
  }

  async readBytes(response: Response, operation: string): Promise<Uint8Array> {
    if (!response.ok) throw await this.httpFailure(operation, response)
    try {
      return new Uint8Array(await response.arrayBuffer())
    } catch (cause) {
      throw this.bodyReadFailure(operation, response, cause)
    }
  }

  private async httpFailure(operation: string, response: Response): Promise<RunnerTransportError> {
    let code: string | undefined
    let message: string | undefined
    let text: string
    try {
      text = await response.text()
    } catch {
      const signal = this.responseContexts.get(response)?.signal
      if (signal?.aborted) throw this.cancelled(operation, signal)
      return this.httpFailureWithoutBody(operation, response.status)
    }
    try {
      if (text.length <= MAX_ERROR_BODY_PARSE_LENGTH) {
        const payload = JSON.parse(text) as unknown
        const fields = readServerFields(payload)
        code = fields.code
        message = fields.message
      }
    } catch {
      // The HTTP status remains authoritative when the body is absent or invalid.
    }

    return this.httpFailureWithMessage(operation, response.status, code, message)
  }

  private httpFailureWithoutBody(operation: string, httpStatus: number): RunnerTransportError {
    return this.httpFailureWithMessage(operation, httpStatus)
  }

  private httpFailureWithMessage(
    operation: string,
    httpStatus: number,
    code?: string,
    message?: string,
  ): RunnerTransportError {
    const safeOperation = this.safeOperation(operation)
    const safeCode = code ? this.safeText(code, MAX_SERVER_CODE_LENGTH) : undefined
    const safeMessage = message
      ? `${safeOperation} failed with HTTP status ${httpStatus}: ${this.safeText(message)}`
      : `${safeOperation} failed with HTTP status ${httpStatus}`
    return new RunnerTransportError({
      operation: safeOperation,
      kind: 'http',
      httpStatus,
      ...(safeCode ? { serverCode: safeCode } : {}),
      safeMessage: this.safeText(safeMessage),
    })
  }

  private bodyReadFailure(operation: string, response: Response, cause: unknown): RunnerTransportError {
    const signal = this.responseContexts.get(response)?.signal
    if (signal?.aborted) return this.cancelled(operation, signal)
    const safeOperation = this.safeOperation(operation)
    return new RunnerTransportError({
      operation: safeOperation,
      kind: 'network',
      safeMessage: `${safeOperation} failed due to a network error while reading the response`,
      cause,
    })
  }

  private protocolFailure(operation: string, cause?: unknown): RunnerTransportError {
    return createRunnerProtocolError(this.safeOperation(operation), 'returned malformed JSON', cause)
  }

  private cancelled(operation: string, signal: RequestInit['signal']): RunnerTransportError {
    const options: RunnerTransportErrorOptions = {
      operation: this.safeOperation(operation),
      kind: 'cancelled',
      safeMessage: `${this.safeOperation(operation)} was cancelled or timed out`,
    }
    if (signal?.reason !== undefined) options.cause = signal.reason
    return new RunnerTransportError(options)
  }

  private safeOperation(operation: string): string {
    return this.safeText(operation) || 'Runner request'
  }

  private safeText(value: string, limit = MAX_SAFE_MESSAGE_LENGTH): string {
    let result = value
    for (const secret of this.secrets) result = result.split(secret).join(REDACTED)
    result = maskKnownPatterns(result)
    if (result.length <= limit) return result
    return `${result.slice(0, Math.max(0, limit - 3))}...`
  }
}

export function createRunnerProtocolError(
  operation: string,
  detail = 'returned malformed response',
  cause?: unknown,
  serverCode?: string,
): RunnerTransportError {
  const safeMessage = `${operation} ${detail}`
  const options: RunnerTransportErrorOptions = {
    operation,
    kind: 'protocol',
    safeMessage:
      safeMessage.length <= MAX_SAFE_MESSAGE_LENGTH
        ? safeMessage
        : `${safeMessage.slice(0, MAX_SAFE_MESSAGE_LENGTH - 3)}...`,
  }
  if (cause !== undefined) options.cause = cause
  if (serverCode) options.serverCode = serverCode
  return new RunnerTransportError(options)
}

function readServerFields(value: unknown): { code?: string; message?: string } {
  if (!isRecord(value)) return {}
  const data = isRecord(value.data) ? value.data : null
  const error = isRecord(value.error) ? value.error : null
  const code = firstString(value.code, data?.code, error?.code)
  const message = firstString(data?.message, value.error, value.message, error?.message, data?.error)
  return {
    ...(code ? { code } : {}),
    ...(message ? { message } : {}),
  }
}

function firstString(...values: unknown[]): string | undefined {
  for (const value of values) {
    if (typeof value === 'string' && value.trim().length > 0) return value
  }
  return undefined
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function maskKnownPatterns(text: string): string {
  let result = text
  result = result.replace(
    /\b([a-z][a-z0-9+.\-]*:\/\/)([^:\s/]+):([^@\s/]+)@/gi,
    (_match: string, scheme: string) => `${scheme}${REDACTED}@`,
  )
  result = result.replace(
    /\b([a-z][a-z0-9+.\-]*:\/\/)([^:\s/]{12,}):@/gi,
    (_match: string, scheme: string) => `${scheme}${REDACTED}@`,
  )
  result = result.replace(
    /\b([a-z][a-z0-9+.\-]*:\/\/)([A-Za-z0-9._\-]{12,})@/g,
    (_match: string, scheme: string) => `${scheme}${REDACTED}@`,
  )
  result = result.replace(/\bBearer\s+[^\s,;)}\]]+/gi, `Bearer ${REDACTED}`)
  result = result.replace(
    /\b(Authorization|X-Api-Key|X-Auth-Key|X-Auth-Token)\s*:\s*(Bearer|Basic)?\s*[^\s,;)}\]]+/gi,
    (_match: string, header: string, scheme: string | undefined) =>
      `${header}: ${scheme ? `${scheme} ` : ''}${REDACTED}`,
  )
  result = result.replace(/\bBasic\s+[A-Za-z0-9+/=]{8,}/gi, `Basic ${REDACTED}`)
  result = result.replace(
    /\b(gh[pousr])_[A-Za-z0-9]{20,}/g,
    (_match: string, prefix: string) => `${prefix}_${REDACTED}`,
  )
  result = result.replace(/\bsk-[A-Za-z0-9_\-]{16,}/g, `sk-${REDACTED}`)
  result = result.replace(
    /\b(access_token|refresh_token|enrollment_token|token)\s*=\s*[^\s,;)}\]]+/gi,
    (_match: string, name: string) => `${name}=${REDACTED}`,
  )
  return result
}
