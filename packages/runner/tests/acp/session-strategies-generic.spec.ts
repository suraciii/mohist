import { afterEach, describe, expect, it, vi } from "vitest"
import { AgentSideConnection, ClientSideConnection, PROTOCOL_VERSION } from "@agentclientprotocol/sdk"
import type { Agent, RequestPermissionRequest, RequestPermissionResponse, SessionNotification, Stream } from "@agentclientprotocol/sdk"
import { acpAgentAction, setAcpProcessFactoryForTest, type AcpProcessHandle } from "../../src/actions/acp-agent.js"
import { stringInput } from "../../src/core/json.js"
import { setPromptLoaderRegistryForTest } from "../../src/core/prompt.js"
import type { ActionContext } from "../../src/core/types.js"
import { AcpSessionManager, type SessionTarget, type SharedAcpConnection } from "../../src/runtime/acp-connection.js"
import { ServerConnection } from "../../src/server/connection.js"

afterEach(() => {
  setAcpProcessFactoryForTest(null)
  setPromptLoaderRegistryForTest(null)
})

async function runWithProviderDefaultModelWarning<T>(context: Parameters<typeof acpAgentAction>[0], operation: () => Promise<T>): Promise<T> {
  const warningSpy = vi.spyOn(console, "warn").mockClear().mockImplementation(() => undefined)
  try {
    const result = await operation()

    expect(warningSpy).toHaveBeenCalledTimes(1)
    expect(warningSpy).toHaveBeenNthCalledWith(
      1,
      "mohist acp model not configured; using provider default",
      providerDefaultModelWarningContext(context),
    )
    return result
  } finally {
    warningSpy.mockRestore()
  }
}

function runDefaultModelAction(context: Parameters<typeof acpAgentAction>[0]) {
  return runWithProviderDefaultModelWarning(context, () => acpAgentAction(context))
}

function providerDefaultModelWarningContext(context: Parameters<typeof acpAgentAction>[0]) {
  return {
    workflowRunId: context.workflowRunId,
    workId: context.workId,
    stage: context.stage,
    sessionName: stringInput(context.with, "session") ?? context.workId,
    requestedModel: null,
    requestedModelSource: "none",
  }
}

function baseContext(overrides: Partial<ActionContext> = {}): ActionContext {
  return {
    workflowRunId: "wf-1",
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

class GenericFakeAgent {
  readonly calls: any[] = []
  private connection!: AgentSideConnection

  bind(connection: AgentSideConnection) {
    this.connection = connection
  }

  handler(): Agent {
    const self = this
    return {
      async initialize() {
        return { protocolVersion: PROTOCOL_VERSION, agentInfo: { name: "fake-generic-acp-agent", version: "0.1.0" }, agentCapabilities: {} }
      },
      async newSession() {
        self.calls.push({ event: "newSession" })
        return { sessionId: "acp-session-1" }
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
        self.calls.push({ event: "prompt", sessionId: params.sessionId })
        await self.connection.sessionUpdate({ sessionId: params.sessionId, update: { sessionUpdate: "agent_message_chunk", content: { type: "text", text: "done" } } } as never)
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

class FakeServerConnection {
  readonly calls: Array<{ event: string; type?: string; payload?: unknown; body?: unknown; sessionName?: string; sessionId?: string }> = []
  nextGetGenericSession: { runtimeSessionId?: string | null; workDir?: string; model?: string | null } | null = null
  nextGenericSession: { runtimeSessionId?: string; workDir?: string; model?: string | null } = { runtimeSessionId: "acp-session-1", workDir: "D:/work" }

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
    return { runtimeSessionId: "wf-acp-1", workDir: "D:/work" }
  }

  async attachWorkflowAgentSession() {
    this.calls.push({ event: "attachWorkflowAgentSession" })
  }

  async workflowAgentSessionRuntimeEvents() {
    this.calls.push({ event: "workflowAgentSessionRuntimeEvents" })
  }
}

function createGenericFixture() {
  const agent = new GenericFakeAgent()
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

  const serverConnection = new FakeServerConnection()
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
  setAcpProcessFactoryForTest(() => createFakeProcess(agent))

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

function createFakeProcess(agent: GenericFakeAgent): AcpProcessHandle {
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

describe("runAcpAgentSession — generic session dispatch", () => {
  it("AgentJobWithSessionId_DispatchesToGenericConnectionMethods_NotWorkflowOnes", async () => {
    const fixture = createGenericFixture()

    const result = await runDefaultModelAction(fixture.context({ with: { prompt: "do the work" } as never }))

    expect(result.status).toBe("success")
    const events = fixture.serverConnection.calls.map((entry) => entry.event)
    expect(events).toContain("getAgentSession")
    expect(events).toContain("openAgentSession")
    expect(events).toContain("attachAgentSession")
    expect(events).toContain("agentSessionRuntimeEvents")
    expect(events).not.toContain("getWorkflowAgentSession")
    expect(events).not.toContain("openWorkflowAgentSession")
    expect(events).not.toContain("attachWorkflowAgentSession")
    expect(events).not.toContain("workflowAgentSessionRuntimeEvents")
  })

  it("PreMintedGenericSessionWithoutRuntimeSessionId_OpensBeforeRunning", async () => {
    const fixture = createGenericFixture()
    fixture.serverConnection.nextGetGenericSession = { runtimeSessionId: null, workDir: "D:/work" }
    fixture.serverConnection.nextGenericSession = { runtimeSessionId: undefined, workDir: "D:/work" }

    const result = await runDefaultModelAction(fixture.context({ with: { prompt: "run minted session" } as never }))

    expect(result.status).toBe("success")
    const events = fixture.serverConnection.calls.map((entry) => entry.event)
    expect(events.indexOf("getAgentSession")).toBeGreaterThanOrEqual(0)
    expect(events.indexOf("openAgentSession")).toBeGreaterThan(events.indexOf("getAgentSession"))
    expect(events).toContain("attachAgentSession")
    expect(events).not.toContain("openWorkflowAgentSession")
  })

  it("GenericSession_StoresCachedEntryUnderGenericKey", async () => {
    const fixture = createGenericFixture()

    await runDefaultModelAction(fixture.context({ with: { prompt: "first" } as never }))

    const target: SessionTarget = { kind: "generic", projectId: "project-1", sessionId: "session-abc" }
    const expectedKey = fixture.acpSessionManager.key(target)
    expect(expectedKey).toBe("generic:session-abc")
    const cached = fixture.acpSessionManager.get(expectedKey)
    expect(cached).toBeTruthy()
    expect(cached?.sessionId).toBe("acp-session-1")
  })

  it("GenericSession_RuntimeEventsIncludeSessionInputAndDoNotCloseAfterSuccessfulTurn", async () => {
    const fixture = createGenericFixture()

    await runDefaultModelAction(fixture.context({ with: { prompt: "do the work" } as never }))

    const runtimeEvents = fixture.serverConnection.calls
      .filter((entry) => entry.event === "agentSessionRuntimeEvents")
      .map((entry) => ({ type: entry.type, sessionId: entry.sessionId }))
    expect(runtimeEvents.some((event) => event.type === "session.input" && event.sessionId === "session-abc")).toBe(true)
    expect(runtimeEvents.some((event) => event.type === "session.closed" && event.sessionId === "session-abc")).toBe(false)
  })

  it("GenericSession_CanFollowUpAfterFirstTurnUsingSameCachedAcpSession", async () => {
    const fixture = createGenericFixture()

    await runDefaultModelAction(fixture.context({ with: { prompt: "first" } as never }))
    await runDefaultModelAction(fixture.context({ with: { prompt: "second" } as never }))

    const prompts = fixture.agent.calls.filter((call) => call.event === "prompt")
    expect(prompts).toHaveLength(2)
    expect(prompts.every((call) => call.sessionId === "acp-session-1")).toBe(true)
    const closedEvents = fixture.serverConnection.calls.filter((entry) => entry.event === "agentSessionRuntimeEvents" && entry.type === "session.closed")
    expect(closedEvents).toHaveLength(0)
  })

  it("RawAgentJobWithProjectIdAndNoSessionId_StaysEphemeral", async () => {
    const fixture = createGenericFixture()

    const result = await runDefaultModelAction(fixture.context({ agentSessionId: undefined, with: { prompt: "raw" } as never }))

    expect(result.status).toBe("success")
    const events = fixture.serverConnection.calls.map((entry) => entry.event)
    expect(events).not.toContain("getWorkflowAgentSession")
    expect(events).not.toContain("openWorkflowAgentSession")
    expect(events).not.toContain("attachWorkflowAgentSession")
    expect(events).not.toContain("workflowAgentSessionRuntimeEvents")
    expect(events).not.toContain("getAgentSession")
    expect(events).not.toContain("openAgentSession")
    expect(events).not.toContain("attachAgentSession")
    expect(events).not.toContain("agentSessionRuntimeEvents")
  })

  it("GenericSession_AgentConfigModelSelectsAcpModel", async () => {
    const fixture = createGenericFixture()

    await acpAgentAction(fixture.context({ with: { prompt: "do the work", agent: { model: "openai/gpt-5.5" } } as never }))

    expect(fixture.agent.calls).toContainEqual({ event: "unstable_setSessionModel", sessionId: "acp-session-1", modelId: "openai/gpt-5.5" })
  })

  it("GenericSessionWithDifferentModel_ResumesSamePhysicalSession", async () => {
    const fixture = createGenericFixture()
    fixture.serverConnection.nextGetGenericSession = {
      runtimeSessionId: "persisted-acp-session",
      workDir: "D:/work",
      model: "kimi-for-coding/k2p6",
    }

    const result = await acpAgentAction(fixture.context({
      with: { prompt: "continue with a different model", agent: { model: "openai/gpt-5.5" } } as never,
    }))

    expect(result.status).toBe("success")
    expect(fixture.agent.calls).toContainEqual({ event: "resumeSession", sessionId: "persisted-acp-session" })
    expect(fixture.agent.calls.some((entry) => entry.event === "newSession")).toBe(false)
    expect(fixture.agent.calls).toContainEqual({ event: "unstable_setSessionModel", sessionId: "persisted-acp-session", modelId: "openai/gpt-5.5" })
    expect(fixture.agent.calls).toContainEqual({ event: "prompt", sessionId: "persisted-acp-session" })
  })

  it("WorkflowOwnerKind_StillUsesWorkflowConnectionMethods", async () => {
    const fixture = createGenericFixture()
    fixture.context({ ownerKind: "workflow", agentSessionId: undefined, workflowRunId: "wf-1", with: { session: "build", prompt: "do the work" } as never })

    await runDefaultModelAction(fixture.context({ ownerKind: "workflow", agentSessionId: undefined, workflowRunId: "wf-1", with: { session: "build", prompt: "do the work" } as never }))

    const events = fixture.serverConnection.calls.map((entry) => entry.event)
    expect(events).toContain("getWorkflowAgentSession")
    expect(events).toContain("openWorkflowAgentSession")
    expect(events).toContain("attachWorkflowAgentSession")
    expect(events).toContain("workflowAgentSessionRuntimeEvents")
    expect(events).not.toContain("getAgentSession")
    expect(events).not.toContain("openAgentSession")
    expect(events).not.toContain("attachAgentSession")
    expect(events).not.toContain("agentSessionRuntimeEvents")
  })

  it("GenericSession_AttachBodyCarriesProjectAndSessionId", async () => {
    const fixture = createGenericFixture()

    await runDefaultModelAction(fixture.context({ with: { prompt: "do the work" } as never }))

    const attachCall = fixture.serverConnection.calls.find((entry) => entry.event === "attachAgentSession")
    expect(attachCall).toBeTruthy()
    expect(attachCall?.sessionId).toBe("session-abc")
    expect(attachCall?.body).toMatchObject({ agentSessionId: "acp-session-1", workDir: "D:/work" })
  })
})
