import { describe, expect, it, vi } from "vitest"
import type { Dispatcher } from "undici"
import { createOpenCodeFetch } from "../src/runtime/opencode/server-process.js"

describe("createOpenCodeFetch", () => {
  it("uses the dedicated dispatcher without changing the global fetch", async () => {
    const dispatcher = {} as Dispatcher
    const fetchImpl = vi.fn(async () => new Response(null, { status: 204 }))
    const openCodeFetch = createOpenCodeFetch(dispatcher, fetchImpl as unknown as typeof fetch)
    const request = new Request("http://opencode.local/session")

    await openCodeFetch(request)

    expect(fetchImpl).toHaveBeenCalledWith(request, { dispatcher })
  })
})
