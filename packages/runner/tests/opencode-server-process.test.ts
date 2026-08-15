import { describe, expect, it as vitestIt, vi } from "vitest"
import type { Dispatcher } from "undici"

type SdkMock = ReturnType<typeof vi.fn>
type SdkMocks = Record<"createOpencodeServer" | "createOpencodeClient", SdkMock>

const sdkMocks = vi.hoisted(() => {
  type State = { readonly mocks: Record<"createOpencodeServer" | "createOpencodeClient", ReturnType<typeof vi.fn>> }
  const { AsyncLocalStorage } = process.getBuiltinModule("node:async_hooks") as typeof import("node:async_hooks")
  const storage = new AsyncLocalStorage<State>()
  const current = () => {
    const state = storage.getStore()
    if (!state) throw new Error("OpenCode SDK test context is not active")
    return state
  }
  const scoped = (name: "createOpencodeServer" | "createOpencodeClient") => {
    const target = (() => undefined) as (...args: unknown[]) => unknown
    Object.defineProperty(target, "_isMockFunction", { value: true })
    return new Proxy(target, {
      apply(_target, thisArg, args) {
        return Reflect.apply(current().mocks[name], thisArg, args)
      },
      get(_target, property) {
        const value = Reflect.get(current().mocks[name], property)
        return typeof value === "function" ? value.bind(current().mocks[name]) : value
      },
      set(_target, property, value) {
        return Reflect.set(current().mocks[name], property, value)
      },
    }) as unknown as ReturnType<typeof vi.fn>
  }
  return {
    storage,
    createOpencodeServer: scoped("createOpencodeServer"),
    createOpencodeClient: scoped("createOpencodeClient"),
  }
})

const sdkTestStorage = sdkMocks.storage

function createSdkMocks(): SdkMocks {
  return {
    createOpencodeServer: vi.fn(),
    createOpencodeClient: vi.fn(),
  }
}

vi.mock("@opencode-ai/sdk/v2", () => ({
  createOpencodeServer: sdkMocks.createOpencodeServer,
  createOpencodeClient: sdkMocks.createOpencodeClient,
}))

import { createOpenCodeFetch, createSpawnedOpencodeServer, terminateOpencodeTree } from "../src/runtime/opencode/server-process.js"

function it(name: string, body: () => Promise<void>): void {
  vitestIt(name, async () => await sdkTestStorage.run({ mocks: createSdkMocks() }, body))
}

describe("createOpenCodeFetch", () => {
  it("bounds a hanging dispatcher close and destroys it after the deadline", async () => {
    vi.useFakeTimers()
    try {
      const close = vi.fn(() => new Promise<void>(() => {}))
      const destroy = vi.fn()
      const serverClose = vi.fn()
      const pending = terminateOpencodeTree(
        { close: serverClose },
        { close, destroy } as unknown as Dispatcher,
        25,
      )
      await vi.advanceTimersByTimeAsync(24)
      expect(destroy).not.toHaveBeenCalled()
      await vi.advanceTimersByTimeAsync(1)
      await expect(pending).resolves.toBeUndefined()
      expect(serverClose).toHaveBeenCalledOnce()
      expect(close).toHaveBeenCalledOnce()
      expect(destroy).toHaveBeenCalledOnce()
    } finally {
      vi.useRealTimers()
    }
  })

  it("best-effort SIGKILLs a process tree when graceful close does not finish", async () => {
    vi.useFakeTimers()
    try {
      const kill = vi.fn()
      const pending = terminateOpencodeTree(
        { close: () => new Promise<void>(() => {}), process: { kill } },
        { close: () => new Promise<void>(() => {}), destroy: vi.fn() } as unknown as Dispatcher,
        25,
      )
      await vi.advanceTimersByTimeAsync(25)
      await expect(pending).resolves.toBeUndefined()
      expect(kill).toHaveBeenCalledWith("SIGKILL")
    } finally {
      vi.useRealTimers()
    }
  })

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
    sdkMocks.createOpencodeServer.mockResolvedValue({ url: "https://opencode.test", close })
    sdkMocks.createOpencodeClient.mockReturnValue({})

    const handle = await createSpawnedOpencodeServer("/virtual/workspace", new AbortController().signal)

    expect(sdkMocks.createOpencodeServer).toHaveBeenCalledWith({
      signal: expect.any(AbortSignal),
      port: 0,
    })
    expect(handle.url).toBe("https://opencode.test")
    await handle.close()
    expect(close).toHaveBeenCalledOnce()
  })
})
