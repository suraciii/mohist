import { afterEach, describe, expect, it, vi } from "vitest"
import * as signalR from "@microsoft/signalr"
import { resolveSessionTarget, RunnerSignalRClient, type ReceiveFollowupPayload, setRunnerSignalRExistsCheckerForTest, setRunnerSignalRGitRunnerForTest } from "../src/server/runner-signalr.js"
import type { SessionTarget } from "../src/runtime/acp-connection.js"


interface CapturedBuilder {
  handlers: Map<string, (...args: unknown[]) => unknown>
  connection: FakeConnection
}

const builders: CapturedBuilder[] = []
let nextConnectionId = 0

afterEach(() => {
  vi.restoreAllMocks()
  builders.length = 0
  nextConnectionId = 0
  setRunnerSignalRGitRunnerForTest(null)
  setRunnerSignalRExistsCheckerForTest(null)
})

interface FakeConnection {
  state: signalR.HubConnectionState
  connectionId: string | null
  start: ReturnType<typeof vi.fn>
  stop: ReturnType<typeof vi.fn>
  invoke: ReturnType<typeof vi.fn>
  on: ReturnType<typeof vi.fn>
  onreconnected: ((cb: (id?: string) => void) => void) | undefined
  _reconnectHandler?: (connectionId?: string) => void
}

function makeFakeConnection(): FakeConnection {
  const conn: FakeConnection = {
    state: signalR.HubConnectionState.Disconnected,
    connectionId: null,
    start: vi.fn(),
    stop: vi.fn(),
    invoke: vi.fn(),
    on: vi.fn(),
    onreconnected: undefined,
  }
  conn.start.mockImplementation(async () => {
    conn.state = signalR.HubConnectionState.Connected
    conn.connectionId = `conn-${++nextConnectionId}`
  })
  conn.stop.mockImplementation(async () => {
    conn.state = signalR.HubConnectionState.Disconnected
    conn.connectionId = null
  })
  conn.onreconnected = ((cb: (id?: string) => void) => {
    conn._reconnectHandler = cb
  }) as FakeConnection["onreconnected"]
  return conn
}

vi.mock("@microsoft/signalr", () => {
  return {
    HubConnectionBuilder: class {
      private _handlers: Map<string, (...args: unknown[]) => unknown> = new Map()
      private _connection: FakeConnection = makeFakeConnection()
      withUrl(_url: string) {
        builders.push({ handlers: this._handlers, connection: this._connection })
        return this
      }
      withAutomaticReconnect(_reconnectPolicy: number[]) {
        return this
      }
      build() {
        this._connection.on.mockImplementation((event: string, handler: (...args: unknown[]) => unknown) => {
          this._handlers.set(event, handler)
          return this._connection
        })
        return this._connection as unknown as signalR.HubConnection
      }
    },
    HubConnectionState: {
      Disconnected: "Disconnected",
      Connecting: "Connecting",
      Connected: "Connected",
      Disconnecting: "Disconnecting",
      Reconnecting: "Reconnecting",
    },
  }
})

function lastBuilder(): CapturedBuilder {
  const builder = builders.at(-1)
  if (!builder) throw new Error("no captured builder; construct a RunnerSignalRClient first")
  return builder
}

type AnyFn = (...args: any[]) => any

interface MockServerConnection {
  workflowAgentSessionRuntimeEvents: AnyFn
  agentSessionRuntimeEvents?: AnyFn
}

interface MockConnection {
  prompt: AnyFn
  cancel?: AnyFn
}

function buildClient(opts: {
  resolver?: AnyFn | null
  serverConnection?: MockServerConnection | null
}) {
  builders.length = 0
  const defaultServerConnection: MockServerConnection = {
    workflowAgentSessionRuntimeEvents: vi.fn(async () => undefined),
    agentSessionRuntimeEvents: vi.fn(async () => undefined),
  }
  const serverConnection = opts.serverConnection === undefined ? defaultServerConnection : opts.serverConnection
  const resolver = opts.resolver === undefined ? null : opts.resolver
  const client = new RunnerSignalRClient(
    "http://localhost:3456",
    "runner-1",
    "/tmp/mohist/projects",
    null,
    {
      serverConnection: serverConnection as never,
      followupTargetResolver: resolver as never,
    },
  )
  return client
}

function emitFollowup(builder: CapturedBuilder, payload: ReceiveFollowupPayload | null | undefined) {
  const handler = builder.handlers.get("ReceiveFollowup")
  if (!handler) throw new Error("ReceiveFollowup handler was not registered")
  handler(payload)
}

async function invokeFollowup(builder: CapturedBuilder, payload: ReceiveFollowupPayload | null | undefined) {
  const handler = builder.handlers.get("ReceiveFollowup")
  if (!handler) throw new Error("ReceiveFollowup handler was not registered")
  return await handler(payload)
}

async function flush() {
  await new Promise((resolve) => setImmediate(resolve))
}

describe("RunnerSignalRClient ReceiveFollowup handler", () => {
  it("Followup_FireAndForgetPromptCallsConnectionPromptWithoutAwait", async () => {
    const prompt = vi.fn(async () => undefined)
    const connection: MockConnection = { prompt }
    const resolver = vi.fn(() => ({ connection: connection as never, sessionId: "acp-1", projectId: "proj-1" }))
    const runtimeEvents = vi.fn(async () => undefined)
    const serverConnection: MockServerConnection = { workflowAgentSessionRuntimeEvents: runtimeEvents }

    buildClient({ resolver, serverConnection })
    const builder = lastBuilder()

    emitFollowup(builder, { workflowRunId: "wr-1", sessionName: "work-1", text: "add a logout button" })
    await flush()

    expect(prompt).toHaveBeenCalledTimes(1)
    expect(prompt).toHaveBeenCalledWith({
      sessionId: "acp-1",
      prompt: [{ type: "text", text: "add a logout button" }],
    })
  })

  it("Followup_ReturnsImmediatelyWithoutAwaitingPromptResolution", async () => {
    let resolvePrompt!: (value: unknown) => void
    const prompt = vi.fn(() => new Promise((resolve) => { resolvePrompt = resolve }))
    const connection: MockConnection = { prompt }
    const resolver = vi.fn(() => ({ connection: connection as never, sessionId: "acp-1", projectId: "proj-1" }))
    const runtimeEvents = vi.fn(async () => undefined)
    const serverConnection: MockServerConnection = { workflowAgentSessionRuntimeEvents: runtimeEvents }

    buildClient({ resolver, serverConnection })
    const builder = lastBuilder()

    emitFollowup(builder, { workflowRunId: "wr-1", sessionName: "work-1", text: "ship it" })
    await flush()
    expect(prompt).toHaveBeenCalledTimes(1)
    resolvePrompt(undefined)
    await flush()
  })

  it("Followup_PromptsEvenWhenRuntimeEventsEmitIsStillPending", async () => {
    const prompt = vi.fn(async () => undefined)
    const connection: MockConnection = { prompt }
    const resolver = vi.fn(() => ({ connection: connection as never, sessionId: "acp-1", projectId: "proj-1" }))
    const runtimeEvents = vi.fn(() => new Promise(() => undefined))
    const serverConnection: MockServerConnection = { workflowAgentSessionRuntimeEvents: runtimeEvents }

    buildClient({ resolver, serverConnection })
    const builder = lastBuilder()

    emitFollowup(builder, { workflowRunId: "wr-1", sessionName: "work-1", text: "ship while event is pending" })
    await flush()

    expect(runtimeEvents).toHaveBeenCalledTimes(1)
    expect(prompt).toHaveBeenCalledTimes(1)
    expect(prompt).toHaveBeenCalledWith({
      sessionId: "acp-1",
      prompt: [{ type: "text", text: "ship while event is pending" }],
    })
  })

  it("Followup_EmitsSessionInputEventBeforeCallingPrompt", async () => {
    const callOrder: string[] = []
    const prompt = vi.fn(async () => {
      callOrder.push("prompt")
    })
    const connection: MockConnection = { prompt }
    const resolver = vi.fn(() => ({ connection: connection as never, sessionId: "acp-1", projectId: "proj-1" }))
    const runtimeEvents = vi.fn(async () => {
      callOrder.push("session.input")
    })
    const serverConnection: MockServerConnection = { workflowAgentSessionRuntimeEvents: runtimeEvents }

    buildClient({ resolver, serverConnection })
    const builder = lastBuilder()

    emitFollowup(builder, { workflowRunId: "wr-1", sessionName: "work-1", text: "fix the typo" })
    await flush()
    await flush()

    expect(runtimeEvents).toHaveBeenCalledTimes(1)
    expect(callOrder).toEqual(["session.input", "prompt"])
  })

  it("Followup_TagsEventWithPromptKindFollowup", async () => {
    const prompt = vi.fn(async () => undefined)
    const connection: MockConnection = { prompt }
    const resolver = vi.fn(() => ({ connection: connection as never, sessionId: "acp-1", projectId: "proj-1" }))
    const runtimeEvents = vi.fn(async () => undefined)
    const serverConnection: MockServerConnection = { workflowAgentSessionRuntimeEvents: runtimeEvents }

    buildClient({ resolver, serverConnection })
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
              runtimeSessionId: "acp-1",
              source: "followup",
            }),
          }),
        ],
      }),
      expect.any(AbortSignal),
    )
    const workflowCalls = runtimeEvents.mock.calls as unknown as Array<[
      string,
      string,
      string,
      { runtimeEvents: Array<{ payload: Record<string, unknown> }> },
      AbortSignal,
    ]>
    const workflowEventBatch = workflowCalls[0]?.[3]
    expect(workflowEventBatch.runtimeEvents[0]?.payload).not.toHaveProperty("acpSessionId")
  })

  it("Followup_DropsWhenResolverReturnsNullAndDoesNotThrow", async () => {
    const prompt = vi.fn(async () => undefined)
    const runtimeEvents = vi.fn(async () => undefined)
    const resolver = vi.fn(() => null)
    const serverConnection: MockServerConnection = { workflowAgentSessionRuntimeEvents: runtimeEvents }

    buildClient({ resolver, serverConnection })
    const builder = lastBuilder()

    expect(() => emitFollowup(builder, { workflowRunId: "wr-1", sessionName: "work-1", text: "ignored" })).not.toThrow()
    await flush()

    expect(prompt).not.toHaveBeenCalled()
    expect(runtimeEvents).not.toHaveBeenCalled()
  })

  it("Followup_ReturnsMissingWhenTheRuntimeSessionCannotBeResolved", async () => {
    const resolver = vi.fn(() => null)
    buildClient({ resolver })

    await expect(invokeFollowup(lastBuilder(), {
      workflowRunId: "wr-1", sessionName: "work-1", text: "resume",
    })).resolves.toEqual({ accepted: false, error: "missing" })
  })

  it("Followup_DropsWhenResolverThrows", async () => {
    const prompt = vi.fn(async () => undefined)
    const runtimeEvents = vi.fn(async () => undefined)
    const resolver = vi.fn(() => { throw new Error("resolver boom") })
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)
    const serverConnection: MockServerConnection = { workflowAgentSessionRuntimeEvents: runtimeEvents }

    buildClient({ resolver, serverConnection })
    const builder = lastBuilder()

    expect(() => emitFollowup(builder, { workflowRunId: "wr-1", sessionName: "work-1", text: "ignored" })).not.toThrow()
    await flush()

    expect(prompt).not.toHaveBeenCalled()
    expect(runtimeEvents).not.toHaveBeenCalled()
    expect(errorSpy).toHaveBeenCalled()
    errorSpy.mockRestore()
  })

  it("Followup_CatchesPromptRejectionAndLogsWithoutThrowing", async () => {
    const prompt = vi.fn(async () => { throw new Error("opencode crashed") })
    const connection: MockConnection = { prompt }
    const resolver = vi.fn(() => ({ connection: connection as never, sessionId: "acp-1", projectId: "proj-1" }))
    const runtimeEvents = vi.fn(async () => undefined)
    const serverConnection: MockServerConnection = { workflowAgentSessionRuntimeEvents: runtimeEvents }
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)

    buildClient({ resolver, serverConnection })
    const builder = lastBuilder()

    expect(() => emitFollowup(builder, { workflowRunId: "wr-1", sessionName: "work-1", text: "boom" })).not.toThrow()
    await flush()
    await flush()

    expect(prompt).toHaveBeenCalledTimes(1)
    expect(errorSpy).toHaveBeenCalledWith("followup connection.prompt rejected:", expect.stringContaining("opencode crashed"))
    errorSpy.mockRestore()
  })

  it("Followup_ContinuesToPromptEvenIfRuntimeEventsEmitFails", async () => {
    const prompt = vi.fn(async () => undefined)
    const connection: MockConnection = { prompt }
    const resolver = vi.fn(() => ({ connection: connection as never, sessionId: "acp-1", projectId: "proj-1" }))
    const runtimeEvents = vi.fn(async () => { throw new Error("server unreachable") })
    const serverConnection: MockServerConnection = { workflowAgentSessionRuntimeEvents: runtimeEvents }
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)

    buildClient({ resolver, serverConnection })
    const builder = lastBuilder()

    emitFollowup(builder, { workflowRunId: "wr-1", sessionName: "work-1", text: "keep going" })
    await flush()
    await flush()

    expect(runtimeEvents).toHaveBeenCalledTimes(1)
    expect(prompt).toHaveBeenCalledTimes(1)
    expect(errorSpy).toHaveBeenCalledWith("failed to emit followup session.input event:", expect.any(Error))
    errorSpy.mockRestore()
  })

  it("Followup_DropsPayloadWhenTextIsMissing", async () => {
    const prompt = vi.fn(async () => undefined)
    const connection: MockConnection = { prompt }
    const resolver = vi.fn(() => ({ connection: connection as never, sessionId: "acp-1", projectId: "proj-1" }))
    const runtimeEvents = vi.fn(async () => undefined)
    const serverConnection: MockServerConnection = { workflowAgentSessionRuntimeEvents: runtimeEvents }

    buildClient({ resolver, serverConnection })
    const builder = lastBuilder()

    emitFollowup(builder, { workflowRunId: "wr-1", sessionName: "work-1", text: "" })
    await flush()

    expect(prompt).not.toHaveBeenCalled()
    expect(runtimeEvents).not.toHaveBeenCalled()
  })

  it("Followup_DropsPayloadWhenResolverIsNull", async () => {
    const prompt = vi.fn(async () => undefined)
    const runtimeEvents = vi.fn(async () => undefined)
    const serverConnection: MockServerConnection = { workflowAgentSessionRuntimeEvents: runtimeEvents }

    buildClient({ resolver: null, serverConnection })
    const builder = lastBuilder()

    emitFollowup(builder, { workflowRunId: "wr-1", sessionName: "work-1", text: "noop" })
    await flush()

    expect(prompt).not.toHaveBeenCalled()
    expect(runtimeEvents).not.toHaveBeenCalled()
  })

  it("Followup_DropsPayloadWhenServerConnectionIsNull", async () => {
    const prompt = vi.fn(async () => undefined)
    const resolver = vi.fn(() => ({ connection: { prompt } as never, sessionId: "acp-1", projectId: "proj-1" }))

    buildClient({ resolver, serverConnection: null })
    const builder = lastBuilder()

    emitFollowup(builder, { workflowRunId: "wr-1", sessionName: "work-1", text: "noop" })
    await flush()

    expect(prompt).not.toHaveBeenCalled()
  })

  it("Followup_DropsNullOrUndefinedPayload", async () => {
    const prompt = vi.fn(async () => undefined)
    const resolver = vi.fn(() => ({ connection: { prompt } as never, sessionId: "acp-1", projectId: "proj-1" }))
    const runtimeEvents = vi.fn(async () => undefined)
    const serverConnection: MockServerConnection = { workflowAgentSessionRuntimeEvents: runtimeEvents }

    buildClient({ resolver, serverConnection })
    const builder = lastBuilder()

    emitFollowup(builder, null)
    emitFollowup(builder, undefined)
    await flush()

    expect(prompt).not.toHaveBeenCalled()
    expect(runtimeEvents).not.toHaveBeenCalled()
  })
})

describe("RunnerSignalRClient routes follow-ups to generic sessions", () => {
  function genericPayload(text: string): ReceiveFollowupPayload {
    return {
      target: { kind: "generic", projectId: "proj-1", sessionId: "gen-session-1" },
      text,
    }
  }

  it("GenericFollowup_LocatesSessionByGenericKey_AndCallsConnectionPrompt", async () => {
    const prompt = vi.fn(async () => undefined)
    const connection: MockConnection = { prompt }
    const resolver = vi.fn((target: SessionTarget) => {
      expect(target.kind).toBe("generic")
      expect((target as { sessionId: string }).sessionId).toBe("gen-session-1")
      return { connection: connection as never, sessionId: "acp-1", projectId: "proj-1" }
    })
    const workflowRuntimeEvents = vi.fn(async () => undefined)
    const agentSessionRuntimeEvents = vi.fn(async () => undefined)
    const serverConnection: MockServerConnection = {
      workflowAgentSessionRuntimeEvents: workflowRuntimeEvents,
      agentSessionRuntimeEvents,
    }

    buildClient({ resolver, serverConnection })
    const builder = lastBuilder()

    emitFollowup(builder, genericPayload("add a logout route"))
    await flush()

    expect(prompt).toHaveBeenCalledTimes(1)
    expect(prompt).toHaveBeenCalledWith({
      sessionId: "acp-1",
      prompt: [{ type: "text", text: "add a logout route" }],
    })
    expect(workflowRuntimeEvents).not.toHaveBeenCalled()
    expect(agentSessionRuntimeEvents).toHaveBeenCalledTimes(1)
  })

  it("GenericFollowup_EmitsSessionInputViaAgentSessionRuntimeEventsEndpoint", async () => {
    const prompt = vi.fn(async () => undefined)
    const connection: MockConnection = { prompt }
    const resolver = vi.fn(() => ({ connection: connection as never, sessionId: "acp-1", projectId: "proj-1" }))
    const workflowRuntimeEvents = vi.fn(async () => undefined)
    const agentSessionRuntimeEvents = vi.fn(async () => undefined)
    const serverConnection: MockServerConnection = {
      workflowAgentSessionRuntimeEvents: workflowRuntimeEvents,
      agentSessionRuntimeEvents,
    }

    buildClient({ resolver, serverConnection })
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
    const genericCalls = agentSessionRuntimeEvents.mock.calls as unknown as Array<[
      string,
      string,
      { runtimeEvents: Array<{ payload: Record<string, unknown> }> },
      AbortSignal,
    ]>
    const genericEventBatch = genericCalls[0]?.[2]
    expect(genericEventBatch.runtimeEvents[0]?.payload).not.toHaveProperty("acpSessionId")
    expect(workflowRuntimeEvents).not.toHaveBeenCalled()
  })

  it("GenericFollowup_ContinuesToPromptEvenIfAgentSessionRuntimeEventsEmitFails", async () => {
    const prompt = vi.fn(async () => undefined)
    const connection: MockConnection = { prompt }
    const resolver = vi.fn(() => ({ connection: connection as never, sessionId: "acp-1", projectId: "proj-1" }))
    const workflowRuntimeEvents = vi.fn(async () => undefined)
    const agentSessionRuntimeEvents = vi.fn(async () => { throw new Error("server unreachable") })
    const serverConnection: MockServerConnection = {
      workflowAgentSessionRuntimeEvents: workflowRuntimeEvents,
      agentSessionRuntimeEvents,
    }
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)

    buildClient({ resolver, serverConnection })
    const builder = lastBuilder()

    emitFollowup(builder, genericPayload("keep going"))
    await flush()
    await flush()

    expect(agentSessionRuntimeEvents).toHaveBeenCalledTimes(1)
    expect(prompt).toHaveBeenCalledTimes(1)
    expect(errorSpy).toHaveBeenCalledWith("failed to emit followup session.input event:", expect.any(Error))
    errorSpy.mockRestore()
  })

  it("GenericFollowup_DropsUnknownSessionWithoutThrowing", async () => {
    const prompt = vi.fn(async () => undefined)
    const resolver = vi.fn(() => null)
    const workflowRuntimeEvents = vi.fn(async () => undefined)
    const agentSessionRuntimeEvents = vi.fn(async () => undefined)
    const serverConnection: MockServerConnection = {
      workflowAgentSessionRuntimeEvents: workflowRuntimeEvents,
      agentSessionRuntimeEvents,
    }

    buildClient({ resolver, serverConnection })
    const builder = lastBuilder()

    expect(() => emitFollowup(builder, genericPayload("ignored"))).not.toThrow()
    await flush()

    expect(prompt).not.toHaveBeenCalled()
    expect(workflowRuntimeEvents).not.toHaveBeenCalled()
    expect(agentSessionRuntimeEvents).not.toHaveBeenCalled()
  })

  it("GenericFollowup_DropsWhenTargetSessionIdMissing", async () => {
    const prompt = vi.fn(async () => undefined)
    const resolver = vi.fn(() => ({ connection: { prompt } as never, sessionId: "acp-1", projectId: "proj-1" }))
    const workflowRuntimeEvents = vi.fn(async () => undefined)
    const agentSessionRuntimeEvents = vi.fn(async () => undefined)
    const serverConnection: MockServerConnection = {
      workflowAgentSessionRuntimeEvents: workflowRuntimeEvents,
      agentSessionRuntimeEvents,
    }

    buildClient({ resolver, serverConnection })
    const builder = lastBuilder()

    emitFollowup(builder, {
      target: { kind: "generic", projectId: "proj-1" },
      text: "no sessionId",
    })
    await flush()

    expect(prompt).not.toHaveBeenCalled()
    expect(workflowRuntimeEvents).not.toHaveBeenCalled()
    expect(agentSessionRuntimeEvents).not.toHaveBeenCalled()
  })

  it("GenericFollowup_DropsWhenTextMissing", async () => {
    const prompt = vi.fn(async () => undefined)
    const resolver = vi.fn(() => ({ connection: { prompt } as never, sessionId: "acp-1", projectId: "proj-1" }))
    const workflowRuntimeEvents = vi.fn(async () => undefined)
    const agentSessionRuntimeEvents = vi.fn(async () => undefined)
    const serverConnection: MockServerConnection = {
      workflowAgentSessionRuntimeEvents: workflowRuntimeEvents,
      agentSessionRuntimeEvents,
    }

    buildClient({ resolver, serverConnection })
    const builder = lastBuilder()

    emitFollowup(builder, { ...genericPayload(""), text: "" })
    await flush()

    expect(prompt).not.toHaveBeenCalled()
    expect(workflowRuntimeEvents).not.toHaveBeenCalled()
    expect(agentSessionRuntimeEvents).not.toHaveBeenCalled()
  })

  it("WorkflowFollowup_StillUsesWorkflowRuntimeEventsEndpoint_WhenTargetShapeCarriesIt", async () => {
    const prompt = vi.fn(async () => undefined)
    const connection: MockConnection = { prompt }
    const resolver = vi.fn((target: SessionTarget) => {
      expect(target.kind).toBe("workflow")
      return { connection: connection as never, sessionId: "acp-1", projectId: "proj-1" }
    })
    const workflowRuntimeEvents = vi.fn(async () => undefined)
    const agentSessionRuntimeEvents = vi.fn(async () => undefined)
    const serverConnection: MockServerConnection = {
      workflowAgentSessionRuntimeEvents: workflowRuntimeEvents,
      agentSessionRuntimeEvents,
    }

    buildClient({ resolver, serverConnection })
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
    expect(prompt).toHaveBeenCalledTimes(1)
  })

  it("WorkflowFollowup_LegacyTopLevelFields_StillResolveToWorkflowTarget", async () => {
    const prompt = vi.fn(async () => undefined)
    const connection: MockConnection = { prompt }
    const resolver = vi.fn((target: SessionTarget) => {
      expect(target.kind).toBe("workflow")
      if (target.kind === "workflow") {
        expect(target.workflowRunId).toBe("wr-legacy")
        expect(target.sessionName).toBe("work-legacy")
      }
      return { connection: connection as never, sessionId: "acp-1", projectId: "proj-legacy" }
    })
    const workflowRuntimeEvents = vi.fn(async () => undefined)
    const agentSessionRuntimeEvents = vi.fn(async () => undefined)
    const serverConnection: MockServerConnection = {
      workflowAgentSessionRuntimeEvents: workflowRuntimeEvents,
      agentSessionRuntimeEvents,
    }

    buildClient({ resolver, serverConnection })
    const builder = lastBuilder()

    // Older server builds only populate top-level workflowRunId/sessionName
    // and emit no `target` field. The handler must still resolve them
    // (the workflowRunId/sessionName fallback inside `resolveSessionTarget`).
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
    expect(prompt).toHaveBeenCalledTimes(1)
  })
})

describe("resolveSessionTarget", () => {
  it("PrefersTargetField_WhenPresent", () => {
    const payload: ReceiveFollowupPayload = {
      workflowRunId: "wr-ignored",
      sessionName: "name-ignored",
      target: { kind: "generic", projectId: "proj-1", sessionId: "gen-1" },
      text: "x",
    }
    expect(resolveSessionTarget(payload)).toEqual({
      kind: "generic",
      projectId: "proj-1",
      sessionId: "gen-1",
    })
  })

  it("ReturnsNull_WhenGenericTargetMissingSessionId", () => {
    const payload: ReceiveFollowupPayload = {
      target: { kind: "generic", projectId: "proj-1" },
      text: "x",
    }
    expect(resolveSessionTarget(payload)).toBeNull()
  })

  it("ReturnsNull_WhenWorkflowTargetMissingSessionName", () => {
    const payload: ReceiveFollowupPayload = {
      target: { kind: "workflow", projectId: "proj-1", workflowRunId: "wr-1" },
      text: "x",
    }
    expect(resolveSessionTarget(payload)).toBeNull()
  })

  it("FallsBackToLegacyWorkflowTopLevelFields_WhenNoTarget", () => {
    const payload: ReceiveFollowupPayload = {
      workflowRunId: "wr-1",
      sessionName: "work-1",
      text: "x",
    }
    expect(resolveSessionTarget(payload)).toEqual({
      kind: "workflow",
      projectId: "",
      workflowRunId: "wr-1",
      sessionName: "work-1",
    })
  })

  it("ReturnsNull_WhenNoTargetAndNoLegacyFields", () => {
    const payload: ReceiveFollowupPayload = { text: "x" }
    expect(resolveSessionTarget(payload)).toBeNull()
  })

  it("ReturnsNull_OnUnknownTargetKind", () => {
    const payload: ReceiveFollowupPayload = {
      target: { kind: "weird" as unknown as "workflow", projectId: "proj-1" },
      text: "x",
    }
    expect(resolveSessionTarget(payload)).toBeNull()
  })
})
