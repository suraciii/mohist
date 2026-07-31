import { describe, expect, it, vi } from "vitest"
import type { OpencodeClient } from "@opencode-ai/sdk/v2"
import type { RuntimeEventSubscription, RuntimeGlobalEvent } from "../src/runtime/opencode/event-subscription.js"
import { OpenCodeRuntime } from "../src/runtime/opencode/index.js"
import { OpenCodeDirectoryInstances } from "../src/runtime/opencode/directory-instance.js"
import type { OpencodeServerHandle } from "../src/runtime/opencode/server-process.js"

function deferred<T>() {
  let resolve!: (value: T) => void
  let reject!: (reason?: unknown) => void
  const promise = new Promise<T>((resolvePromise, rejectPromise) => {
    resolve = resolvePromise
    reject = rejectPromise
  })
  return { promise, resolve, reject }
}

function buildDirectoryClient(status: unknown = {}, disposeData: unknown = true) {
  const sessionStatus = vi.fn(async () => ({ data: status }))
  const instanceDispose = vi.fn(async () => ({ data: disposeData }))
  const client = {
    session: { status: sessionStatus },
    instance: { dispose: instanceDispose },
  } as unknown as OpencodeClient
  return { client, sessionStatus, instanceDispose }
}

async function used(boundary: OpenCodeDirectoryInstances, directory: string): Promise<void> {
  await boundary.withOperation(directory, async (lease) => {
    lease.markUsed()
  })
}

describe("OpenCodeDirectoryInstances", () => {
  it("does not call status or dispose for an untracked directory", async () => {
    const fake = buildDirectoryClient()
    const boundary = new OpenCodeDirectoryInstances(() => fake.client)

    await expect(boundary.release("/virtual/untracked")).resolves.toMatchObject({ outcome: "untracked" })
    expect(fake.sessionStatus).not.toHaveBeenCalled()
    expect(fake.instanceDispose).not.toHaveBeenCalled()
  })

  it("defers disposal while a local operation is admitted", async () => {
    const fake = buildDirectoryClient()
    const boundary = new OpenCodeDirectoryInstances(() => fake.client)
    const gate = deferred<void>()
    const operation = boundary.withOperation("/virtual/project", async (lease) => {
      lease.markUsed()
      await gate.promise
    })

    const blocked = await boundary.release("/virtual/project")
    expect(blocked.outcome).toBe("busy")
    expect(fake.sessionStatus).not.toHaveBeenCalled()

    gate.resolve()
    await operation
    expect((await boundary.release("/virtual/project")).outcome).toBe("disposed")
  })

  it("keeps a directory busy until a tracked operation settles", async () => {
    const fake = buildDirectoryClient()
    const reply = deferred<void>()
    let trackedFinished!: Promise<void>
    const boundary = new OpenCodeDirectoryInstances(() => fake.client)

    await boundary.withOperation("/virtual/project", async (lease) => {
      lease.markUsed()
      trackedFinished = lease.trackPending(reply.promise)
    })

    expect((await boundary.release("/virtual/project")).outcome).toBe("busy")
    reply.resolve()
    await trackedFinished
    expect((await boundary.release("/virtual/project")).outcome).toBe("disposed")
  })

  it("disposes an empty or all-idle status map once and forgets the directory", async () => {
    const fake = buildDirectoryClient({ ses_idle: { type: "idle" } })
    const boundary = new OpenCodeDirectoryInstances(() => fake.client)
    await used(boundary, "/virtual/project")

    expect((await boundary.release("/virtual/project")).outcome).toBe("disposed")
    expect((await boundary.release("/virtual/project")).outcome).toBe("untracked")
    expect(fake.sessionStatus).toHaveBeenCalledTimes(1)
    expect(fake.instanceDispose).toHaveBeenCalledTimes(1)
  })

  it.each([
    ["busy", { ses: { type: "busy" } }],
    ["retry", { ses: { type: "retry", attempt: 1, message: "retry", next: 1 } }],
    ["unknown", { ses: { type: "streaming" } }],
    ["malformed", { ses: null }],
    ["missing map", null],
  ])("retains a %s status candidate without disposing", async (_name, status) => {
    const fake = buildDirectoryClient(status)
    const boundary = new OpenCodeDirectoryInstances(() => fake.client)
    await used(boundary, "/virtual/project")

    const result = await boundary.release("/virtual/project")
    expect(["busy", "failed"]).toContain(result.outcome)
    expect(fake.instanceDispose).not.toHaveBeenCalled()
    expect((await boundary.release("/virtual/project")).outcome).toBe(result.outcome)
    expect(fake.sessionStatus).toHaveBeenCalledTimes(2)
  })

  it("retains a candidate when status throws, dispose throws, or dispose is false", async () => {
    const statusFailure = buildDirectoryClient()
    statusFailure.sessionStatus.mockRejectedValue(new Error("status failed"))
    const statusBoundary = new OpenCodeDirectoryInstances(() => statusFailure.client)
    await used(statusBoundary, "/virtual/status-failure")
    expect((await statusBoundary.release("/virtual/status-failure")).outcome).toBe("failed")

    const disposeFailure = buildDirectoryClient()
    disposeFailure.instanceDispose.mockRejectedValue(new Error("dispose failed"))
    const disposeBoundary = new OpenCodeDirectoryInstances(() => disposeFailure.client)
    await used(disposeBoundary, "/virtual/dispose-failure")
    expect((await disposeBoundary.release("/virtual/dispose-failure")).outcome).toBe("failed")

    const falseDispose = buildDirectoryClient({}, false)
    const falseBoundary = new OpenCodeDirectoryInstances(() => falseDispose.client)
    await used(falseBoundary, "/virtual/false-dispose")
    expect((await falseBoundary.release("/virtual/false-dispose")).outcome).toBe("failed")
    expect((await falseBoundary.release("/virtual/false-dispose")).outcome).toBe("failed")
  })

  it("retracks a directory after a successful release", async () => {
    const fake = buildDirectoryClient()
    const boundary = new OpenCodeDirectoryInstances(() => fake.client)
    await used(boundary, "/virtual/project")
    await boundary.release("/virtual/project")
    await used(boundary, "/virtual/project")

    expect((await boundary.release("/virtual/project")).outcome).toBe("disposed")
    expect(fake.sessionStatus).toHaveBeenCalledTimes(2)
    expect(fake.instanceDispose).toHaveBeenCalledTimes(2)
  })

  it("waits a normal operation behind an active disposal and then retracks it", async () => {
    const fake = buildDirectoryClient()
    const statusStarted = deferred<void>()
    const statusGate = deferred<{ data: unknown }>()
    const disposeStarted = deferred<void>()
    const disposeGate = deferred<{ data: unknown }>()
    fake.sessionStatus.mockImplementationOnce(async () => {
      statusStarted.resolve()
      return await statusGate.promise
    })
    fake.instanceDispose.mockImplementationOnce(async () => {
      disposeStarted.resolve()
      return await disposeGate.promise
    })
    const boundary = new OpenCodeDirectoryInstances(() => fake.client)
    await used(boundary, "/virtual/project")

    const release = boundary.release("/virtual/project")
    await statusStarted.promise
    const operation = deferred<void>()
    const operationStarted = deferred<void>()
    const next = boundary.withOperation("/virtual/project", async (lease) => {
      operationStarted.resolve()
      lease.markUsed()
      await operation.promise
    })
    expect(fake.instanceDispose).not.toHaveBeenCalled()

    statusGate.resolve({ data: {} })
    await disposeStarted.promise
    expect(fake.instanceDispose).toHaveBeenCalledTimes(1)
    disposeGate.resolve({ data: true })
    await release
    await operationStarted.promise
    operation.resolve()
    await next
  })

  it("does not block a different directory while one directory is disposing", async () => {
    const fake = buildDirectoryClient()
    const statusStarted = deferred<void>()
    const statusGate = deferred<{ data: unknown }>()
    fake.sessionStatus.mockImplementationOnce(async () => {
      statusStarted.resolve()
      return await statusGate.promise
    })
    const boundary = new OpenCodeDirectoryInstances(() => fake.client)
    await used(boundary, "/virtual/a")
    const releaseA = boundary.release("/virtual/a")
    await statusStarted.promise

    let startedB = false
    await boundary.withOperation("/virtual/b", async (lease) => {
      startedB = true
      lease.markUsed()
    })
    expect(startedB).toBe(true)
    statusGate.resolve({ data: { session: { type: "busy" } } })
    await releaseA
  })

  it("clears old-generation candidates and ignores later old operations", async () => {
    const fake = buildDirectoryClient()
    const boundary = new OpenCodeDirectoryInstances(() => fake.client)
    await used(boundary, "/virtual/project")
    boundary.resetGeneration()

    expect((await boundary.release("/virtual/project")).outcome).toBe("untracked")
    expect(fake.sessionStatus).not.toHaveBeenCalled()
  })

  it("wakes waiters on generation reset and fences the old disposal result", async () => {
    const fake = buildDirectoryClient()
    const statusStarted = deferred<void>()
    const statusGate = deferred<{ data: unknown }>()
    fake.sessionStatus.mockImplementationOnce(async () => {
      statusStarted.resolve()
      return await statusGate.promise
    })
    const boundary = new OpenCodeDirectoryInstances(() => fake.client)
    await used(boundary, "/virtual/project")

    const oldRelease = boundary.release("/virtual/project")
    await statusStarted.promise
    let operationStarted = false
    const next = boundary.withOperation("/virtual/project", async (lease) => {
      operationStarted = true
      lease.markUsed()
    })
    expect(operationStarted).toBe(false)

    boundary.resetGeneration()
    await next
    expect(operationStarted).toBe(true)

    statusGate.resolve({ data: {} })
    await oldRelease
    expect((await boundary.release("/virtual/project")).outcome).toBe("disposed")
    expect(fake.sessionStatus).toHaveBeenCalledTimes(2)
    expect(fake.instanceDispose).toHaveBeenCalledTimes(2)
  })
})

class FakeSubscription implements RuntimeEventSubscription {
  private readonly listeners = new Set<(event: RuntimeGlobalEvent) => void>()

  subscribe(listener: (event: RuntimeGlobalEvent) => void): () => void {
    this.listeners.add(listener)
    return () => this.listeners.delete(listener)
  }

  emit(event: RuntimeGlobalEvent): void {
    for (const listener of [...this.listeners]) listener(event)
  }

  async close(): Promise<void> {
    this.listeners.clear()
  }
}

it("holds the runTurn directory lease until a pending permission reply settles", async () => {
  const subscription = new FakeSubscription()
  const permissionReply = deferred<{ data: boolean }>()
  const permissionFinished = deferred<void>()
  const promptStarted = deferred<void>()
  const prompt = deferred<unknown>()
  const sessionStatus = vi.fn(async () => ({ data: {} }))
  const sessionCreate = vi.fn(async () => ({ data: { id: "ses_permission" } }))
  const sessionPrompt = vi.fn(async () => {
    promptStarted.resolve()
    return await prompt.promise
  })
  const sessionAbort = vi.fn(async () => ({ data: true }))
  const permission = vi.fn(async () => {
    const response = await permissionReply.promise
    permissionFinished.resolve()
    return response
  })
  const instanceDispose = vi.fn(async () => ({ data: true }))
  const client = {
    global: { health: vi.fn(async () => ({ data: { ok: true } })) },
    session: { create: sessionCreate, prompt: sessionPrompt, status: sessionStatus, abort: sessionAbort },
    permission: { reply: permission },
    instance: { dispose: instanceDispose },
  } as unknown as OpencodeClient
  const server: OpencodeServerHandle = {
    url: "http://fake",
    directory: "/virtual/root",
    client,
    async close() {},
  }
  const runtime = new OpenCodeRuntime({
    directory: "/virtual/root",
    serverFactory: async () => server,
    eventSubscriptionFactory: () => subscription,
  })
  await runtime.start()

  const turn = runtime.runTurn({
    target: { runtime: "opencode", runtimeSessionId: null, workDir: "/virtual/project" },
    prompt: "do",
  }, new AbortController().signal)
  await promptStarted.promise
  subscription.emit({
    type: "permission.asked",
    sessionID: "ses_permission",
    directory: "/virtual/project",
    payload: { id: "permission-1" },
  })
  prompt.resolve({ data: { parts: [{ type: "text", text: "done" }] } })
  const result = await turn

  expect(result.ok).toBe(true)
  expect(permission).toHaveBeenCalledTimes(1)
  expect(sessionStatus).toHaveBeenCalledTimes(1)
  expect((await runtime.release("/virtual/project")).outcome).toBe("busy")
  expect(sessionStatus).toHaveBeenCalledTimes(1)
  expect(instanceDispose).not.toHaveBeenCalled()

  permissionReply.resolve({ data: true })
  await permissionFinished.promise
  expect(instanceDispose).not.toHaveBeenCalled()
})

it("gets the current Runtime client after waiting behind disposal", async () => {
  const statusGate = deferred<{ data: unknown }>()
  const statusStarted = deferred<void>()
  const createA = vi.fn(async () => ({ data: { id: "ses_a" } }))
  const createB = vi.fn(async () => ({ data: { id: "ses_b" } }))
  const clientA = {
    global: { health: vi.fn(async () => ({ data: { ok: true } })) },
    session: { create: createA, status: vi.fn(() => {
      statusStarted.resolve()
      return statusGate.promise
    }) },
    instance: { dispose: vi.fn(async () => ({ data: true })) },
  } as unknown as OpencodeClient
  const clientB = {
    session: { create: createB },
  } as unknown as OpencodeClient
  let currentClient = clientA
  const server = {
    url: "http://fake",
    directory: "/virtual/root",
    async close() {},
  } as OpencodeServerHandle & { client: OpencodeClient }
  Object.defineProperty(server, "client", { get: () => currentClient })
  const runtime = new OpenCodeRuntime({
    directory: "/virtual/root",
    serverFactory: async () => server,
    eventSubscriptionFactory: () => new FakeSubscription(),
  })
  await runtime.start()

  const request = { target: { runtime: "opencode" as const, runtimeSessionId: null, workDir: "/virtual/project" } }
  await runtime.createSession(request)
  const oldRelease = runtime.release("/virtual/project")
  await statusStarted.promise

  const next = runtime.createSession(request)
  expect(createB).not.toHaveBeenCalled()

  currentClient = clientB
  statusGate.resolve({ data: {} })
  await oldRelease
  await next

  expect(createA).toHaveBeenCalledTimes(1)
  expect(createB).toHaveBeenCalledTimes(1)
})
