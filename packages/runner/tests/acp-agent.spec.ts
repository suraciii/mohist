import { afterEach, describe, expect, it } from "vitest"
import { AgentSideConnection, PROTOCOL_VERSION } from "@agentclientprotocol/sdk"
import type { Agent, Stream } from "@agentclientprotocol/sdk"
import { acpAgentAction, setAcpProcessFactoryForTest, type AcpProcessHandle } from "../src/actions/acp-agent.js"
import type { ActionContext, RunnerTelemetry } from "../src/core/types.js"

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

  it("AgentMessageChunkArrives_SessionUpdateHandled_PersistsRawEventAndAccumulatesAgentText", async () => {
    const fixture = createFixture("basic")

    const result = await acpAgentAction(fixture.context({ prompt: "do the work" }))

    expect(JSON.parse(result.output ?? "{}").text).toBe("hello")
    expect(fixture.telemetry.collectedEvents.some((event) => event.type === "agent_message_chunk" && event.payload.content.text === "hello")).toBe(true)
  })

  it("ToolEventMissingToolNameButHasProviderId_ToolCallUpdateHandled_InfersToolNameAndReusesToolCallId", async () => {
    const fixture = createFixture("tool-weird")

    const result = await acpAgentAction(fixture.context({ prompt: "use tools" }))

    expect(result.status).toBe("success")
    const toolEvents = fixture.telemetry.collectedEvents.filter((event) => event.type === "tool_call" || event.type === "tool_call_update")
    expect(toolEvents).toHaveLength(2)
    expect(toolEvents[0].payload.toolCall.toolName).toBe("bash")
    expect(toolEvents[0].payload.toolCall.toolCallId).toBe("provider-tool-1")
    expect(toolEvents[1].payload.toolCall.toolCallId).toBe("provider-tool-1")
  })

  it("RunningSessionExceedsQuietThreshold_LivenessMonitored_EntersProbingAndSendsProbePrompt", async () => {
    const fixture = createFixture("liveness")

    const result = await acpAgentAction(fixture.context({ prompt: "long task", livenessQuietThresholdMs: 30, probeTimeoutMs: 500, timeout: 2_000 }))

    expect(result.status).toBe("success")
    expect(fixture.telemetry.statuses.some((status) => status.status === "probing")).toBe(true)
    expect(fixture.telemetry.statuses.some((status) => status.status === "running")).toBe(true)
    expect(fixture.agent.calls.some((entry) => entry.event === "prompt" && entry.promptCount === 2 && entry.text.includes("still alive"))).toBe(true)
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
  const telemetry = createTelemetry()
  const agent = new FakeAcpAgent(scenario)
  setAcpProcessFactoryForTest(() => createFakeProcess(agent))
  return {
    agent,
    telemetry,
    context(withInput: Record<string, unknown>, signal = new AbortController().signal): ActionContext {
      return {
        workflowRunId: "workflow-1",
        workId: "work-1",
        workType: "task",
        stage: "build",
        title: "Build task",
        uses: "mohist/acp-agent",
        with: withInput as never,
        variables: { project: { path: "D:/fake/work" } } as never,
        workDir: "D:/fake/work",
        signal,
        session: { id: "session-row-1", projectId: "project-1", issueNumber: 7, workflowRunId: "workflow-1", workId: "work-1", stage: "build", title: "Build task" },
        telemetry,
      }
    },
  }
}

type Scenario = "basic" | "model-fallback" | "permission" | "tool-weird" | "liveness" | "abort"

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

function linkedStreams(): [Stream, Stream] {
  const clientToAgent = new TransformStream()
  const agentToClient = new TransformStream()
  return [
    { writable: clientToAgent.writable, readable: agentToClient.readable },
    { writable: agentToClient.writable, readable: clientToAgent.readable },
  ]
}

function createTelemetry(): RunnerTelemetry & { startedBodies: unknown[]; collectedEvents: Array<{ type: string; payload: any }>; completedBodies: unknown[]; statuses: any[] } {
  const telemetry = {
    startedBodies: [] as unknown[],
    collectedEvents: [] as Array<{ type: string; payload: any }>,
    completedBodies: [] as unknown[],
    statuses: [] as any[],
    async started(_sessionId: string, body: unknown) { telemetry.startedBodies.push(body) },
    async events(_sessionId: string, events: unknown[]) { telemetry.collectedEvents.push(...events as Array<{ type: string; payload: any }>) },
    async completed(_sessionId: string, body: unknown) { telemetry.completedBodies.push(body) },
    async status(_sessionId: string, body: unknown) { telemetry.statuses.push(body) },
  }
  return telemetry
}
