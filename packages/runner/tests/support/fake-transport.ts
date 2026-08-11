import { AsyncLocalStorage } from "node:async_hooks"
import { vi } from "vitest"
import { withTestRunnerResources } from "./test-resources.js"
import type { RunnerResourceContext } from "../../src/system/filesystem.js"

export class FakeTransport {
  readonly fetch = vi.fn(async (_input: RequestInfo | URL, _init?: RequestInit): Promise<Response> => {
    throw new Error("fake transport response was not configured")
  })
}

const transportStorage = new AsyncLocalStorage<FakeTransport>()

export function currentFakeTransport(): FakeTransport {
  const value = transportStorage.getStore()
  if (!value) throw new Error("fake transport context is not active")
  return value
}

export async function withFakeTransport<T>(
  body: (transport: FakeTransport) => Promise<T>,
  resources: Omit<RunnerResourceContext, "transport" | "fileSystem"> = {},
): Promise<T> {
  const transport = new FakeTransport()
  return await withTestRunnerResources(
    async () => await transportStorage.run(transport, async () => await body(transport)),
    { ...resources, transport: { fetch: (input, init) => transport.fetch(input, init) } },
  )
}

type TransportFetchMock = ReturnType<typeof vi.fn>

const transportFetchTarget = function(this: unknown, input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
  return Reflect.apply(currentFakeTransport().fetch, this, [input, init])
} as unknown as TransportFetchMock

Object.defineProperty(transportFetchTarget, "_isMockFunction", { value: true })

export const transportFetch = new Proxy(transportFetchTarget, {
  get(_target, property) {
    if (property === "_isMockFunction") return true
    const fetch = currentFakeTransport().fetch
    const value = Reflect.get(fetch, property, fetch)
    return typeof value === "function" ? value.bind(fetch) : value
  },
  set(_target, property, value) {
    return Reflect.set(currentFakeTransport().fetch, property, value, currentFakeTransport().fetch)
  },
}) as TransportFetchMock
