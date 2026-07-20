import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import {
  buildClient,
  emitFollowup,
  flush,
  genericPayload,
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

describe("RunnerSignalRClient routes follow-ups to generic sessions", () => {
  it("GenericFollowup_LocatesSessionByGenericKey_AndCallsRuntimeFollowup", async () => {
    const resolver = vi.fn((target: { kind: string }) => {
      expect(target.kind).toBe("generic")
      return { runtimeSessionId: "acp-1", workDir: "/work/project", projectId: "proj-1" }
    })
    const workflowRuntimeEvents = vi.fn(async () => undefined)
    const agentSessionRuntimeEvents = vi.fn(async () => undefined)
    const serverConnection: MockServerConnection = {
      workflowAgentSessionRuntimeEvents: workflowRuntimeEvents,
      agentSessionRuntimeEvents,
    }

    buildClient({ resolver: resolver as never, serverConnection, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    emitFollowup(builder, genericPayload("add a logout route"))
    await flush()

    expect(runtime.followupCalls).toHaveLength(1)
    expect(runtime.followupCalls[0].prompt).toBe("add a logout route")
    expect(runtime.followupCalls[0].target).toEqual({ runtime: "opencode", runtimeSessionId: "acp-1", workDir: "/work/project" })
    expect(workflowRuntimeEvents).not.toHaveBeenCalled()
    expect(agentSessionRuntimeEvents).toHaveBeenCalledTimes(1)
  })

  it("GenericFollowup_OmittedLegacyWorkDir_StillUsesCachedRuntimeTarget", async () => {
    const resolver = vi.fn((target: { kind: string; binding?: unknown }) => {
      expect(target.kind).toBe("generic")
      expect(target.binding).toEqual({
        runtime: "opencode",
        runtimeSessionId: "runtime-1",
        runnerId: "runner-1",
        workDir: null,
      })
      return { runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }
    })
    const serverConnection: MockServerConnection = {
      workflowAgentSessionRuntimeEvents: vi.fn(async () => undefined),
      agentSessionRuntimeEvents: vi.fn(async () => undefined),
    }

    buildClient({ resolver: resolver as never, serverConnection, openCodeRuntime: runtime.runtime })
    await expect(invokeFollowup(lastBuilder(), {
      target: {
        kind: "generic",
        projectId: "proj-1",
        sessionId: "gen-session-1",
        binding: {
          runtime: "opencode",
          runtimeSessionId: "runtime-1",
          runnerId: "runner-1",
        },
      },
      text: "continue",
    })).resolves.toEqual({ accepted: true })

    expect(runtime.followupCalls).toHaveLength(1)
    expect(runtime.followupCalls[0].target.runtimeSessionId).toBe("runtime-1")
    expect(runtime.followupCalls[0].prompt).toBe("continue")
  })

  it("GenericFollowup_AcknowledgesDeliveryBeforeRuntimeCompletion", async () => {
    let resolveFollowup!: (value: { ok: true; value: { facts: { runtimeSessionId: string; workDir: string }; diagnostics: readonly never[] }; diagnostics: readonly never[] }) => void
    const promise = new Promise<{ ok: true; value: { facts: { runtimeSessionId: string; workDir: string }; diagnostics: readonly never[] }; diagnostics: readonly never[] }>((resolve) => { resolveFollowup = resolve })
    const followupCalls = runtime.followupCalls
    runtime.runtime.followup = async () => {
      followupCalls.push({
        target: { runtime: "opencode", runtimeSessionId: "runtime-1", workDir: "/work/project" },
        prompt: "continue",
      })
      return await promise
    }
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
    const serverConnection: MockServerConnection = {
      workflowAgentSessionRuntimeEvents: vi.fn(async () => undefined),
      agentSessionRuntimeEvents: vi.fn(async () => undefined),
    }

    buildClient({ resolver: resolver as never, serverConnection, openCodeRuntime: runtime.runtime })

    const delivery = invokeFollowup(lastBuilder(), genericPayload("continue"))

    await expect(delivery).resolves.toEqual({ accepted: true })
    expect(followupCalls).toHaveLength(1)
    resolveFollowup({
      ok: true,
      value: { facts: { runtimeSessionId: "runtime-1", workDir: "/work/project" }, diagnostics: [] },
      diagnostics: [],
    })
    await flush()
  })

  it("GenericFollowup_EmitsSessionInputViaAgentSessionRuntimeEventsEndpoint", async () => {
    const resolver = vi.fn(() => ({ runtimeSessionId: "acp-1", workDir: "/work/project", projectId: "proj-1" }))
    const workflowRuntimeEvents = vi.fn(async () => undefined)
    const agentSessionRuntimeEvents = vi.fn(async () => undefined)
    const serverConnection: MockServerConnection = {
      workflowAgentSessionRuntimeEvents: workflowRuntimeEvents,
      agentSessionRuntimeEvents,
    }

    buildClient({ resolver: resolver as never, serverConnection, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    emitFollowup(builder, genericPayload("kind tag"))
    await flush()

    expect(agentSessionRuntimeEvents).toHaveBeenCalledWith(
      "proj-1",
      "gen-session-1",
      expect.objectContaining({
        runtimeEvents: [
          expect.objectContaining({
            type: "session.input",
            payload: expect.objectContaining({
              kind: "followup",
              text: "kind tag",
              role: "user",
              runtimeSessionId: "acp-1",
              source: "followup",
            }),
          }),
        ],
      }),
      expect.any(AbortSignal),
    )
    expect(workflowRuntimeEvents).not.toHaveBeenCalled()
  })

  it("GenericFollowup_ContinuesToPromptEvenIfAgentSessionRuntimeEventsEmitFails", async () => {
    const resolver = vi.fn(() => ({ runtimeSessionId: "acp-1", workDir: "/work/project", projectId: "proj-1" }))
    const workflowRuntimeEvents = vi.fn(async () => undefined)
    const agentSessionRuntimeEvents = vi.fn(async () => { throw new Error("server unreachable") })
    const serverConnection: MockServerConnection = {
      workflowAgentSessionRuntimeEvents: workflowRuntimeEvents,
      agentSessionRuntimeEvents,
    }
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)

    buildClient({ resolver: resolver as never, serverConnection, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    emitFollowup(builder, genericPayload("keep going"))
    await flush()
    await flush()

    expect(agentSessionRuntimeEvents).toHaveBeenCalledTimes(1)
    expect(runtime.followupCalls).toHaveLength(1)
    expect(errorSpy).toHaveBeenCalledWith("failed to emit followup session.input event:", expect.any(Error))
    errorSpy.mockRestore()
  })

  it("GenericFollowup_DropsUnknownSessionWithoutThrowing", async () => {
    const resolver = vi.fn(() => null)
    const workflowRuntimeEvents = vi.fn(async () => undefined)
    const agentSessionRuntimeEvents = vi.fn(async () => undefined)
    const serverConnection: MockServerConnection = {
      workflowAgentSessionRuntimeEvents: workflowRuntimeEvents,
      agentSessionRuntimeEvents,
    }

    buildClient({ resolver: resolver as never, serverConnection, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    expect(() => emitFollowup(builder, genericPayload("ignored"))).not.toThrow()
    await flush()

    expect(runtime.followupCalls).toHaveLength(0)
    expect(workflowRuntimeEvents).not.toHaveBeenCalled()
    expect(agentSessionRuntimeEvents).not.toHaveBeenCalled()
  })

  it("GenericFollowup_DropsWhenTargetSessionIdMissing", async () => {
    const resolver = vi.fn(() => ({ runtimeSessionId: "acp-1", workDir: "/work/project", projectId: "proj-1" }))
    const workflowRuntimeEvents = vi.fn(async () => undefined)
    const agentSessionRuntimeEvents = vi.fn(async () => undefined)
    const serverConnection: MockServerConnection = {
      workflowAgentSessionRuntimeEvents: workflowRuntimeEvents,
      agentSessionRuntimeEvents,
    }

    buildClient({ resolver: resolver as never, serverConnection, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    emitFollowup(builder, {
      target: { kind: "generic", projectId: "proj-1" },
      text: "no sessionId",
    })
    await flush()

    expect(runtime.followupCalls).toHaveLength(0)
    expect(workflowRuntimeEvents).not.toHaveBeenCalled()
    expect(agentSessionRuntimeEvents).not.toHaveBeenCalled()
  })

  it("GenericFollowup_DropsWhenTextMissing", async () => {
    const resolver = vi.fn(() => ({ runtimeSessionId: "acp-1", workDir: "/work/project", projectId: "proj-1" }))
    const workflowRuntimeEvents = vi.fn(async () => undefined)
    const agentSessionRuntimeEvents = vi.fn(async () => undefined)
    const serverConnection: MockServerConnection = {
      workflowAgentSessionRuntimeEvents: workflowRuntimeEvents,
      agentSessionRuntimeEvents,
    }

    buildClient({ resolver: resolver as never, serverConnection, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    emitFollowup(builder, { ...genericPayload(""), text: "" })
    await flush()

    expect(runtime.followupCalls).toHaveLength(0)
    expect(workflowRuntimeEvents).not.toHaveBeenCalled()
    expect(agentSessionRuntimeEvents).not.toHaveBeenCalled()
  })

  it("WorkflowFollowup_StillUsesWorkflowRuntimeEventsEndpoint_WhenTargetShapeCarriesIt", async () => {
    const resolver = vi.fn((target: { kind: string }) => {
      expect(target.kind).toBe("workflow")
      return { runtimeSessionId: "acp-1", workDir: "/work/project", projectId: "proj-1" }
    })
    const workflowRuntimeEvents = vi.fn(async () => undefined)
    const agentSessionRuntimeEvents = vi.fn(async () => undefined)
    const serverConnection: MockServerConnection = {
      workflowAgentSessionRuntimeEvents: workflowRuntimeEvents,
      agentSessionRuntimeEvents,
    }

    buildClient({ resolver: resolver as never, serverConnection, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    emitFollowup(builder, {
      target: {
        kind: "workflow",
        projectId: "proj-1",
        workflowRunId: "wr-1",
        sessionName: "work-1",
      },
      text: "tag me",
    })
    await flush()

    expect(workflowRuntimeEvents).toHaveBeenCalledTimes(1)
    expect(agentSessionRuntimeEvents).not.toHaveBeenCalled()
    expect(runtime.followupCalls).toHaveLength(1)
  })

  it("WorkflowFollowup_LegacyTopLevelFields_StillResolveToWorkflowTarget", async () => {
    const resolver = vi.fn((target: { kind: string; workflowRunId?: string; sessionName?: string }) => {
      expect(target.kind).toBe("workflow")
      expect(target.workflowRunId).toBe("wr-legacy")
      expect(target.sessionName).toBe("work-legacy")
      return { runtimeSessionId: "acp-1", workDir: "/work/project", projectId: "proj-legacy" }
    })
    const workflowRuntimeEvents = vi.fn(async () => undefined)
    const agentSessionRuntimeEvents = vi.fn(async () => undefined)
    const serverConnection: MockServerConnection = {
      workflowAgentSessionRuntimeEvents: workflowRuntimeEvents,
      agentSessionRuntimeEvents,
    }

    buildClient({ resolver: resolver as never, serverConnection, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    emitFollowup(builder, { workflowRunId: "wr-legacy", sessionName: "work-legacy", text: "legacy ok" })
    await flush()

    expect(workflowRuntimeEvents).toHaveBeenCalledTimes(1)
    expect(workflowRuntimeEvents).toHaveBeenCalledWith(
      "proj-legacy",
      "wr-legacy",
      "work-legacy",
      expect.objectContaining({
        runtimeEvents: [
          expect.objectContaining({
            type: "session.input",
            payload: expect.objectContaining({ kind: "followup", text: "legacy ok" }),
          }),
        ],
      }),
      expect.any(AbortSignal),
    )
    expect(agentSessionRuntimeEvents).not.toHaveBeenCalled()
    expect(runtime.followupCalls).toHaveLength(1)
  })
})
