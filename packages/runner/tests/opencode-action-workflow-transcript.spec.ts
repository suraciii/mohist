import { afterEach, describe, expect, it, vi } from "vitest"
import { opencodeAction } from "../src/actions/opencode.js"
import { OpenCodeRuntime } from "../src/runtime/opencode/index.js"
import type { OpenCodeRuntimeDeps } from "../src/runtime/opencode/runtime.js"
import type { OpencodeServerHandle } from "../src/runtime/opencode/server-process.js"
import type { RuntimeEventSubscription, RuntimeGlobalEvent } from "../src/runtime/opencode/event-subscription.js"
import type { OpencodeClient } from "@opencode-ai/sdk/v2"
import type { ActionContext } from "../src/core/types.js"
import type { ServerConnection } from "../src/server/connection.js"
import { WorkflowAgentSessionReporter } from "../src/actions/workflow-agent-session-reporter.js"
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

interface FakeClient {
  sessionCreate: ReturnType<typeof vi.fn>
  sessionPrompt: ReturnType<typeof vi.fn>
  sessionPromptAsync: ReturnType<typeof vi.fn>
  sessionAbort: ReturnType<typeof vi.fn>
  sessionGet: ReturnType<typeof vi.fn>
  sessionStatus: ReturnType<typeof vi.fn>
}

interface BuildArgs {
  promptResult?: unknown
  failCreate?: boolean
  failPrompt?: boolean
  failPromptMessage?: string
  emitDuringPrompt?: (subscription: FakeSubscription, sessionId: string) => Promise<void> | void
}

interface BuildResult {
  deps: OpenCodeRuntimeDeps
  runtime: OpenCodeRuntime
  client: FakeClient
  subscription: FakeSubscription
}

function buildRuntime(args: BuildArgs = {}): BuildResult {
  const subscription = new FakeSubscription()
  const sessionCreate = vi.fn(async (_params: { directory?: string; model?: unknown }) => {
    if (args.failCreate) throw new Error("create boom")
    return { data: { id: "ses_default" } }
  })
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
  const sessionAbort = vi.fn(async (_params: { sessionID: string; directory?: string }) => ({ data: true }))
  const sessionPromptAsync = vi.fn(async (_params: { sessionID: string; directory?: string; parts?: unknown }) => ({ data: true }))
  const sessionGet = vi.fn(async (params: { sessionID: string; directory?: string }) => ({ data: { id: params.sessionID } }))
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
  return {
    deps,
    runtime,
    client: { sessionCreate, sessionPrompt, sessionPromptAsync, sessionAbort, sessionGet, sessionStatus },
    subscription,
  }
}

async function ensureReady(runtime: OpenCodeRuntime): Promise<void> {
  await runtime.start()
}

interface FakeConnectionHandles {
  connection: ServerConnection
  openCalls: Array<{ projectId: string; workflowRunId: string; sessionName: string; body: unknown }>
  attachCalls: Array<{ projectId: string; workflowRunId: string; sessionName: string; body: unknown }>
  eventCalls: Array<{ projectId: string; workflowRunId: string; sessionName: string; body: unknown }>
  setEventBehavior: (writer: (body: unknown) => Promise<void> | void) => void
  setEventRejection: (types: ReadonlySet<string>) => void
  setInputAccepted: (accepted: boolean) => void
}

function makeFakeConnection(): FakeConnectionHandles {
  const openCalls: FakeConnectionHandles["openCalls"] = []
  const attachCalls: FakeConnectionHandles["attachCalls"] = []
  const eventCalls: FakeConnectionHandles["eventCalls"] = []
  let writer: (body: unknown) => Promise<void> | void = async () => {}
  let rejectTypes: ReadonlySet<string> = new Set()
  let inputAccepted = true
  const connection = {
    async openWorkflowAgentSession(projectId: string, workflowRunId: string, sessionName: string, body: unknown) {
      openCalls.push({ projectId, workflowRunId, sessionName, body })
      return { runtimeSessionId: "ses_bound", workDir: "/tmp/work" }
    },
    async attachWorkflowAgentSession(projectId: string, workflowRunId: string, sessionName: string, body: unknown) {
      attachCalls.push({ projectId, workflowRunId, sessionName, body })
    },
    async workflowAgentSessionRuntimeEvents(projectId: string, workflowRunId: string, sessionName: string, body: unknown) {
      eventCalls.push({ projectId, workflowRunId, sessionName, body })
      const runtimeEvents = (body as { runtimeEvents: Array<{ type: string; payload: unknown }> }).runtimeEvents ?? []
      if (runtimeEvents.some((e) => rejectTypes.has(e.type))) {
        throw new Error(`rejected: ${runtimeEvents[0]?.type ?? "?"}`)
      }
      await writer(body)
      if (!inputAccepted && runtimeEvents.some((event) => event.type === "session.input")) return []
      return runtimeEvents.map((event) => ({ type: event.type }))
    },
  } as unknown as ServerConnection
  return {
    connection,
    openCalls,
    attachCalls,
    eventCalls,
    setEventBehavior(next) {
      writer = next
    },
    setEventRejection(next) {
      rejectTypes = next
    },
    setInputAccepted(accepted) {
      inputAccepted = accepted
    },
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

afterEach(() => {
  setPromptLoaderRegistryForTest(null)
  clearOpenCodeRuntimeFactoryForTest()
  vi.useRealTimers()
})

async function emitStandardSequence(subscription: FakeSubscription, sessionId: string): Promise<void> {
  subscription.emit({
    type: "message.updated",
    sessionID: sessionId,
    payload: {
      info: {
        id: "msg_1",
        role: "assistant",
        providerID: "openai",
        modelID: "gpt-5",
        tokens: { input: 5, output: 7, total: 12, reasoning: 0, cache: { read: 0 } },
        cost: 0.0001,
      },
    },
  })
  subscription.emit({
    type: "session.next.reasoning.delta",
    sessionID: sessionId,
    payload: { reasoningID: "r_1", assistantMessageID: "msg_1", delta: "thinking... " },
  })
  subscription.emit({
    type: "session.next.tool.called",
    sessionID: sessionId,
    payload: { callID: "tool_1", tool: "read", input: { path: "/etc" } },
  })
  subscription.emit({
    type: "session.next.tool.progress",
    sessionID: sessionId,
    payload: { callID: "tool_1", tool: "read", input: { path: "/etc" } },
  })
  subscription.emit({
    type: "session.next.tool.success",
    sessionID: sessionId,
    payload: { callID: "tool_1", tool: "read", result: "ok" },
  })
  subscription.emit({
    type: "message.part.updated",
    sessionID: sessionId,
    payload: { part: { id: "txt_1", messageID: "msg_1", type: "text", text: "hi" } },
  })
  await new Promise((resolve) => setImmediate(resolve))
}

describe("opencodeAction — Workflow AgentSession transcript reporting", () => {
  it("sends the composed prompt as session.input and forwards projected events in production order", async () => {
    const { runtime } = buildRuntime({
      emitDuringPrompt: emitStandardSequence,
    })
    await ensureReady(runtime)
    const connection = makeFakeConnection()
    const context = baseContext({ openCodeRuntime: runtime, serverConnection: connection.connection })

    const result = await opencodeAction(context)

    expect(result.error).toBeUndefined()
    expect(connection.openCalls).toHaveLength(1)
    expect(connection.eventCalls.length).toBeGreaterThan(0)
    const types = connection.eventCalls.map((call) => {
      const events = (call.body as { runtimeEvents: Array<{ type: string }> }).runtimeEvents
      return events[0]?.type ?? "?"
    })
    expect(types[0]).toBe("session.input")
    expect(types).toEqual(expect.arrayContaining([
      "model.resolved",
      "usage.updated",
      "reasoning.delta",
      "tool_call.started",
      "tool_call.updated",
      "tool_call.completed",
      "message.delta",
    ]))
    const sessionInput = connection.eventCalls[0]?.body as {
      runtimeEvents: Array<{ type: string; payload: Record<string, unknown> }>
    }
    expect(sessionInput.runtimeEvents[0]?.type).toBe("session.input")
    expect(sessionInput.runtimeEvents[0]?.payload).toMatchObject({
      text: "do the work",
      kind: "task",
      source: "workflow",
      role: "user",
      runtimeSessionId: "ses_bound",
    })
    expect(sessionInput).toMatchObject({
      workId: "work-1",
      workType: "task",
      stage: "plan",
      runtimeSessionId: "ses_bound",
    })
  })

  it("does not reproject SDK payloads; reporter receives runtime event payloads as-is", async () => {
    const { runtime } = buildRuntime({
      emitDuringPrompt: async (subscription, sessionId) => {
        subscription.emit({
          type: "session.next.tool.called",
          sessionID: sessionId,
          payload: { callID: "tool_2", tool: "bash", input: { cmd: "ls" } },
        })
        subscription.emit({
          type: "session.next.tool.failed",
          sessionID: sessionId,
          payload: { callID: "tool_2", tool: "bash", error: "boom" },
        })
        await new Promise((resolve) => setImmediate(resolve))
      },
    })
    await ensureReady(runtime)
    const connection = makeFakeConnection()
    const context = baseContext({ openCodeRuntime: runtime, serverConnection: connection.connection })
    await opencodeAction(context)

    const events = connection.eventCalls.flatMap((call) => {
      const body = call.body as { runtimeEvents: Array<{ type: string; payload: Record<string, unknown> }> }
      return body.runtimeEvents.map((e) => ({ type: e.type, payload: e.payload }))
    })
    const started = events.find((e) => e.type === "tool_call.started")
    const failed = events.find((e) => e.type === "tool_call.completed")
    expect(started?.payload).toMatchObject({
      toolCallId: "tool_2",
      toolName: "bash",
      rawInput: { cmd: "ls" },
    })
    expect(failed?.payload).toMatchObject({
      toolCallId: "tool_2",
      toolName: "bash",
      status: "failed",
      state: "failed",
    })
  })

  it("serializes input and projected event uploads in observation order", async () => {
    const { runtime } = buildRuntime({
      promptResult: {
        data: {
          info: { id: "msg_1", sessionID: "ses_bound", role: "assistant" },
          parts: [],
        },
      },
      emitDuringPrompt: async (subscription, sessionId) => {
        subscription.emit({
          type: "session.next.text.delta",
          sessionID: sessionId,
          payload: { textID: "txt_a", assistantMessageID: "msg_1", delta: "alpha " },
        })
        subscription.emit({
          type: "session.next.text.delta",
          sessionID: sessionId,
          payload: { textID: "txt_b", assistantMessageID: "msg_1", delta: "beta" },
        })
        await new Promise((resolve) => setImmediate(resolve))
      },
    })
    await ensureReady(runtime)
    const connection = makeFakeConnection()
    const order: string[] = []
    connection.setEventBehavior(async (body) => {
      const events = (body as { runtimeEvents: Array<{ type: string }> }).runtimeEvents
      const type = events[0]?.type ?? "?"
      order.push(type)
      await new Promise((resolve) => setImmediate(resolve))
    })
    const context = baseContext({ openCodeRuntime: runtime, serverConnection: connection.connection })
    await opencodeAction(context)

    expect(order).toEqual([
      "session.input",
      "message.delta",
      "message.delta",
      "session.closed",
    ])
  })

  it("suppresses activity and close reports when session.input upload is rejected", async () => {
    const { runtime } = buildRuntime({
      emitDuringPrompt: async (subscription, sessionId) => {
        subscription.emit({
          type: "message.part.updated",
          sessionID: sessionId,
          payload: { part: { id: "txt_a", messageID: "msg_1", type: "text", text: "alpha" } },
        })
        await new Promise((resolve) => setImmediate(resolve))
      },
    })
    await ensureReady(runtime)
    const connection = makeFakeConnection()
    connection.setEventRejection(new Set(["session.input"]))
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)
    const context = baseContext({ openCodeRuntime: runtime, serverConnection: connection.connection })

    try {
      const result = await opencodeAction(context)
      expect(result.error).toBeUndefined()
      const types = connection.eventCalls.map((call) => {
        const events = (call.body as { runtimeEvents: Array<{ type: string }> }).runtimeEvents
        return events[0]?.type ?? "?"
      })
      expect(types).toEqual(["session.input"])
    } finally {
      errorSpy.mockRestore()
    }
  })

  it("suppresses activity and close reports when the server returns no accepted session.input receipt", async () => {
    const { runtime } = buildRuntime({
      emitDuringPrompt: async (subscription, sessionId) => {
        subscription.emit({
          type: "message.part.updated",
          sessionID: sessionId,
          payload: { part: { id: "txt_a", messageID: "msg_1", type: "text", text: "alpha" } },
        })
        await new Promise((resolve) => setImmediate(resolve))
      },
    })
    await ensureReady(runtime)
    const connection = makeFakeConnection()
    connection.setInputAccepted(false)
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)

    try {
      const result = await opencodeAction(baseContext({ openCodeRuntime: runtime, serverConnection: connection.connection }))
      expect(result.error).toBeUndefined()
      expect(connection.eventCalls.map((call) => {
        const events = (call.body as { runtimeEvents: Array<{ type: string }> }).runtimeEvents
        return events[0]?.type ?? "?"
      })).toEqual(["session.input"])
      expect(errorSpy).toHaveBeenCalledWith(expect.stringContaining("type=session.input"))
    } finally {
      errorSpy.mockRestore()
    }
  })

  it("does not change a successful Action result when a projected event upload fails after input accepted", async () => {
    const { runtime } = buildRuntime({
      emitDuringPrompt: async (subscription, sessionId) => {
        subscription.emit({
          type: "message.part.updated",
          sessionID: sessionId,
          payload: { part: { id: "txt_a", messageID: "msg_1", type: "text", text: "alpha" } },
        })
        subscription.emit({
          type: "message.part.updated",
          sessionID: sessionId,
          payload: { part: { id: "txt_b", messageID: "msg_1", type: "text", text: "beta" } },
        })
        await new Promise((resolve) => setImmediate(resolve))
      },
    })
    await ensureReady(runtime)
    const connection = makeFakeConnection()
    connection.setEventRejection(new Set(["message.delta"]))
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)
    const context = baseContext({ openCodeRuntime: runtime, serverConnection: connection.connection })

    try {
      const result = await opencodeAction(context)
      expect(result.error).toBeUndefined()
      expect(result.turnFact?.finalAssistantText).toBe("final answer")
      const types = connection.eventCalls.map((call) => {
        const events = (call.body as { runtimeEvents: Array<{ type: string }> }).runtimeEvents
        return events[0]?.type ?? "?"
      })
      expect(types[0]).toBe("session.input")
      expect(types).toContain("message.delta")
      expect(errorSpy).toHaveBeenCalledWith(expect.stringContaining("workflow agent-session event upload failed"))
    } finally {
      errorSpy.mockRestore()
    }
  })

  it("does not replace a runtime failure when a projected event upload also fails", async () => {
    const { runtime } = buildRuntime({
      failPrompt: true,
      failPromptMessage: "opencode-runtime-explosion",
      emitDuringPrompt: async (subscription, sessionId) => {
        subscription.emit({
          type: "message.part.updated",
          sessionID: sessionId,
          payload: { part: { id: "txt_a", messageID: "msg_1", type: "text", text: "alpha" } },
        })
        await new Promise((resolve) => setImmediate(resolve))
      },
    })
    await ensureReady(runtime)
    const connection = makeFakeConnection()
    connection.setEventRejection(new Set(["session.input", "message.delta"]))
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)

    try {
      const context = baseContext({
        openCodeRuntime: runtime,
        serverConnection: connection.connection,
        with: { prompt: "opencode will fail", session: "plan" } as never,
      })

      const result = await opencodeAction(context)

      expect(result.error).toBeDefined()
      // The Action error must come from the OpenCode runtime, not
      // from the upload rejections.
      expect(result.error?.message).toBe("opencode-runtime-explosion")
      const diagnostics = (result as unknown as { output?: unknown }).output
      expect(diagnostics).toBeUndefined()
      const exitCode = (result as unknown as { exitCode?: number | null }).exitCode
      expect(exitCode).toBe(1)
    } finally {
      errorSpy.mockRestore()
    }
  })

  it("logs upload failures with workflow / work / session / event identity and never logs prompt or payload content", async () => {
    const { runtime } = buildRuntime({
      emitDuringPrompt: async (subscription, sessionId) => {
        subscription.emit({
          type: "message.part.updated",
          sessionID: sessionId,
          payload: { part: { id: "txt_a", messageID: "msg_1", type: "text", text: "PRIVATE-PROMPT-CONTENT" } },
        })
        await new Promise((resolve) => setImmediate(resolve))
      },
    })
    await ensureReady(runtime)
    const connection = makeFakeConnection()
    connection.setEventRejection(new Set(["message.delta"]))
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)
    const context = baseContext({
      openCodeRuntime: runtime,
      serverConnection: connection.connection,
      with: { prompt: "secret-prompt-text-should-not-leak", session: "plan" } as never,
    })

    try {
      await opencodeAction(context)
      const logged = errorSpy.mock.calls.map((call) => String(call[0])).join("\n")
      expect(logged).toMatch(/workflow=wf-1/)
      expect(logged).toMatch(/work=work-1/)
      expect(logged).toMatch(/session=plan/)
      expect(logged).toMatch(/type=message\.delta/)
      expect(logged).not.toContain("secret-prompt-text-should-not-leak")
      expect(logged).not.toContain("PRIVATE-PROMPT-CONTENT")
    } finally {
      errorSpy.mockRestore()
    }
  })

  it("two turns reusing one logical and physical session record two input + event sequences in order", async () => {
    const { runtime } = buildRuntime()
    await ensureReady(runtime)
    let boundId: string | null = null
    const eventCalls: unknown[] = []
    const connection = {
      async openWorkflowAgentSession(_projectId: string, _workflowRunId: string, _sessionName: string, _body: unknown) {
        return { runtimeSessionId: boundId ?? "ses_first", workDir: "/tmp/work" }
      },
      async attachWorkflowAgentSession(_projectId: string, _workflowRunId: string, _sessionName: string, body: unknown) {
        boundId = (body as { runtimeSessionId: string }).runtimeSessionId
      },
      async workflowAgentSessionRuntimeEvents(_projectId: string, _workflowRunId: string, _sessionName: string, body: unknown) {
        eventCalls.push(body)
        return (body as { runtimeEvents: Array<{ type: string }> }).runtimeEvents.map((event) => ({ type: event.type }))
      },
    } as unknown as ServerConnection

    const first = await opencodeAction(baseContext({
      openCodeRuntime: runtime,
      serverConnection: connection,
      workId: "work-a",
      with: { prompt: "first prompt", session: "plan" } as never,
    }))
    const second = await opencodeAction(baseContext({
      openCodeRuntime: runtime,
      serverConnection: connection,
      workId: "work-b",
      with: { prompt: "second prompt", session: "plan" } as never,
    }))

    expect(first.error).toBeUndefined()
    expect(second.error).toBeUndefined()
    const inputCalls = eventCalls.filter((entry) => {
      const events = (entry as { runtimeEvents: Array<{ type: string }> }).runtimeEvents
      return events[0]?.type === "session.input"
    })
    expect(inputCalls).toHaveLength(2)
    const firstInput = (inputCalls[0] as { runtimeEvents: Array<{ payload: Record<string, unknown> }> }).runtimeEvents[0]?.payload
    const secondInput = (inputCalls[1] as { runtimeEvents: Array<{ payload: Record<string, unknown> }> }).runtimeEvents[0]?.payload
    expect(firstInput?.text).toBe("first prompt")
    expect(secondInput?.text).toBe("second prompt")
  })

  it("does not wire a reporter when no serverConnection is provided", async () => {
    const { runtime } = buildRuntime({
      emitDuringPrompt: async (subscription, sessionId) => {
        subscription.emit({
          type: "message.part.updated",
          sessionID: sessionId,
          payload: { part: { id: "txt_a", messageID: "msg_1", type: "text", text: "alpha" } },
        })
        await new Promise((resolve) => setImmediate(resolve))
      },
    })
    await ensureReady(runtime)
    const context = baseContext({
      openCodeRuntime: runtime,
      with: { prompt: "no connection", session: "plan" } as never,
    })

    const result = await opencodeAction(context)
    expect(result.error).toBeUndefined()
  })

  it("the observer passes events to the reporter synchronously without awaiting", () => {
    const events: string[] = []
    const observer = {
      onEvent(event: { type: string }) {
        events.push(event.type)
      },
    }
    const observed = [
      { type: "message.delta", runtimeSessionId: "x", workDir: "/w", payload: {} },
      { type: "tool_call.started", runtimeSessionId: "x", workDir: "/w", payload: {} },
    ]
    for (const event of observed) observer.onEvent?.(event)
    expect(events).toEqual(["message.delta", "tool_call.started"])
  })
})

describe("WorkflowAgentSessionReporter — independent failure semantics", () => {
  function buildReporter(eventWriter: (body: unknown) => Promise<void> | void, options?: { timeoutMs?: number }) {
    const eventCalls: unknown[] = []
    const connection = {
      async workflowAgentSessionRuntimeEvents(_projectId: string, _workflowRunId: string, _sessionName: string, body: unknown) {
        eventCalls.push(body)
        await eventWriter(body)
        return (body as { runtimeEvents: Array<{ type: string }> }).runtimeEvents.map((event) => ({ type: event.type }))
      },
    } as unknown as ServerConnection
    const reporter = new WorkflowAgentSessionReporter({
      connection,
      projectId: "proj-1",
      workflowRunId: "wf-1",
      sessionName: "plan",
      workMetadata: { workId: "work-1", workType: "task", stage: "plan" },
      signal: new AbortController().signal,
      ...(options?.timeoutMs !== undefined ? { timeoutMs: options.timeoutMs } : {}),
    })
    return { reporter, eventCalls }
  }

  it("settles after all queued uploads, including rejected input", async () => {
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)
    try {
      const { reporter } = buildReporter(async () => { throw new Error("input rejected") })
      reporter.enqueueInput("p", "ses_1")
      await reporter.settle()
      expect(reporter.inputWasAccepted()).toBe(false)
    } finally {
      errorSpy.mockRestore()
    }
  })

  it("input rejection suppresses later close reports without skipping the runtime observation chain", async () => {
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)
    try {
      const { reporter, eventCalls } = buildReporter(async () => { throw new Error("input rejected") })
      reporter.enqueueInput("p", "ses_1")
      reporter.enqueueEvent({ type: "message.delta", runtimeSessionId: "ses_1", workDir: "/w", payload: { text: "x" } })
      reporter.enqueueClose({ status: "completed", exitCode: 0, runtimeSessionId: "ses_1" })
      await reporter.settle()
      const types = eventCalls.map((entry) => {
        const events = (entry as { runtimeEvents: Array<{ type: string }> }).runtimeEvents
        return events[0]?.type ?? "?"
      })
      expect(types).toEqual(["session.input"])
    } finally {
      errorSpy.mockRestore()
    }
  })

  it("continues after a later event rejection when input was accepted", async () => {
    const eventWrites: string[] = []
    const { reporter } = buildReporter(async (body) => {
      const events = (body as { runtimeEvents: Array<{ type: string }> }).runtimeEvents
      const type = events[0]?.type ?? "?"
      eventWrites.push(type)
      if (type === "message.delta") throw new Error("delta rejected")
    })
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)
    try {
      reporter.enqueueInput("p", "ses_1")
      reporter.enqueueEvent({ type: "message.delta", runtimeSessionId: "ses_1", workDir: "/w", payload: { text: "x" } })
      reporter.enqueueEvent({ type: "message.delta", runtimeSessionId: "ses_1", workDir: "/w", payload: { text: "y" } })
      await reporter.settle()
      expect(eventWrites).toEqual(["session.input", "message.delta", "message.delta"])
      expect(reporter.inputWasAccepted()).toBe(true)
    } finally {
      errorSpy.mockRestore()
    }
  })

  it("does not block settle when the reporter signal is already aborted", async () => {
    const controller = new AbortController()
    controller.abort()
    const connection = {
      async workflowAgentSessionRuntimeEvents(_projectId: string, _workflowRunId: string, _sessionName: string, body: unknown, signal: AbortSignal) {
        expect(signal.aborted).toBe(false)
        void body
        return [{ type: "session.input" }]
      },
    } as unknown as ServerConnection
    const reporter = new WorkflowAgentSessionReporter({
      connection,
      projectId: "proj-1",
      workflowRunId: "wf-1",
      sessionName: "plan",
      workMetadata: { workId: "work-1", workType: "task", stage: "plan" },
      signal: controller.signal,
    })
    reporter.enqueueInput("p", "ses_1")
    await reporter.settle()
    expect(reporter.inputWasAccepted()).toBe(true)
  })
})
