import { afterEach, describe, expect, it, vi } from "vitest"
import { opencodeAction } from "../src/actions/opencode.js"
import { OpenCodeRuntime } from "../src/runtime/opencode/index.js"
import type { OpenCodeRuntimeDeps } from "../src/runtime/opencode/runtime.js"
import type { OpencodeServerHandle } from "../src/runtime/opencode/server-process.js"
import type { RuntimeEventSubscription, RuntimeGlobalEvent } from "../src/runtime/opencode/event-subscription.js"
import type { OpencodeClient } from "@opencode-ai/sdk/v2"
import type { ActionContext } from "../src/core/types.js"
import { WorkflowAgentSessionReporter } from "../src/actions/workflow-agent-session-reporter.js"
import type { AgentSessionRuntimeEventOutbox, RuntimeEventRecord } from "../src/server/runtime-event-outbox.js"
import { clearOpenCodeRuntimeFactoryForTest } from "./support/opencode-runtime-factory.js"
import { setPromptLoaderRegistryForTest } from "../src/core/prompt.js"
import { makeRecordingOutbox, type OutboxHandles } from "./support/outbox-test-helpers.js"

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
  failCreate?: boolean
  failPrompt?: boolean
  failPromptMessage?: string
  emitDuringPrompt?: (subscription: FakeSubscription, sessionId: string) => Promise<void> | void
}

interface BuildResult {
  deps: OpenCodeRuntimeDeps
  runtime: OpenCodeRuntime
  client: {
    sessionCreate: ReturnType<typeof vi.fn>
    sessionPrompt: ReturnType<typeof vi.fn>
    sessionPromptAsync: ReturnType<typeof vi.fn>
    sessionAbort: ReturnType<typeof vi.fn>
    sessionGet: ReturnType<typeof vi.fn>
    sessionStatus: ReturnType<typeof vi.fn>
  }
  subscription: FakeSubscription
}

function buildRuntime(args: BuildArgs = {}): BuildResult {
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
          modelID: "gpt-5.6",
          tokens: { input: 0, output: 0, total: 0, reasoning: 0, cache: { read: 0, write: 0 } },
          cost: 0,
        },
        parts: [],
      },
    }
  })
  const sessionPromptAsync = vi.fn(async () => undefined)
  const sessionAbort = vi.fn(async () => undefined)
  const sessionGet = vi.fn(async () => ({ data: { id: "ses_bound" } }))
  const sessionStatus = vi.fn(async () => ({ data: {} }))
  const client: OpencodeClient = {
    global: { health: vi.fn(async () => ({ data: { ok: true } })), event: vi.fn() },
    session: {
      create: sessionCreate,
      prompt: sessionPrompt,
      promptAsync: sessionPromptAsync,
      abort: sessionAbort,
      get: sessionGet,
      status: sessionStatus,
    },
  } as unknown as OpencodeClient
  const serverHandle: OpencodeServerHandle = {
    url: "http://fake",
    directory: "/tmp/work",
    client,
    async close() {
      subscription.closed = true
    },
  }
  const deps: OpenCodeRuntimeDeps = {
    directory: "/tmp/work",
    serverFactory: async () => serverHandle,
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

function makeFailureOutbox(): AgentSessionRuntimeEventOutbox {
  return {
    ready: () => true,
    load: async () => {},
    async enqueueBeforeExecution() { throw new Error("disk full (input)") },
    async enqueueProducedFact() { throw new Error("disk full (produced)") },
    kick: async () => {},
    stop: async () => {},
    snapshot() { return [] },
  }
}

function makeProducedFactFailureOutbox(): AgentSessionRuntimeEventOutbox {
  return {
    ready: () => true,
    load: async () => {},
    async enqueueBeforeExecution() {},
    async enqueueProducedFact() { throw new Error("disk full (produced)") },
    kick: async () => {},
    stop: async () => {},
    snapshot() { return [] },
  }
}

describe("opencodeAction — Workflow AgentSession transcript reporting", () => {
  it("enqueues the composed prompt as session.input and forwards projected events in production order", async () => {
    const { runtime } = buildRuntime({
      emitDuringPrompt: emitStandardSequence,
    })
    await ensureReady(runtime)
    const handles = makeRecordingOutbox()
    const context = baseContext({
      openCodeRuntime: runtime,
      serverConnection: handles.connection,
      agentSessionRuntimeEventOutbox: handles.outbox,
    })

    const result = await opencodeAction(context)

    expect(result.error).toBeUndefined()
    const types = handles.eventTypeList()
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
    expect(handles.eventsByType("session.input")[0]).toMatchObject({
      event: {
        type: "session.input",
        payload: expect.objectContaining({
          text: "do the work",
          kind: "task",
          source: "workflow",
          role: "user",
          runtimeSessionId: "ses_bound",
        }),
      },
    })
    const closeEvent = handles.eventsByType("session.closed")[0]
    expect(closeEvent?.event.payload).toMatchObject({ status: "completed", exitCode: 0 })
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
    const handles = makeRecordingOutbox()
    const context = baseContext({
      openCodeRuntime: runtime,
      serverConnection: handles.connection,
      agentSessionRuntimeEventOutbox: handles.outbox,
    })
    await opencodeAction(context)

    const started = handles.eventsByType("tool_call.started")[0]
    const failed = handles.eventsByType("tool_call.completed")[0]
    expect(started?.event.payload).toMatchObject({
      toolCallId: "tool_2",
      toolName: "bash",
      rawInput: { cmd: "ls" },
    })
    expect(failed?.event.payload).toMatchObject({
      toolCallId: "tool_2",
      toolName: "bash",
      status: "failed",
      state: "failed",
    })
  })

  it("serializes input and projected event enqueues in observation order", async () => {
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
    const handles = makeRecordingOutbox()
    const context = baseContext({
      openCodeRuntime: runtime,
      serverConnection: handles.connection,
      agentSessionRuntimeEventOutbox: handles.outbox,
    })
    await opencodeAction(context)

    expect(handles.eventTypeList()).toEqual([
      "session.input",
      "message.delta",
      "message.delta",
      "session.closed",
    ])
  })

  it("rejected input persistence returns execution-unavailable and never invokes OpenCodeRuntime.runTurn", async () => {
    const { runtime, client } = buildRuntime({
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
    const outbox = makeFailureOutbox()
    const handles = makeRecordingOutbox()
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)
    const context = baseContext({
      openCodeRuntime: runtime,
      serverConnection: handles.connection,
      agentSessionRuntimeEventOutbox: outbox,
    })

    try {
      const result = await opencodeAction(context)
      expect(result.error?.code).toBe("execution-unavailable")
      expect(client.sessionPrompt).not.toHaveBeenCalled()
    } finally {
      errorSpy.mockRestore()
    }
  })

  it("does not replace a successful Action result when a projected event enqueue fails after input accepted", async () => {
    const { runtime } = buildRuntime({
      emitDuringPrompt: async (subscription, sessionId) => {
        subscription.emit({
          type: "session.next.text.delta",
          sessionID: sessionId,
          payload: { textID: "txt_a", assistantMessageID: "msg_1", delta: "alpha" },
        })
        await new Promise((resolve) => setImmediate(resolve))
      },
    })
    await ensureReady(runtime)
    let firstCall = true
    const outbox: AgentSessionRuntimeEventOutbox = {
      ready: () => true,
      load: async () => {},
      async enqueueBeforeExecution(record) {
        handles.records.push(record as RuntimeEventRecord)
      },
      async enqueueProducedFact() {
        if (firstCall) {
          firstCall = false
          throw new Error("disk full (produced)")
        }
      },
      kick: async () => {},
      stop: async () => {},
      snapshot() { return [] },
    }
    const handles = makeRecordingOutbox()
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)
    const context = baseContext({
      openCodeRuntime: runtime,
      serverConnection: handles.connection,
      agentSessionRuntimeEventOutbox: outbox,
    })
    try {
      const result = await opencodeAction(context)
      expect(result.error).toBeUndefined()
    } finally {
      errorSpy.mockRestore()
    }
  })

  it("does not replace a runtime failure when produced-fact enqueue also fails", async () => {
    const { runtime } = buildRuntime({
      failPrompt: true,
      failPromptMessage: "opencode crashed",
      emitDuringPrompt: async (subscription, sessionId) => {
        subscription.emit({
          type: "session.next.text.delta",
          sessionID: sessionId,
          payload: { textID: "txt_a", assistantMessageID: "msg_1", delta: "alpha" },
        })
        await new Promise((resolve) => setImmediate(resolve))
      },
    })
    await ensureReady(runtime)
    const outbox = makeProducedFactFailureOutbox()
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)
    const handles = makeRecordingOutbox()
    const context = baseContext({
      openCodeRuntime: runtime,
      serverConnection: handles.connection,
      agentSessionRuntimeEventOutbox: outbox,
    })
    try {
      const result = await opencodeAction(context)
      expect(result.error?.code).toBe("turn-failed")
      expect(result.error?.message).toBe("opencode crashed")
    } finally {
      errorSpy.mockRestore()
    }
  })

  it("does not wire a reporter when no outbox is provided", async () => {
    const { runtime } = buildRuntime()
    await ensureReady(runtime)
    const handles = makeRecordingOutbox()
    const context = baseContext({
      openCodeRuntime: runtime,
      serverConnection: handles.connection,
    })
    const result = await opencodeAction(context)
    expect(result.error).toBeUndefined()
    expect(handles.records).toHaveLength(0)
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

describe("WorkflowAgentSessionReporter — outbox-driven failure semantics", () => {
  function buildReporter(failEnqueueBeforeExecution = false, failEnqueueProducedFact = false) {
    const records: RuntimeEventRecord[] = []
    const beforeExecutionCalls: RuntimeEventRecord[] = []
    const producedFactCalls: RuntimeEventRecord[] = []
    const outbox: AgentSessionRuntimeEventOutbox = {
      ready: () => true,
      load: async () => {},
      async enqueueBeforeExecution(record) {
        beforeExecutionCalls.push(record as RuntimeEventRecord)
        if (failEnqueueBeforeExecution) throw new Error("input snapshot failed")
        records.push(record as RuntimeEventRecord)
      },
      async enqueueProducedFact(record) {
        producedFactCalls.push(record as RuntimeEventRecord)
        if (failEnqueueProducedFact) throw new Error("produced-fact snapshot failed")
        records.push(record as RuntimeEventRecord)
      },
      kick: async () => {},
      stop: async () => {},
      snapshot() {
        return records
      },
    }
    const reporter = new WorkflowAgentSessionReporter({
      outbox,
      projectId: "proj-1",
      workflowRunId: "wf-1",
      sessionName: "plan",
      workMetadata: { workId: "work-1", workType: "task", stage: "plan" },
      randomId: (() => {
        let counter = 0
        return () => `id_${++counter}`
      })(),
    })
    return { reporter, outbox, records, beforeExecutionCalls, producedFactCalls }
  }

  it("settles after all queued produced-fact enqueues, including a rejected input", async () => {
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)
    try {
      const { reporter } = buildReporter(true)
      await expect(reporter.awaitInput("p", "ses_1")).rejects.toThrow(/input snapshot failed/)
      expect(reporter.inputWasAccepted()).toBe(false)
      expect(reporter.inputWasRejected()).toBe(true)
    } finally {
      errorSpy.mockRestore()
    }
  })

  it("rejected input suppresses later close reports", async () => {
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)
    try {
      const { reporter, producedFactCalls } = buildReporter(true)
      await expect(reporter.awaitInput("p", "ses_1")).rejects.toThrow()
      reporter.registerEvent({ type: "message.delta", runtimeSessionId: "ses_1", workDir: "/w", payload: { text: "x" } })
      reporter.registerClose({ status: "completed", exitCode: 0, runtimeSessionId: "ses_1" })
      await reporter.settle()
      expect(producedFactCalls).toHaveLength(0)
    } finally {
      errorSpy.mockRestore()
    }
  })

  it("continues after a later produced-fact rejection when input was accepted", async () => {
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)
    try {
      const { reporter, producedFactCalls } = buildReporter(false, true)
      await reporter.awaitInput("p", "ses_1")
      reporter.registerEvent({ type: "message.delta", runtimeSessionId: "ses_1", workDir: "/w", payload: { text: "x" } })
      reporter.registerEvent({ type: "message.delta", runtimeSessionId: "ses_1", workDir: "/w", payload: { text: "y" } })
      await expect(reporter.settle()).resolves.toBeUndefined()
      expect(producedFactCalls.map((r) => r.event.type)).toEqual(["message.delta", "message.delta"])
      expect(reporter.inputWasAccepted()).toBe(true)
    } finally {
      errorSpy.mockRestore()
    }
  })
})