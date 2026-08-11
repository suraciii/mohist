import { describe, expect, it, vi } from "vitest"
import type { OpencodeClient } from "@opencode-ai/sdk/v2"
import { OpenCodeRuntime, type RuntimeClock, type OpenCodeRuntimeDeps } from "../src/runtime/opencode/index.js"
import type { OpencodeServerHandle } from "../src/runtime/opencode/server-process.js"
import type { RuntimeEventSubscription, RuntimeGlobalEvent } from "../src/runtime/opencode/event-subscription.js"

class FakeClock implements RuntimeClock {
  private current = 0
  private nextId = 1
  private readonly timers = new Map<number, { due: number; callback: () => void }>()

  now = () => this.current

  setTimeout = (callback: () => void, delayMs: number): number => {
    const id = this.nextId++
    this.timers.set(id, { due: this.current + delayMs, callback })
    return id
  }

  clearTimeout = (handle: unknown): void => {
    this.timers.delete(handle as number)
  }

  async advance(milliseconds: number): Promise<void> {
    this.current += milliseconds
    while (true) {
      const due = [...this.timers.entries()]
        .filter(([, timer]) => timer.due <= this.current)
        .sort(([, left], [, right]) => left.due - right.due)
      const next = due[0]
      if (!next) return
      this.timers.delete(next[0])
      next[1].callback()
      await Promise.resolve()
    }
  }
}

class FakeEvents implements RuntimeEventSubscription {
  subscribe(_listener: (event: RuntimeGlobalEvent) => void): () => void {
    return () => {}
  }

  async close(): Promise<void> {}
}

interface ServerFixture {
  server: OpencodeServerHandle
  terminateTree: ReturnType<typeof vi.fn>
  close: ReturnType<typeof vi.fn>
  createSession: ReturnType<typeof vi.fn>
}

function serverFixture(): ServerFixture {
  const terminateTree = vi.fn(async () => {})
  const close = vi.fn(async () => {})
  const createSession = vi.fn(async () => ({ data: { id: "ses_created" } }))
  const client = {
    global: { health: vi.fn(async () => ({ data: { ok: true } })) },
    session: { create: createSession },
  } as unknown as OpencodeClient
  const server: OpencodeServerHandle = {
    url: "http://fake",
    directory: "/virtual/runner",
    client,
    close,
    terminateTree,
  }
  return { server, terminateTree, close, createSession }
}

function buildRuntime(clock: FakeClock, idleGraceMs = 100) {
  const servers: ServerFixture[] = []
  const deps: OpenCodeRuntimeDeps = {
    directory: "/virtual/runner",
    idleGraceMs,
    clock,
    serverFactory: async () => {
      const fixture = serverFixture()
      servers.push(fixture)
      return fixture.server
    },
    eventSubscriptionFactory: () => new FakeEvents(),
  }
  return { runtime: new OpenCodeRuntime(deps), servers }
}

describe("OpenCodeRuntime ownership lifecycle", () => {
  it("terminates the complete server tree after the grace period without an owner", async () => {
    const clock = new FakeClock()
    const { runtime, servers } = buildRuntime(clock)

    await runtime.start()
    expect(runtime.ownership()).toMatchObject({ ownerIds: [], idleSince: 0, activeOperations: 0, generation: 1 })

    await clock.advance(99)
    expect(servers[0]?.terminateTree).not.toHaveBeenCalled()

    await clock.advance(1)
    expect(servers[0]?.terminateTree).toHaveBeenCalledOnce()
    expect(servers[0]?.close).not.toHaveBeenCalled()
    expect(runtime.ready()).toBe(false)
    expect(runtime.ownership()).toEqual({ ownerIds: [], idleSince: null, activeOperations: 0, generation: null })
  })

  it("cancels a pending reclaim when work is reused during idle grace", async () => {
    const clock = new FakeClock()
    const { runtime, servers } = buildRuntime(clock)

    await runtime.start()
    runtime.setWorkOwners(["workflow:wr-1:work-1"])
    await clock.advance(500)
    expect(servers[0]?.terminateTree).not.toHaveBeenCalled()

    runtime.setWorkOwners([])
    expect(runtime.ownership().idleSince).toBe(500)
    await clock.advance(99)
    runtime.setWorkOwners(["workflow:wr-1:work-1"])
    await clock.advance(1)
    expect(servers[0]?.terminateTree).not.toHaveBeenCalled()
    expect(runtime.ownership().ownerIds).toEqual(["workflow:wr-1:work-1"])

    runtime.setWorkOwners([])
    await clock.advance(100)
    expect(servers[0]?.terminateTree).toHaveBeenCalledOnce()
  })

  it("keeps the tree alive while a runtime operation is in progress", async () => {
    const clock = new FakeClock()
    const { runtime, servers } = buildRuntime(clock)
    const release = {} as { resolve?: () => void }
    const pending = new Promise<void>((resolve) => { release.resolve = resolve })

    const started = await runtime.start()
    expect(started.ok).toBe(true)
    const activeServer = servers[0]!
    activeServer.createSession.mockImplementationOnce(async () => {
      await pending
      return { data: { id: "ses_created" } }
    })
    const operation = runtime.createSession({ target: { runtime: "opencode", runtimeSessionId: null, workDir: "/virtual/work" } })

    expect(runtime.ownership().activeOperations).toBe(1)
    await clock.advance(100)
    expect(activeServer.terminateTree).not.toHaveBeenCalled()

    release.resolve!()
    await operation
    expect(runtime.ownership().activeOperations).toBe(0)
    await clock.advance(100)
    expect(activeServer.terminateTree).toHaveBeenCalledOnce()
  })

  it("recreates a cold runtime when a new owner arrives", async () => {
    const clock = new FakeClock()
    const { runtime, servers } = buildRuntime(clock)

    await runtime.start()
    await clock.advance(100)
    expect(runtime.ready()).toBe(false)

    runtime.setWorkOwners(["agent-job:job-1:work-1"])
    const started = await runtime.start()
    expect(started.ok).toBe(true)
    expect(servers).toHaveLength(2)
    expect(runtime.ownership().ownerIds).toEqual(["agent-job:job-1:work-1"])
    expect(servers[0]?.terminateTree).toHaveBeenCalledOnce()
    expect(servers[1]?.terminateTree).not.toHaveBeenCalled()
  })
})
