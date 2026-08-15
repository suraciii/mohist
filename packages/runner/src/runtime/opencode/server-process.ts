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

import { createOpencodeClient, createOpencodeServer } from "@opencode-ai/sdk/v2"
import type { OpencodeClient } from "@opencode-ai/sdk/v2"
import { Agent } from "undici"
import type { Dispatcher } from "undici"
import { boundedTimeoutMs, boundedWait } from "../bounded-wait.js"

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

export async function terminateOpencodeTree(
  server: { close(): void | Promise<void>; readonly process?: OpencodeProcessControl; readonly pid?: number },
  dispatcher: Pick<Dispatcher, "close" | "destroy">,
  timeoutMs = DEFAULT_RUNTIME_SHUTDOWN_TIMEOUT_MS,
): Promise<void> {
  // The SDK's close() sends SIGTERM to the OpenCode process. Keep its wait
  // and the undici close in one bounded operation because either can be held
  // by an in-flight request.
  const completed = await boundedWait(
    () => Promise.allSettled([
      Promise.resolve().then(() => server.close()),
      Promise.resolve().then(() => dispatcher.close()),
    ]),
    boundedTimeoutMs(timeoutMs, DEFAULT_RUNTIME_SHUTDOWN_TIMEOUT_MS),
  )
  if (completed) return

  // Abandon graceful cleanup. Destroying the dispatcher prevents future
  // callers from reusing the dead generation; killing the process group is
  // best-effort because the pinned SDK does not expose its ChildProcess.
  try { dispatcher.destroy() } catch { /* best effort */ }
  forceKillProcessTree(server)
}

function forceKillProcessTree(server: { readonly process?: OpencodeProcessControl; readonly pid?: number }): void {
  const processControl = server.process
  const pid = processControl?.pid ?? server.pid
  if (pid !== undefined && Number.isInteger(pid) && pid > 0) {
    try { globalThis.process.kill(-pid, "SIGKILL") } catch {
      try { globalThis.process.kill(pid, "SIGKILL") } catch { /* best effort */ }
    }
    return
  }
  try { processControl?.kill?.("SIGKILL") } catch { /* best effort */ }
}
