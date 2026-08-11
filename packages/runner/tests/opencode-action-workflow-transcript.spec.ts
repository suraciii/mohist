import { afterEach, describe, expect, it, vi } from "vitest"
import { opencodeAction } from "../src/actions/opencode.js"
import { OpenCodeRuntime } from "../src/runtime/opencode/index.js"
import type { OpenCodeRuntimeDeps } from "../src/runtime/opencode/runtime.js"
import type { OpencodeServerHandle } from "../src/runtime/opencode/server-process.js"
import type { RuntimeEventSubscription, RuntimeGlobalEvent } from "../src/runtime/opencode/event-subscription.js"
import type { OpencodeClient } from "@opencode-ai/sdk/v2"
import type { ActionTestContext as ActionContext } from "./support/action-test-context.js"
import { WorkflowAgentSessionReporter } from "../src/actions/workflow-agent-session-reporter.js"
import type { AgentSessionRuntimeEventOutbox, RuntimeEventRecord } from "../src/server/runtime-event-outbox.js"
import { makeRecordingOutbox, type OutboxHandles } from "./support/outbox-test-helpers.js"
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
    recover: async () => {},
    async enqueueBeforeExecution() { throw new Error("disk full (input)") },
    async enqueueProducedFact() { throw new Error("disk full (produced)") },
    async enqueueProducedFactBatch() { throw new Error("disk full (produced)") },
    kick: async () => {},
    stop: async () => {},
    snapshot() { return [] },
  }
}

function makeProducedFactFailureOutbox(): AgentSessionRuntimeEventOutbox {
  return {
    ready: () => true,
    load: async () => {},
    recover: async () => {},
    async enqueueBeforeExecution() {},
    async awaitInputReceipt(recordId) { return { type: "session.input", inputDeliveryId: recordId, agentTurnId: `turn-${recordId}` } },
    async enqueueProducedFact() { throw new Error("disk full (produced)") },
    async enqueueProducedFactBatch() { throw new Error("disk full (produced)") },
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

    const result = await callAction(opencodeAction, context)

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
    const closeEvent = handles.eventsByType("session.activity")[0]
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
    await callAction(opencodeAction, context)

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
    expect(handles.eventsByType("turn.failed")).toHaveLength(0)
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
    await callAction(opencodeAction, context)

    expect(handles.eventTypeList()).toEqual([
      "session.input",
      "message.delta",
      "message.delta",
      "session.activity",
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
      const result = await callAction(opencodeAction, context)
      expect(result.error?.code).toBe("execution-unavailable")
      expect(client.sessionPrompt).not.toHaveBeenCalled()
    } finally {
      errorSpy.mockRestore()
    }
  })

  it("rejected Server input receipt returns execution-unavailable and never invokes OpenCodeRuntime.runTurn", async () => {
    const { runtime, client } = buildRuntime()
    await ensureReady(runtime)
    const handles = makeRecordingOutbox()
    handles.setInputAccepted(false)
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)
    const context = baseContext({
      openCodeRuntime: runtime,
      serverConnection: handles.connection,
      agentSessionRuntimeEventOutbox: handles.outbox,
    })

    try {
      const result = await callAction(opencodeAction, context)
      expect(result.error?.code).toBe("execution-unavailable")
      expect(client.sessionPrompt).not.toHaveBeenCalled()
      expect(handles.eventsByType("session.input")).toHaveLength(1)
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
      recover: async () => {},
      async enqueueBeforeExecution(record) {
        handles.records.push(record as RuntimeEventRecord)
      },
      async awaitInputReceipt(recordId) { return { type: "session.input", inputDeliveryId: recordId, agentTurnId: `turn-${recordId}` } },
      async enqueueProducedFact() {
        if (firstCall) {
          firstCall = false
          throw new Error("disk full (produced)")
        }
      },
      async enqueueProducedFactBatch() {
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
      const result = await callAction(opencodeAction, context)
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
      const result = await callAction(opencodeAction, context)
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
    const result = await callAction(opencodeAction, context)
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
    const producedFactSingleCalls: RuntimeEventRecord[] = []
    const producedFactBatchCalls: RuntimeEventRecord[][] = []
    const outbox: AgentSessionRuntimeEventOutbox = {
      ready: () => true,
      load: async () => {},
      recover: async () => {},
      async enqueueBeforeExecution(record) {
        beforeExecutionCalls.push(record as RuntimeEventRecord)
        if (failEnqueueBeforeExecution) throw new Error("input snapshot failed")
        records.push(record as RuntimeEventRecord)
      },
      async awaitInputReceipt(recordId) {
        return { type: "session.input", inputDeliveryId: recordId, agentTurnId: `turn-${recordId}` }
      },
      async enqueueProducedFact(record) {
        producedFactSingleCalls.push(record as RuntimeEventRecord)
        producedFactCalls.push(record as RuntimeEventRecord)
        if (failEnqueueProducedFact) throw new Error("produced-fact snapshot failed")
        records.push(record as RuntimeEventRecord)
      },
      async enqueueProducedFactBatch(batch) {
        producedFactBatchCalls.push([...batch] as RuntimeEventRecord[])
        for (const record of batch) {
          producedFactCalls.push(record as RuntimeEventRecord)
        }
        if (failEnqueueProducedFact) throw new Error("produced-fact snapshot failed")
        for (const record of batch) {
          records.push(record as RuntimeEventRecord)
        }
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
      workMetadata: { workId: "work-1", taskRunId: "task-1.1", workType: "task", stage: "plan" },
      runtime: "opencode",
      randomId: (() => {
        let counter = 0
        return () => `id_${++counter}`
      })(),
    })
    return { reporter, outbox, records, beforeExecutionCalls, producedFactCalls, producedFactSingleCalls, producedFactBatchCalls }
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

  it("stamps every later runtime event with the acknowledged Workflow turn", async () => {
    const { reporter, beforeExecutionCalls, producedFactCalls } = buildReporter()

    await reporter.awaitInput("p", "ses_1")
    reporter.registerEvent({ type: "tool_call.started", runtimeSessionId: "ses_1", workDir: "/w", payload: { tool: "test" } })
    await reporter.settle()

    const input = beforeExecutionCalls[0]
    const fact = producedFactCalls[0]
    if (!input || !fact) throw new Error("expected input and produced runtime event")
    expect(input.event.type).toBe("session.input")
    expect(fact.event.type).toBe("tool_call.started")
    expect(input.work).toMatchObject({
      taskRunId: "task-1.1",
      inputDeliveryId: input.id,
      agentTurnId: null,
    })
    expect(fact.work).toMatchObject({
      taskRunId: "task-1.1",
      inputDeliveryId: input.id,
      agentTurnId: `turn-${input.id}`,
    })
    expect(fact.event.payload).toMatchObject({ turnId: `turn-${input.id}` })
  })

  it("buffers streaming deltas and flushes them as one batch before the close fact", async () => {
    const { reporter, producedFactCalls, producedFactSingleCalls, producedFactBatchCalls } = buildReporter()
    await reporter.awaitInput("p", "ses_1")
    reporter.registerEvent({ type: "reasoning.delta", runtimeSessionId: "ses_1", workDir: "/w", payload: { text: "a" } })
    reporter.registerEvent({ type: "reasoning.delta", runtimeSessionId: "ses_1", workDir: "/w", payload: { text: "b" } })
    reporter.registerEvent({ type: "reasoning.delta", runtimeSessionId: "ses_1", workDir: "/w", payload: { text: "c" } })
    reporter.registerClose({ status: "completed", exitCode: 0, runtimeSessionId: "ses_1" })
    await reporter.settle()

    expect(producedFactSingleCalls).toHaveLength(0)
    expect(producedFactBatchCalls.map((batch) => batch.map((record) => record.event.type))).toEqual([
      ["reasoning.delta", "reasoning.delta", "reasoning.delta"],
      ["session.activity"],
    ])
    expect(producedFactCalls.map((r) => r.event.type)).toEqual([
      "reasoning.delta",
      "reasoning.delta",
      "reasoning.delta",
      "session.activity",
    ])
  })

  it("flushes buffered deltas when a non-delta event arrives mid-turn", async () => {
    const { reporter, producedFactCalls, producedFactSingleCalls, producedFactBatchCalls } = buildReporter()
    await reporter.awaitInput("p", "ses_1")
    reporter.registerEvent({ type: "reasoning.delta", runtimeSessionId: "ses_1", workDir: "/w", payload: { text: "a" } })
    reporter.registerEvent({ type: "tool_call.started", runtimeSessionId: "ses_1", workDir: "/w", payload: {} })
    reporter.registerEvent({ type: "reasoning.delta", runtimeSessionId: "ses_1", workDir: "/w", payload: { text: "b" } })
    reporter.registerClose({ status: "completed", exitCode: 0, runtimeSessionId: "ses_1" })
    await reporter.settle()

    expect(producedFactSingleCalls.map((record) => record.event.type)).toEqual(["tool_call.started"])
    expect(producedFactBatchCalls.map((batch) => batch.map((record) => record.event.type))).toEqual([
      ["reasoning.delta"],
      ["reasoning.delta"],
      ["session.activity"],
    ])
    expect(producedFactCalls.map((r) => r.event.type)).toEqual([
      "reasoning.delta",
      "tool_call.started",
      "reasoning.delta",
      "session.activity",
    ])
  })

  it("bounds streaming delta batches before a turn ends", async () => {
    const { reporter, producedFactSingleCalls, producedFactBatchCalls } = buildReporter()
    await reporter.awaitInput("p", "ses_1")
    for (let index = 0; index < 300; index += 1) {
      reporter.registerEvent({ type: "message.delta", runtimeSessionId: "ses_1", workDir: "/w", payload: { text: String(index) } })
    }
    reporter.registerClose({ status: "completed", exitCode: 0, runtimeSessionId: "ses_1" })
    await reporter.settle()

    expect(producedFactSingleCalls).toHaveLength(0)
    expect(producedFactBatchCalls.map((batch) => batch.length)).toEqual([256, 44, 1])
    expect(producedFactBatchCalls.at(-1)?.[0].event.type).toBe("session.activity")
  })
})
