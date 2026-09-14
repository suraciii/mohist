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
  subscribe(listener: (message: CodexJsonRpcMessage) => void): () => void
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
  const child = spawner(
    'codex',
    ['app-server', '--stdio'],
    {
      cwd: options.cwd,
      env,
      stdio: ['pipe', 'pipe', 'pipe'],
      detached: process.platform !== 'win32',
    },
  ) as ChildProcessWithoutNullStreams

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
  const listeners = new Set<(message: CodexJsonRpcMessage) => void>()
  const seenResponseIds = new Set<number | string>()
  let buffer = ''
  let exited = false
  let exitCause: { kind: 'child-exit'; code: number | null } | { kind: 'malformed-stdout' } | null = null
  let nextId = 1

  const child = input.child

  function rejectAllPending(cause: unknown) {
    for (const [id, handlers] of pending) {
      handlers.reject(cause)
      pending.delete(id)
    }
  }

  type Deliverable =
  | CodexJsonRpcMessage
  | { jsonrpc: '2.0'; method: string; params?: unknown; id?: number | string }

function deliver(message: Deliverable) {
  const m = message as {
    jsonrpc?: unknown
    id?: number | string
    method?: string
    result?: unknown
    error?: unknown
    params?: unknown
  }
  if (
    typeof m.id !== 'undefined' &&
    typeof m.method !== 'string' &&
    ('result' in m || 'error' in m)
  ) {
    const handlers = pending.get(m.id)
    if (handlers) {
      pending.delete(m.id)
      if (seenResponseIds.has(m.id)) {
        handlers.reject(new Error(`codex protocol failure: duplicate response id ${String(m.id)}`))
        return
      }
      seenResponseIds.add(m.id)
      if ('result' in m) handlers.resolve(m.result)
      else handlers.reject(m.error)
      return
    }
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
          exitCause = { kind: 'malformed-stdout' }
          rejectAllPending(new Error(`codex protocol failure: malformed JSON-RPC line: ${line.slice(0, 80)}`))
          for (const listener of listeners) {
            listener({ jsonrpc: '2.0', method: 'protocol-failure', params: { reason: 'malformed-stdout', line: line.slice(0, 256) } })
          }
          return
        }
        deliver(parsed as Deliverable)
      }
      newlineIndex = buffer.indexOf('\n')
    }
  })

  child.stderr.setEncoding('utf8')
  child.stderr.on('data', () => {
    /* stderr is diagnostic-only; we never surface its content */
  })

  child.on('exit', (code) => {
    exited = true
    exitCause = exitCause ?? { kind: 'child-exit', code }
    rejectAllPending(new Error(`codex app-server exited (code=${code ?? 'null'}) before response`))
  })

  function send<P, R>(request: { readonly method: string; readonly params?: P; readonly id: number }): Promise<R> {
    if (exited) {
      return Promise.reject(new Error(`codex app-server exited before request ${request.method} could be sent`))
    }
    if (!isCodexLockedMethod(request.method)) {
      return Promise.reject(
        new Error(
          `codex protocol failure: refusing to send method outside the locked v2 subset (${request.method}); locked methods: ${CODEX_LOCKED_METHODS.join(', ')}`,
        ),
      )
    }
    const id = request.id
    return new Promise<R>((resolve, reject) => {
      pending.set(id, { resolve: (value) => resolve(value as R), reject })
      const envelope = JSON.stringify({ jsonrpc: '2.0', id, method: request.method, params: request.params ?? {} })
      child.stdin.write(`${envelope}\n`, (error) => {
        if (error) {
          pending.delete(id)
          reject(error)
        }
      })
    })
  }

  function denyServerRequest(id: number | string, reason: string): void {
    if (exited) return
    const envelope = JSON.stringify({
      jsonrpc: '2.0',
      id,
      result: { ok: false, denied: true, reason },
    })
    child.stdin.write(`${envelope}\n`)
  }

  function subscribe(listener: (message: CodexJsonRpcMessage) => void): () => void {
    listeners.add(listener)
    return () => {
      listeners.delete(listener)
    }
  }

  async function close(): Promise<void> {
    if (exited) return
    child.stdin.end()
    try {
      child.kill('SIGTERM')
    } catch {
      /* best effort */
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
    denyServerRequest,
    subscribe,
    close,
    pid: child.pid,
  }
}

export function nextCodexRequestId(): number {
  return Math.floor(Math.random() * 0x7fffffff) + 1
}

export function assignCodexRequestId(previous: number): number {
  return (previous + 1) | 0
}