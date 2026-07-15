import { writeFile } from "node:fs/promises"
import { join } from "node:path"
import { tmpdir } from "node:os"
import { AgentSideConnection, ClientSideConnection, PROTOCOL_VERSION } from "@agentclientprotocol/sdk"
import type { Agent, RequestPermissionRequest, RequestPermissionResponse, SessionNotification, Stream } from "@agentclientprotocol/sdk"
import { vi } from "vitest"
import { deferred } from "../support/deferred.js"
import { setAcpProcessFactoryForTest, type AcpProcessHandle } from "../../src/actions/acp-agent.js"
import { setAcpCancelTimeoutMsForTest } from "../../src/actions/acp/liveness.js"
import type { ActionContext } from "../../src/core/types.js"
import { AcpSessionManager, type SharedAcpConnection } from "../../src/runtime/acp-connection.js"
import {
  isFailFastOpencodeProviderError,
  setOpencodeProviderErrorDiagnosticFinderForTest,
  type OpencodeProviderErrorDiagnostic,
} from "../../src/runtime/opencode-log-diagnostics.js"
import { ServerConnection } from "../../src/server/connection.js"

export type Scenario =
  | "basic"
  | "model-fallback"
  | "model-config-fails"
  | "permission"
  | "tool-weird"
  | "liveness"
  | "quiet-then-done"
  | "liveness-non-message"
  | "abort"
  | "tool-liveness"
  | "probe-timeout"
  | "abort-during-probe"
  | "empty-complete"
  | "resolved-model"
  | "config-option-update"
  | "usage-update"
  | "usage-only"
  | "prompt-usage"
  | "compaction"
  | "expectation-repair"
  | "expectation-repair-usage-only"
  | "cancel-hangs"
  | "failif-fail"

export function createFixture(scenario: Scenario) {
  const timeline: Array<{ event: string }> = []
  const agent = new FakeAcpAgent(scenario, timeline)
  const serverConnection = new FakeServerConnection(timeline)
  setAcpProcessFactoryForTest(() => createFakeProcess(agent))
  return {
    agent,
    serverConnection,
    timeline,
    context(withInput: Record<string, unknown>, signal = new AbortController().signal, overrides: Partial<ActionContext> = {}): ActionContext {
      return {
        ...baseContext(withInput, signal),
        serverConnection: serverConnection as unknown as ServerConnection,
        ...overrides,
      }
    },
  }
}

export function createSharedFixture(scenario: Scenario) {
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

export function contextWithOverrides(withInput: Record<string, unknown>, signal = new AbortController().signal, overrides: Partial<ActionContext> = {}): ActionContext {
  return {
    ...baseContext(withInput, signal),
    ...overrides,
  }
}

export function baseContext(withInput: Record<string, unknown>, signal = new AbortController().signal): ActionContext {
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
    writeVars: async () => {},
  }
}

export class FakeAcpAgent {
  readonly calls: any[] = []
  private connection!: AgentSideConnection
  private readonly promptStarted = deferred<void>()
  private promptCount = 0
  private initialPromptResolve: ((value: { stopReason: "end_turn" }) => void) | null = null
  cancelHangs = false

  constructor(private readonly scenario: Scenario, private readonly timeline?: Array<{ event: string }>) {}

  bind(connection: AgentSideConnection) {
    this.connection = connection
  }

  waitForPrompt(): Promise<void> {
    if (this.calls.some((call) => call.event === "prompt")) return Promise.resolve()
    return this.promptStarted.promise
  }

  handler(): Agent {
    const self = this
    return {
      async initialize(params) {
        self.calls.push({ event: "initialize", protocolVersion: params.protocolVersion })
        return { protocolVersion: PROTOCOL_VERSION, agentInfo: { name: "fake-acp-agent", version: "0.1.0" }, agentCapabilities: {} }
      },
      async newSession(params) {
        self.timeline?.push({ event: "newSession" })
        self.calls.push({ event: "newSession", cwd: params.cwd, _meta: params._meta })
        if (self.scenario === "resolved-model") {
          return { sessionId: "fake-session-1", models: { currentModelId: "openai/gpt-4.1", availableModels: [] } }
        }
        return { sessionId: "fake-session-1" }
      },
      async resumeSession(params) {
        self.timeline?.push({ event: "resumeSession" })
        self.calls.push({ event: "resumeSession", sessionId: params.sessionId, cwd: params.cwd })
        return {}
      },
      async setSessionConfigOption(params) {
        self.timeline?.push({ event: "setSessionConfigOption" })
        self.calls.push({ event: "setSessionConfigOption", ...params })
        if (self.scenario === "model-fallback" || self.scenario === "model-config-fails") throw new Error("set config unsupported")
        return { configOptions: [] }
      },
      async unstable_setSessionModel(params) {
        self.timeline?.push({ event: "unstable_setSessionModel" })
        self.calls.push({ event: "unstable_setSessionModel", ...params })
        if (self.scenario === "model-config-fails") throw new Error("set model unsupported")
        return {}
      },
      async prompt(params) {
        self.promptCount += 1
        const text = promptText(params.prompt)
        self.calls.push({ event: "prompt", promptCount: self.promptCount, text })
        self.promptStarted.resolve()
        if (self.scenario === "permission") {
          const response = await self.connection.requestPermission({ sessionId: params.sessionId, toolCall: { toolCallId: "tool-permission", title: "Run command", kind: "execute", status: "pending" }, options: [{ optionId: "reject", name: "Reject", kind: "reject_once" }, { optionId: "allow", name: "Allow", kind: "allow_once" }] })
          self.calls.push({ event: "permissionResponse", ...response })
        }
        if (self.scenario === "liveness") return await self.runLivenessPrompt(params.sessionId)
        if (self.scenario === "quiet-then-done") return await self.runQuietThenDonePrompt(params.sessionId)
        if (self.scenario === "liveness-non-message") return await self.runNonMessageLivenessPrompt(params.sessionId)
        if (self.scenario === "tool-liveness") return await self.runToolLivenessPrompt(params.sessionId)
        if (self.scenario === "probe-timeout") return await self.runProbeTimeoutPrompt()
        if (self.scenario === "abort-during-probe") return await self.runAbortDuringProbePrompt()
        if (self.scenario === "abort") return await new Promise(() => {})
        if (self.scenario === "cancel-hangs") return await new Promise(() => {})
        if (self.scenario === "empty-complete") return { stopReason: "end_turn" }
        if (self.scenario === "expectation-repair") return await self.runExpectationRepairPrompt(params.sessionId, text)
        if (self.scenario === "expectation-repair-usage-only") return await self.runExpectationRepairUsageOnlyPrompt(params.sessionId, text)
        if (self.scenario === "failif-fail") return await self.runFailIfFailPrompt(params.sessionId)
        if (self.scenario === "tool-weird") await self.emitWeirdToolEvents(params.sessionId)
        if (self.scenario === "config-option-update") {
          await self.connection.sessionUpdate({ sessionId: params.sessionId, update: { sessionUpdate: "agent_message_chunk", content: { type: "text", text: "switching" } } } as never)
          await self.connection.sessionUpdate({ sessionId: params.sessionId, update: { sessionUpdate: "config_option_update", configOptions: [{ id: "model", category: "model", name: "Model", type: "select", currentValue: "anthropic/claude-sonnet-4-5", options: [{ value: "anthropic/claude-sonnet-4-5", name: "Claude Sonnet 4.5" }] }] } } as never)
          return { stopReason: "end_turn" }
        }
        if (self.scenario === "usage-update") {
          await self.connection.sessionUpdate(textUpdate(params.sessionId, "tracked usage"))
          await self.connection.sessionUpdate({ sessionId: params.sessionId, update: { sessionUpdate: "usage_update", size: 200000, used: 15000, cost: { amount: 0.0012, currency: "USD" } } } as never)
          return { stopReason: "end_turn" }
        }
        if (self.scenario === "usage-only") {
          await self.connection.sessionUpdate({ sessionId: params.sessionId, update: { sessionUpdate: "usage_update", size: 200000, used: 15000, cost: { amount: 0.0012, currency: "USD" } } } as never)
          return { stopReason: "end_turn" }
        }
        if (self.scenario === "prompt-usage") {
          await self.connection.sessionUpdate(textUpdate(params.sessionId, "usage test"))
          return { stopReason: "end_turn", usage: { inputTokens: 120, outputTokens: 40, totalTokens: 160, cachedReadTokens: 80, thoughtTokens: 5 } }
        }
        if (self.scenario === "compaction") {
          await self.connection.sessionUpdate(textUpdate(params.sessionId, "before-compact"))
          await self.connection.sessionUpdate({ sessionId: params.sessionId, update: { sessionUpdate: "usage_update", size: 200000, used: 60000, _meta: { "opencode.compaction": { contextWindowUsedBefore: 180000, contextWindowUsedAfter: 60000, strategy: "summary" } } } } as never)
          return { stopReason: "end_turn" }
        }
        else await self.emitBasicEvents(params.sessionId)
        return { stopReason: "end_turn" }
      },
      async cancel(params) {
        self.calls.push({ event: "cancel", ...params })
        if (self.scenario === "cancel-hangs" || self.cancelHangs) {
          await new Promise(() => {})
        }
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

  private async runQuietThenDonePrompt(sessionId: string) {
    if (this.promptCount > 1) return { stopReason: "end_turn" as const }

    await new Promise<void>((resolve) => setTimeout(resolve, 120))
    await this.connection.sessionUpdate(textUpdate(sessionId, "done-after-quiet-period"))
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

  private async runToolLivenessPrompt(sessionId: string) {
    if (this.promptCount === 1) {
      return await new Promise<{ stopReason: "end_turn" }>((resolve) => { this.initialPromptResolve = resolve })
    }
    await this.connection.sessionUpdate({ sessionId, update: { sessionUpdate: "tool_call", toolCallId: "tool-probe-1", title: "Read file", kind: "read", status: "in_progress", rawInput: { path: "README.md" } } })
    await this.connection.sessionUpdate({ sessionId, update: { sessionUpdate: "tool_call_update", toolCallId: "tool-probe-1", title: "Read file", status: "completed", rawOutput: { text: "content" } } })
    setTimeout(() => {
      this.initialPromptResolve?.({ stopReason: "end_turn" })
    }, 20)
    return { stopReason: "end_turn" as const }
  }

  private async runProbeTimeoutPrompt() {
    return await new Promise<{ stopReason: "end_turn" }>(() => {})
  }

  private async runAbortDuringProbePrompt() {
    return await new Promise<{ stopReason: "end_turn" }>(() => {})
  }

  private async runExpectationRepairPrompt(sessionId: string, text: string) {
    if (text.includes("did not satisfy this task's completion requirements")) {
      await writeFile(join(this.extractCwd(), "review.md"), "<promise>PASS</promise>\n")
      await this.connection.sessionUpdate(textUpdate(sessionId, "wrote review.md"))
      return { stopReason: "end_turn" as const }
    }

    await this.connection.sessionUpdate(textUpdate(sessionId, "review complete"))
    return { stopReason: "end_turn" as const }
  }

  private async runExpectationRepairUsageOnlyPrompt(sessionId: string, text: string) {
    if (text.includes("did not satisfy this task's completion requirements")) {
      await this.connection.sessionUpdate({ sessionId, update: { sessionUpdate: "usage_update", size: 262144, used: 0, cost: { amount: 0, currency: "USD" } } } as never)
      return { stopReason: "end_turn" as const }
    }

    await this.connection.sessionUpdate(textUpdate(sessionId, "review complete"))
    return { stopReason: "end_turn" as const }
  }

  private async runFailIfFailPrompt(sessionId: string) {
    await writeFile(join(this.extractCwd(), "review.md"), "Found issues.\n<promise>FAIL</promise>\n")
    await this.connection.sessionUpdate(textUpdate(sessionId, "wrote review.md"))
    return { stopReason: "end_turn" as const }
  }

  private extractCwd() {
    const newSession = [...this.calls].reverse().find((entry) => entry.event === "newSession")
    return typeof newSession?.cwd === "string" ? newSession.cwd : tmpdir()
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

export function textUpdate(sessionId: string, text: string) {
  return { sessionId, update: { sessionUpdate: "agent_message_chunk" as const, content: { type: "text" as const, text } } }
}

function promptText(prompt: ReadonlyArray<{ type: string; text?: string }>): string {
  return prompt.map((part) => part.type === "text" ? part.text ?? "" : "").join("\n")
}

export function thoughtUpdate(sessionId: string, text: string) {
  return { sessionId, update: { sessionUpdate: "agent_thought_chunk" as const, content: { type: "text" as const, text } } }
}

export function createSharedSessionFixture(
  scenario: "thought-liveness" | "probe-send-failed" | "resolved-model" | "compaction",
  options?: {
    newSessionId?: string
    sessionRecord?: { acpSessionId: string; model?: string | null }
  },
) {
  const agent = new FakeSharedAcpAgent(scenario, { newSessionId: options?.newSessionId })
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
  acpSessionManager.set(acpSessionManager.workflowKey("workflow-1", "shared-session"), { sessionId: "shared-session-1", workDir: "D:/fake/work" })
  serverConnection.nextEnsureWorkflowAgentSession = options?.sessionRecord ? { ...options.sessionRecord, workDir: "D:/fake/work" } : { acpSessionId: "shared-session-1", workDir: "D:/fake/work" }
  const connection = clientConnection
  if (scenario === "probe-send-failed") {
    const originalPrompt = clientConnection.prompt.bind(clientConnection)
    connection.prompt = async (params: Parameters<ClientSideConnection["prompt"]>[0]) => {
      const text = promptText(params.prompt)
      if (text.includes("still alive")) throw new Error("probe transport failed")
      return await originalPrompt(params)
    }
  }
  const acpConnection: SharedAcpConnection = {
    connection,
    processPid: 4321,
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

  return {
    agent,
    serverConnection,
    context(): Partial<ActionContext> {
      return {
        acpConnection,
        acpSessionManager,
        serverConnection: serverConnection as unknown as ServerConnection,
      }
    },
  }
}

export class FakeServerConnection {
  readonly calls: Array<{ event: string; type?: string; payload?: unknown; body?: unknown; sessionName?: string }> = []
  nextEnsureWorkflowAgentSession: { acpSessionId?: string; workDir?: string; model?: string | null } = { acpSessionId: "shared-session-1", workDir: "D:/fake/work" }
  private readonly livenessProbeStarted = deferred<void>()

  constructor(private readonly timeline?: Array<{ event: string }>) {}

  waitForLivenessProbe(): Promise<void> {
    if (this.calls.some((call) => call.event === "workflowAgentSessionEvents" && call.type === "session.liveness" && (call.payload as { status?: string }).status === "probing")) {
      return Promise.resolve()
    }
    return this.livenessProbeStarted.promise
  }

  async ensureWorkflowAgentSession(_projectId: string, _workflowRunId: string, sessionName: string) {
    this.calls.push({ event: "ensureWorkflowAgentSession", sessionName })
    return this.nextEnsureWorkflowAgentSession
  }

  async getWorkflowAgentSession(_projectId: string, _workflowRunId: string, sessionName: string) {
    this.calls.push({ event: "getWorkflowAgentSession", sessionName })
    return null
  }

  async openWorkflowAgentSession(_projectId: string, _workflowRunId: string, sessionName: string, body: unknown) {
    this.calls.push({ event: "openWorkflowAgentSession", sessionName, body })
    return this.nextEnsureWorkflowAgentSession
  }

  async attachWorkflowAgentSession(_projectId: string, _workflowRunId: string, sessionName: string, body: unknown) {
    this.timeline?.push({ event: "attachWorkflowAgentSession" })
    this.calls.push({ event: "attachWorkflowAgentSession", sessionName, body })
  }

  async workflowAgentSessionRuntimeEvents(_projectId: string, _workflowRunId: string, sessionName: string, payload: { events?: Array<{ type: string; payload: unknown }>; runtimeEvents?: Array<{ type: string; payload: unknown }> }) {
    const events = payload?.events ?? payload?.runtimeEvents ?? []
    for (const event of events) {
      this.calls.push({ event: "workflowAgentSessionEvents", sessionName, type: event.type, payload: event.payload })
      if (event.type === "session.liveness" && (event.payload as { status?: string }).status === "probing") this.livenessProbeStarted.resolve()
    }
  }
}

export class FakeSharedAcpAgent {
  readonly calls: any[] = []
  private connection!: AgentSideConnection
  private readonly promptStarted = deferred<void>()
  cancelHangs = false

  constructor(
    private readonly scenario: "thought-liveness" | "probe-send-failed" | "resolved-model" | "compaction",
    private readonly options: { newSessionId?: string } = {},
  ) {}

  bind(connection: AgentSideConnection) {
    this.connection = connection
  }

  waitForPrompt(): Promise<void> {
    if (this.calls.some((call) => call.event === "prompt")) return Promise.resolve()
    return this.promptStarted.promise
  }

  handler(): Agent {
    const self = this
    return {
      async initialize() {
        return { protocolVersion: PROTOCOL_VERSION, agentInfo: { name: "fake-shared-acp-agent", version: "0.1.0" }, agentCapabilities: {} }
      },
      async newSession(params) {
        self.calls.push({ event: "newSession", _meta: params._meta })
        return { sessionId: self.options.newSessionId ?? "shared-session-1" }
      },
      async resumeSession(params) {
        self.calls.push({ event: "resumeSession", sessionId: params.sessionId, cwd: params.cwd, _meta: params._meta })
        if (self.scenario === "resolved-model") {
          return { models: { currentModelId: "anthropic/claude-haiku-4-5", availableModels: [] } }
        }
        return {}
      },
      async setSessionConfigOption(params) {
        self.calls.push({ event: "setSessionConfigOption", ...params })
        return { configOptions: [] }
      },
      async unstable_setSessionModel(params) {
        self.calls.push({ event: "unstable_setSessionModel", ...params })
        return {}
      },
      async prompt(params) {
        self.calls.push({ event: "prompt", sessionId: params.sessionId, text: promptText(params.prompt) })
        self.promptStarted.resolve()
        if (self.scenario === "thought-liveness") {
          for (let index = 0; index < 5; index += 1) {
            await delay(20)
            self.calls.push({ event: "thought", index })
            await self.connection.sessionUpdate(thoughtUpdate(params.sessionId, `thinking-${index}`))
          }
        } else if (self.scenario === "probe-send-failed") {
          await delay(80)
        } else if (self.scenario === "resolved-model") {
          await self.connection.sessionUpdate(thoughtUpdate(params.sessionId, "thinking"))
        }
        return { stopReason: "end_turn" }
      },
      async closeSession(params) {
        self.calls.push({ event: "closeSession", sessionId: params.sessionId })
      },
      async cancel() {
        self.calls.push({ event: "cancel" })
        if (self.cancelHangs) {
          await new Promise(() => {})
        }
      },
      async authenticate() { return {} },
    }
  }
}

export function delay(ms: number) {
  return new Promise((resolve) => setTimeout(resolve, ms))
}

export function useAcpFakeTimers(now = "2026-06-30T00:00:00.000Z") {
  vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout", "Date"] })
  vi.setSystemTime(new Date(now))
  setAcpCancelTimeoutMsForTest(50)
  setOpencodeProviderErrorDiagnosticFinderForTest(async () => undefined)
}

export function resetAcpTestHooks() {
  setAcpCancelTimeoutMsForTest(null)
  setOpencodeProviderErrorDiagnosticFinderForTest(null)
  vi.useRealTimers()
  delete process.env.MOHIST_OPENCODE_LOG_DIR
}

export function useAcpProviderDiagnostic(diagnostic: OpencodeProviderErrorDiagnostic | undefined) {
  setOpencodeProviderErrorDiagnosticFinderForTest(async (sessionId, options) => {
    if (!diagnostic || diagnostic.sessionId !== sessionId) return undefined
    if (options.sinceMs !== undefined) {
      if (!diagnostic.occurredAt) return undefined
      const occurredAtMs = Date.parse(diagnostic.occurredAt)
      if (!Number.isFinite(occurredAtMs) || occurredAtMs < options.sinceMs) return undefined
    }
    if (options.failFastOnly && !isFailFastOpencodeProviderError(diagnostic)) return undefined
    return diagnostic
  })
}

export function createTrackedFakeProcess(
  agent: FakeAcpAgent,
  options: { hangCancelWrites?: boolean } = {},
): AcpProcessHandle & { cleanupCount: () => number; waitForCancelWrite: () => Promise<void> } {
  const cancelWriteStarted = deferred<void>()
  const base = createFakeProcess(agent, {
    ...options,
    onCancelWrite: cancelWriteStarted.resolve,
  })
  let cleanupCalls = 0
  return {
    ...base,
    cleanupCount: () => cleanupCalls,
    waitForCancelWrite: () => cancelWriteStarted.promise,
    async cleanup() {
      cleanupCalls += 1
      await base.cleanup()
    },
  }
}

export function createFakeProcess(
  agent: FakeAcpAgent,
  options: { hangCancelWrites?: boolean; onCancelWrite?: () => void } = {},
): AcpProcessHandle {
  const [baseClientStream, agentStream] = linkedStreams()
  const clientStream: Stream = options.hangCancelWrites
    ? { writable: createCancelHangingWritable(baseClientStream.writable, options.onCancelWrite), readable: baseClientStream.readable }
    : baseClientStream
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

export function createCancelHangingWritable(inner: WritableStream<any>, onCancelWrite?: () => void): WritableStream<any> {
  let pendingWriteReject: ((reason: unknown) => void) | undefined
  const stream = new WritableStream<any>({
    async write(chunk) {
      const text = describeMessage(chunk)
      if (text.includes("\"session/cancel\"")) {
        onCancelWrite?.()
        await new Promise<void>((_, reject) => { pendingWriteReject = reject })
        return
      }
      const writer = inner.getWriter()
      try {
        await writer.write(chunk)
      } finally {
        writer.releaseLock()
      }
    },
    async abort(reason) {
      pendingWriteReject?.(reason)
      pendingWriteReject = undefined
      try {
        await inner.abort(reason)
      } catch {}
    },
    async close() {
      pendingWriteReject?.(new Error("stream closed"))
      pendingWriteReject = undefined
      try {
        await inner.close()
      } catch {}
    },
  })
  return stream
}

export function describeMessage(message: unknown): string {
  if (typeof message === "string") return message
  if (message instanceof Uint8Array) return new TextDecoder().decode(message)
  try {
    return JSON.stringify(message)
  } catch {
    return String(message)
  }
}

export function createSharedConnection(stream: Stream): SharedAcpConnection {
  const sessionUpdateHandlers = new Map<string, Parameters<SharedAcpConnection["setSessionHandlers"]>[1]>()
  const permissionHandlers = new Map<string, Parameters<SharedAcpConnection["setSessionHandlers"]>[2]>()
  const connection = new ClientSideConnection(
    () => ({
      sessionUpdate: async (notification) => {
        await (sessionUpdateHandlers.get(notification.sessionId) ?? (async () => {}))(notification)
      },
      requestPermission: async (params) => (permissionHandlers.get(params.sessionId) ?? (async () => ({ outcome: { outcome: "cancelled" } } as RequestPermissionResponse)))(params),
    }),
    stream,
  )

  return {
    connection,
    processPid: 12345,
    setSessionHandlers(sessionId, sessionUpdate, permission) {
      sessionUpdateHandlers.set(sessionId, sessionUpdate)
      permissionHandlers.set(sessionId, permission)
    },
    clearSessionHandlers(sessionId) {
      sessionUpdateHandlers.delete(sessionId)
      permissionHandlers.delete(sessionId)
    },
    async shutdown() {
      await Promise.allSettled([stream.readable.cancel(), stream.writable.abort()])
    },
  }
}

export function fakeServerConnection() {
  const events: Array<{ type: string; payload: unknown }> = []
  return {
    events,
    async ensureWorkflowAgentSession() {
      return {}
    },
    async getWorkflowAgentSession() {
      return null
    },
    async openWorkflowAgentSession() {
      return {}
    },
    async attachWorkflowAgentSession() {},
    async workflowAgentSessionEvents(_projectId: string, _workflowRunId: string, _sessionName: string, body: { events?: Array<{ type: string; payload: unknown }> }) {
      events.push(...(body.events ?? []))
    },
    async workflowAgentSessionRuntimeEvents(_projectId: string, _workflowRunId: string, _sessionName: string, body: { events?: Array<{ type: string; payload: unknown }>; runtimeEvents?: Array<{ type: string; payload: unknown }> }) {
      const all = body?.events ?? body?.runtimeEvents ?? []
      events.push(...all)
    },
  }
}

export function linkedStreams(): [Stream, Stream] {
  const clientToAgent = createSyncPipe()
  const agentToClient = createSyncPipe()
  return [
    { writable: clientToAgent.writable, readable: agentToClient.readable },
    { writable: agentToClient.writable, readable: clientToAgent.readable },
  ]
}

function createSyncPipe(): { writable: any, readable: any } {
  const queue: unknown[] = []
  const pendingReads: Array<(value: { value: unknown; done: boolean }) => void> = []
  let closed = false

  const enqueue = (chunk: unknown) => {
    if (closed) return
    const pendingRead = pendingReads.shift()
    if (pendingRead) {
      pendingRead({ value: chunk, done: false })
    } else {
      queue.push(chunk)
    }
  }

  const close = () => {
    closed = true
    while (pendingReads.length > 0) {
      const pendingRead = pendingReads.shift()
      if (!pendingRead) continue
      if (queue.length > 0) {
        pendingRead({ value: queue.shift(), done: false })
      } else {
        pendingRead({ value: undefined, done: true })
      }
    }
    return Promise.resolve()
  }

  const cancel = () => {
    closed = true
    queue.length = 0
    while (pendingReads.length > 0) {
      pendingReads.shift()?.({ value: undefined, done: true })
    }
    return Promise.resolve()
  }

  return {
    readable: {
      getReader: () => ({
        read: () => {
          if (queue.length > 0) return Promise.resolve({ value: queue.shift(), done: false })
          if (closed) return Promise.resolve({ value: undefined, done: true })
          return new Promise(resolve => { pendingReads.push(resolve) })
        },
        releaseLock() {},
        cancel,
      }),
      locked: false,
      cancel,
    },
    writable: {
      getWriter: () => ({
        write: (chunk: unknown) => { enqueue(chunk); return Promise.resolve() },
        releaseLock() {},
        close,
        abort: cancel,
      }),
      locked: false,
      abort: cancel,
      close,
    },
  }
}
