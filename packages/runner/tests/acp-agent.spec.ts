import { afterEach, describe, expect, it } from "vitest"
import { AgentSideConnection, ClientSideConnection, PROTOCOL_VERSION } from "@agentclientprotocol/sdk"
import type { Agent, Stream } from "@agentclientprotocol/sdk"
import { acpAgentAction, buildPromptWithMohistContext, setAcpProcessFactoryForTest, type AcpProcessHandle } from "../src/actions/acp-agent.js"
import type { ActionContext } from "../src/core/types.js"
import { AcpSessionManager, type SharedAcpConnection } from "../src/runtime/acp-connection.js"

afterEach(() => setAcpProcessFactoryForTest(null))

describe("mohist/acp-agent", () => {
  it("ValidAcpAgentWork_ActionRuns_SpawnsAcpAndInitializesSessionBeforePrompt", async () => {
    const fixture = createFixture("basic")

    const result = await acpAgentAction(fixture.context({ prompt: "do the work" }))

    expect(result.status).toBe("success")
    expect(JSON.parse(result.output ?? "{}").acpSessionId).toBe("fake-session-1")
    expect(fixture.agent.calls.map((call) => call.event).filter((event) => ["initialize", "newSession", "prompt"].includes(event))).toEqual(["initialize", "newSession", "prompt"])
  })

  it("OpenSpecTaskWithoutPrompt_ActionBuildsPromptFromTaskFields", async () => {
    const fixture = createFixture("basic")

    const result = await acpAgentAction(fixture.context({
      description: "Requeue runnable workflows on server startup.",
      acceptanceCriteria: ["runner can claim recovered work"],
      output: "packages/server/src/Mohist.Server/Workflow/Recovery",
    }))

    expect(result.status).toBe("success")
    const prompt = fixture.agent.calls.find((entry) => entry.event === "prompt")?.text ?? ""
    expect(prompt).toContain("Implement this task: Build task")
    expect(prompt).toContain("Requeue runnable workflows on server startup.")
    expect(prompt).toContain("runner can claim recovered work")
  })

  it("IssueVariablesPresent_ActionPrependsIssueContextToPrompt", async () => {
    const fixture = createFixture("basic")

    const result = await acpAgentAction(fixture.context({ prompt: "create the proposal" }))

    expect(result.status).toBe("success")
    const prompt = fixture.agent.calls.find((entry) => entry.event === "prompt")?.text ?? ""
    expect(prompt).toContain("## Mohist Issue Context")
    expect(prompt).toContain("Number: 7")
    expect(prompt).toContain("Title: Document update smoke validation note")
    expect(prompt).toContain("Body:\nAdd a short note that records the expected local post-update smoke validation path.")
    expect(prompt).toContain("## Task Prompt\n\ncreate the proposal")
  })

  it("IssueVariablesMissing_PromptContextBuilderLeavesPromptUnchanged", () => {
    expect(buildPromptWithMohistContext({ variables: {}, issueNumber: null }, "plain prompt")).toBe("plain prompt")
  })

  it("ModelConfigured_AcpSessionStarts_SetsSessionConfigModelBeforePrompt", async () => {
    const fixture = createFixture("basic")

    await acpAgentAction(fixture.context({ prompt: "do the work", model: "openai/gpt-4.1" }))

    expect(fixture.agent.calls.find((entry) => entry.event === "setSessionConfigOption" && entry.configId === "model" && entry.value === "openai/gpt-4.1")).toBeTruthy()
    expect(fixture.agent.calls.findIndex((entry) => entry.event === "setSessionConfigOption")).toBeLessThan(fixture.agent.calls.findIndex((entry) => entry.event === "prompt"))
  })

  it("SessionConfigModelFails_ModelConfigured_FallsBackToUnstableSetSessionModel", async () => {
    const fixture = createFixture("model-fallback")

    const result = await acpAgentAction(fixture.context({ prompt: "do the work", model: "anthropic/claude" }))

    expect(result.status).toBe("success")
    expect(fixture.agent.calls.find((entry) => entry.event === "unstable_setSessionModel" && entry.modelId === "anthropic/claude")).toBeTruthy()
  })

  it("PermissionRequestHasAllowOption_AgentRequestsPermission_SelectsAllowOption", async () => {
    const fixture = createFixture("permission")

    const result = await acpAgentAction(fixture.context({ prompt: "needs permission" }))

    expect(result.status).toBe("success")
    expect(fixture.agent.calls.find((entry) => entry.event === "permissionResponse" && entry.outcome?.optionId === "allow")).toBeTruthy()
  })

  it("AgentMessageChunkArrives_SessionUpdateHandled_ReturnsAgentTextInOutput", async () => {
    const fixture = createFixture("basic")

    const result = await acpAgentAction(fixture.context({ prompt: "do the work" }))

    expect(result.status).toBe("success")
    expect(JSON.parse(result.output ?? "{}").text).toBe("hello")
  })

  it("ToolEventMissingToolNameButHasProviderId_ToolCallUpdateHandled_InfersToolNameAndReusesToolCallId", async () => {
    const fixture = createFixture("tool-weird")

    const result = await acpAgentAction(fixture.context({ prompt: "use tools" }))

    expect(result.status).toBe("success")
  })

  it("RunningSessionExceedsQuietThreshold_LivenessMonitored_EntersProbingAndSendsProbePrompt", async () => {
    const fixture = createFixture("liveness")

    const result = await acpAgentAction(fixture.context({ prompt: "long task", livenessQuietThresholdMs: 30, probeTimeoutMs: 500, timeout: 2_000 }))

    expect(result.status).toBe("success")
    expect(fixture.agent.calls.some((entry) => entry.event === "prompt" && entry.promptCount === 2 && entry.text.includes("still alive"))).toBe(true)
  })

  it("ThoughtAndToolUpdatesArrive_LivenessMonitored_DoNotProbeWhileAgentIsActive", async () => {
    const fixture = createFixture("liveness-non-message")

    const result = await acpAgentAction(fixture.context({ prompt: "long task", livenessQuietThresholdMs: 30, probeTimeoutMs: 500, timeout: 2_000 }))

    expect(result.status).toBe("success")
    expect(fixture.agent.calls.filter((entry) => entry.event === "prompt")).toHaveLength(1)
  })

  it("SharedAcpThoughtAndToolUpdatesArrive_LivenessMonitored_DoNotProbeWhileAgentIsActive", async () => {
    const fixture = createSharedFixture("liveness-non-message")

    const result = await acpAgentAction(fixture.context({ prompt: "long task", session: "build", livenessQuietThresholdMs: 30, probeTimeoutMs: 500, timeout: 2_000 }))

    expect(result.status).toBe("success")
    expect(fixture.agent.calls.filter((entry) => entry.event === "prompt")).toHaveLength(1)
    expect(fixture.server.events.map((entry) => entry.type)).toEqual(
      expect.arrayContaining(["agent_thought_chunk", "tool_call", "tool_call_update", "agent_session_terminal"]),
    )
  })

  it("AbortSignalFires_PromptRunning_SendsSessionCancelBeforeCleanup", async () => {
    const fixture = createFixture("abort")
    const controller = new AbortController()
    setTimeout(() => controller.abort(), 50)

    const result = await acpAgentAction(fixture.context({ prompt: "cancel me", timeout: 500 }, controller.signal))

    expect(result.status).toBe("failure")
    expect(result.message ?? "").toMatch(/stopped by user/i)
    expect(fixture.agent.calls.some((entry) => entry.event === "cancel")).toBe(true)
  })
})

function createFixture(scenario: Scenario) {
  const agent = new FakeAcpAgent(scenario)
  setAcpProcessFactoryForTest(() => createFakeProcess(agent))
  return {
    agent,
    context(withInput: Record<string, unknown>, signal = new AbortController().signal): ActionContext {
      return baseContext(withInput, signal)
    },
  }
}

function createSharedFixture(scenario: Scenario) {
  const agent = new FakeAcpAgent(scenario)
  const [clientStream, agentStream] = linkedStreams()
  const agentConnection = new AgentSideConnection(() => agent.handler(), agentStream)
  agent.bind(agentConnection)
  const server = fakeServerConnection()
  const sharedConnection = createSharedConnection(clientStream)

  return {
    agent,
    server,
    context(withInput: Record<string, unknown>, signal = new AbortController().signal): ActionContext {
      return {
        ...baseContext(withInput, signal),
        acpSessionManager: new AcpSessionManager(),
        acpConnection: sharedConnection,
        serverConnection: server as never,
      }
    },
  }
}

function baseContext(withInput: Record<string, unknown>, signal = new AbortController().signal): ActionContext {
  return {
    workflowRunId: "workflow-1",
    workId: "work-1",
    workType: "task",
    stage: "build",
    title: "Build task",
    uses: "mohist/acp-agent",
    with: withInput as never,
    variables: {
      project: { path: "D:/fake/work" },
      issue: {
        number: 7,
        title: "Document update smoke validation note",
        body: "Add a short note that records the expected local post-update smoke validation path.",
      },
    } as never,
    workDir: "D:/fake/work",
    signal,
    projectId: "project-1",
    issueNumber: 7,
  }
}

type Scenario = "basic" | "model-fallback" | "permission" | "tool-weird" | "liveness" | "liveness-non-message" | "abort"

class FakeAcpAgent {
  readonly calls: any[] = []
  private connection!: AgentSideConnection
  private promptCount = 0
  private initialPromptResolve: ((value: { stopReason: "end_turn" }) => void) | null = null

  constructor(private readonly scenario: Scenario) {}

  bind(connection: AgentSideConnection) {
    this.connection = connection
  }

  handler(): Agent {
    const self = this
    return {
      async initialize(params) {
        self.calls.push({ event: "initialize", protocolVersion: params.protocolVersion })
        return { protocolVersion: PROTOCOL_VERSION, agentInfo: { name: "fake-acp-agent", version: "0.1.0" }, agentCapabilities: {} }
      },
      async newSession(params) {
        self.calls.push({ event: "newSession", cwd: params.cwd })
        return { sessionId: "fake-session-1" }
      },
      async setSessionConfigOption(params) {
        self.calls.push({ event: "setSessionConfigOption", ...params })
        if (self.scenario === "model-fallback") throw new Error("set config unsupported")
        return { configOptions: [] }
      },
      async unstable_setSessionModel(params) {
        self.calls.push({ event: "unstable_setSessionModel", ...params })
        return {}
      },
      async prompt(params) {
        self.promptCount += 1
        const text = params.prompt.map((part) => part.type === "text" ? part.text : "").join("\n")
        self.calls.push({ event: "prompt", promptCount: self.promptCount, text })
        if (self.scenario === "permission") {
          const response = await self.connection.requestPermission({ sessionId: params.sessionId, toolCall: { toolCallId: "tool-permission", title: "Run command", kind: "execute", status: "pending" }, options: [{ optionId: "reject", name: "Reject", kind: "reject_once" }, { optionId: "allow", name: "Allow", kind: "allow_once" }] })
          self.calls.push({ event: "permissionResponse", ...response })
        }
        if (self.scenario === "liveness") return await self.runLivenessPrompt(params.sessionId)
        if (self.scenario === "liveness-non-message") return await self.runNonMessageLivenessPrompt(params.sessionId)
        if (self.scenario === "abort") return await new Promise(() => {})
        if (self.scenario === "tool-weird") await self.emitWeirdToolEvents(params.sessionId)
        else await self.emitBasicEvents(params.sessionId)
        return { stopReason: "end_turn" }
      },
      async cancel(params) {
        self.calls.push({ event: "cancel", ...params })
      },
      async authenticate() { return {} },
    }
  }

  private async runLivenessPrompt(sessionId: string) {
    if (this.promptCount === 1) {
      return await new Promise<{ stopReason: "end_turn" }>((resolve) => { this.initialPromptResolve = resolve })
    }
    await this.connection.sessionUpdate(textUpdate(sessionId, "probe-alive"))
      setTimeout(async () => {
        await this.connection.sessionUpdate(textUpdate(sessionId, "done-after-probe"))
        this.initialPromptResolve?.({ stopReason: "end_turn" })
      }, 20)
    return { stopReason: "end_turn" as const }
  }

  private async runNonMessageLivenessPrompt(sessionId: string) {
    await new Promise<void>((resolve) => setTimeout(resolve, 20))
    await this.connection.sessionUpdate({ sessionId, update: { sessionUpdate: "agent_thought_chunk", content: { type: "text", text: "thinking" } } } as never)
    await new Promise<void>((resolve) => setTimeout(resolve, 20))
    await this.connection.sessionUpdate({ sessionId, update: { sessionUpdate: "tool_call", toolCallId: "tool-quiet", title: "Read file", kind: "read", status: "in_progress", rawInput: { path: "README.md" } } } as never)
    await new Promise<void>((resolve) => setTimeout(resolve, 20))
    await this.connection.sessionUpdate({ sessionId, update: { sessionUpdate: "tool_call_update", toolCallId: "tool-quiet", title: "Read file", status: "completed", rawOutput: { text: "content" } } } as never)
    return { stopReason: "end_turn" as const }
  }

  private async emitBasicEvents(sessionId: string) {
    await this.connection.sessionUpdate(textUpdate(sessionId, "hello"))
    await this.connection.sessionUpdate({ sessionId, update: { sessionUpdate: "tool_call", toolCallId: "tool-1", title: "Read file", kind: "read", status: "in_progress", rawInput: { path: "README.md" } } })
    await this.connection.sessionUpdate({ sessionId, update: { sessionUpdate: "tool_call_update", toolCallId: "tool-1", title: "Read file", status: "completed", rawOutput: { text: "content" } } })
  }

  private async emitWeirdToolEvents(sessionId: string) {
    await this.connection.sessionUpdate({ sessionId, update: { sessionUpdate: "tool_call", toolCallId: "provider-tool-1", title: "Run bash command", status: "in_progress", rawInput: { command: "npm test" } } })
    await this.connection.sessionUpdate({ sessionId, update: { sessionUpdate: "tool_call_update", toolCallId: "provider-tool-1", title: "Run bash command", status: "completed", rawOutput: { stdout: "ok" } } })
  }
}

function textUpdate(sessionId: string, text: string) {
  return { sessionId, update: { sessionUpdate: "agent_message_chunk" as const, content: { type: "text" as const, text } } }
}

function createFakeProcess(agent: FakeAcpAgent): AcpProcessHandle {
  const [clientStream, agentStream] = linkedStreams()
  const connection = new AgentSideConnection(() => agent.handler(), agentStream)
  agent.bind(connection)
  return {
    stream: clientStream,
    processPid: 12345,
    spawnFailure: new Promise<never>(() => {}),
    exitFailure: new Promise<never>(() => {}),
    markInitialized() {},
    exitCode() { return 0 },
    async cleanup() {
      await Promise.allSettled([clientStream.readable.cancel(), clientStream.writable.abort()])
    },
  }
}

function createSharedConnection(stream: Stream): SharedAcpConnection {
  let activeSessionUpdateHandler: Parameters<SharedAcpConnection["setActiveHandlers"]>[0] = async () => {}
  let activePermissionHandler: Parameters<SharedAcpConnection["setActiveHandlers"]>[1] = async () => ({ outcome: { outcome: "cancelled" } })
  const connection = new ClientSideConnection(
    () => ({
      sessionUpdate: async (notification) => {
        await activeSessionUpdateHandler(notification)
      },
      requestPermission: async (params) => activePermissionHandler(params),
    }),
    stream,
  )

  return {
    connection,
    processPid: 12345,
    setActiveHandlers(sessionUpdate, permission) {
      activeSessionUpdateHandler = sessionUpdate
      activePermissionHandler = permission
    },
    clearActiveHandlers() {
      activeSessionUpdateHandler = async () => {}
      activePermissionHandler = async () => ({ outcome: { outcome: "cancelled" } })
    },
    async shutdown() {
      await Promise.allSettled([stream.readable.cancel(), stream.writable.abort()])
    },
  }
}

function fakeServerConnection() {
  const events: Array<{ type: string; payload: unknown }> = []
  return {
    events,
    async ensureWorkflowAgentSession() {
      return {}
    },
    async attachWorkflowAgentSession() {},
    async workflowAgentSessionEvents(_projectId: string, _workflowRunId: string, _sessionName: string, body: { events?: Array<{ type: string; payload: unknown }> }) {
      events.push(...(body.events ?? []))
    },
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
