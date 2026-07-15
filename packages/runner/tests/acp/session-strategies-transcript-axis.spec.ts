import { afterEach, describe, expect, it, vi } from "vitest"
import { AgentSideConnection, ClientSideConnection, PROTOCOL_VERSION } from "@agentclientprotocol/sdk"
import type { Agent, RequestPermissionRequest, RequestPermissionResponse, SessionNotification, Stream } from "@agentclientprotocol/sdk"
import { acpAgentAction, setAcpProcessFactoryForTest, type AcpProcessHandle } from "../../src/actions/acp-agent.js"
import { setPromptLoaderRegistryForTest } from "../../src/core/prompt.js"
import type { ActionContext, RenderedWorkItem } from "../../src/core/types.js"
import { AcpSessionManager, type SharedAcpConnection } from "../../src/runtime/acp-connection.js"
import { ActionRegistry } from "../../src/actions/registry.js"
import { WorkExecutor } from "../../src/runtime/executor.js"
import { ServerConnection } from "../../src/server/connection.js"
import { stringInput } from "../../src/core/json.js"
import { verifyOnlyWorkspaceManager } from "../support/workspace-mock.js"
import { setExecutorGitRunnerForTest } from "../../src/runtime/git-probe.js"

afterEach(() => {
  setAcpProcessFactoryForTest(null)
  setPromptLoaderRegistryForTest(null)
  setExecutorGitRunnerForTest(null)
})

type ProviderDefaultModelWarningContext = Pick<ActionContext, "workflowRunId" | "workId" | "stage" | "with">

async function runWithProviderDefaultModelWarning<T>(context: ProviderDefaultModelWarningContext, operation: () => Promise<T>): Promise<T> {
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

function providerDefaultModelWarningContext(context: ProviderDefaultModelWarningContext) {
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
  private readonly sessionRecord: { runtimeSessionId: string }

  constructor(sessionRecord: { runtimeSessionId: string } = { runtimeSessionId: "acp-session-1" }) {
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
        return { sessionId: self.sessionRecord.runtimeSessionId }
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
  nextGetGenericSession: { runtimeSessionId?: string | null; workDir?: string; model?: string | null } | null = null
  nextGenericSession: { runtimeSessionId: string; workDir: string; model?: string | null } = { runtimeSessionId: "acp-session-1", workDir: "D:/work" }

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

function createTranscriptAxisFixture(opts: { nextGenericSession?: { runtimeSessionId: string; workDir: string; model?: string | null } } = {}) {
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
    acpConnection,
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

describe("generic sessionId transcript axis", () => {
  it("ExecutorUsesPolledDispatchAgentSessionId_AsGenericRuntimeEventsTarget", async () => {
    setExecutorGitRunnerForTest(async (_workDir, args) => {
      const joined = args.join(" ")
      return {
        success: true,
        stdout: joined === "rev-parse --abbrev-ref HEAD"
          ? "main\n"
          : joined === "rev-parse --is-inside-work-tree"
            ? "true\n"
            : "",
        stderr: "",
        exitCode: 0,
        combinedOutput: "",
      }
    })
    const fixture = createTranscriptAxisFixture()
    const registry = new ActionRegistry()
    registry.register("mohist/acp-agent", acpAgentAction)
    const executor = new WorkExecutor(
      registry,
      verifyOnlyWorkspaceManager({ path: "/tmp/opencode/mohist-runner-transcript-axis", branch: null, changeDir: null }),
      fixture.serverConnection as unknown as ServerConnection,
      fixture.acpSessionManager,
      fixture.acpConnection,
      "/tmp/opencode/mohist-runner-transcript-axis",
    )
    const polledDispatch: RenderedWorkItem = {
      workflowRunId: "",
      workId: "agent-work-polled",
      workType: "agent-job",
      stage: "agent",
      title: "Agent Job",
      uses: "mohist/acp-agent",
      with: { prompt: "do the work from a polled dispatch" },
      variables: { workspace: { path: "/tmp/opencode/mohist-runner-transcript-axis", branch: null, changeDir: null } },
      projectId: "project-1",
      ownerKind: "agent-job",
      agentJobId: "agent-job-polled",
      agentSessionId: "session-from-polled-dispatch",
    }

    const result = await runWithProviderDefaultModelWarning(polledDispatch, () => executor.execute(polledDispatch, new AbortController().signal))

    expect(result.status, result.message ?? undefined).toBe("completed")
    const genericEvents = fixture.serverConnection.calls.filter((entry) => entry.event === "agentSessionRuntimeEvents")
    expect(genericEvents.length).toBeGreaterThan(0)
    expect(genericEvents.every((entry) => entry.sessionId === "session-from-polled-dispatch")).toBe(true)
    expect(runtimeEventsByType(fixture.serverConnection, "message.delta")).not.toHaveLength(0)
    expect(runtimeEventsByType(fixture.serverConnection, "tool_call.started")).not.toHaveLength(0)
    expect(runtimeEventsByType(fixture.serverConnection, "tool_call.completed")).not.toHaveLength(0)
    expect(runtimeEventsByType(fixture.serverConnection, "usage.updated")).not.toHaveLength(0)
    const trace = fixture.serverConnection.calls.map((entry) => entry.event)
    expect(trace).not.toContain("workflowAgentSessionRuntimeEvents")
  })

  it("MapsPolledDispatchAgentSessionIdToActionContext_AndGenericAxisEmitsAllTranscriptEvents", async () => {
    const fixture = createTranscriptAxisFixture()

    const result = await runDefaultModelAction(fixture.context({ with: { prompt: "do the work" } as never }))

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

    const first = await runDefaultModelAction(fixture.context({ with: { prompt: "first" } as never }))
    const second = await runDefaultModelAction(fixture.context({ with: { prompt: "second" } as never }))

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

    const result = await runDefaultModelAction(fixture.context({ with: { prompt: "do the work" } as never }))

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
