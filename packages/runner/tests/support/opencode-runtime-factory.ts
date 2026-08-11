import { vi } from "vitest"
import type { OpenCodeRuntimeFactory } from "../../src/runtime/opencode/factory.js"
import { OpenCodeRuntime } from "../../src/runtime/opencode/runtime.js"
import type { OpenCodeRuntimeDeps } from "../../src/runtime/opencode/runtime.js"
import type { OpencodeServerHandle } from "../../src/runtime/opencode/server-process.js"
import type { RuntimeEventSubscription, RuntimeGlobalEvent } from "../../src/runtime/opencode/event-subscription.js"

/**
 * Fake subscription whose `emit` is callable from outside the runtime
 * so a test can simulate a `server.disconnected` / `server.heartbeat-
 * failed` event after `start()` has passed.
 */
export class FakeRuntimeSubscription implements RuntimeEventSubscription {
  private listeners = new Set<(event: RuntimeGlobalEvent) => void>()
  closed = false
  subscribe(listener: (event: RuntimeGlobalEvent) => void): () => void {
    if (this.closed) return () => {}
    this.listeners.add(listener)
    return () => {
      this.listeners.delete(listener)
    }
  }
  emit(event: RuntimeGlobalEvent): void {
    for (const listener of [...this.listeners]) {
      listener(event)
    }
  }
  async close(): Promise<void> {
    this.closed = true
    this.listeners.clear()
  }
}

export interface FakeRuntimeHandles {
  subscription: FakeRuntimeSubscription
  client: {
    health: ReturnType<typeof vi.fn>
  }
  server: OpencodeServerHandle
  lastRuntime: OpenCodeRuntime | null
  runtimeCreated: Promise<OpenCodeRuntime>
}

export interface InstallFakeRuntimeArgs {
  rebuildDelayMs?: number
  failStart?: boolean
  failHealth?: boolean
}

export interface OpenCodeRuntimeTestResources {
  openCodeRuntimeFactory?: OpenCodeRuntimeFactory
}

/**
 * Install a fake OpenCode runtime factory. The factory returns a real
 * `OpenCodeRuntime` instance wired with stubbed Server and Subscription
 * factories — no real process, network, filesystem, or wall-clock is touched.
 *
 * `start()` is NOT called here (the factory is fire-and-forget about
 * priming); the host's `initializeSharedConnection` awaits
 * `runtime.start(signal)` itself and the runtime's idempotent
 * `start()` resolves with `ready: true` when health passes.
 */
export function installFakeOpenCodeRuntimeFactory(resources: OpenCodeRuntimeTestResources, args: InstallFakeRuntimeArgs = {}): FakeRuntimeHandles {
  const subscription = new FakeRuntimeSubscription()
  const closed = { value: false }
  const health = vi.fn(async () => ({ data: { ok: true } }))
  if (args.failHealth) health.mockRejectedValueOnce(new Error("health boom"))
  const clientProxy = {
    global: { health },
    session: { create: vi.fn(async (params: { directory?: string }) => ({
      data: { id: `ses_${(params.directory ?? "default").replace(/[^a-z0-9]+/gi, "_")}` },
    })) },
  }
  const server: OpencodeServerHandle = {
    url: "http://fake",
    directory: "/tmp/work",
    client: clientProxy as unknown as OpencodeServerHandle["client"],
    async close() {
      closed.value = true
    },
  }
  let resolveRuntime!: (runtime: OpenCodeRuntime) => void
  const handles: FakeRuntimeHandles = {
    subscription,
    client: { health },
    server,
    lastRuntime: null,
    runtimeCreated: new Promise(resolve => {
      resolveRuntime = resolve
    }),
  }
  resources.openCodeRuntimeFactory = (deps: OpenCodeRuntimeDeps) => {
    const runtime = new OpenCodeRuntime({
      ...deps,
      serverFactory: async () => {
        if (args.failStart) throw new Error("spawn failed")
        return server
      },
      eventSubscriptionFactory: () => subscription,
      ...(args.rebuildDelayMs !== undefined ? { rebuildDelayMs: args.rebuildDelayMs } : {}),
    })
    handles.lastRuntime = runtime
    resolveRuntime(runtime)
    return runtime
  }
  return handles
}

export function installReadyOpenCodeRuntimeFactory(resources: OpenCodeRuntimeTestResources): FakeRuntimeHandles {
  return installFakeOpenCodeRuntimeFactory(resources)
}
