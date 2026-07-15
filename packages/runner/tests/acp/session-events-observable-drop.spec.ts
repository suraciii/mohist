import { describe, expect, it } from "vitest"
import { emitSessionEvent } from "../../src/actions/acp/session-events.js"
import type { ActionContext } from "../../src/core/types.js"
import type { ServerConnection } from "../../src/server/connection.js"
import { TaskLogCollector, TaskLogger } from "../../src/runtime/task-log.js"

function baseContext(overrides: Partial<ActionContext> = {}): ActionContext {
  return {
    workflowRunId: "wf-1",
    workId: "work-1",
    workType: "task",
    variables: {},
    workDir: "D:/work",
    signal: new AbortController().signal,
    writeVars: async () => {},
    ...overrides,
  }
}

function capturingLogger(): { logger: TaskLogger; collector: TaskLogCollector } {
  const collector = new TaskLogCollector()
  const logger = new TaskLogger({ collector })
  return { logger, collector }
}

function fakeServerConnection(): ServerConnection & { calls: Array<{ method: string; args: unknown[] }> } {
  const calls: Array<{ method: string; args: unknown[] }> = []
  return {
    calls,
    async agentSessionRuntimeEvents(...args: unknown[]) {
      calls.push({ method: "agentSessionRuntimeEvents", args })
    },
    async workflowAgentSessionRuntimeEvents(...args: unknown[]) {
      calls.push({ method: "workflowAgentSessionRuntimeEvents", args })
    },
    async attachAgentSession(...args: unknown[]) {
      calls.push({ method: "attachAgentSession", args })
    },
    async attachWorkflowAgentSession(...args: unknown[]) {
      calls.push({ method: "attachWorkflowAgentSession", args })
    },
    async ensureWorkflowAgentSession(...args: unknown[]) {
      calls.push({ method: "ensureWorkflowAgentSession", args })
      return { runtimeSessionId: "shared", runtime: "opencode", workDir: "D:/work" }
    },
    async getWorkflowAgentSession(...args: unknown[]) {
      calls.push({ method: "getWorkflowAgentSession", args })
      return null
    },
    async openWorkflowAgentSession(...args: unknown[]) {
      calls.push({ method: "openWorkflowAgentSession", args })
      return { runtimeSessionId: "shared", runtime: "opencode", workDir: "D:/work" }
    },
  } as unknown as ServerConnection & { calls: Array<{ method: string; args: unknown[] }> }
}

describe("emitSessionEvent observable drop (D3)", () => {
  it("AgentJobWithMissingAgentSessionId_LogsUnresolvedTargetOnce_AndDoesNotThrow", async () => {
    const { logger, collector } = capturingLogger()
    const server = fakeServerConnection()
    const context = baseContext({
      ownerKind: "agent-job",
      projectId: "project-1",
      workflowRunId: "",
      agentJobId: "job-77",
      agentSessionId: undefined,
      serverConnection: server,
      log: logger,
    })

    await emitSessionEvent(context, "message.delta", { content: { text: "first drop" } })
    await emitSessionEvent(context, "tool_call.started", { toolName: "Read" })
    await emitSessionEvent(context, "usage.updated", { inputTokens: 10, outputTokens: 5 })

    const dropped = collector
      .flush()
      .entries.filter((entry) => entry.source === "action:session-events")
    expect(dropped).toHaveLength(1)
    expect(dropped[0]!.text).toMatch(/unresolved generic session target — events dropped/)
    expect(dropped[0]!.text).toContain("workId=work-1")
    expect(dropped[0]!.text).toContain("agentJobId=job-77")
    expect(server.calls).toEqual([])
  })

  it("AgentJobWithNullAgentSessionId_LogsUnresolvedTargetOnce", async () => {
    const { logger, collector } = capturingLogger()
    const server = fakeServerConnection()
    const context = baseContext({
      ownerKind: "agent-job",
      projectId: "project-1",
      workflowRunId: "",
      agentJobId: "job-null",
      agentSessionId: null as unknown as undefined,
      serverConnection: server,
      log: logger,
    })

    await emitSessionEvent(context, "message.delta", { content: { text: "drop" } })
    await emitSessionEvent(context, "message.delta", { content: { text: "drop" } })

    const dropped = collector
      .flush()
      .entries.filter((entry) => entry.source === "action:session-events")
    expect(dropped).toHaveLength(1)
  })

  it("AgentJobWithWhitespaceAgentSessionId_LogsUnresolvedTargetOnce", async () => {
    const { logger, collector } = capturingLogger()
    const server = fakeServerConnection()
    const context = baseContext({
      ownerKind: "agent-job",
      projectId: "project-1",
      workflowRunId: "",
      agentJobId: "job-ws",
      agentSessionId: "   ",
      serverConnection: server,
      log: logger,
    })

    await emitSessionEvent(context, "message.delta", { content: { text: "drop" } })

    const dropped = collector
      .flush()
      .entries.filter((entry) => entry.source === "action:session-events")
    expect(dropped).toHaveLength(1)
    expect(dropped[0]!.text).toContain("agentJobId=job-ws")
    expect(server.calls).toEqual([])
  })

  it("EphemeralJobWithoutOwnerKindAgent_DoesNotLogAndDoesNotThrow", async () => {
    const { logger, collector } = capturingLogger()
    const server = fakeServerConnection()
    const context = baseContext({
      ownerKind: undefined,
      projectId: "project-1",
      workflowRunId: "",
      workId: "",
      with: {},
      serverConnection: server,
      log: logger,
    })

    await emitSessionEvent(context, "message.delta", { content: { text: "ephemeral" } })
    await emitSessionEvent(context, "tool_call.started", { toolName: "Read" })

    const dropped = collector
      .flush()
      .entries.filter((entry) => entry.source === "action:session-events")
    expect(dropped).toEqual([])
    expect(server.calls).toEqual([])
  })

  it("AgentJobWithoutLogSink_SilentlyDropsButDoesNotThrow", async () => {
    const server = fakeServerConnection()
    const context = baseContext({
      ownerKind: "agent-job",
      projectId: "project-1",
      workflowRunId: "",
      agentJobId: "job-no-log",
      agentSessionId: undefined,
      serverConnection: server,
      log: null,
    })

    await expect(
      emitSessionEvent(context, "message.delta", { content: { text: "no log" } }),
    ).resolves.toBeUndefined()
    expect(server.calls).toEqual([])
  })

  it("AgentJobWithNonNullAgentSessionId_DeliversGenericEventsWithoutDropLog", async () => {
    const { logger, collector } = capturingLogger()
    const server = fakeServerConnection()
    const context = baseContext({
      ownerKind: "agent-job",
      projectId: "project-1",
      workflowRunId: "",
      agentJobId: "job-happy",
      agentSessionId: "session-minted",
      serverConnection: server,
      log: logger,
    })

    await emitSessionEvent(context, "message.delta", { content: { text: "hello generic" } }, "runtime-generic")
    await emitSessionEvent(context, "usage.updated", { inputTokens: 12, outputTokens: 3 }, "runtime-generic")

    expect(server.calls.map((entry) => entry.method)).toEqual([
      "agentSessionRuntimeEvents",
      "agentSessionRuntimeEvents",
    ])
    expect(server.calls[0]!.args[2]).toMatchObject({
      runtimeSessionId: "runtime-generic",
      runtimeEvents: [{ type: "message.delta", payload: { content: { text: "hello generic" } } }],
    })
    const dropped = collector
      .flush()
      .entries.filter((entry) => entry.source === "action:session-events")
    expect(dropped).toEqual([])
  })

  it("AgentJobWithoutServerConnection_LogsMissingConnectionOnceButDoesNotReportUnresolvedTarget", async () => {
    const { logger, collector } = capturingLogger()
    const context = baseContext({
      ownerKind: "agent-job",
      projectId: "project-1",
      workflowRunId: "",
      agentJobId: "job-no-conn",
      agentSessionId: "session-1",
      serverConnection: undefined,
      log: logger,
    })

    await emitSessionEvent(context, "message.delta", { content: { text: "no conn" } })
    await emitSessionEvent(context, "message.delta", { content: { text: "no conn" } })

    const dropped = collector
      .flush()
      .entries.filter((entry) => entry.source === "action:session-events")
    expect(dropped).toHaveLength(1)
    expect(dropped[0]!.text).toMatch(/missing server connection — session events dropped/)
    expect(dropped[0]!.text).not.toMatch(/unresolved generic session target/)
  })
})
