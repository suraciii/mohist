/**
 * Codex app-server process seam.
 *
 * Owns:
 *   - spawning `codex app-server --stdio` with no shell, with the
 *     managed `CODEX_HOME` and bounded startup / shutdown budgets;
 *   - the line-framed JSON-RPC v2 consumer (one message per line on
 *     stdout, stderr diagnostic-only);
 *   - unique response id tracking (duplicate ids are a protocol
 *     failure boundary);
 *   - server-initiated approval / permission / user-input rejection
 *     (the protocol-defined denial response, never auto-approve);
 *   - child exit and malformed-stdout → protocol-failure boundary.
 *
 * Production code calls `getCodexRuntimeServerFactory()` to obtain a
 * `CodexServerFactory`; tests inject a fake factory via the resource
 * context so unit tests never spawn a real process.
 *
 * The factory returns a `CodexServerHandle` whose consumer speaks the
 * locked v2 protocol subset declared in `./protocol-types.ts`. SDK
 * access (if any) is contained inside this module.
 */

import { spawn, type ChildProcessWithoutNullStreams } from 'node:child_process'
import { CODEX_LOCKED_METHODS, isCodexLockedMethod, type CodexJsonRpcMessage } from './protocol-types.js'
import { boundedTimeoutMs, boundedWait } from '../bounded-wait.js'
import { redactCodexCredentialString } from './credential.js'
import type { CodexClock } from './types.js'
import { defaultClock } from './runtime-clock.js'

export const DEFAULT_CODEX_STARTUP_TIMEOUT_MS = 10_000
export const DEFAULT_CODEX_SHUTDOWN_TIMEOUT_MS = 5_000

export interface CodexServerHandle {
  /**
   * The resolved managed `CODEX_HOME` the server was launched with.
   * The runtime asserts equality on the `initialize` response before
   * admitting any further request.
   */
  readonly codexHome: string
  /**
   * Send a single JSON-RPC v2 envelope and await the response. Throws
   * on protocol failure (malformed stdout, duplicate id, child exit
   * during the request).
   */
  send<P, R>(request: { readonly method: string; readonly params?: P; readonly id: number }): Promise<R>
  /**
   * Send a JSON-RPC v2 notification (no id, no awaited response).
   * Used by the initialization handshake for the `initialized`
   * notification. Notification failures are surfaced synchronously
   * via the returned boolean so callers can decide whether the
   * failure blocks readiness.
   */
  notify<P>(notification: { readonly method: string; readonly params?: P }): boolean
  /**
   * Push a server-initiated denial response for an approval /
   * permission / user-input / MCP elicitation / dynamic-tool request.
   * The runtime never auto-approves; this is the only path that
   * answers server-initiated requests.
   */
  denyServerRequest(id: number | string, reason: string): void
  /**
   * Subscribe to JSON-RPC notifications, server-initiated requests,
   * and unmatched responses (anything that is not the awaited reply).
   */
  subscribe(listener: (message: unknown) => void): () => void
  /** True after the child has emitted its exit event. */
  hasExited?: () => boolean
  /**
   * Shut the app-server child down within the configured deadline.
   */
  close(): Promise<void>
  readonly pid?: number
}

export interface CodexServerFactoryOptions {
  readonly codexHome: string
  readonly cwd: string
  readonly startupTimeoutMs?: number
  readonly shutdownTimeoutMs?: number
  readonly clock?: CodexClock
  readonly environment?: NodeJS.ProcessEnv
  readonly spawner?: (
    command: string,
    args: string[],
    options: Parameters<typeof spawn>[2],
  ) => ChildProcessWithoutNullStreams
}

export type CodexServerFactory = (options: CodexServerFactoryOptions) => Promise<CodexServerHandle>

/**
 * Default factory body. Spawns `codex app-server --stdio` with the
 * managed `CODEX_HOME`, attaches the line-framed JSON-RPC consumer,
 * and wires the bounded shutdown. Production code calls this through
 * the factory seam; tests inject a fake.
 */
export function createSpawnedCodexServer(options: CodexServerFactoryOptions): Promise<CodexServerHandle> {
  const spawner = options.spawner ?? spawn
  const clock = options.clock ?? defaultClock
  const startupTimeoutMs = boundedTimeoutMs(options.startupTimeoutMs, DEFAULT_CODEX_STARTUP_TIMEOUT_MS)
  const shutdownTimeoutMs = boundedTimeoutMs(options.shutdownTimeoutMs, DEFAULT_CODEX_SHUTDOWN_TIMEOUT_MS)

  const env: NodeJS.ProcessEnv = { ...(options.environment ?? process.env), CODEX_HOME: options.codexHome }
  const child = spawner('codex', ['app-server', '--stdio'], {
    cwd: options.cwd,
    env,
    stdio: ['pipe', 'pipe', 'pipe'],
    shell: false,
    detached: process.platform !== 'win32',
  }) as ChildProcessWithoutNullStreams

  return buildCodexServerHandle({
    child,
    codexHome: options.codexHome,
    shutdownTimeoutMs,
    clock,
  })
}

interface CodexServerHandleBuilder {
  readonly child: ChildProcessWithoutNullStreams
  readonly codexHome: string
  readonly shutdownTimeoutMs: number
  readonly clock: CodexClock
}

async function buildCodexServerHandle(input: CodexServerHandleBuilder): Promise<CodexServerHandle> {
  const pending = new Map<number | string, { resolve: (value: unknown) => void; reject: (cause: unknown) => void }>()
  const listeners = new Set<(message: unknown) => void>()
  const seenResponseIds = new Set<number | string>()
  let buffer = ''
  let exited = false
  let closed = false
  let protocolError: Error | null = null
  let failureNotified = false

  const child = input.child

  function rejectAllPending(cause: unknown) {
    for (const [id, handlers] of pending) {
      pending.delete(id)
      handlers.reject(cause)
    }
  }

  function notifyProtocolFailure(reason: string, message: string): Error {
    const error = protocolError ?? new Error(`codex protocol failure: ${message}`)
    protocolError = error
    rejectAllPending(error)
    if (!failureNotified && !closed) {
      failureNotified = true
      const payload = {
        jsonrpc: '2.0' as const,
        method: 'protocol-failure',
        params: {
          reason,
          message: redactCodexCredentialString(message).slice(0, 256),
        },
      }
      for (const listener of listeners) listener(payload)
    }
    return error
  }

  type Deliverable = CodexJsonRpcMessage | { jsonrpc: '2.0'; method: string; params?: unknown; id?: number | string }

  function deliver(message: Deliverable) {
    const m = message as {
      jsonrpc?: unknown
      id?: number | string | null
      method?: string
      result?: unknown
      error?: unknown
      params?: unknown
    }
    const hasId = typeof m.id !== 'undefined'
    const isResponse = hasId && typeof m.method !== 'string' && ('result' in m || 'error' in m)
    if (isResponse) {
      if (seenResponseIds.has(m.id!)) {
        notifyProtocolFailure('duplicate-response-id', `duplicate response id ${String(m.id)}`)
        return
      }
      const handlers = pending.get(m.id!)
      if (!handlers) {
        notifyProtocolFailure('unexpected-response-id', `response for unknown request id ${String(m.id)}`)
        return
      }
      pending.delete(m.id!)
      seenResponseIds.add(m.id!)
      if ('result' in m && 'error' in m) {
        notifyProtocolFailure('invalid-response', `response id ${String(m.id)} contained both result and error`)
        return
      }
      if ('result' in m) handlers.resolve(m.result)
      else handlers.reject(m.error)
      return
    }
    for (const listener of listeners) listener(message as CodexJsonRpcMessage)
  }

  child.stdout.setEncoding('utf8')
  child.stdout.on('data', (chunk: string) => {
    buffer += chunk
    let newlineIndex = buffer.indexOf('\n')
    while (newlineIndex !== -1) {
      const line = buffer.slice(0, newlineIndex).trim()
      buffer = buffer.slice(newlineIndex + 1)
      if (line.length > 0) {
        let parsed: unknown
        try {
          parsed = JSON.parse(line)
        } catch {
          notifyProtocolFailure(
            'malformed-stdout',
            `malformed JSON-RPC line: ${redactCodexCredentialString(line).slice(0, 80)}`,
          )
          return
        }
        if (!isJsonRpcEnvelope(parsed)) {
          notifyProtocolFailure('invalid-stdout-envelope', 'stdout line was not a JSON-RPC 2.0 envelope')
          return
        }
        deliver(parsed as Deliverable)
        if (protocolError) return
      }
      newlineIndex = buffer.indexOf('\n')
    }
  })

  child.stderr.setEncoding('utf8')
  child.stderr.on('data', () => {
    /* stderr is diagnostic-only; it is never parsed as JSON-RPC or forwarded. */
  })

  child.on('error', (cause) => {
    if (closed) return
    notifyProtocolFailure('child-error', `codex app-server process error: ${errorMessage(cause)}`)
  })

  child.on('exit', (code) => {
    exited = true
    const message = `codex app-server exited (code=${code ?? 'null'}) before response`
    if (!closed) notifyProtocolFailure('child-exit', message)
    else rejectAllPending(new Error(message))
  })

  function send<P, R>(request: { readonly method: string; readonly params?: P; readonly id: number }): Promise<R> {
    if (closed || exited || protocolError) {
      return Promise.reject(
        protocolError ?? new Error(`codex app-server exited before request ${request.method} could be sent`),
      )
    }
    if (!isCodexLockedMethod(request.method)) {
      return Promise.reject(
        new Error(
          `codex protocol failure: refusing to send method outside the locked v2 subset (${request.method}); locked methods: ${CODEX_LOCKED_METHODS.join(', ')}`,
        ),
      )
    }
    const id = request.id
    if (!Number.isSafeInteger(id) || id <= 0) {
      return Promise.reject(
        notifyProtocolFailure('invalid-request-id', `request id ${String(id)} is not a positive integer`),
      )
    }
    if (pending.has(id) || seenResponseIds.has(id)) {
      return Promise.reject(
        notifyProtocolFailure('duplicate-request-id', `duplicate response id ${String(id)}: request id was reused`),
      )
    }
    return new Promise<R>((resolve, reject) => {
      pending.set(id, { resolve: (value) => resolve(value as R), reject })
      // Codex app-server uses JSON-RPC semantics but omits the jsonrpc header on stdio.
      const envelope = JSON.stringify({ id, method: request.method, params: request.params ?? {} })
      try {
        child.stdin.write(`${envelope}\n`, (error) => {
          if (error) {
            pending.delete(id)
            reject(
              notifyProtocolFailure('stdin-write', `codex app-server request write failed: ${errorMessage(error)}`),
            )
          }
        })
      } catch (cause) {
        pending.delete(id)
        reject(notifyProtocolFailure('stdin-write', `codex app-server request write failed: ${errorMessage(cause)}`))
      }
    })
  }

  function denyServerRequest(id: number | string, reason: string): void {
    if (closed || exited || protocolError) return
    const envelope = JSON.stringify({
      id,
      result: { ok: false, denied: true, reason: redactCodexCredentialString(reason) },
    })
    try {
      child.stdin.write(`${envelope}\n`)
    } catch (cause) {
      notifyProtocolFailure('stdin-write', `codex app-server denial write failed: ${errorMessage(cause)}`)
    }
  }

  function notify<P>(notification: { readonly method: string; readonly params?: P }): boolean {
    if (closed || exited || protocolError) return false
    if (!isCodexLockedMethod(notification.method)) return false
    try {
      const envelope = JSON.stringify({ method: notification.method, params: notification.params ?? {} })
      const accepted = child.stdin.write(`${envelope}\n`, (error) => {
        if (error)
          notifyProtocolFailure('stdin-write', `codex app-server notification write failed: ${errorMessage(error)}`)
      })
      return accepted
    } catch (cause) {
      notifyProtocolFailure('stdin-write', `codex app-server notification write failed: ${errorMessage(cause)}`)
      return false
    }
  }

  function subscribe(listener: (message: unknown) => void): () => void {
    listeners.add(listener)
    return () => {
      listeners.delete(listener)
    }
  }

  async function close(): Promise<void> {
    if (closed) return
    closed = true
    rejectAllPending(new Error('codex app-server handle closed'))
    try {
      child.stdin.end()
    } catch {
      /* best effort */
    }
    if (!exited) {
      try {
        child.kill('SIGTERM')
      } catch {
        /* best effort */
      }
    }
    await boundedWait(
      () =>
        new Promise<void>((resolve) => {
          if (exited) return resolve()
          child.once('exit', () => resolve())
        }),
      input.shutdownTimeoutMs,
    )
    if (!exited) {
      try {
        child.kill('SIGKILL')
      } catch {
        /* best effort */
      }
    }
  }

  return {
    codexHome: input.codexHome,
    send,
    notify,
    denyServerRequest,
    subscribe,
    hasExited: () => exited,
    close,
    pid: child.pid,
  }
}

function isJsonRpcEnvelope(value: unknown): value is CodexJsonRpcMessage {
  if (!value || typeof value !== 'object') return false
  const candidate = value as {
    jsonrpc?: unknown
    id?: unknown
    method?: unknown
    result?: unknown
    error?: unknown
  }
  if (candidate.jsonrpc !== undefined && candidate.jsonrpc !== '2.0') return false
  if (candidate.method !== undefined) {
    if (typeof candidate.method !== 'string') return false
    if (candidate.id !== undefined && typeof candidate.id !== 'string' && typeof candidate.id !== 'number') return false
    return true
  }
  if (typeof candidate.id !== 'string' && typeof candidate.id !== 'number') return false
  return 'result' in candidate !== 'error' in candidate
}

function errorMessage(cause: unknown): string {
  if (cause instanceof Error) return cause.message || 'unknown process failure'
  if (typeof cause === 'string') return cause
  return 'unknown process failure'
}

export function nextCodexRequestId(): number {
  return Math.floor(Math.random() * 0x7fffffff) + 1
}

export function assignCodexRequestId(previous: number): number {
  return (previous + 1) | 0
}
