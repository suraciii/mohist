import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import {
  buildClient,
  buildRecordingOutbox,
  emitFollowup,
  flush,
  invokeFollowup,
  lastBuilder,
  makeFakeRuntime,
  resetBuilders,
  type FakeRuntimeHandles,
  type RecordingOutbox,
} from "./support/followup-handler-fixture.js"
import type { AgentSessionRuntimeEventOutbox } from "../src/server/runtime-event-outbox.js"

let runtime: FakeRuntimeHandles
let recording: RecordingOutbox

beforeEach(() => {
  resetBuilders()
  runtime = makeFakeRuntime()
  recording = buildRecordingOutbox()
})

afterEach(() => {
  vi.restoreAllMocks()
})

describe("RunnerSignalRClient ReceiveFollowup handler", () => {
  it("Followup_FireAndForgetPromptCallsRuntimeFollowupWithoutAwait", async () => {
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))

    buildClient({ resolver, outbox: recording.outbox, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    emitFollowup(builder, { workflowRunId: "wr-1", sessionName: "work-1", text: "add a logout button" })
    await flush()

    expect(runtime.followupCalls).toHaveLength(1)
    expect(runtime.followupCalls[0]).toEqual({
      target: { runtime: "opencode", runtimeSessionId: "runtime-1", workDir: "/work/project" },
      prompt: "add a logout button",
    })
  })

  it("Followup_AcknowledgesDeliveryBeforeRuntimeCompletion", async () => {
    let resolveFollowup!: (value: { ok: true; value: { facts: { runtimeSessionId: string; workDir: string }; diagnostics: readonly never[] }; diagnostics: readonly never[] }) => void
    const promise = new Promise<{ ok: true; value: { facts: { runtimeSessionId: string; workDir: string }; diagnostics: readonly never[] }; diagnostics: readonly never[] }>((resolve) => { resolveFollowup = resolve })
    const followupCalls = runtime.followupCalls
    runtime.runtime.followup = async () => {
      followupCalls.push({
        target: { runtime: "opencode", runtimeSessionId: "runtime-1", workDir: "/work/project" },
        prompt: "ship it",
      })
      return await promise
    }
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))

    buildClient({ resolver, outbox: recording.outbox, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    const delivery = invokeFollowup(builder, { workflowRunId: "wr-1", sessionName: "work-1", text: "ship it" })
    await flush()
    expect(followupCalls).toHaveLength(1)
    await expect(delivery).resolves.toEqual({ accepted: true })
    resolveFollowup({
      ok: true,
      value: { facts: { runtimeSessionId: "runtime-1", workDir: "/work/project" }, diagnostics: [] },
      diagnostics: [],
    })
    await flush()
  })

  it("Followup_AwaitsDurableInputEnqueue_BeforeInvokingRuntime", async () => {
    const order: string[] = []
    runtime.runtime.followup = async () => {
      order.push("followup")
      return { ok: true, value: { facts: { runtimeSessionId: "runtime-1", workDir: "/work/project" }, diagnostics: [] }, diagnostics: [] }
    }
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
    const outbox: AgentSessionRuntimeEventOutbox = {
      ready: () => true,
      load: async () => {},
      recover: async () => {},
      async enqueueBeforeExecution(record) {
        order.push(`before:${record.event.type}`)
      },
      enqueueProducedFact: async () => {},
      enqueueProducedFactBatch: async () => {},
      kick: async () => {},
      stop: async () => {},
      snapshot() { return [] },
    }

    buildClient({ resolver, outbox, openCodeRuntime: runtime.runtime })
    emitFollowup(lastBuilder(), { workflowRunId: "wr-1", sessionName: "work-1", text: "ship while enqueue settles" })
    await flush()

    expect(order).toEqual(["before:session.input", "followup"])
  })

  it("Followup_TagsInputWithKindFollowup", async () => {
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
    buildClient({ resolver, outbox: recording.outbox, openCodeRuntime: runtime.runtime })

    emitFollowup(lastBuilder(), { workflowRunId: "wr-1", sessionName: "work-1", text: "tag me" })
    await flush()

    expect(recording.beforeExecutionCalls).toHaveLength(1)
    expect(recording.beforeExecutionCalls[0]).toMatchObject({
      producerFamily: "workflow-session",
      acknowledgementPolicy: "matching-receipt",
      event: {
        type: "session.input",
        payload: expect.objectContaining({
          kind: "followup",
          text: "tag me",
          role: "user",
          runtimeSessionId: "runtime-1",
          source: "followup",
        }),
      },
    })
  })

  it("Followup_DropsWhenResolverReturnsNullAndDoesNotThrow", async () => {
    const resolver = vi.fn(() => null)

    buildClient({ resolver, outbox: recording.outbox, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    expect(() => emitFollowup(builder, { workflowRunId: "wr-1", sessionName: "work-1", text: "ignored" })).not.toThrow()
    await flush()

    expect(runtime.followupCalls).toHaveLength(0)
    expect(recording.beforeExecutionCalls).toHaveLength(0)
  })

  it("Followup_ReturnsMissingWhenTheRuntimeSessionCannotBeResolved", async () => {
    const resolver = vi.fn(() => null)
    buildClient({ resolver, outbox: recording.outbox, openCodeRuntime: runtime.runtime })

    await expect(invokeFollowup(lastBuilder(), {
      workflowRunId: "wr-1", sessionName: "work-1", text: "resume",
    })).resolves.toEqual({ accepted: false, error: "missing" })
  })

  it("Followup_ReturnsUnavailableWhileRuntimeIsInitializing", async () => {
    runtime.setReady(false)
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
    buildClient({ resolver, outbox: recording.outbox, openCodeRuntime: runtime.runtime })

    await expect(invokeFollowup(lastBuilder(), {
      workflowRunId: "wr-1", sessionName: "work-1", text: "resume",
    })).resolves.toEqual({ accepted: false, error: "unavailable" })
    expect(runtime.followupCalls).toHaveLength(0)
    expect(recording.beforeExecutionCalls).toHaveLength(0)
  })

  it("Followup_ReturnsUnavailableWhenOutboxUnhealthy", async () => {
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
    const outbox: AgentSessionRuntimeEventOutbox = {
      ready: () => false,
      load: async () => {},
      recover: async () => {},
      enqueueBeforeExecution: async () => {},
      enqueueProducedFact: async () => {},
      enqueueProducedFactBatch: async () => {},
      kick: async () => {},
      stop: async () => {},
      snapshot() { return [] },
    }

    buildClient({ resolver, outbox, openCodeRuntime: runtime.runtime })

    await expect(invokeFollowup(lastBuilder(), {
      workflowRunId: "wr-1", sessionName: "work-1", text: "resume",
    })).resolves.toEqual({ accepted: false, error: "unavailable" })
    expect(runtime.followupCalls).toHaveLength(0)
  })

  it("Followup_ReturnsUnavailableWhenLocalEnqueueFails_BeforeInvokingRuntime", async () => {
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
    const outbox: AgentSessionRuntimeEventOutbox = {
      ready: () => true,
      load: async () => {},
      recover: async () => {},
      async enqueueBeforeExecution() { throw new Error("disk full") },
      enqueueProducedFact: async () => {},
      enqueueProducedFactBatch: async () => {},
      kick: async () => {},
      stop: async () => {},
      snapshot() { return [] },
    }
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)

    buildClient({ resolver, outbox, openCodeRuntime: runtime.runtime })

    await expect(invokeFollowup(lastBuilder(), {
      workflowRunId: "wr-1", sessionName: "work-1", text: "resume",
    })).resolves.toEqual({ accepted: false, error: "unavailable" })
    expect(runtime.followupCalls).toHaveLength(0)
    expect(errorSpy).toHaveBeenCalledWith(
      "followup durable input enqueue failed:",
      expect.stringContaining("disk full"),
    )
    errorSpy.mockRestore()
  })

  it("Followup_DropsWhenResolverThrows", async () => {
    const resolver = vi.fn(() => { throw new Error("resolver boom") })
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)

    buildClient({ resolver, outbox: recording.outbox, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    expect(() => emitFollowup(builder, { workflowRunId: "wr-1", sessionName: "work-1", text: "ignored" })).not.toThrow()
    await flush()

    expect(runtime.followupCalls).toHaveLength(0)
    expect(recording.beforeExecutionCalls).toHaveLength(0)
    expect(errorSpy).toHaveBeenCalled()
    errorSpy.mockRestore()
  })

  it("Followup_Completion_RecordsFollowupCompletedTerminal", async () => {
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
    buildClient({ resolver, outbox: recording.outbox, openCodeRuntime: runtime.runtime })

    emitFollowup(lastBuilder(), {
      target: { kind: "generic", projectId: "proj-1", sessionId: "session-1" },
      text: "continue",
      operationId: "followup-1",
    })
    await flush()

    expect(recording.producedFactCalls.find((r) => r.event.type === "session.followup_completed")).toMatchObject({
      acknowledgementPolicy: "successful-response",
      target: { kind: "generic", projectId: "proj-1", sessionId: "session-1" },
      event: {
        type: "session.followup_completed",
        payload: expect.objectContaining({
          status: "completed",
          operationId: "followup-1",
          source: "followup",
        }),
      },
    })
  })

  it("Followup_Failure_RecordsFollowupFailedTerminal", async () => {
    runtime.setFollowupResult({
      ok: false,
      error: {
        kind: "turn-failed",
        message: "opencode crashed",
        diagnostics: [{ severity: "error", code: "turn-failed", message: "opencode crashed" }],
      },
      diagnostics: [{ severity: "error", code: "turn-failed", message: "opencode crashed" }],
    })
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)
    try {
      buildClient({ resolver, outbox: recording.outbox, openCodeRuntime: runtime.runtime })

      emitFollowup(lastBuilder(), {
        target: { kind: "generic", projectId: "proj-1", sessionId: "session-1" },
        text: "continue",
        operationId: "followup-1",
      })
      await flush()
      await flush()

      const terminal = recording.producedFactCalls.find((r) => r.event.type === "session.followup_failed")
      expect(terminal).toMatchObject({
        acknowledgementPolicy: "successful-response",
        event: {
          type: "session.followup_failed",
          payload: expect.objectContaining({
            status: "failed",
            failureReason: "opencode crashed",
            operationId: "followup-1",
          }),
        },
      })
    } finally {
      errorSpy.mockRestore()
    }
  })

  it("Followup_DropsPayloadWhenTextIsMissing", async () => {
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
    buildClient({ resolver, outbox: recording.outbox, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    emitFollowup(builder, { workflowRunId: "wr-1", sessionName: "work-1", text: "" })
    await flush()

    expect(runtime.followupCalls).toHaveLength(0)
    expect(recording.beforeExecutionCalls).toHaveLength(0)
  })

  it("Followup_DropsPayloadWhenResolverIsNull", async () => {
    buildClient({ resolver: null, outbox: recording.outbox, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    emitFollowup(builder, { workflowRunId: "wr-1", sessionName: "work-1", text: "noop" })
    await flush()

    expect(runtime.followupCalls).toHaveLength(0)
  })

  it("Followup_DropsPayloadWhenOutboxIsNull", async () => {
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
    buildClient({ resolver, outbox: null, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    emitFollowup(builder, { workflowRunId: "wr-1", sessionName: "work-1", text: "noop" })
    await flush()

    expect(runtime.followupCalls).toHaveLength(0)
  })

  it("Followup_DropsPayloadWhenRuntimeIsNull", async () => {
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
    buildClient({ resolver, outbox: recording.outbox, openCodeRuntime: null })
    const builder = lastBuilder()

    emitFollowup(builder, { workflowRunId: "wr-1", sessionName: "work-1", text: "noop" })
    await flush()

    expect(recording.beforeExecutionCalls).toHaveLength(0)
  })

  it("Followup_DropsNullOrUndefinedPayload", async () => {
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
    buildClient({ resolver, outbox: recording.outbox, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    emitFollowup(builder, null)
    emitFollowup(builder, undefined)
    await flush()

    expect(runtime.followupCalls).toHaveLength(0)
    expect(recording.beforeExecutionCalls).toHaveLength(0)
  })

  it("Followup_InvokesRuntimeOnce_OnAcceptedDeliveryEvenIfOutboxLaterDrains", async () => {
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
    buildClient({ resolver, outbox: recording.outbox, openCodeRuntime: runtime.runtime })

    emitFollowup(lastBuilder(), { workflowRunId: "wr-1", sessionName: "work-1", text: "go" })
    await flush()
    await recording.flush()
    await flush()

    expect(runtime.followupCalls).toHaveLength(1)
  })
})
