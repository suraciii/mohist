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

export interface OpencodeServerHandle {
  readonly url: string
  readonly directory: string
  readonly client: OpencodeClient
  close(): Promise<void>
  terminateTree?(): Promise<void>
}

export type OpencodeServerFactory = (directory: string, signal: AbortSignal) => Promise<OpencodeServerHandle>

export function createOpenCodeFetch(dispatcher: Dispatcher, fetchImpl: typeof fetch = fetch): typeof fetch {
  return (input, init) => fetchImpl(input, { ...init, dispatcher } as RequestInit)
}

export const createSpawnedOpencodeServer: OpencodeServerFactory = async (directory, signal) => {
  const server = await createOpencodeServer({ signal, port: 0 })
  const dispatcher = new Agent({ headersTimeout: 0, bodyTimeout: 0 })
  let terminated = false
  const client = createOpencodeClient({
    baseUrl: server.url,
    directory,
    fetch: createOpenCodeFetch(dispatcher),
  })
  const terminateTree = async () => {
    if (terminated) return
    terminated = true
    server.close()
    await dispatcher.close()
  }
  return {
    url: server.url,
    directory,
    client,
    close: terminateTree,
    terminateTree,
  }
}
