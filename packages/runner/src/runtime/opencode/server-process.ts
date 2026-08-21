/**
 * OpenCode Server process factory.
 *
 * Wraps the pinned `@opencode-ai/sdk/v2` `createOpencodeServer()` and
 * `createOpencodeClient()` factories. The runtime does NOT spawn the
 * OpenCode process directly, does NOT pass `--pure`, and does NOT clean
 * up a `.opencode` lockfile.
 *
 * The server lives on `127.0.0.1` on an OS-assigned port.
 * The client is constructed against that URL and the work directory
 * is passed per call (the SDK requires `directory` on the client).
 *
 * This module is only reachable from production code paths (the
 * default factory); tests inject a fake Server/Client pair via the
 * factory seam, so unit tests never trigger a real spawn.
 */

import { createOpencodeClient, createOpencodeServer } from '@opencode-ai/sdk/v2'
import { spawn, type ChildProcessWithoutNullStreams } from 'node:child_process'
import type { OpencodeClient } from '@opencode-ai/sdk/v2'
import { Agent } from 'undici'
import type { Dispatcher } from 'undici'
import { boundedTimeoutMs, boundedWait } from '../bounded-wait.js'

export const DEFAULT_RUNTIME_SHUTDOWN_TIMEOUT_MS = 30_000

/** Optional process controls used by alternate server handles and tests. */
export interface OpencodeProcessControl {
  readonly pid?: number
  kill?(signal: NodeJS.Signals): void
}

export interface OpencodeServerHandle {
  readonly url: string
  readonly directory: string
  readonly client: OpencodeClient
  /** Present when the factory can expose the spawned server process. */
  readonly process?: OpencodeProcessControl
  readonly pid?: number
  close(): Promise<void>
  terminateTree?(): Promise<void>
}

export interface OpencodeServerFactoryOptions {
  readonly shutdownTimeoutMs?: number
  /** Non-secret process boundary values for an isolated Manager server. */
  readonly environment?: NodeJS.ProcessEnv
}

export type OpencodeServerFactory = (
  directory: string,
  signal: AbortSignal,
  options?: OpencodeServerFactoryOptions,
) => Promise<OpencodeServerHandle>

export function createOpenCodeFetch(dispatcher: Dispatcher, fetchImpl: typeof fetch = fetch): typeof fetch {
  return (input, init) => fetchImpl(input, { ...init, dispatcher } as RequestInit)
}

export const createSpawnedOpencodeServer: OpencodeServerFactory = async (directory, signal, options) => {
  const server = await createOpencodeServer({ signal, port: 0 })
  const dispatcher = new Agent({ headersTimeout: 0, bodyTimeout: 0 })
  const shutdownTimeoutMs = boundedTimeoutMs(options?.shutdownTimeoutMs, DEFAULT_RUNTIME_SHUTDOWN_TIMEOUT_MS)
  let terminationPromise: Promise<void> | null = null
  const client = createOpencodeClient({
    baseUrl: server.url,
    directory,
    fetch: createOpenCodeFetch(dispatcher),
  })
  const terminateTree = async (): Promise<void> => {
    if (terminationPromise) return await terminationPromise
    terminationPromise = terminateOpencodeTree(server, dispatcher, shutdownTimeoutMs)
    return await terminationPromise
  }
  return {
    url: server.url,
    directory,
    client,
    close: terminateTree,
    terminateTree,
  }
}

/**
 * Process adapter for Manager executions. The pinned SDK does not expose an
 * environment option, so Manager work uses an owned `opencode serve` child
 * and a client bound to that child. The environment contains only the scoped
 * broker locator and launcher path, never either lease value.
 */
export const createIsolatedOpencodeServer: OpencodeServerFactory = async (directory, signal, options) => {
  const child = spawn('opencode', ['serve', '--hostname=127.0.0.1', '--port=0'], {
    cwd: directory,
    env: { ...process.env, ...(options?.environment ?? {}) },
    stdio: ['ignore', 'pipe', 'pipe'],
    detached: process.platform !== 'win32',
  }) as unknown as ChildProcessWithoutNullStreams
  const output: string[] = []
  const timeoutMs = boundedTimeoutMs(options?.shutdownTimeoutMs, 5_000)
  const url = await new Promise<string>((resolve, reject) => {
    let settled = false
    const timer = setTimeout(
      () => finish(new Error(`Timed out waiting for isolated OpenCode server after ${timeoutMs}ms`)),
      timeoutMs,
    )
    timer.unref?.()
    const onData = (chunk: Buffer) => {
      output.push(chunk.toString())
      const text = output.join('')
      const match = text.match(/opencode server listening\s+on\s+(https?:\/\/[^\s]+)/)
      if (match) finish(null, match[1])
    }
    const finish = (error: Error | null, value?: string) => {
      if (settled) return
      settled = true
      clearTimeout(timer)
      if (error) {
        killChildTree(child)
        reject(new Error(`${error.message}${output.length > 0 ? `\nServer output: ${output.join('')}` : ''}`))
      } else {
        resolve(value!)
      }
    }
    child.stdout.on('data', onData)
    child.stderr.on('data', onData)
    child.once('error', (error) => finish(error))
    child.once('exit', (code) => finish(new Error(`Isolated OpenCode server exited with code ${code ?? 'unknown'}`)))
    signal.addEventListener(
      'abort',
      () => finish(signal.reason instanceof Error ? signal.reason : new Error('OpenCode server start aborted')),
      { once: true },
    )
  })
  const dispatcher = new Agent({ headersTimeout: 0, bodyTimeout: 0 })
  const client = createOpencodeClient({
    baseUrl: url,
    directory,
    fetch: createOpenCodeFetch(dispatcher),
  })
  const processControl: OpencodeProcessControl = {
    pid: child.pid,
    kill: (sig) => child.kill(sig),
  }
  const close = async () => {
    try {
      child.kill('SIGTERM')
    } catch {
      /* already exited */
    }
    await dispatcher.close().catch(() => undefined)
  }
  return {
    url,
    directory,
    client,
    process: processControl,
    pid: child.pid,
    close,
    terminateTree: async () => {
      await boundedWait(() => close(), timeoutMs)
      killChildTree(child)
      dispatcher.destroy()
    },
  }
}

function killChildTree(child: { pid?: number; kill(signal?: NodeJS.Signals): boolean }): void {
  if (child.pid && process.platform !== 'win32') {
    try {
      process.kill(-child.pid, 'SIGKILL')
      return
    } catch {
      /* fall through */
    }
  }
  try {
    child.kill('SIGKILL')
  } catch {
    /* already exited */
  }
}

export async function terminateOpencodeTree(
  server: { close(): void | Promise<void>; readonly process?: OpencodeProcessControl; readonly pid?: number },
  dispatcher: Pick<Dispatcher, 'close' | 'destroy'>,
  timeoutMs = DEFAULT_RUNTIME_SHUTDOWN_TIMEOUT_MS,
): Promise<void> {
  // The SDK's close() sends SIGTERM to the OpenCode process. Keep its wait
  // and the undici close in one bounded operation because either can be held
  // by an in-flight request.
  const completed = await boundedWait(
    () =>
      Promise.allSettled([
        Promise.resolve().then(() => server.close()),
        Promise.resolve().then(() => dispatcher.close()),
      ]),
    boundedTimeoutMs(timeoutMs, DEFAULT_RUNTIME_SHUTDOWN_TIMEOUT_MS),
  )
  if (completed) return

  // Abandon graceful cleanup. Destroying the dispatcher prevents future
  // callers from reusing the dead generation; killing the process group is
  // best-effort because the pinned SDK does not expose its ChildProcess.
  try {
    dispatcher.destroy()
  } catch {
    /* best effort */
  }
  forceKillProcessTree(server)
}

function forceKillProcessTree(server: { readonly process?: OpencodeProcessControl; readonly pid?: number }): void {
  const processControl = server.process
  const pid = processControl?.pid ?? server.pid
  if (pid !== undefined && Number.isInteger(pid) && pid > 0) {
    try {
      globalThis.process.kill(-pid, 'SIGKILL')
    } catch {
      try {
        globalThis.process.kill(pid, 'SIGKILL')
      } catch {
        /* best effort */
      }
    }
    return
  }
  try {
    processControl?.kill?.('SIGKILL')
  } catch {
    /* best effort */
  }
}
