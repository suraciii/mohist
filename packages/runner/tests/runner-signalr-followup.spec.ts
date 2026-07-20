import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { FOLLOWUP_TARGET_UNAVAILABLE } from "../src/server/session-target.js"
import {
  buildClient,
  emitFollowup,
  flush,
  invokeFollowup,
  lastBuilder,
  makeFakeRuntime,
  resetBuilders,
  type FakeRuntimeHandles,
  type MockServerConnection,
} from "./support/followup-handler-fixture.js"

let runtime: FakeRuntimeHandles

beforeEach(() => {
  resetBuilders()
  runtime = makeFakeRuntime()
})

afterEach(() => {
  vi.restoreAllMocks()
})

describe("RunnerSignalRClient ReceiveFollowup handler", () => {
  it("Followup_FireAndForgetPromptCallsRuntimeFollowupWithoutAwait", async () => {
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
    const runtimeEvents = vi.fn(async () => undefined)
    const serverConnection: MockServerConnection = { workflowAgentSessionRuntimeEvents: runtimeEvents }

    buildClient({ resolver, serverConnection, openCodeRuntime: runtime.runtime })
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
    const runtimeEvents = vi.fn(async () => undefined)
    const serverConnection: MockServerConnection = { workflowAgentSessionRuntimeEvents: runtimeEvents }

    buildClient({ resolver, serverConnection, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    const delivery = invokeFollowup(builder, { workflowRunId: "wr-1", sessionName: "work-1", text: "ship it" })
    expect(followupCalls).toHaveLength(1)
    await expect(delivery).resolves.toEqual({ accepted: true })
    resolveFollowup({
      ok: true,
      value: { facts: { runtimeSessionId: "runtime-1", workDir: "/work/project" }, diagnostics: [] },
      diagnostics: [],
    })
    await flush()
  })

  it("Followup_PromptsEvenWhenRuntimeEventsEmitIsStillPending", async () => {
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
    const runtimeEvents = vi.fn(() => new Promise(() => undefined))
    const serverConnection: MockServerConnection = { workflowAgentSessionRuntimeEvents: runtimeEvents }

    buildClient({ resolver, serverConnection, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    emitFollowup(builder, { workflowRunId: "wr-1", sessionName: "work-1", text: "ship while event is pending" })
    await flush()

    expect(runtimeEvents).toHaveBeenCalledTimes(1)
    expect(runtime.followupCalls).toHaveLength(1)
    expect(runtime.followupCalls[0].prompt).toBe("ship while event is pending")
  })

  it("Followup_EmitsSessionInputEventBeforeCallingRuntime", async () => {
    const callOrder: string[] = []
    runtime.runtime.followup = async () => {
      callOrder.push("followup")
      return { ok: true, value: { facts: { runtimeSessionId: "runtime-1", workDir: "/work/project" }, diagnostics: [] }, diagnostics: [] }
    }
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
    const runtimeEvents = vi.fn(async () => {
      callOrder.push("session.input")
    })
    const serverConnection: MockServerConnection = { workflowAgentSessionRuntimeEvents: runtimeEvents }

    buildClient({ resolver, serverConnection, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    emitFollowup(builder, { workflowRunId: "wr-1", sessionName: "work-1", text: "fix the typo" })
    await flush()
    await flush()

    expect(runtimeEvents).toHaveBeenCalledTimes(1)
    expect(callOrder).toEqual(["session.input", "followup"])
  })

  it("Followup_TagsEventWithPromptKindFollowup", async () => {
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
    const runtimeEvents = vi.fn(async () => undefined)
    const serverConnection: MockServerConnection = { workflowAgentSessionRuntimeEvents: runtimeEvents }

    buildClient({ resolver, serverConnection, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    emitFollowup(builder, { workflowRunId: "wr-1", sessionName: "work-1", text: "tag me" })
    await flush()

    expect(runtimeEvents).toHaveBeenCalledWith(
      "proj-1",
      "wr-1",
      "work-1",
      expect.objectContaining({
        runtimeEvents: [
          expect.objectContaining({
            type: "session.input",
            payload: expect.objectContaining({
              kind: "followup",
              text: "tag me",
              role: "user",
              runtimeSessionId: "runtime-1",
              source: "followup",
            }),
          }),
        ],
      }),
      expect.any(AbortSignal),
    )
  })

  it("Followup_DropsWhenResolverReturnsNullAndDoesNotThrow", async () => {
    const runtimeEvents = vi.fn(async () => undefined)
    const resolver = vi.fn(() => null)
    const serverConnection: MockServerConnection = { workflowAgentSessionRuntimeEvents: runtimeEvents }

    buildClient({ resolver, serverConnection, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    expect(() => emitFollowup(builder, { workflowRunId: "wr-1", sessionName: "work-1", text: "ignored" })).not.toThrow()
    await flush()

    expect(runtime.followupCalls).toHaveLength(0)
    expect(runtimeEvents).not.toHaveBeenCalled()
  })

  it("Followup_ReturnsMissingWhenTheRuntimeSessionCannotBeResolved", async () => {
    const resolver = vi.fn(() => null)
    buildClient({ resolver, openCodeRuntime: runtime.runtime })

    await expect(invokeFollowup(lastBuilder(), {
      workflowRunId: "wr-1", sessionName: "work-1", text: "resume",
    })).resolves.toEqual({ accepted: false, error: "missing" })
  })

  it("Followup_ReturnsUnavailableWhileTheRuntimeIsInitializing", async () => {
    buildClient({ resolver: () => FOLLOWUP_TARGET_UNAVAILABLE, openCodeRuntime: runtime.runtime })

    await expect(invokeFollowup(lastBuilder(), {
      workflowRunId: "wr-1", sessionName: "work-1", text: "resume",
    })).resolves.toEqual({ accepted: false, error: "unavailable" })
  })

  it("Followup_ReturnsUnavailableWhenRuntimeReadyIsFalse", async () => {
    runtime.setReady(false)
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
    buildClient({ resolver, openCodeRuntime: runtime.runtime })

    await expect(invokeFollowup(lastBuilder(), {
      workflowRunId: "wr-1", sessionName: "work-1", text: "resume",
    })).resolves.toEqual({ accepted: false, error: "unavailable" })
    expect(runtime.followupCalls).toHaveLength(0)
  })

  it("Followup_DropsWhenResolverThrows", async () => {
    const runtimeEvents = vi.fn(async () => undefined)
    const resolver = vi.fn(() => { throw new Error("resolver boom") })
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)
    const serverConnection: MockServerConnection = { workflowAgentSessionRuntimeEvents: runtimeEvents }

    buildClient({ resolver, serverConnection, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    expect(() => emitFollowup(builder, { workflowRunId: "wr-1", sessionName: "work-1", text: "ignored" })).not.toThrow()
    await flush()

    expect(runtime.followupCalls).toHaveLength(0)
    expect(runtimeEvents).not.toHaveBeenCalled()
    expect(errorSpy).toHaveBeenCalled()
    errorSpy.mockRestore()
  })

  it("Followup_CatchesFollowupRejectionAndLogsWithoutThrowing", async () => {
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
    const runtimeEvents = vi.fn(async () => undefined)
    const serverConnection: MockServerConnection = { workflowAgentSessionRuntimeEvents: runtimeEvents }
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)

    buildClient({ resolver, serverConnection, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    expect(() => emitFollowup(builder, { workflowRunId: "wr-1", sessionName: "work-1", text: "boom" })).not.toThrow()
    await flush()
    await flush()

    expect(runtime.followupCalls).toHaveLength(1)
    expect(errorSpy).toHaveBeenCalled()
    errorSpy.mockRestore()
  })

  it("Followup_FollowupRejectionRecordsTheMatchingOperationForDurableDelivery", async () => {
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
    const serverConnection: MockServerConnection = {
      workflowAgentSessionRuntimeEvents: vi.fn(async () => undefined),
      agentSessionRuntimeEvents: vi.fn(async () => undefined),
    }
    const outbox = { record: vi.fn(async () => undefined) }
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)

    buildClient({ resolver, serverConnection, followupFailureOutbox: outbox, openCodeRuntime: runtime.runtime })
    emitFollowup(lastBuilder(), {
      target: { kind: "generic", projectId: "proj-1", sessionId: "session-1" },
      text: "continue",
      operationId: "followup-1",
    })
    await flush()
    await flush()

    expect(outbox.record).toHaveBeenCalledWith(
      expect.objectContaining({
        operationId: "followup-1",
        runtimeSessionId: "runtime-1",
        target: expect.objectContaining({ kind: "generic", sessionId: "session-1" }),
      }),
      serverConnection,
    )
    errorSpy.mockRestore()
  })

  it("Followup_FollowupCompletionRecordsTheMatchingOperationForDurableDelivery", async () => {
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
    const serverConnection: MockServerConnection = {
      workflowAgentSessionRuntimeEvents: vi.fn(async () => undefined),
      agentSessionRuntimeEvents: vi.fn(async () => undefined),
    }
    const outbox = { record: vi.fn(async () => undefined) }

    buildClient({ resolver, serverConnection, followupFailureOutbox: outbox, openCodeRuntime: runtime.runtime })
    emitFollowup(lastBuilder(), {
      target: { kind: "generic", projectId: "proj-1", sessionId: "session-1" },
      text: "continue",
      operationId: "followup-1",
    })
    await flush()
    await flush()

    expect(outbox.record).toHaveBeenCalledWith(
      expect.objectContaining({
        operationId: "followup-1",
        status: "completed",
        error: null,
      }),
      serverConnection,
    )
  })

  it("Followup_ContinuesToPromptEvenIfRuntimeEventsEmitFails", async () => {
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
    const runtimeEvents = vi.fn(async () => { throw new Error("server unreachable") })
    const serverConnection: MockServerConnection = { workflowAgentSessionRuntimeEvents: runtimeEvents }
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)

    buildClient({ resolver, serverConnection, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    emitFollowup(builder, { workflowRunId: "wr-1", sessionName: "work-1", text: "keep going" })
    await flush()
    await flush()

    expect(runtimeEvents).toHaveBeenCalledTimes(1)
    expect(runtime.followupCalls).toHaveLength(1)
    expect(errorSpy).toHaveBeenCalledWith("failed to emit followup session.input event:", expect.any(Error))
    errorSpy.mockRestore()
  })

  it("Followup_DropsPayloadWhenTextIsMissing", async () => {
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
    const runtimeEvents = vi.fn(async () => undefined)
    const serverConnection: MockServerConnection = { workflowAgentSessionRuntimeEvents: runtimeEvents }

    buildClient({ resolver, serverConnection, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    emitFollowup(builder, { workflowRunId: "wr-1", sessionName: "work-1", text: "" })
    await flush()

    expect(runtime.followupCalls).toHaveLength(0)
    expect(runtimeEvents).not.toHaveBeenCalled()
  })

  it("Followup_DropsPayloadWhenResolverIsNull", async () => {
    const runtimeEvents = vi.fn(async () => undefined)
    const serverConnection: MockServerConnection = { workflowAgentSessionRuntimeEvents: runtimeEvents }

    buildClient({ resolver: null, serverConnection, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    emitFollowup(builder, { workflowRunId: "wr-1", sessionName: "work-1", text: "noop" })
    await flush()

    expect(runtime.followupCalls).toHaveLength(0)
    expect(runtimeEvents).not.toHaveBeenCalled()
  })

  it("Followup_DropsPayloadWhenServerConnectionIsNull", async () => {
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))

    buildClient({ resolver, serverConnection: null, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    emitFollowup(builder, { workflowRunId: "wr-1", sessionName: "work-1", text: "noop" })
    await flush()

    expect(runtime.followupCalls).toHaveLength(0)
  })

  it("Followup_DropsPayloadWhenRuntimeIsNull", async () => {
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
    const runtimeEvents = vi.fn(async () => undefined)
    const serverConnection: MockServerConnection = { workflowAgentSessionRuntimeEvents: runtimeEvents }

    buildClient({ resolver, serverConnection, openCodeRuntime: null })
    const builder = lastBuilder()

    emitFollowup(builder, { workflowRunId: "wr-1", sessionName: "work-1", text: "noop" })
    await flush()

    expect(runtimeEvents).not.toHaveBeenCalled()
  })

  it("Followup_DropsNullOrUndefinedPayload", async () => {
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
    const runtimeEvents = vi.fn(async () => undefined)
    const serverConnection: MockServerConnection = { workflowAgentSessionRuntimeEvents: runtimeEvents }

    buildClient({ resolver, serverConnection, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    emitFollowup(builder, null)
    emitFollowup(builder, undefined)
    await flush()

    expect(runtime.followupCalls).toHaveLength(0)
    expect(runtimeEvents).not.toHaveBeenCalled()
  })
})
