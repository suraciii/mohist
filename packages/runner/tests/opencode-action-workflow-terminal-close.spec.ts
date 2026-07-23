import { afterEach, describe, expect, it, vi } from "vitest"
import { opencodeAction } from "../src/actions/opencode.js"
import { OpenCodeRuntime } from "../src/runtime/opencode/index.js"
import type { OpenCodeRuntimeDeps } from "../src/runtime/opencode/runtime.js"
import type { OpencodeServerHandle } from "../src/runtime/opencode/server-process.js"
import type { RuntimeEventSubscription, RuntimeGlobalEvent } from "../src/runtime/opencode/event-subscription.js"
import type { OpencodeClient } from "@opencode-ai/sdk/v2"
import type { ActionTestContext as ActionContext } from "./support/action-test-context.js"
import { clearOpenCodeRuntimeFactoryForTest } from "./support/opencode-runtime-factory.js"
import { setPromptLoaderRegistryForTest } from "../src/core/prompt.js"
import { makeRecordingOutbox } from "./support/outbox-test-helpers.js"
import type { RuntimeEventRecord } from "../src/server/runtime-event-outbox.js"
import { callAction } from "./support/call-action.js"

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
  const clientProxy: OpencodeClient = {
    global: { health: vi.fn(async () => ({ data: { ok: true } })), event: vi.fn() },
    session: { create: sessionCreate, prompt: sessionPrompt, promptAsync: sessionPromptAsync, abort: sessionAbort, messages: vi.fn(), get: sessionGet, status: sessionStatus },
  } as unknown as OpencodeClient
  const server: OpencodeServerHandle = {
    url: "http://fake",
    directory: "/tmp/work",
    client: clientProxy,
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

afterEach(() => {
  setPromptLoaderRegistryForTest(null)
  clearOpenCodeRuntimeFactoryForTest()
  vi.useRealTimers()
})

describe("opencodeAction — Workflow AgentSession terminal-state close", () => {
  it("enqueues exactly one completed session.closed after all observed and reconciled runtime events", async () => {
    const { runtime } = buildRuntime({})
    await ensureReady(runtime)
    const handles = makeRecordingOutbox()
    const context = baseContext({
      openCodeRuntime: runtime,
      serverConnection: handles.connection,
      agentSessionRuntimeEventOutbox: handles.outbox,
    })

    const result = await callAction(opencodeAction, context)
    expect(result.error).toBeUndefined()

    const closed = handles.eventsByType("session.activity")
    expect(closed).toHaveLength(1)
    expect(closed[0]).toMatchObject({
      event: {
        type: "session.activity",
        payload: expect.objectContaining({ status: "completed", exitCode: 0, runtimeSessionId: "ses_bound" }),
      },
    })
    const types = handles.eventTypeList()
    expect(types[types.length - 1]).toBe("session.activity")
  })

  it("enqueues exactly one failed session.closed when runTurn fails and the failure reason is the runtime error message", async () => {
    const { runtime } = buildRuntime({ failPrompt: true, failPromptMessage: "opencode-runtime-explosion" })
    await ensureReady(runtime)
    const handles = makeRecordingOutbox()
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)
    try {
      const result = await callAction(opencodeAction, baseContext({
        openCodeRuntime: runtime,
        serverConnection: handles.connection,
        agentSessionRuntimeEventOutbox: handles.outbox,
      }))
      expect(result.error).toBeDefined()
      const closed = handles.eventsByType("session.activity")[0]
      expect(closed?.event.payload).toMatchObject({ status: "failed", exitCode: 1, runtimeSessionId: "ses_bound" })
      expect((closed?.event.payload as Record<string, unknown>).failureReason).toBe(result.error?.message)
    } finally {
      errorSpy.mockRestore()
    }
  })

  it("enqueues exactly one failed session.closed when runTurn is interrupted by an aborted signal", async () => {
    const { runtime } = buildRuntime()
    await ensureReady(runtime)
    const handles = makeRecordingOutbox()
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)
    const abortController = new AbortController()
    abortController.abort()
    try {
      const context = baseContext({
        openCodeRuntime: runtime,
        serverConnection: handles.connection,
        agentSessionRuntimeEventOutbox: handles.outbox,
        signal: abortController.signal,
      })
      const result = await callAction(opencodeAction, context)
      expect(result.error).toBeDefined()
      const closed = handles.eventsByType("session.activity")[0]
      const reason = (closed?.event.payload as Record<string, unknown>).failureReason as string
      expect(reason).toBe(result.error?.message)
      expect(reason).not.toMatch(/rejected/)
      expect(reason).not.toMatch(/upload/)
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
    const handles = makeRecordingOutbox()
    const context = baseContext({
      openCodeRuntime: runtime,
      serverConnection: handles.connection,
      agentSessionRuntimeEventOutbox: handles.outbox,
    })

    const result = await callAction(opencodeAction, context)
    expect(result.error).toBeUndefined()
    const types = handles.eventTypeList()
    const closedIdx = types.lastIndexOf("session.activity")
    expect(closedIdx).toBeGreaterThan(0)
    expect(closedIdx).toBe(types.length - 1)
    const closed = handles.eventsByType("session.activity")[0]
    expect(closed?.event.payload).toMatchObject({ status: "completed", exitCode: 0, runtimeSessionId: "ses_bound" })
  })

  it("does not change a successful Action result when the close enqueue is rejected", async () => {
    const { runtime } = buildRuntime({})
    await ensureReady(runtime)
    const handles = makeRecordingOutbox()
    const failingOutbox = {
      ...handles.outbox,
      async enqueueProducedFact(record: RuntimeEventRecord) {
        if (record.event.type === "session.activity") throw new Error("disk full (activity)")
        return handles.outbox.enqueueProducedFact(record)
      },
    }
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)
    try {
      const result = await callAction(opencodeAction, baseContext({
        openCodeRuntime: runtime,
        serverConnection: handles.connection,
        agentSessionRuntimeEventOutbox: failingOutbox,
      }))
      expect(result.error).toBeUndefined()
      expect(result.turnFact?.finalAssistantText).toBe("final answer")
      const closed = handles.eventsByType("session.activity")
      // Close enqueue failed locally, so the record is tracked in the
      // reporter's pending promises for autonomous recovery rather than
      // appearing in the durable snapshot.
      expect(closed).toHaveLength(1)
      const logged = errorSpy.mock.calls.map((call) => String(call[0])).join("\n")
      expect(logged).toBe("")
      expect(logged).not.toContain("final answer")
    } finally {
      errorSpy.mockRestore()
    }
  })

  it("does not replace the runtime failure when a failed-turn's close enqueue is also rejected", async () => {
    const { runtime } = buildRuntime({ failPrompt: true, failPromptMessage: "opencode-runtime-explosion" })
    await ensureReady(runtime)
    const handles = makeRecordingOutbox()
    const failingOutbox = {
      ...handles.outbox,
      async enqueueProducedFact(record: RuntimeEventRecord) {
        if (record.event.type === "session.activity") throw new Error("disk full (activity)")
        return handles.outbox.enqueueProducedFact(record)
      },
    }
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)
    try {
      const context = baseContext({
        openCodeRuntime: runtime,
        serverConnection: handles.connection,
        agentSessionRuntimeEventOutbox: failingOutbox,
        with: { prompt: "opencode will fail", session: "plan" } as never,
      })
      const result = await callAction(opencodeAction, context)
      expect(result.error?.code).toBe("turn-failed")
      expect(result.error?.message).toBe("opencode-runtime-explosion")
    const closed = handles.eventsByType("session.activity")
      expect(closed).toHaveLength(1)
    } finally {
      errorSpy.mockRestore()
    }
  })

  it("two turns reusing one logical and physical AgentSession record two input/activity/close triples in order", async () => {
    const { runtime } = buildRuntime()
    await ensureReady(runtime)
    let boundId: string | null = null
    const handles = makeRecordingOutbox()
    handles.connection = {
      async openWorkflowAgentSession() {
        return { runtimeSessionId: boundId ?? "ses_first", workDir: "/tmp/work" }
      },
      async attachWorkflowAgentSession(_projectId: string, _workflowRunId: string, _sessionName: string, body: unknown) {
        boundId = (body as { runtimeSessionId: string }).runtimeSessionId
      },
      async workflowAgentSessionRuntimeEvents(_projectId: string, _workflowRunId: string, _sessionName: string, _body: unknown) {
        return []
      },
    } as unknown as typeof handles.connection

    await callAction(opencodeAction, baseContext({
      openCodeRuntime: runtime,
      serverConnection: handles.connection,
      agentSessionRuntimeEventOutbox: handles.outbox,
      workId: "work-a",
      with: { prompt: "first prompt", session: "plan" } as never,
    }))
    await callAction(opencodeAction, baseContext({
      openCodeRuntime: runtime,
      serverConnection: handles.connection,
      agentSessionRuntimeEventOutbox: handles.outbox,
      workId: "work-b",
      with: { prompt: "second prompt", session: "plan" } as never,
    }))

    const types = handles.eventTypeList()
    const inputIndexes = types.flatMap((t, i) => (t === "session.input" ? [i] : []))
    expect(inputIndexes.length).toBe(2)
    const firstTurn = types.slice(0, inputIndexes[1])
    const secondTurn = types.slice(inputIndexes[1])
    expect(firstTurn[0]).toBe("session.input")
    expect(firstTurn[firstTurn.length - 1]).toBe("session.activity")
    expect(secondTurn[0]).toBe("session.input")
    expect(secondTurn[secondTurn.length - 1]).toBe("session.activity")

    const allCloses = handles.eventsByType("session.activity").map((r) => r.event.payload)
    expect(allCloses).toHaveLength(2)
    expect(allCloses[0]).toMatchObject({ status: "completed", exitCode: 0, runtimeSessionId: "ses_first" })
    expect(allCloses[1]).toMatchObject({ status: "completed", exitCode: 0, runtimeSessionId: "ses_first" })
  })
})
