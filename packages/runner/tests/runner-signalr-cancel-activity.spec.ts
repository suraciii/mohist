import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import type { CancelAgentSessionPayload } from "../src/server/runner-signalr.js"
import type { AgentSessionRuntimeEventOutbox, RuntimeEventRecord } from "../src/server/runtime-event-outbox.js"
import type { OpenCodeRuntime } from "../src/runtime/opencode/index.js"
import { makeFakePiRuntime, type FakePiRuntimeHandles } from "./support/pi-runtime-fixture.js"
import {
  buildClient,
  lastBuilder,
  makeFakeRuntime,
  resetBuilders,
  type CapturedBuilder,
} from "./support/followup-handler-fixture.js"
import { setRunnerSignalRExistsCheckerForTest, setRunnerSignalRGitRunnerForTest } from "../src/server/runner-signalr.js"
import { capturedLogs } from "./support/logger-test.js"

// `vi.mock("@microsoft/signalr", ...)` lives in `./support/followup-handler-fixture.ts`.
// It activates once any test file imports from that fixture, so the
// SignalR builder observations below route through the same shared
// captured builder list the test file uses.

function emitCancel(builder: CapturedBuilder, payload: CancelAgentSessionPayload | null | undefined): Promise<unknown> {
  const handler = builder.handlers.get("CancelAgentSession")
  if (!handler) throw new Error("CancelAgentSession handler was not registered")
  return Promise.resolve(handler(payload))
}

interface RecordingOutbox {
  outbox: AgentSessionRuntimeEventOutbox
  producedFactCalls: RuntimeEventRecord[]
  beforeExecutionCalls: RuntimeEventRecord[]
}

function buildRecordingOutbox(): RecordingOutbox {
  const producedFactCalls: RuntimeEventRecord[] = []
  const beforeExecutionCalls: RuntimeEventRecord[] = []
  const outbox: AgentSessionRuntimeEventOutbox = {
    ready: () => true,
    load: async () => {},
    recover: async () => {},
    async enqueueBeforeExecution(record) {
      beforeExecutionCalls.push(record)
    },
    async enqueueProducedFact(record) {
      producedFactCalls.push(record)
    },
    enqueueProducedFactBatch: async () => {},
    kick: async () => {},
    stop: async () => {},
    snapshot() { return [] },
  }
  return { outbox, producedFactCalls, beforeExecutionCalls }
}

let opencode: ReturnType<typeof makeFakeRuntime>
let pi: FakePiRuntimeHandles
let recording: RecordingOutbox

beforeEach(() => {
  resetBuilders()
  opencode = makeFakeRuntime()
  pi = makeFakePiRuntime()
  recording = buildRecordingOutbox()
})

afterEach(() => {
  vi.restoreAllMocks()
  resetBuilders()
  setRunnerSignalRGitRunnerForTest(null)
  setRunnerSignalRExistsCheckerForTest(null)
})

function opencodePayload(): CancelAgentSessionPayload {
  return {
    turnId: "turn-1",
    operationId: "stop-1",
    target: {
      kind: "generic",
      projectId: "proj-1",
      sessionId: "gen-session-1",
      binding: { runtime: "opencode", runtimeSessionId: "runtime-1", runnerId: "runner-1", workDir: "/work/project" },
    },
  }
}

function piPayload(): CancelAgentSessionPayload {
  return {
    turnId: "turn-1",
    operationId: "stop-1",
    target: {
      kind: "generic",
      projectId: "proj-1",
      sessionId: "gen-session-1",
      binding: { runtime: "pi", runtimeSessionId: "/virtual/sessions/one.jsonl", runnerId: "runner-1", workDir: "/workspace" },
    },
  }
}

function workflowPayload(): CancelAgentSessionPayload {
  return {
    target: {
      kind: "workflow",
      projectId: "proj-1",
      workflowRunId: "wr-1",
      sessionName: "work-1",
      binding: { runtime: "opencode", runtimeSessionId: "runtime-1", runnerId: "runner-1", workDir: "/work/project" },
    },
  }
}

describe("RunnerSignalRClient CancelAgentSession activity-fact settlement", () => {
  it("ConfirmedCancel_AgainstActiveSession_EnqueuesBindingGuardedSessionActivityIdle", async () => {
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
    buildClient({
      resolver,
      outbox: recording.outbox,
      openCodeRuntime: opencode.runtime,
      piRuntime: pi.runtime,
    })
    const builder = lastBuilder()

    const reply = (await emitCancel(builder, opencodePayload())) as { state: string; interruptUnconfirmed?: boolean }

    expect(reply).toEqual({ state: "stopped" })
    expect(recording.producedFactCalls).toHaveLength(1)
    expect(recording.beforeExecutionCalls).toHaveLength(0)
    expect(recording.producedFactCalls[0]).toMatchObject({
      producerFamily: "generic-followup",
      target: { kind: "generic", projectId: "proj-1", sessionId: "gen-session-1" },
      runtimeSessionId: "runtime-1",
      acknowledgementPolicy: "successful-response",
      event: {
        type: "session.activity",
        payload: expect.objectContaining({
          activity: "idle",
          status: "completed",
          source: "cancel",
          turnId: "turn-1",
          stopOperationId: "stop-1",
          stopConfirmed: true,
          runtimeSessionId: "runtime-1",
        }),
      },
    })
  })

  it("ConfirmedCancel_AgainstUnknownSession_SettlesActivityToIdle_BindingStillCurrent", async () => {
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
    buildClient({
      resolver,
      outbox: recording.outbox,
      openCodeRuntime: opencode.runtime,
      piRuntime: pi.runtime,
    })
    const builder = lastBuilder()

    const reply = (await emitCancel(builder, opencodePayload())) as { state: string; interruptUnconfirmed?: boolean }

    expect(reply).toEqual({ state: "stopped" })
    const fact = recording.producedFactCalls[0]
    expect(fact.runtimeSessionId).toBe("runtime-1")
    expect(fact.event.type).toBe("session.activity")
    expect(fact.event.payload).toMatchObject({
      activity: "idle",
      status: "completed",
      stopConfirmed: true,
      source: "cancel",
      turnId: "turn-1",
    })
  })

  it("UnconfirmedCancel_EnqueuesSessionActivityUnknown_AndSurfacesInterruptUnconfirmedTrue", async () => {
    pi.setCancelResult({
      ok: true,
      value: { runtimeSessionId: "/virtual/sessions/one.jsonl", workDir: "/workspace", cancelled: true, stopConfirmed: false },
      diagnostics: [{ severity: "error", code: "abort-unconfirmed", message: "still streaming" }],
    })
    const resolver = vi.fn(() => ({ runtimeSessionId: "/virtual/sessions/one.jsonl", workDir: "/workspace", projectId: "proj-1" }))
    buildClient({
      resolver,
      outbox: recording.outbox,
      openCodeRuntime: opencode.runtime,
      piRuntime: pi.runtime,
    })
    const builder = lastBuilder()

    const reply = (await emitCancel(builder, piPayload())) as { state: string; interruptUnconfirmed?: boolean }

    expect(reply).toEqual({ state: "unknown", interruptUnconfirmed: true })
    expect(recording.producedFactCalls).toHaveLength(1)
    expect(recording.producedFactCalls[0]).toMatchObject({
      producerFamily: "generic-followup",
      target: { kind: "generic", projectId: "proj-1", sessionId: "gen-session-1" },
      runtimeSessionId: "/virtual/sessions/one.jsonl",
      acknowledgementPolicy: "successful-response",
      event: {
        type: "session.activity",
        payload: expect.objectContaining({
          activity: "unknown",
          status: "failed",
          stopConfirmed: false,
          source: "cancel",
          turnId: "turn-1",
          stopOperationId: "stop-1",
        }),
      },
    })
  })

  it("CancelFactForSupersededBinding_CarriesTheOutboundBindingRuntimeSessionId_AndAcknowledgementPolicyIsSuccessfulResponse", async () => {
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
    buildClient({
      resolver,
      outbox: recording.outbox,
      openCodeRuntime: opencode.runtime,
      piRuntime: pi.runtime,
    })
    const builder = lastBuilder()

    await emitCancel(builder, opencodePayload())

    const fact = recording.producedFactCalls[0]
    expect(fact.runtimeSessionId).toBe("runtime-1")
    expect(fact.acknowledgementPolicy).toBe("successful-response")
    expect(fact.event.type).toBe("session.activity")
    expect(fact.event.payload).toMatchObject({
      activity: "idle",
      stopConfirmed: true,
      turnId: "turn-1",
    })
  })

  it("Cancel_DoesNotCreateCandidateSession_AndDoesNotInvokeRuntimeCreateSession", async () => {
    const createSessionCalls = vi.fn()
    const cancelCalls: unknown[] = []
    const runtime = {
      ready: () => true,
      diagnostic: () => null,
      async cancel(request: unknown) {
        cancelCalls.push(request)
        return { ok: true, value: { facts: { runtimeSessionId: "runtime-1", workDir: "/work/project", cancelled: true }, diagnostics: [] }, diagnostics: [] }
      },
      async createSession() {
        createSessionCalls()
        return { ok: true, value: { runtimeSessionId: "ses_new", workDir: "/work/project" }, diagnostics: [] }
      },
    } as unknown as OpenCodeRuntime
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
    buildClient({
      resolver,
      outbox: recording.outbox,
      openCodeRuntime: runtime,
      piRuntime: pi.runtime,
    })
    const builder = lastBuilder()

    await emitCancel(builder, opencodePayload())

    expect(createSessionCalls).toHaveBeenCalledTimes(0)
    expect(cancelCalls).toHaveLength(1)
    expect(pi.followupCalls).toHaveLength(0)
    expect(pi.cancelCalls).toHaveLength(0)
    expect(recording.producedFactCalls).toHaveLength(1)
    expect(recording.producedFactCalls[0].event.type).toBe("session.activity")
  })

  it("Cancel_EnqueuesGenericTarget_AndWorkflowTarget_WithCorrectProjectId", async () => {
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
    buildClient({
      resolver,
      outbox: recording.outbox,
      openCodeRuntime: opencode.runtime,
      piRuntime: pi.runtime,
    })
    const builder = lastBuilder()

    await emitCancel(builder, opencodePayload())
    await emitCancel(builder, workflowPayload())

    expect(recording.producedFactCalls).toHaveLength(2)
    expect(recording.producedFactCalls[0].target).toEqual({ kind: "generic", projectId: "proj-1", sessionId: "gen-session-1" })
    expect(recording.producedFactCalls[1].target).toEqual({ kind: "workflow", projectId: "proj-1", workflowRunId: "wr-1", sessionName: "work-1" })
    expect(recording.producedFactCalls[0].producerFamily).toBe("generic-followup")
    expect(recording.producedFactCalls[1].producerFamily).toBe("workflow-session")
  })

  it("Cancel_StaleFactForSupersededBinding_IsCarriedWithTheOutboundBindingRuntimeSessionId", async () => {
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
    buildClient({
      resolver,
      outbox: recording.outbox,
      openCodeRuntime: opencode.runtime,
      piRuntime: pi.runtime,
    })
    const builder = lastBuilder()

    const supersededPayload: CancelAgentSessionPayload = {
      target: {
        kind: "generic",
        projectId: "proj-1",
        sessionId: "gen-session-1",
        binding: { runtime: "opencode", runtimeSessionId: "runtime-old", runnerId: "runner-1", workDir: "/work/project" },
      },
    }

    const reply = (await emitCancel(builder, supersededPayload)) as { state: string; interruptUnconfirmed?: boolean }

    expect(reply).toEqual({ state: "stopped" })
    expect(recording.producedFactCalls).toHaveLength(1)
    expect(recording.producedFactCalls[0].runtimeSessionId).toBe("runtime-old")
    expect(recording.producedFactCalls[0].event.payload).toMatchObject({
      activity: "idle",
      stopConfirmed: true,
    })
  })

  it("Cancel_WithoutOutbox_StillRepliesAndQuietlySkipsFactWrite", async () => {
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
    buildClient({
      resolver,
      outbox: null,
      openCodeRuntime: opencode.runtime,
      piRuntime: pi.runtime,
    })
    const builder = lastBuilder()

    const reply = (await emitCancel(builder, opencodePayload())) as { state: string; interruptUnconfirmed?: boolean }

    expect(reply).toEqual({ state: "stopped" })
    expect(recording.producedFactCalls).toHaveLength(0)
  })

  it("Cancel_FailedEnqueue_LeavesStopRequested", async () => {
    const failingOutbox: AgentSessionRuntimeEventOutbox = {
      ready: () => true,
      load: async () => {},
      recover: async () => {},
      enqueueBeforeExecution: async () => {},
      async enqueueProducedFact() { throw new Error("disk full") },
      enqueueProducedFactBatch: async () => {},
      kick: async () => {},
      stop: async () => {},
      snapshot() { return [] },
    }
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
    buildClient({
      resolver,
      outbox: failingOutbox,
      openCodeRuntime: opencode.runtime,
      piRuntime: pi.runtime,
    })
    const builder = lastBuilder()

    const reply = (await emitCancel(builder, opencodePayload())) as { state: string; interruptUnconfirmed?: boolean }

    expect(reply).toEqual({ state: "stop-requested" })
    expect(capturedLogs()).toEqual(expect.arrayContaining([
      expect.objectContaining({ level: "ERROR", message: "failed to persist cancel activity", fields: expect.objectContaining({ session: "runtime-1", exception: expect.any(Error) }) }),
    ]))
  })
})
