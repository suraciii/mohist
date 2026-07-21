import { afterEach, describe, expect, it, vi } from "vitest"
import { opencodeAction } from "../src/actions/opencode.js"
import { OpenCodeRuntime } from "../src/runtime/opencode/index.js"
import type { OpenCodeRuntimeDeps } from "../src/runtime/opencode/runtime.js"
import type { OpencodeServerHandle } from "../src/runtime/opencode/server-process.js"
import type { RuntimeEventSubscription, RuntimeGlobalEvent } from "../src/runtime/opencode/event-subscription.js"
import type { OpencodeClient } from "@opencode-ai/sdk/v2"
import type { ActionContext } from "../src/core/types.js"
import type { ServerConnection } from "../src/server/connection.js"
import { clearOpenCodeRuntimeFactoryForTest } from "./support/opencode-runtime-factory.js"
import { setPromptLoaderRegistryForTest } from "../src/core/prompt.js"

class FakeSubscription implements RuntimeEventSubscription {
  private listeners = new Set<(event: RuntimeGlobalEvent) => void>()
  closed = false
  subscribe(listener: (event: RuntimeGlobalEvent) => void): () => void {
    if (this.closed) return () => {}
    this.listeners.add(listener)
    return () => { this.listeners.delete(listener) }
  }
  emit(event: RuntimeGlobalEvent): void {
    for (const listener of [...this.listeners]) listener(event)
  }
  async close(): Promise<void> {
    this.closed = true
    this.listeners.clear()
  }
}

interface BuildArgs {
  promptResult?: unknown
  failPrompt?: boolean
  failPromptMessage?: string
  emitDuringPrompt?: (subscription: FakeSubscription, sessionId: string) => Promise<void> | void
}

function buildRuntime(args: BuildArgs = {}) {
  const subscription = new FakeSubscription()
  const sessionCreate = vi.fn(async () => ({ data: { id: "ses_bound" } }))
  const sessionPrompt = vi.fn(async (params: { sessionID: string; directory?: string; parts?: unknown }) => {
    if (args.emitDuringPrompt) {
      await args.emitDuringPrompt(subscription, params.sessionID)
    }
    if (args.failPrompt) throw new Error(args.failPromptMessage ?? "opencode prompt failed")
    if (args.promptResult !== undefined) return args.promptResult
    return {
      data: {
        info: {
          id: "msg_1",
          sessionID: "ses_bound",
          role: "assistant",
          providerID: "openai",
          modelID: "gpt-5",
          tokens: { input: 5, output: 7, total: 12, reasoning: 0, cache: { read: 0 } },
          cost: 0.0001,
        },
        parts: [{ type: "text", text: "final answer" }],
      },
    }
  })
  const sessionAbort = vi.fn(async () => ({ data: true }))
  const sessionPromptAsync = vi.fn(async () => ({ data: true }))
  const sessionGet = vi.fn(async (params: { sessionID: string }) => ({ data: { id: params.sessionID } }))
  const sessionStatus = vi.fn(async () => ({ data: {} }))
  const clientProxy = {
    global: { health: vi.fn(async () => ({ data: { ok: true } })), event: vi.fn() },
    session: { create: sessionCreate, prompt: sessionPrompt, promptAsync: sessionPromptAsync, abort: sessionAbort, messages: vi.fn(), get: sessionGet, status: sessionStatus },
  }
  const server: OpencodeServerHandle = {
    url: "http://fake",
    directory: "/tmp/work",
    client: clientProxy as unknown as OpencodeClient,
    async close() {},
  }
  const deps: OpenCodeRuntimeDeps = {
    directory: "/tmp/work",
    serverFactory: async () => server,
    eventSubscriptionFactory: () => subscription,
  }
  const runtime = new OpenCodeRuntime(deps)
  void runtime.start()
  return { runtime, subscription }
}

async function ensureReady(runtime: OpenCodeRuntime): Promise<void> {
  await runtime.start()
}

interface FakeConnectionHandles {
  connection: ServerConnection
  eventCalls: Array<{ body: unknown }>
  setEventBehavior: (writer: (body: unknown) => Promise<void> | void) => void
  setEventRejection: (types: ReadonlySet<string>) => void
}

function makeFakeConnection(): FakeConnectionHandles {
  const eventCalls: FakeConnectionHandles["eventCalls"] = []
  let writer: (body: unknown) => Promise<void> | void = async () => {}
  let rejectTypes: ReadonlySet<string> = new Set()
  const connection = {
    async openWorkflowAgentSession() {
      return { runtimeSessionId: "ses_bound", workDir: "/tmp/work" }
    },
    async attachWorkflowAgentSession() {
      void 0
    },
    async workflowAgentSessionRuntimeEvents(_projectId: string, _workflowRunId: string, _sessionName: string, body: unknown) {
      eventCalls.push({ body })
      const runtimeEvents = (body as { runtimeEvents: Array<{ type: string; payload: unknown }> }).runtimeEvents ?? []
      if (runtimeEvents.some((e) => rejectTypes.has(e.type))) {
        throw new Error(`rejected: ${runtimeEvents[0]?.type ?? "?"}`)
      }
      await writer(body)
      return runtimeEvents.map((event) => ({ type: event.type }))
    },
  } as unknown as ServerConnection
  return {
    connection,
    eventCalls,
    setEventBehavior(next) { writer = next },
    setEventRejection(next) { rejectTypes = next },
  }
}

function baseContext(overrides: Partial<ActionContext> = {}): ActionContext {
  return {
    workflowRunId: "wf-1",
    workId: "work-1",
    workType: "task",
    stage: "plan",
    title: "Workflow turn",
    uses: "mohist/opencode",
    with: { prompt: "do the work", session: "plan" } as never,
    variables: {},
    workDir: "/tmp/work",
    signal: new AbortController().signal,
    projectId: "proj-1",
    writeVars: async () => {},
    ...overrides,
  }
}

function eventTypeList(eventCalls: Array<{ body: unknown }>): string[] {
  return eventCalls.map((call) => {
    const events = (call.body as { runtimeEvents: Array<{ type: string }> }).runtimeEvents
    return events[0]?.type ?? "?"
  })
}

function readCloseBody(eventCalls: Array<{ body: unknown }>) {
  for (const call of eventCalls) {
    const events = (call.body as { runtimeEvents: Array<{ type: string; payload: Record<string, unknown> }> }).runtimeEvents ?? []
    const close = events.find((e) => e.type === "session.closed")
    if (close) return close.payload
  }
  return undefined
}

function closeIndex(eventCalls: Array<{ body: unknown }>): number {
  return eventCalls.findIndex((entry) => {
    const events = (entry.body as { runtimeEvents: Array<{ type: string }> }).runtimeEvents
    return events[0]?.type === "session.closed"
  })
}

afterEach(() => {
  setPromptLoaderRegistryForTest(null)
  clearOpenCodeRuntimeFactoryForTest()
  vi.useRealTimers()
})

describe("opencodeAction — Workflow AgentSession terminal-state close reporting", () => {
  it("enqueues exactly one completed session.closed after all observed and reconciled runtime events", async () => {
    const { runtime } = buildRuntime({})
    await ensureReady(runtime)
    const connection = makeFakeConnection()
    const context = baseContext({ openCodeRuntime: runtime, serverConnection: connection.connection })

    const result = await opencodeAction(context)
    expect(result.error).toBeUndefined()
    const types = eventTypeList(connection.eventCalls)
    const closeAt = closeIndex(connection.eventCalls)
    expect(types.filter((t) => t === "session.closed")).toHaveLength(1)
    expect(closeAt).toBe(types.length - 1)
    const closeBody = readCloseBody(connection.eventCalls)
    expect(closeBody).toMatchObject({ status: "completed", exitCode: 0, runtimeSessionId: "ses_bound" })
    expect(closeBody).not.toHaveProperty("failureReason")
  })

  it("enqueues exactly one failed session.closed when runTurn fails and the failure reason is the runtime error message", async () => {
    const { runtime } = buildRuntime({ failPrompt: true, failPromptMessage: "opencode-runtime-explosion" })
    await ensureReady(runtime)
    const connection = makeFakeConnection()
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)
    try {
      const result = await opencodeAction(baseContext({ openCodeRuntime: runtime, serverConnection: connection.connection }))
      expect(result.error).toBeDefined()
      const closeBody = readCloseBody(connection.eventCalls)
      expect(closeBody).toMatchObject({ status: "failed", exitCode: 1, runtimeSessionId: "ses_bound" })
      expect(closeBody?.failureReason).toBe(result.error?.message)
      expect(closeIndex(connection.eventCalls)).toBe(eventTypeList(connection.eventCalls).length - 1)
    } finally {
      errorSpy.mockRestore()
    }
  })

  it("enqueues exactly one failed session.closed when runTurn is interrupted by an aborted signal", async () => {
    const { runtime } = buildRuntime()
    await ensureReady(runtime)
    const connection = makeFakeConnection()
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)
    const abortController = new AbortController()
    abortController.abort()
    try {
      const context = baseContext({
        openCodeRuntime: runtime,
        serverConnection: connection.connection,
        signal: abortController.signal,
      })
      const result = await opencodeAction(context)
      expect(result.error).toBeDefined()
      const closeBody = readCloseBody(connection.eventCalls)
      expect(closeBody).toMatchObject({ status: "failed", exitCode: 1 })
      expect(closeBody?.failureReason).toBe(result.error?.message)
      expect(closeBody?.failureReason).not.toMatch(/rejected/)
      expect(closeBody?.failureReason).not.toMatch(/upload/)
    } finally {
      errorSpy.mockRestore()
    }
  })

  it("settles close after reconciled events when no live deltas fire but final-response reconciliation emits after prompt returns", async () => {
    const { runtime } = buildRuntime({
      emitDuringPrompt: async (subscription, sessionId) => {
        subscription.emit({
          type: "session.next.text.delta",
          sessionID: sessionId,
          payload: { textID: "txt_recon", assistantMessageID: "msg_1", delta: "final answer" },
        })
        await new Promise((resolve) => setImmediate(resolve))
      },
    })
    await ensureReady(runtime)
    const connection = makeFakeConnection()
    const order: string[] = []
    connection.setEventBehavior(async (body) => {
      const events = (body as { runtimeEvents: Array<{ type: string }> }).runtimeEvents
      order.push(events[0]?.type ?? "?")
      await new Promise((resolve) => setImmediate(resolve))
    })
    const context = baseContext({ openCodeRuntime: runtime, serverConnection: connection.connection })

    const result = await opencodeAction(context)
    expect(result.error).toBeUndefined()
    const closedIdx = order.lastIndexOf("session.closed")
    expect(closedIdx).toBeGreaterThan(0)
    expect(closedIdx).toBe(order.length - 1)
    const closeBody = readCloseBody(connection.eventCalls)
    expect(closeBody).toMatchObject({ status: "completed", exitCode: 0, runtimeSessionId: "ses_bound" })
  })

  it("does not change a successful Action result when the close upload is rejected", async () => {
    const { runtime } = buildRuntime({})
    await ensureReady(runtime)
    const connection = makeFakeConnection()
    connection.setEventRejection(new Set(["session.closed"]))
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)
    try {
      const result = await opencodeAction(baseContext({ openCodeRuntime: runtime, serverConnection: connection.connection }))
      expect(result.error).toBeUndefined()
      expect(result.turnFact?.finalAssistantText).toBe("final answer")
      const types = eventTypeList(connection.eventCalls)
      expect(types.filter((t) => t === "session.closed")).toHaveLength(1)
      const logged = errorSpy.mock.calls.map((call) => String(call[0])).join("\n")
      expect(logged).toMatch(/type=session\.closed/)
      expect(logged).not.toContain("final answer")
    } finally {
      errorSpy.mockRestore()
    }
  })

  it("does not replace the runtime failure when a failed-turn's close upload is also rejected", async () => {
    const { runtime } = buildRuntime({ failPrompt: true, failPromptMessage: "opencode-runtime-explosion" })
    await ensureReady(runtime)
    const connection = makeFakeConnection()
    connection.setEventRejection(new Set(["session.closed"]))
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)
    try {
      const context = baseContext({
        openCodeRuntime: runtime,
        serverConnection: connection.connection,
        with: { prompt: "opencode will fail", session: "plan" } as never,
      })
      const result = await opencodeAction(context)
      expect(result.error?.message).toBe("opencode-runtime-explosion")
      expect(result.error?.message).not.toMatch(/rejected/)
      expect(result.error?.message).not.toMatch(/upload/)
      const types = eventTypeList(connection.eventCalls)
      expect(types.filter((t) => t === "session.closed")).toHaveLength(1)
    } finally {
      errorSpy.mockRestore()
    }
  })

  it("two turns reusing one logical and physical AgentSession record two input/activity/close triples in order", async () => {
    const { runtime } = buildRuntime()
    await ensureReady(runtime)
    let boundId: string | null = null
    const events: unknown[] = []
    const connection = {
      async openWorkflowAgentSession() {
        return { runtimeSessionId: boundId ?? "ses_first", workDir: "/tmp/work" }
      },
      async attachWorkflowAgentSession(_projectId: string, _workflowRunId: string, _sessionName: string, body: unknown) {
        boundId = (body as { runtimeSessionId: string }).runtimeSessionId
      },
      async workflowAgentSessionRuntimeEvents(_projectId: string, _workflowRunId: string, _sessionName: string, body: unknown) {
        events.push(body)
        return (body as { runtimeEvents: Array<{ type: string }> }).runtimeEvents.map((event) => ({ type: event.type }))
      },
    } as unknown as ServerConnection

    await opencodeAction(baseContext({
      openCodeRuntime: runtime,
      serverConnection: connection,
      workId: "work-a",
      with: { prompt: "first prompt", session: "plan" } as never,
    }))
    await opencodeAction(baseContext({
      openCodeRuntime: runtime,
      serverConnection: connection,
      workId: "work-b",
      with: { prompt: "second prompt", session: "plan" } as never,
    }))

    const types = events.map((entry) => {
      const ev = (entry as { runtimeEvents: Array<{ type: string }> }).runtimeEvents
      return ev[0]?.type ?? "?"
    })
    const inputIndexes = types.flatMap((t, i) => (t === "session.input" ? [i] : []))
    expect(inputIndexes.length).toBe(2)
    const splits = [0, inputIndexes[1]!, types.length]
    const firstTurn = types.slice(splits[0], splits[1])
    const secondTurn = types.slice(splits[1], splits[2])
    expect(firstTurn[0]).toBe("session.input")
    expect(firstTurn[firstTurn.length - 1]).toBe("session.closed")
    expect(secondTurn[0]).toBe("session.input")
    expect(secondTurn[secondTurn.length - 1]).toBe("session.closed")

    const allCloses = events
      .map((entry) => (entry as { runtimeEvents: Array<{ type: string; payload: Record<string, unknown> }> }).runtimeEvents[0])
      .filter((event) => event?.type === "session.closed")
      .map((event) => event!.payload)
    expect(allCloses).toHaveLength(2)
    expect(allCloses[0]).toMatchObject({ status: "completed", exitCode: 0, runtimeSessionId: "ses_first" })
    expect(allCloses[1]).toMatchObject({ status: "completed", exitCode: 0, runtimeSessionId: "ses_first" })
  })
})
