import { describe, expect, it, vi } from "vitest"
import type { Dispatcher } from "undici"

const sdkMocks = vi.hoisted(() => ({
  createOpencodeServer: vi.fn(),
  createOpencodeClient: vi.fn(),
}))

vi.mock("@opencode-ai/sdk/v2", () => sdkMocks)

import { createOpenCodeFetch, createSpawnedOpencodeServer } from "../src/runtime/opencode/server-process.js"

describe("createOpenCodeFetch", () => {
  it("uses the dedicated dispatcher without changing the global fetch", async () => {
    const dispatcher = {} as Dispatcher
    const fetchImpl = vi.fn(async () => new Response(null, { status: 204 }))
    const openCodeFetch = createOpenCodeFetch(dispatcher, fetchImpl as unknown as typeof fetch)
    const request = new Request("http://opencode.local/session")

    await openCodeFetch(request)

    expect(fetchImpl).toHaveBeenCalledWith(request, { dispatcher })
  })

  it("starts the SDK server on an OS-assigned port", async () => {
    const close = vi.fn()
    sdkMocks.createOpencodeServer.mockResolvedValue({ url: "http://127.0.0.1:46237", close })
    sdkMocks.createOpencodeClient.mockReturnValue({})

    const handle = await createSpawnedOpencodeServer("/virtual/workspace", new AbortController().signal)

    expect(sdkMocks.createOpencodeServer).toHaveBeenCalledWith({
      signal: expect.any(AbortSignal),
      port: 0,
    })
    expect(handle.url).toBe("http://127.0.0.1:46237")
    await handle.close()
    expect(close).toHaveBeenCalledOnce()
  })
})
