import { describe, expect, it, vi } from "vitest"
import { AgentJobExecutor } from "../src/runtime/agent-job-executor.js"
import type { AgentJobRuntimeAccessors } from "../src/runtime/agent-job-executor.js"
import type { ServerConnection } from "../src/server/connection.js"
import { WorkspaceHomeClaimedError } from "../src/runtime/workspace-entity.js"
import type { DispatchWorkItem } from "../src/core/types.js"
import type {
  OpenCodeRuntime,
  RuntimeResult,
  RuntimeTurnRequest,
  RuntimeTurnResult,
} from "../src/runtime/opencode/index.js"

interface FakeRuntimeHandles {
  runtime: OpenCodeRuntime
  runTurnCalls: RuntimeTurnRequest[]
}

function makeFakeRuntime(): FakeRuntimeHandles {
  const runTurnCalls: RuntimeTurnRequest[] = []
  let nextResult: RuntimeResult<RuntimeTurnResult> = {
    ok: true,
    value: {
      facts: {
        finalAssistantText: "agent finished",
        runtimeSessionId: "ses_default",
        workDir: "/tmp/ws",
      },
      diagnostics: [],
    },
    diagnostics: [],
  }
  const runtime: Partial<OpenCodeRuntime> = {
    ready: () => true,
    diagnostic: () => null,
    async runTurn(
      request: RuntimeTurnRequest,
      _signal: AbortSignal,
      _observer?: unknown,
    ): Promise<RuntimeResult<RuntimeTurnResult>> {
      runTurnCalls.push(request)
      return nextResult
    },
  }
  return {
    runtime: runtime as OpenCodeRuntime,
    runTurnCalls,
  }
}

function makeAccessors(runtime: OpenCodeRuntime | null = makeFakeRuntime().runtime): AgentJobRuntimeAccessors {
  return {
    openCode: runtime,
    pi: null,
  }
}

function makeFakeConnection() {
  const connection = {
    async openAgentSession() {},
    async attachAgentSession() {},
    async getAgentSession() {
      return {
        runtimeSessionId: "ses_default",
        workDir: "/tmp/ws",
      } as never
    },
    async agentSessionRuntimeEvents() {},
  } as unknown as ServerConnection
  return { connection }
}

function buildAgentJobWork(overrides: Partial<DispatchWorkItem> = {}): DispatchWorkItem {
  return {
    workflowRunId: "",
    workId: "aj-1",
    workType: "task",
    ownerKind: "agent-job",
    agentJobId: "aj-1",
    agentSessionId: "session-1",
    projectId: "proj-1",
    with: { prompt: "do the agent thing" },
    variables: {
      workspace: { path: "/tmp/agent-job-ws", branch: null, changeDir: null },
    },
    ...overrides,
  }
}

describe("AgentJobExecutor resolves a named workspace binding", () => {
  it("materializes the named workspace and anchors the prompt to its directory", async () => {
    const runtime = makeFakeRuntime()
    const connection = makeFakeConnection()
    const materialize = vi.fn(async () => ({ path: "/runner-root/workspaces/mohist-pay-abc123", created: true }))
    const manager = { materialize } as never
    const executor = new AgentJobExecutor(
      connection.connection,
      makeAccessors(runtime.runtime),
      null,
      "/virtual/runner",
      undefined,
      null,
      manager,
    )

    const work = buildAgentJobWork({
      projectId: "proj-1",
      variables: {
        workspace: {
          name: "pay",
          repositories: [{ name: "server", gitUrl: "https://github.com/mohist/server.git" }],
        },
      },
    })
    const result = await executor.execute(work, new AbortController().signal)

    expect(result.status).toBe("completed")
    expect(materialize).toHaveBeenCalledWith(
      "proj-1",
      "pay",
      [{ name: "server", gitUrl: "https://github.com/mohist/server.git" }],
      expect.any(AbortSignal),
    )
    const request = runtime.runTurnCalls[0]
    expect(request.target.workDir).toBe("/runner-root/workspaces/mohist-pay-abc123")
    expect(request.prompt).toContain("[mohist-workspace-anchor]")
    expect(request.prompt).toContain("Working directory: /runner-root/workspaces/mohist-pay-abc123")
    expect(request.prompt).toContain("do not search $HOME")
    expect(request.prompt).toContain("repos/")
  })

  it("skips the anchor when bound through the legacy workspace.path branch", async () => {
    const runtime = makeFakeRuntime()
    const connection = makeFakeConnection()
    const executor = new AgentJobExecutor(connection.connection, makeAccessors(runtime.runtime))

    const work = buildAgentJobWork({
      variables: { workspace: { path: "/legacy/path" } },
    })
    await executor.execute(work, new AbortController().signal)

    expect(runtime.runTurnCalls[0]?.target.workDir).toBe("/legacy/path")
    expect(runtime.runTurnCalls[0]?.prompt).not.toContain("[mohist-workspace-anchor]")
  })

  it("fails with workspace-home-claimed when another runner owns the home", async () => {
    const runtime = makeFakeRuntime()
    const connection = makeFakeConnection()
    const materialize = vi.fn(async () => {
      throw new WorkspaceHomeClaimedError("already materialized on runner-2")
    })
    const executor = new AgentJobExecutor(
      connection.connection,
      makeAccessors(runtime.runtime),
      null,
      "/virtual/runner",
      undefined,
      null,
      { materialize } as never,
    )

    const work = buildAgentJobWork({
      projectId: "proj-1",
      variables: { workspace: { name: "pay" } },
    })
    const result = await executor.execute(work, new AbortController().signal)

    expect(result.status).toBe("failed")
    expect(result.error?.code).toBe("workspace-home-claimed")
    expect(runtime.runTurnCalls).toHaveLength(0)
  })

  it("fails with workspace-materialization-failed when materialization throws", async () => {
    const runtime = makeFakeRuntime()
    const connection = makeFakeConnection()
    const materialize = vi.fn(async () => {
      throw new Error("workspace materialization failed: 500")
    })
    const executor = new AgentJobExecutor(
      connection.connection,
      makeAccessors(runtime.runtime),
      null,
      "/virtual/runner",
      undefined,
      null,
      { materialize } as never,
    )

    const work = buildAgentJobWork({
      projectId: "proj-1",
      variables: { workspace: { name: "pay" } },
    })
    const result = await executor.execute(work, new AbortController().signal)

    expect(result.status).toBe("failed")
    expect(result.error?.code).toBe("workspace-materialization-failed")
    expect(runtime.runTurnCalls).toHaveLength(0)
  })

  it("rejects a workspace object with neither name nor path", async () => {
    const runtime = makeFakeRuntime()
    const connection = makeFakeConnection()
    const executor = new AgentJobExecutor(connection.connection, makeAccessors(runtime.runtime))

    const work = buildAgentJobWork({ variables: { workspace: { repositories: [] } } })
    const result = await executor.execute(work, new AbortController().signal)

    expect(result.status).toBe("failed")
    expect(result.message).toMatch(/workspace\.name/)
    expect(runtime.runTurnCalls).toHaveLength(0)
  })
})
