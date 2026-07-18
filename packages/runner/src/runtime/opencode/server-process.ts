/**
 * OpenCode Server process factory.
 *
 * Wraps the pinned `@opencode-ai/sdk/v2` `createOpencodeServer()` and
 * `createOpencodeClient()` factories. The runtime does NOT spawn the
 * OpenCode process directly, does NOT pass `--pure`, and does NOT clean
 * up a `.opencode` lockfile.
 *
 * The server lives on `127.0.0.1` on an OS-assigned port by default.
 * The client is constructed against that URL and the work directory
 * is passed per call (the SDK requires `directory` on the client).
 *
 * This module is only reachable from production code paths (the
 * default factory); tests inject a fake Server/Client pair via the
 * factory seam, so unit tests never trigger a real spawn.
 */

import { createOpencodeClient, createOpencodeServer } from "@opencode-ai/sdk/v2"
import type { OpencodeClient } from "@opencode-ai/sdk/v2"

export interface OpencodeServerHandle {
  readonly url: string
  readonly directory: string
  readonly client: OpencodeClient
  close(): Promise<void>
}

export type OpencodeServerFactory = (directory: string, signal: AbortSignal) => Promise<OpencodeServerHandle>

export const createSpawnedOpencodeServer: OpencodeServerFactory = async (directory, signal) => {
  const server = await createOpencodeServer({ signal })
  const client = createOpencodeClient({ baseUrl: server.url, directory })
  return {
    url: server.url,
    directory,
    client,
    async close() {
      server.close()
    },
  }
}
