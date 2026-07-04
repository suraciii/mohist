import { afterEach, describe, expect, it } from "vitest"
import { AgentSideConnection, ClientSideConnection, PROTOCOL_VERSION } from "@agentclientprotocol/sdk"
import type { Agent, RequestPermissionRequest, RequestPermissionResponse, SessionNotification, Stream } from "@agentclientprotocol/sdk"
import { acpAgentAction, setAcpProcessFactoryForTest, type AcpProcessHandle } from "../../src/actions/acp-agent.js"
import { setPromptLoaderRegistryForTest } from "../../src/core/prompt.js"
import type { ActionContext } from "../../src/core/types.js"
import { AcpSessionManager, type SharedAcpConnection } from "../../src/runtime/acp-connection.js"
import { ServerConnection } from "../../src/server/connection.js"

afterEach(() => {
  setAcpProcessFactoryForTest(null)
  setPromptLoaderRegistryForTest(null)
})

function baseContext(overrides: Partial<ActionContext> = {}): ActionContext {
  return {
    workflowRunId: "",
    workId: "work-1",
    workType: "agent-job",
    stage: "agent",
    title: "Agent Job",
    uses: "mohist/acp-agent",
    with: {} as never,
    variables: {} as never,
    workDir: "D:/work",
    signal: new AbortController().signal,
    projectId: "project-1",
    ownerKind: "agent-job",
    agentSessionId: "session-abc",
    writeVars: async () => {},
    ...overrides,
  }
}

function linkedStreams(): [Stream, Stream] {
  const clientToAgent = new TransformStream()
  const agentToClient = new TransformStream()
  return [
    { writable: clientToAgent.writable, readable: agentToClient.readable },
    { writable: agentToClient.writable, readable: clientToAgent.readable },
  ]
}

class TranscriptAxisFakeAgent {
  readonly calls: any[] = []
  private connection!: AgentSideConnection
  private readonly emitPlan: "full" = "full"
  private readonly sessionRecord: { acpSessionId: string }

  constructor(sessionRecord: { acpSessionId: string } = { acpSessionId: "acp-session-1" }) {
    this.sessionRecord = sessionRecord
  }

  bind(connection: AgentSideConnection) {
    this.connection = connection
  }

  handler(): Agent {
    const self = this
    return {
      async initialize() {
        return { protocolVersion: PROTOCOL_VERSION, agentInfo: { name: "fake-transcript-axis-agent", version: "0.1.0" }, agentCapabilities: {} }
      },
      async newSession() {
        self.calls.push({ event: "newSession" })
        return { sessionId: self.sessionRecord.acpSessionId }
      },
      async resumeSession(params: { sessionId: string }) {
        self.calls.push({ event: "resumeSession", sessionId: params.sessionId })
        return {}
      },
      async setSessionConfigOption() {
        return { configOptions: [] }
      },
      async unstable_setSessionModel(params: { sessionId: string; modelId: string }) {
        self.calls.push({ event: "unstable_setSessionModel", ...params })
        return {}
      },
      async prompt(params: { sessionId: string }) {
        self.calls.push({ event: "prompt", sessionId: params.sessionId, promptCount: self.calls.filter((c) => c.event === "prompt").length + 1 })
        const promptCount = self.calls.filter((c) => c.event === "prompt").length

        if (self.emitPlan === "full") {
          await self.connection.sessionUpdate({ sessionId: params.sessionId, update: { sessionUpdate: "agent_message_chunk", content: { type: "text", text: "Hello back from the fake agent." } } } as never)
          await self.connection.sessionUpdate({ sessionId: params.sessionId, update: { sessionUpdate: "tool_call", toolCallId: `tool-${promptCount}-a`, title: "Read file", kind: "read", status: "in_progress", rawInput: { path: "README.md" } } } as never)
          await self.connection.sessionUpdate({ sessionId: params.sessionId, update: { sessionUpdate: "tool_call_update", toolCallId: `tool-${promptCount}-a`, title: "Read file", status: "completed", rawOutput: { text: "README contents" } } } as never)
          await self.connection.sessionUpdate({ sessionId: params.sessionId, update: { sessionUpdate: "usage_update", size: 200000, used: 128, cost: { amount: 0.0001, currency: "USD" } } } as never)
        }

        return { stopReason: "end_turn" }
      },
      async closeSession(params: { sessionId: string }) {
        self.calls.push({ event: "closeSession", sessionId: params.sessionId })
      },
      async cancel() {},
      async authenticate() { return {} },
    }
  }
}

class FakeServerConnectionForTranscriptAxis {
  readonly calls: Array<{ event: string; type?: string; payload?: unknown; body?: unknown; sessionId?: string }> = []
  nextGetGenericSession: { acpSessionId?: string | null; workDir?: string; model?: string | null } | null = null
  nextGenericSession: { acpSessionId: string; workDir: string; model?: string | null } = { acpSessionId: "acp-session-1", workDir: "D:/work" }

  async getAgentSession(_projectId: string, sessionId: string) {
    this.calls.push({ event: "getAgentSession", sessionId })
    return this.nextGetGenericSession
  }

  async openAgentSession(_projectId: string, sessionId: string, body: unknown) {
    this.calls.push({ event: "openAgentSession", sessionId, body })
    return this.nextGenericSession
  }

  async attachAgentSession(_projectId: string, sessionId: string, body: unknown) {
    this.calls.push({ event: "attachAgentSession", sessionId, body })
  }

  async agentSessionRuntimeEvents(_projectId: string, sessionId: string, payload: { events?: Array<{ type: string; payload: unknown }>; runtimeEvents?: Array<{ type: string; payload: unknown }> }) {
    const events = payload?.events ?? payload?.runtimeEvents ?? []
    for (const event of events) this.calls.push({ event: "agentSessionRuntimeEvents", sessionId, type: event.type, payload: event.payload })
  }

  async getWorkflowAgentSession() {
    this.calls.push({ event: "getWorkflowAgentSession" })
    return null
  }

  async openWorkflowAgentSession() {
    this.calls.push({ event: "openWorkflowAgentSession" })
    return { acpSessionId: "wf-acp-1", workDir: "D:/work" }
  }

  async attachWorkflowAgentSession() {
    this.calls.push({ event: "attachWorkflowAgentSession" })
  }

  async workflowAgentSessionRuntimeEvents() {
    this.calls.push({ event: "workflowAgentSessionRuntimeEvents" })
  }
}

function createTranscriptAxisFixture(opts: { nextGenericSession?: { acpSessionId: string; workDir: string; model?: string | null } } = {}) {
  const agent = new TranscriptAxisFakeAgent()
  const [clientStream, agentStream] = linkedStreams()
  const sessionUpdateHandlers = new Map<string, (notification: SessionNotification) => Promise<void>>()
  const permissionHandlers = new Map<string, (params: RequestPermissionRequest) => Promise<RequestPermissionResponse>>()
  const clientConnection = new ClientSideConnection(() => ({
    sessionUpdate: async (notification) => {
      await (sessionUpdateHandlers.get(notification.sessionId) ?? (async () => {}))(notification)
    },
    requestPermission: async (params) => await (permissionHandlers.get(params.sessionId) ?? (async () => ({ outcome: { outcome: "cancelled" } } as RequestPermissionResponse)))(params),
  }), clientStream)
  const agentConnection = new AgentSideConnection(() => agent.handler(), agentStream)
  agent.bind(agentConnection)

  const serverConnection = new FakeServerConnectionForTranscriptAxis()
  if (opts.nextGenericSession) serverConnection.nextGenericSession = opts.nextGenericSession
  const acpSessionManager = new AcpSessionManager()
  const acpConnection: SharedAcpConnection = {
    connection: clientConnection,
    processPid: 9999,
    setSessionHandlers(sessionId, sessionUpdate, requestPermission) {
      sessionUpdateHandlers.set(sessionId, sessionUpdate)
      permissionHandlers.set(sessionId, requestPermission)
    },
    clearSessionHandlers(sessionId) {
      sessionUpdateHandlers.delete(sessionId)
      permissionHandlers.delete(sessionId)
    },
    async shutdown() {},
  }
  setAcpProcessFactoryForTest(() => createFakeProcessForTranscriptAxis(agent))

  return {
    agent,
    serverConnection,
    acpSessionManager,
    context(overrides: Partial<ActionContext> = {}): ActionContext {
      return {
        ...baseContext(overrides),
        acpConnection,
        acpSessionManager,
        serverConnection: serverConnection as unknown as ServerConnection,
        ...overrides,
      }
    },
  }
}

function createFakeProcessForTranscriptAxis(agent: TranscriptAxisFakeAgent): AcpProcessHandle {
  const [clientStream, agentStream] = linkedStreams()
  const connection = new AgentSideConnection(() => agent.handler(), agentStream)
  agent.bind(connection)
  return {
    stream: clientStream,
    processPid: 1234,
    spawnFailure: new Promise<never>(() => {}),
    exitFailure: new Promise<never>(() => {}),
    markInitialized() {},
    exitCode() { return 0 },
    async cleanup() {},
  }
}

function runtimeEventsByType(serverConnection: FakeServerConnectionForTranscriptAxis, type: string) {
  return serverConnection.calls
    .filter((entry) => entry.event === "agentSessionRuntimeEvents" && entry.type === type)
}

describe("generic (sessionId) transcript axis — issue-345 reproduction harness", () => {
  it("MapsPolledDispatchAgentSessionIdToActionContext_AndGenericAxisEmitsAllTranscriptEvents", async () => {
    const fixture = createTranscriptAxisFixture()

    const result = await acpAgentAction(fixture.context({ with: { prompt: "do the work" } as never }))

    expect(result.status).toBe("success")

    const trace = fixture.serverConnection.calls.map((entry) => entry.event)
    expect(trace).toContain("getAgentSession")
    expect(trace).toContain("openAgentSession")
    expect(trace).toContain("attachAgentSession")
    expect(trace).toContain("agentSessionRuntimeEvents")

    const messageDeltas = runtimeEventsByType(fixture.serverConnection, "message.delta")
    expect(messageDeltas.length).toBeGreaterThan(0)
    expect(messageDeltas.every((entry) => entry.sessionId === "session-abc")).toBe(true)
    const messagePayloads = messageDeltas.map((entry) => entry.payload as Record<string, unknown>)
    expect(messagePayloads.some((payload) => payload?.content && (payload.content as Record<string, unknown>)?.text === "Hello back from the fake agent.")).toBe(true)

    const toolStarted = runtimeEventsByType(fixture.serverConnection, "tool_call.started")
    const toolUpdated = runtimeEventsByType(fixture.serverConnection, "tool_call.updated")
    const toolCompleted = runtimeEventsByType(fixture.serverConnection, "tool_call.completed")
    expect(toolStarted.length).toBeGreaterThan(0)
    expect(toolCompleted.length).toBeGreaterThan(0)
    expect(toolStarted.every((entry) => entry.sessionId === "session-abc")).toBe(true)
    expect(toolCompleted.every((entry) => entry.sessionId === "session-abc")).toBe(true)

    const usageEvents = runtimeEventsByType(fixture.serverConnection, "usage.updated")
    expect(usageEvents.length).toBeGreaterThan(0)
    expect(usageEvents.every((entry) => entry.sessionId === "session-abc")).toBe(true)
    const usagePayload = usageEvents[0].payload as Record<string, unknown>
    expect(usagePayload?.contextWindowSize).toBe(200000)
    expect(usagePayload?.contextWindowUsed).toBe(128)
    expect(usagePayload?.costAmount).toBe(0.0001)
    expect(usagePayload?.costCurrency).toBe("USD")

    const sessionInput = runtimeEventsByType(fixture.serverConnection, "session.input")
    expect(sessionInput.length).toBeGreaterThan(0)
    expect(sessionInput.every((entry) => entry.sessionId === "session-abc")).toBe(true)

    const sessionClosed = runtimeEventsByType(fixture.serverConnection, "session.closed")
    expect(sessionClosed).toHaveLength(0)

    expect(trace).not.toContain("openWorkflowAgentSession")
    expect(trace).not.toContain("attachWorkflowAgentSession")
    expect(trace).not.toContain("workflowAgentSessionRuntimeEvents")
  })

  it("GenericAxisPreservesSessionIdAcrossFollowUp_ReusingCachedAcpSession_DeliversAnotherTurnOfEvents", async () => {
    const fixture = createTranscriptAxisFixture()

    const first = await acpAgentAction(fixture.context({ with: { prompt: "first" } as never }))
    const second = await acpAgentAction(fixture.context({ with: { prompt: "second" } as never }))

    expect(first.status).toBe("success")
    expect(second.status).toBe("success")

    const prompts = fixture.agent.calls.filter((call) => call.event === "prompt")
    expect(prompts).toHaveLength(2)
    expect(prompts.every((call) => call.sessionId === "acp-session-1")).toBe(true)

    const openAgentCalls = fixture.serverConnection.calls.filter((c) => c.event === "openAgentSession")
    expect(openAgentCalls.length).toBeGreaterThanOrEqual(1)

    const messageDeltasTurn1 = runtimeEventsByType(fixture.serverConnection, "message.delta")
    expect(messageDeltasTurn1.length).toBeGreaterThanOrEqual(2)

    const sessionInputs = runtimeEventsByType(fixture.serverConnection, "session.input")
    expect(sessionInputs.length).toBeGreaterThanOrEqual(2)

    for (const eventType of ["message.delta", "tool_call.started", "tool_call.completed", "usage.updated"]) {
      const eventsForType = runtimeEventsByType(fixture.serverConnection, eventType)
      expect(eventsForType.length).toBeGreaterThan(0)
      expect(eventsForType.every((entry) => entry.sessionId === "session-abc")).toBe(true)
    }
  })

  it("GenericAxisContextAgentSessionId_EqualsPolledDispatchEnvelope_AndDoesNotCarryWorkflowRunId", async () => {
    const fixture = createTranscriptAxisFixture()

    const result = await acpAgentAction(fixture.context({ with: { prompt: "do the work" } as never }))

    expect(result.status).toBe("success")

    expect(fixture.context().agentSessionId).toBe("session-abc")
    expect(fixture.context().workflowRunId).toBe("")
    expect(fixture.context().ownerKind).toBe("agent-job")

    const genericEvents = fixture.serverConnection.calls.filter((c) => c.event === "agentSessionRuntimeEvents")
    expect(genericEvents.length).toBeGreaterThan(0)
    expect(genericEvents.every((entry) => entry.sessionId === "session-abc")).toBe(true)
    expect(genericEvents.every((entry) => entry.sessionId !== undefined && entry.sessionId !== "")).toBe(true)
  })
})
