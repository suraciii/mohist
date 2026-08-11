import { describe, expect, vi } from "vitest"
import {
  buildClient,
  defaultPiBinding,
  emitFollowup,
  flush,
  invokeFollowup,
  lastBuilder,
  followupIt,
  workflowPayload,
} from "./support/followup-handler-fixture.js"
import type { AgentSessionRuntimeEventOutbox } from "../src/server/runtime-event-outbox.js"
import type { FollowupOperationJournalStore } from "../src/runtime/followup-operation-journal.js"
import type { FollowupOperationClaim, FollowupOperationState } from "../src/runtime/followup-operation-journal.js"
import { capturedLogs } from "./support/logger-test.js"
import { makeFakePiRuntime } from "./support/pi-runtime-fixture.js"

describe("RunnerSignalRClient ReceiveFollowup handler", () => {
  followupIt("Followup_FireAndForgetPromptCallsRuntimeFollowupWithoutAwait", async ({ runtime, recording }) => {
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

  followupIt("Followup_AcknowledgesDeliveryBeforeRuntimeCompletion", async ({ runtime, recording }) => {
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

  followupIt("Followup_AwaitsDurableInputEnqueue_BeforeInvokingRuntime", async ({ runtime }) => {
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

  followupIt("Followup_TagsInputWithKindFollowup", async ({ runtime, recording }) => {
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

  followupIt("Followup_DropsWhenResolverReturnsNullAndDoesNotThrow", async ({ runtime, recording }) => {
    const resolver = vi.fn(() => null)

    buildClient({ resolver, outbox: recording.outbox, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    expect(() => emitFollowup(builder, workflowPayload("ignored"))).not.toThrow()
    await flush()

    expect(runtime.followupCalls).toHaveLength(0)
    expect(recording.beforeExecutionCalls).toHaveLength(0)
  })

  followupIt("Followup_ReturnsMissingWhenTheRuntimeSessionCannotBeResolved", async ({ runtime, recording }) => {
    const resolver = vi.fn(() => null)
    buildClient({ resolver, outbox: recording.outbox, openCodeRuntime: runtime.runtime })

    await expect(invokeFollowup(lastBuilder(), workflowPayload("resume"))).resolves.toEqual({ accepted: false, error: "missing" })
  })

  followupIt("Followup_ReturnsUnavailableWhileRuntimeIsInitializing", async ({ runtime, recording }) => {
    runtime.setReady(false)
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
    buildClient({ resolver, outbox: recording.outbox, openCodeRuntime: runtime.runtime })

    await expect(invokeFollowup(lastBuilder(), workflowPayload("resume"))).resolves.toEqual({ accepted: false, error: "unavailable" })
    expect(runtime.followupCalls).toHaveLength(0)
    expect(recording.beforeExecutionCalls).toHaveLength(0)
  })

  followupIt("Followup_ReturnsUnavailableWhenOutboxUnhealthy", async ({ runtime }) => {
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

  followupIt("Followup_ReturnsUnavailableWhenLocalEnqueueFails_BeforeInvokingRuntime", async ({ runtime }) => {
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
    buildClient({ resolver, outbox, openCodeRuntime: runtime.runtime })

    await expect(invokeFollowup(lastBuilder(), workflowPayload("resume"))).resolves.toEqual({ accepted: false, error: "unavailable" })
    expect(runtime.followupCalls).toHaveLength(0)
    expect(capturedLogs()).toEqual(expect.arrayContaining([
      expect.objectContaining({ level: "ERROR", message: "followup durable input enqueue failed", fields: expect.objectContaining({ exception: expect.objectContaining({ message: "disk full" }) }) }),
    ]))
  })

  followupIt("Followup_DropsWhenResolverThrows", async ({ runtime, recording }) => {
    const resolver = vi.fn(() => { throw new Error("resolver boom") })
    buildClient({ resolver, outbox: recording.outbox, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    expect(() => emitFollowup(builder, workflowPayload("ignored"))).not.toThrow()
    await flush()

    expect(runtime.followupCalls).toHaveLength(0)
    expect(recording.beforeExecutionCalls).toHaveLength(0)
    expect(capturedLogs()).toEqual(expect.arrayContaining([
      expect.objectContaining({ level: "ERROR", message: "followup target resolver threw" }),
    ]))
  })

  followupIt("Followup_Completion_RecordsIdleActivity", async ({ runtime, recording }) => {
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

  followupIt("Followup_EnqueuesAssistantOutputBeforeTerminalActivity", async ({ runtime, recording }) => {
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
    runtime.runtime.followup = async (_request, observer) => {
      observer?.onEvent?.({
        type: "message.delta",
        runtimeSessionId: "runtime-1",
        workDir: "/work/project",
        payload: { text: "RECOVERY_OK LINEN-731 CEDAR-842" },
      })
      observer?.onEvent?.({
        type: "message.delta",
        runtimeSessionId: "runtime-1",
        workDir: "/work/project",
        payload: { text: "RECOVERY_OK LINEN-731 CEDAR-842" },
      })
      return {
        ok: true,
        value: {
          facts: {
            runtimeSessionId: "runtime-1",
            workDir: "/work/project",
            finalAssistantText: "RECOVERY_OK LINEN-731 CEDAR-842",
          },
          diagnostics: [],
        },
        diagnostics: [],
      }
    }
    buildClient({ resolver, outbox: recording.outbox, openCodeRuntime: runtime.runtime })

    emitFollowup(lastBuilder(), {
      target: { kind: "generic", projectId: "proj-1", sessionId: "session-1", binding: { runtime: "opencode", runtimeSessionId: "runtime-1", runnerId: "runner-1", workDir: "/work/project" } },
      text: "continue",
      operationId: "followup-output-order",
      turnId: "turn-output-order",
    })
    await flush()
    await flush()

    expect(recording.producedFactCalls.map((record) => record.event.type)).toEqual([
      "message.delta",
      "message.delta",
      "session.activity",
    ])
    expect(recording.producedFactCalls[0]?.id).not.toBe(recording.producedFactCalls[1]?.id)
    expect(recording.producedFactCalls[2]?.event.payload).toMatchObject({
      status: "completed",
      message: "RECOVERY_OK LINEN-731 CEDAR-842",
      output: "RECOVERY_OK LINEN-731 CEDAR-842",
    })
  })

  followupIt("Followup_Failure_RecordsIdleActivity", async ({ runtime, recording }) => {
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
  })

  followupIt("Followup_DuplicateOperationIdDoesNotEnqueueOrInvokeAgain", async ({ runtime, recording }) => {
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

  followupIt("Followup_IndeterminateClaimIsNotAcknowledgedOrReplayed", async ({ runtime, recording }) => {
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

  followupIt("Followup_DropsPayloadWhenTextIsMissing", async ({ runtime, recording }) => {
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
    buildClient({ resolver, outbox: recording.outbox, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    emitFollowup(builder, workflowPayload(""))
    await flush()

    expect(runtime.followupCalls).toHaveLength(0)
    expect(recording.beforeExecutionCalls).toHaveLength(0)
  })

  followupIt("Followup_DropsPayloadWhenResolverIsNull", async ({ runtime, recording }) => {
    buildClient({ resolver: null, outbox: recording.outbox, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    emitFollowup(builder, workflowPayload("noop"))
    await flush()

    expect(runtime.followupCalls).toHaveLength(0)
  })

  followupIt("Followup_DropsPayloadWhenOutboxIsNull", async ({ runtime }) => {
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
    buildClient({ resolver, outbox: null, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    emitFollowup(builder, workflowPayload("noop"))
    await flush()

    expect(runtime.followupCalls).toHaveLength(0)
  })

  followupIt("Followup_DropsPayloadWhenRuntimeIsNull", async ({ recording }) => {
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
    buildClient({ resolver, outbox: recording.outbox, openCodeRuntime: null })
    const builder = lastBuilder()

    emitFollowup(builder, workflowPayload("noop"))
    await flush()

    expect(recording.beforeExecutionCalls).toHaveLength(0)
  })

  followupIt("Followup_DropsNullOrUndefinedPayload", async ({ runtime, recording }) => {
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
    buildClient({ resolver, outbox: recording.outbox, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    emitFollowup(builder, null)
    emitFollowup(builder, undefined)
    await flush()

    expect(runtime.followupCalls).toHaveLength(0)
    expect(recording.beforeExecutionCalls).toHaveLength(0)
  })

  followupIt("Followup_InvokesRuntimeOnce_OnAcceptedDeliveryEvenIfOutboxLaterDrains", async ({ runtime, recording }) => {
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
  followupIt("PiBinding_DispatchesToPiRuntime_WithThePiTarget", async ({ runtime: opencode, recording }) => {
    const pi = makeFakePiRuntime()
    const resolver = vi.fn(() => ({ runtimeSessionId: "/virtual/sessions/one.jsonl", workDir: "/workspace", projectId: "proj-1" }))
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

  followupIt("OpenCodeBinding_DispatchesToOpenCodeRuntime_AndDoesNotInvokePi", async ({ runtime: opencode, recording }) => {
    const pi = makeFakePiRuntime()
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
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

  followupIt("PiFollowup_PreflightAccepted_ResolvesAcceptedWithoutRotation", async ({ runtime: opencode, recording }) => {
    const pi = makeFakePiRuntime()
    const resolver = vi.fn(() => ({ runtimeSessionId: "/virtual/sessions/one.jsonl", workDir: "/workspace", projectId: "proj-1" }))
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

  followupIt("UnknownRuntime_ReportsUnavailable_AndDoesNotCallAnyRuntime", async ({ runtime: opencode, recording }) => {
    const pi = makeFakePiRuntime()
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-x", workDir: "/work/project", projectId: "proj-1" }))
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

  followupIt("PostResetBinding_HonorsTheReplacedRuntimeImmediately", async ({ runtime: opencode, recording }) => {
    const pi = makeFakePiRuntime()
    const resolver = vi.fn(() => ({ runtimeSessionId: "/virtual/sessions/two.jsonl", workDir: "/workspace", projectId: "proj-1" }))
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
