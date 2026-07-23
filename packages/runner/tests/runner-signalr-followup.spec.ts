import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import {
  buildClient,
  buildRecordingOutbox,
  defaultPiBinding,
  emitFollowup,
  flush,
  invokeFollowup,
  lastBuilder,
  makeFakeRuntime,
  resetBuilders,
  workflowPayload,
  type FakeRuntimeHandles,
  type RecordingOutbox,
} from "./support/followup-handler-fixture.js"
import type { AgentSessionRuntimeEventOutbox } from "../src/server/runtime-event-outbox.js"
import type { FollowupOperationJournalStore } from "../src/runtime/followup-operation-journal.js"
import type { FollowupOperationClaim, FollowupOperationState } from "../src/runtime/followup-operation-journal.js"
import { makeFakePiRuntime, type FakePiRuntimeHandles } from "./support/pi-runtime-fixture.js"

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

    emitFollowup(builder, workflowPayload("add a logout button"))
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

    const delivery = invokeFollowup(builder, workflowPayload("ship it"))
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
    emitFollowup(lastBuilder(), workflowPayload("ship while enqueue settles"))
    await flush()

    expect(order).toEqual(["before:session.input", "followup"])
  })

  it("Followup_TagsInputWithKindFollowup", async () => {
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
    buildClient({ resolver, outbox: recording.outbox, openCodeRuntime: runtime.runtime })

    emitFollowup(lastBuilder(), workflowPayload("tag me"))
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

    expect(() => emitFollowup(builder, workflowPayload("ignored"))).not.toThrow()
    await flush()

    expect(runtime.followupCalls).toHaveLength(0)
    expect(recording.beforeExecutionCalls).toHaveLength(0)
  })

  it("Followup_ReturnsMissingWhenTheRuntimeSessionCannotBeResolved", async () => {
    const resolver = vi.fn(() => null)
    buildClient({ resolver, outbox: recording.outbox, openCodeRuntime: runtime.runtime })

    await expect(invokeFollowup(lastBuilder(), workflowPayload("resume"))).resolves.toEqual({ accepted: false, error: "missing" })
  })

  it("Followup_ReturnsUnavailableWhileRuntimeIsInitializing", async () => {
    runtime.setReady(false)
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
    buildClient({ resolver, outbox: recording.outbox, openCodeRuntime: runtime.runtime })

    await expect(invokeFollowup(lastBuilder(), workflowPayload("resume"))).resolves.toEqual({ accepted: false, error: "unavailable" })
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

    await expect(invokeFollowup(lastBuilder(), workflowPayload("resume"))).resolves.toEqual({ accepted: false, error: "unavailable" })
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

    await expect(invokeFollowup(lastBuilder(), workflowPayload("resume"))).resolves.toEqual({ accepted: false, error: "unavailable" })
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

    expect(() => emitFollowup(builder, workflowPayload("ignored"))).not.toThrow()
    await flush()

    expect(runtime.followupCalls).toHaveLength(0)
    expect(recording.beforeExecutionCalls).toHaveLength(0)
    expect(errorSpy).toHaveBeenCalled()
    errorSpy.mockRestore()
  })

  it("Followup_Completion_RecordsIdleActivity", async () => {
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
    buildClient({ resolver, outbox: recording.outbox, openCodeRuntime: runtime.runtime })

    emitFollowup(lastBuilder(), {
      target: { kind: "generic", projectId: "proj-1", sessionId: "session-1", binding: { runtime: "opencode", runtimeSessionId: "runtime-1", runnerId: "runner-1", workDir: "/work/project" } },
      text: "continue",
      operationId: "followup-1",
    })
    await flush()

    expect(recording.producedFactCalls.find((r) => r.event.type === "session.activity")).toMatchObject({
      acknowledgementPolicy: "successful-response",
      target: { kind: "generic", projectId: "proj-1", sessionId: "session-1" },
      event: {
        type: "session.activity",
        payload: expect.objectContaining({
          status: "completed",
          operationId: "followup-1",
          source: "followup",
        }),
      },
    })
  })

  it("Followup_Failure_RecordsIdleActivity", async () => {
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
        target: { kind: "generic", projectId: "proj-1", sessionId: "session-1", binding: { runtime: "opencode", runtimeSessionId: "runtime-1", runnerId: "runner-1", workDir: "/work/project" } },
        text: "continue",
        operationId: "followup-1",
      })
      await flush()
      await flush()

      const terminal = recording.producedFactCalls.find((r) => r.event.type === "session.activity")
      expect(terminal).toMatchObject({
        acknowledgementPolicy: "successful-response",
        event: {
          type: "session.activity",
          payload: expect.objectContaining({
            status: "completed",
            activity: "idle",
            failureReason: "opencode crashed",
            operationId: "followup-1",
          }),
        },
      })
    } finally {
      errorSpy.mockRestore()
    }
  })

  it("Followup_DuplicateOperationIdDoesNotEnqueueOrInvokeAgain", async () => {
    const states = new Map<string, FollowupOperationState>()
    const journal: FollowupOperationJournalStore = {
      load: async () => {},
      claim: async (_sessionKey, operationId): Promise<FollowupOperationClaim> => {
        const state = states.get(operationId)
        if (state) return state
        states.set(operationId, "claimed")
        return "new"
      },
      markSubmitted: async (_sessionKey, operationId) => { states.set(operationId, "submitted") },
      release: async (_sessionKey, operationId) => { states.delete(operationId) },
    }
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
    buildClient({ resolver, outbox: recording.outbox, openCodeRuntime: runtime.runtime, followupOperationJournal: journal })
    const payload = { ...workflowPayload("send once"), operationId: "followup-once" }

    await expect(invokeFollowup(lastBuilder(), payload)).resolves.toEqual({ accepted: true })
    await expect(invokeFollowup(lastBuilder(), payload)).resolves.toEqual({ accepted: true })
    await flush()

    expect(runtime.followupCalls).toHaveLength(1)
    expect(recording.beforeExecutionCalls).toHaveLength(1)
  })

  it("Followup_IndeterminateClaimIsNotAcknowledgedOrReplayed", async () => {
    const journal: FollowupOperationJournalStore = {
      load: async () => {},
      claim: async () => "claimed",
      markSubmitted: async () => {},
      release: async () => {},
    }
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
    buildClient({ resolver, outbox: recording.outbox, openCodeRuntime: runtime.runtime, followupOperationJournal: journal })

    await expect(invokeFollowup(lastBuilder(), { ...workflowPayload("do not replay"), operationId: "indeterminate" }))
      .resolves.toEqual({ accepted: false, error: "unavailable" })
    expect(runtime.followupCalls).toHaveLength(0)
    expect(recording.beforeExecutionCalls).toHaveLength(0)
  })

  it("Followup_DropsPayloadWhenTextIsMissing", async () => {
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
    buildClient({ resolver, outbox: recording.outbox, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    emitFollowup(builder, workflowPayload(""))
    await flush()

    expect(runtime.followupCalls).toHaveLength(0)
    expect(recording.beforeExecutionCalls).toHaveLength(0)
  })

  it("Followup_DropsPayloadWhenResolverIsNull", async () => {
    buildClient({ resolver: null, outbox: recording.outbox, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    emitFollowup(builder, workflowPayload("noop"))
    await flush()

    expect(runtime.followupCalls).toHaveLength(0)
  })

  it("Followup_DropsPayloadWhenOutboxIsNull", async () => {
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
    buildClient({ resolver, outbox: null, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    emitFollowup(builder, workflowPayload("noop"))
    await flush()

    expect(runtime.followupCalls).toHaveLength(0)
  })

  it("Followup_DropsPayloadWhenRuntimeIsNull", async () => {
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
    buildClient({ resolver, outbox: recording.outbox, openCodeRuntime: null })
    const builder = lastBuilder()

    emitFollowup(builder, workflowPayload("noop"))
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

    emitFollowup(lastBuilder(), workflowPayload("go"))
    await flush()
    await recording.flush()
    await flush()

    expect(runtime.followupCalls).toHaveLength(1)
  })
})

describe("RunnerSignalRClient routes follow-up by persisted binding runtime", () => {
  let opencode: FakeRuntimeHandles
  let pi: FakePiRuntimeHandles

  beforeEach(() => {
    resetBuilders()
    opencode = makeFakeRuntime()
    pi = makeFakePiRuntime()
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it("PiBinding_DispatchesToPiRuntime_WithThePiTarget", async () => {
    const resolver = vi.fn(() => ({ runtimeSessionId: "/virtual/sessions/one.jsonl", workDir: "/workspace", projectId: "proj-1" }))
    const recording = buildRecordingOutbox()
    buildClient({ resolver, outbox: recording.outbox, openCodeRuntime: opencode.runtime, piRuntime: pi.runtime })
    const builder = lastBuilder()

    emitFollowup(builder, {
      target: {
        kind: "generic",
        projectId: "proj-1",
        sessionId: "session-1",
        binding: defaultPiBinding(),
      },
      text: "follow me",
    })
    await flush()

    expect(pi.followupCalls).toHaveLength(1)
    expect(pi.followupCalls[0].target.runtime).toBe("pi")
    expect(pi.followupCalls[0].prompt).toBe("follow me")
    expect(opencode.followupCalls).toHaveLength(0)
  })

  it("OpenCodeBinding_DispatchesToOpenCodeRuntime_AndDoesNotInvokePi", async () => {
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
    const recording = buildRecordingOutbox()
    buildClient({ resolver, outbox: recording.outbox, openCodeRuntime: opencode.runtime, piRuntime: pi.runtime })
    const builder = lastBuilder()

    emitFollowup(builder, {
      target: {
        kind: "generic",
        projectId: "proj-1",
        sessionId: "session-1",
        binding: { runtime: "opencode", runtimeSessionId: "runtime-1", runnerId: "runner-1", workDir: "/work/project" },
      },
      text: "stay on opencode",
    })
    await flush()

    expect(opencode.followupCalls).toHaveLength(1)
    expect(opencode.followupCalls[0].target.runtime).toBe("opencode")
    expect(pi.followupCalls).toHaveLength(0)
  })

  it("PiFollowup_PreflightAccepted_ResolvesAcceptedWithoutRotation", async () => {
    const resolver = vi.fn(() => ({ runtimeSessionId: "/virtual/sessions/one.jsonl", workDir: "/workspace", projectId: "proj-1" }))
    const recording = buildRecordingOutbox()
    buildClient({ resolver, outbox: recording.outbox, openCodeRuntime: opencode.runtime, piRuntime: pi.runtime })
    const builder = lastBuilder()

    const delivery = invokeFollowup(builder, {
      target: {
        kind: "generic",
        projectId: "proj-1",
        sessionId: "session-1",
        binding: defaultPiBinding(),
      },
      text: "go",
    })
    await expect(delivery).resolves.toEqual({ accepted: true })
    expect(pi.followupCalls[0].target.runtimeSessionId).toBe("/virtual/sessions/one.jsonl")
  })

  it("UnknownRuntime_ReportsUnavailable_AndDoesNotCallAnyRuntime", async () => {
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-x", workDir: "/work/project", projectId: "proj-1" }))
    const recording = buildRecordingOutbox()
    buildClient({ resolver, outbox: recording.outbox, openCodeRuntime: opencode.runtime, piRuntime: pi.runtime })
    const builder = lastBuilder()

    await expect(invokeFollowup(builder, {
      target: {
        kind: "generic",
        projectId: "proj-1",
        sessionId: "session-1",
        binding: { runtime: "acp", runtimeSessionId: "runtime-x", runnerId: "runner-1", workDir: "/work/project" },
      },
      text: "no go",
    })).resolves.toEqual({ accepted: false, error: "unavailable" })
    expect(opencode.followupCalls).toHaveLength(0)
    expect(pi.followupCalls).toHaveLength(0)
  })

  it("PostResetBinding_HonorsTheReplacedRuntimeImmediately", async () => {
    const resolver = vi.fn(() => ({ runtimeSessionId: "/virtual/sessions/two.jsonl", workDir: "/workspace", projectId: "proj-1" }))
    const recording = buildRecordingOutbox()
    buildClient({ resolver, outbox: recording.outbox, openCodeRuntime: opencode.runtime, piRuntime: pi.runtime })
    const builder = lastBuilder()

    emitFollowup(builder, {
      target: {
        kind: "generic",
        projectId: "proj-1",
        sessionId: "session-1",
        binding: { runtime: "pi", runtimeSessionId: "/virtual/sessions/two.jsonl", runnerId: "runner-1", workDir: "/workspace" },
      },
      text: "after reset",
    })
    await flush()

    expect(pi.followupCalls).toHaveLength(1)
    expect(pi.followupCalls[0].target.runtimeSessionId).toBe("/virtual/sessions/two.jsonl")
    expect(pi.followupCalls[0].target.runtime).toBe("pi")
    expect(opencode.followupCalls).toHaveLength(0)
  })
})
