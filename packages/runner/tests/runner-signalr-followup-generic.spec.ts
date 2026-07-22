import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import {
  buildClient,
  buildRecordingOutbox,
  emitFollowup,
  flush,
  genericPayload,
  invokeFollowup,
  lastBuilder,
  makeFakeRuntime,
  resetBuilders,
  type FakeRuntimeHandles,
  type RecordingOutbox,
} from "./support/followup-handler-fixture.js"

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

describe("RunnerSignalRClient routes follow-ups to generic sessions", () => {
  it("GenericFollowup_LocatesSessionByGenericKey_AndCallsRuntimeFollowup", async () => {
    const resolver = vi.fn((target: { kind: string }) => {
      expect(target.kind).toBe("generic")
      return { runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }
    })

    buildClient({ resolver: resolver as never, outbox: recording.outbox, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    emitFollowup(builder, genericPayload("add a logout route"))
    await flush()

    expect(runtime.followupCalls).toHaveLength(1)
    expect(runtime.followupCalls[0].prompt).toBe("add a logout route")
    expect(runtime.followupCalls[0].target).toEqual({ runtime: "opencode", runtimeSessionId: "runtime-1", workDir: "/work/project" })
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

    buildClient({ resolver: resolver as never, outbox: recording.outbox, openCodeRuntime: runtime.runtime })
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

  it("GenericFollowup_InputEntersGenericFollowupProducerFamily", async () => {
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
    buildClient({ resolver: resolver as never, outbox: recording.outbox, openCodeRuntime: runtime.runtime })

    emitFollowup(lastBuilder(), genericPayload("kind tag"))
    await flush()

    expect(recording.beforeExecutionCalls).toHaveLength(1)
    expect(recording.beforeExecutionCalls[0]).toMatchObject({
      producerFamily: "generic-followup",
      target: { kind: "generic", projectId: "proj-1", sessionId: "gen-session-1" },
      event: {
        type: "session.input",
        payload: expect.objectContaining({
          kind: "followup",
          text: "kind tag",
          role: "user",
          runtimeSessionId: "runtime-1",
          source: "followup",
        }),
      },
    })
  })

  it("GenericFollowup_Outcome_EntersSameSequenceKey_AsInput", async () => {
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
    buildClient({ resolver: resolver as never, outbox: recording.outbox, openCodeRuntime: runtime.runtime })

    emitFollowup(lastBuilder(), { ...genericPayload("continue"), operationId: "followup-1" })
    await flush()
    await flush()

    const types = recording.records.map((r) => r.event.type)
    expect(types).toEqual([
      "session.input",
      "session.followup_completed",
    ])
    for (const record of recording.records) {
      expect(record.target).toMatchObject({ kind: "generic", projectId: "proj-1", sessionId: "gen-session-1" })
      expect(record.runtimeSessionId).toBe("runtime-1")
    }
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

    buildClient({ resolver: resolver as never, outbox: recording.outbox, openCodeRuntime: runtime.runtime })

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

  it("GenericFollowup_DropsUnknownSessionWithoutThrowing", async () => {
    const resolver = vi.fn(() => null)
    buildClient({ resolver: resolver as never, outbox: recording.outbox, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    expect(() => emitFollowup(builder, genericPayload("ignored"))).not.toThrow()
    await flush()

    expect(runtime.followupCalls).toHaveLength(0)
    expect(recording.beforeExecutionCalls).toHaveLength(0)
  })

  it("GenericFollowup_DropsWhenTargetSessionIdMissing", async () => {
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
    buildClient({ resolver: resolver as never, outbox: recording.outbox, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    emitFollowup(builder, {
      target: { kind: "generic", projectId: "proj-1" },
      text: "no sessionId",
    })
    await flush()

    expect(runtime.followupCalls).toHaveLength(0)
    expect(recording.beforeExecutionCalls).toHaveLength(0)
  })

  it("GenericFollowup_DropsWhenTextMissing", async () => {
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
    buildClient({ resolver: resolver as never, outbox: recording.outbox, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    emitFollowup(builder, { ...genericPayload(""), text: "" })
    await flush()

    expect(runtime.followupCalls).toHaveLength(0)
    expect(recording.beforeExecutionCalls).toHaveLength(0)
  })

  it("WorkflowFollowup_InputEntersWorkflowSessionProducerFamily", async () => {
    const resolver = vi.fn((target: { kind: string }) => {
      expect(target.kind).toBe("workflow")
      return { runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }
    })
    buildClient({ resolver: resolver as never, outbox: recording.outbox, openCodeRuntime: runtime.runtime })

    emitFollowup(lastBuilder(), {
      target: {
        kind: "workflow",
        projectId: "proj-1",
        workflowRunId: "wr-1",
        sessionName: "work-1",
        binding: { runtime: "opencode", runtimeSessionId: "runtime-1", runnerId: "runner-1", workDir: "/work/project" },
      },
      text: "tag me",
    })
    await flush()

    expect(recording.beforeExecutionCalls[0]).toMatchObject({
      producerFamily: "workflow-session",
      target: { kind: "workflow", projectId: "proj-1", workflowRunId: "wr-1", sessionName: "work-1" },
    })
  })

  it("WorkflowFollowup_LegacyTopLevelFields_StillResolveToWorkflowTarget", async () => {
    const resolver = vi.fn((target: { kind: string; workflowRunId?: string; sessionName?: string }) => {
      expect(target.kind).toBe("workflow")
      expect(target.workflowRunId).toBe("wr-legacy")
      expect(target.sessionName).toBe("work-legacy")
      return { runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-legacy" }
    })
    buildClient({ resolver: resolver as never, outbox: recording.outbox, openCodeRuntime: runtime.runtime })

    emitFollowup(lastBuilder(), {
      target: {
        kind: "workflow",
        projectId: "proj-legacy",
        workflowRunId: "wr-legacy",
        sessionName: "work-legacy",
        binding: { runtime: "opencode", runtimeSessionId: "runtime-1", runnerId: "runner-1", workDir: "/work/project" },
      },
      text: "legacy ok",
    })
    await flush()

    expect(recording.beforeExecutionCalls).toHaveLength(1)
    expect(recording.beforeExecutionCalls[0]).toMatchObject({
      target: { kind: "workflow", workflowRunId: "wr-legacy", sessionName: "work-legacy" },
    })
    // Resolver-supplied projectId is reflected through `target.projectId` on the
    // runtime handle (not the Session target key in the outbox).
    expect(runtime.followupCalls[0]?.target).toMatchObject({ runtimeSessionId: "runtime-1" })
  })
})