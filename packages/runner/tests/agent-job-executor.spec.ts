import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { AgentJobExecutor, buildActionOutputFromTurn, projectTurnToActionResult } from "../src/runtime/agent-job-executor.js"
import type { ServerConnection } from "../src/server/connection.js"
import type { RenderedWorkItem } from "../src/core/types.js"
import type {
  OpenCodeRuntime,
  RuntimeResult,
  RuntimeTurnFacts,
  RuntimeTurnEvent,
  RuntimeTurnRequest,
  RuntimeTurnResult,
} from "../src/runtime/opencode/index.js"

interface FakeRuntimeHandles {
  runtime: OpenCodeRuntime
  runTurnCalls: RuntimeTurnRequest[]
  setTurnResult: (result: RuntimeResult<RuntimeTurnResult>) => void
  setTurnEvents: (events: RuntimeTurnEvent[]) => void
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
  let nextEvents: RuntimeTurnEvent[] = []
  const runtime: Partial<OpenCodeRuntime> = {
    ready: () => true,
    diagnostic: () => null,
    async runTurn(request: RuntimeTurnRequest, _signal: AbortSignal): Promise<RuntimeResult<RuntimeTurnResult>> {
      runTurnCalls.push(request)
      if (nextResult.ok) {
        await request.onSessionReady?.(nextResult.value.facts.runtimeSessionId, nextResult.value.facts.workDir)
        for (const event of nextEvents) request.onEvent?.(event)
      }
      return nextResult
    },
  }
  return {
    runtime: runtime as OpenCodeRuntime,
    runTurnCalls,
    setTurnResult(result) {
      nextResult = result
    },
    setTurnEvents(events) {
      nextEvents = events
    },
  }
}

interface FakeConnectionHandles {
  connection: ServerConnection
  attachCalls: Array<{
    projectId: string
    sessionId: string
    body: Record<string, unknown>
  }>
  eventCalls: Array<{ projectId: string; sessionId: string; body: Record<string, unknown> }>
  setAgentSession: (session: { runtimeSessionId: string | null } | null) => void
}

function makeFakeConnection(): FakeConnectionHandles {
  const attachCalls: FakeConnectionHandles["attachCalls"] = []
  const eventCalls: FakeConnectionHandles["eventCalls"] = []
  let agentSession: { runtimeSessionId: string | null } | null = null
  const connection = {
    async attachAgentSession(
      projectId: string,
      sessionId: string,
      body: Record<string, unknown>,
      _signal: AbortSignal,
    ) {
      attachCalls.push({ projectId, sessionId, body })
    },
    async getAgentSession(_projectId: string, sessionId: string, _signal: AbortSignal) {
      if (agentSession === null) return null
      return {
        runtimeSessionId: agentSession.runtimeSessionId,
        workDir: "/tmp/ws",
      } as never
    },
    async agentSessionRuntimeEvents(projectId: string, sessionId: string, body: Record<string, unknown>) {
      eventCalls.push({ projectId, sessionId, body })
    },
  } as unknown as ServerConnection
  return {
    connection,
    attachCalls,
    eventCalls,
    setAgentSession(session) {
      agentSession = session
    },
  }
}

function buildAgentJobWork(overrides: Partial<RenderedWorkItem> = {}): RenderedWorkItem {
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

beforeEach(() => {
  vi.restoreAllMocks()
})

afterEach(() => {
  vi.restoreAllMocks()
})

describe("AgentJobExecutor drives OpenCodeRuntime directly", () => {
  it("calls OpenCodeRuntime.runTurn with a flat Agent-owned request", async () => {
    const runtime = makeFakeRuntime()
    const connection = makeFakeConnection()
    const executor = new AgentJobExecutor(connection.connection, runtime.runtime)

    const work = buildAgentJobWork({
      with: {
        prompt: "review the diff",
        instructions: "be terse",
        model: "openai/gpt-5.5",
        variant: "high",
      },
    })
    const result = await executor.execute(work, new AbortController().signal)

    expect(result.status).toBe("completed")
    expect(runtime.runTurnCalls).toHaveLength(1)
    const request = runtime.runTurnCalls[0]
    expect(request.target.runtime).toBe("opencode")
    expect(request.target.workDir).toBe("/tmp/agent-job-ws")
    expect(request.target.runtimeSessionId).toBeNull()
    expect(request.options?.model).toEqual({ providerID: "openai", modelID: "gpt-5.5" })
    expect(request.options?.variant).toBe("high")
    expect(request.prompt).toBe("be terse\n\nreview the diff")
  })

  it("returns the legacy {kind, status, runtimeSessionId, model, variant, text, error} envelope", async () => {
    const runtime = makeFakeRuntime()
    const connection = makeFakeConnection()
    const executor = new AgentJobExecutor(connection.connection, runtime.runtime)

    const work = buildAgentJobWork({
      with: {
        prompt: "do the thing",
        model: "anthropic/claude-sonnet-4",
        variant: "max",
      },
    })
    runtime.setTurnResult({
      ok: true,
      value: {
        facts: {
          finalAssistantText: "done",
          runtimeSessionId: "ses_xyz",
          workDir: "/tmp/agent-job-ws",
        },
        diagnostics: [],
      },
      diagnostics: [],
    })

    const result = await executor.execute(work, new AbortController().signal)

    expect(result.status).toBe("completed")
    const parsed = JSON.parse(result.output ?? "{}")
    expect(parsed.kind).toBe("opencode")
    expect(parsed.status).toBe("success")
    expect(parsed.runtimeSessionId).toBe("ses_xyz")
    expect(parsed.model).toBe("anthropic/claude-sonnet-4")
    expect(parsed.variant).toBe("max")
    expect(parsed.text).toBe("done")
    expect(parsed.error).toBeNull()
  })

  it("never resolves a Workflow Action for an AgentJob dispatch", async () => {
    const runtime = makeFakeRuntime()
    const connection = makeFakeConnection()
    const executor = new AgentJobExecutor(connection.connection, runtime.runtime)

    // Even if `with.uses` was stamped (it should never be in the
    // new server-side envelope), the executor does not consult an
    // Action registry.
    const work = buildAgentJobWork({
      uses: "mohist/opencode",
      with: {
        prompt: "no action resolution",
        uses: "mohist/opencode",
      },
    })
    const result = await executor.execute(work, new AbortController().signal)
    expect(result.status).toBe("completed")
    expect(runtime.runTurnCalls).toHaveLength(1)
  })

  it("rejects a non-agent-job dispatch with a clear failure", async () => {
    const runtime = makeFakeRuntime()
    const connection = makeFakeConnection()
    const executor = new AgentJobExecutor(connection.connection, runtime.runtime)

    const work = buildAgentJobWork({ ownerKind: "workflow", agentJobId: null })
    const result = await executor.execute(work, new AbortController().signal)
    expect(result.status).toBe("failed")
    expect(result.message).toMatch(/non-agent-job/)
    expect(runtime.runTurnCalls).toHaveLength(0)
  })

  it("requires the OpenCode runtime to be present", async () => {
    const connection = makeFakeConnection()
    const executor = new AgentJobExecutor(connection.connection, null)
    const work = buildAgentJobWork()
    const result = await executor.execute(work, new AbortController().signal)
    expect(result.status).toBe("failed")
    expect(result.message).toMatch(/requires the OpenCode runtime/)
  })

  it("fails when the runtime is not yet ready", async () => {
    const connection = makeFakeConnection()
    const runtime: Partial<OpenCodeRuntime> = {
      ready: () => false,
      diagnostic: () => ({ severity: "warning", code: "runtime-not-ready", message: "not ready" }),
      async runTurn() {
        throw new Error("should not be called")
      },
    }
    const executor = new AgentJobExecutor(connection.connection, runtime as OpenCodeRuntime)
    const work = buildAgentJobWork()
    const result = await executor.execute(work, new AbortController().signal)
    expect(result.status).toBe("failed")
    expect(result.message).toMatch(/ready/)
  })
})

describe("AgentJobExecutor reports the runtime session binding", () => {
  it("reports the runtime session id back via attachAgentSession after a successful turn", async () => {
    const runtime = makeFakeRuntime()
    const connection = makeFakeConnection()
    connection.setAgentSession({ runtimeSessionId: null })
    const executor = new AgentJobExecutor(connection.connection, runtime.runtime)

    runtime.setTurnResult({
      ok: true,
      value: {
        facts: {
          finalAssistantText: "ran",
          runtimeSessionId: "ses_bound",
          workDir: "/tmp/agent-job-ws",
        },
        diagnostics: [],
      },
      diagnostics: [],
    })

    const work = buildAgentJobWork({
      agentSessionId: "session-bound",
      with: { prompt: "report me" },
    })
    const result = await executor.execute(work, new AbortController().signal)
    expect(result.status).toBe("completed")
    expect(connection.attachCalls).toHaveLength(1)
    const attach = connection.attachCalls[0]
    expect(attach.projectId).toBe("proj-1")
    expect(attach.sessionId).toBe("session-bound")
    expect(attach.body).toMatchObject({
      runtimeSessionId: "ses_bound",
      workDir: "/tmp/agent-job-ws",
      workId: "aj-1",
      agentJobId: "aj-1",
    })
  })

  it("forwards matching runtime events to the canonical AgentSession", async () => {
    const runtime = makeFakeRuntime()
    const connection = makeFakeConnection()
    const executor = new AgentJobExecutor(connection.connection, runtime.runtime)
    runtime.setTurnEvents([{
      type: "message.delta",
      runtimeSessionId: "ses_default",
      workDir: "/tmp/ws",
      payload: { text: "working" },
    }])

    await executor.execute(buildAgentJobWork(), new AbortController().signal)

    expect(connection.attachCalls).toHaveLength(1)
    expect(connection.eventCalls).toEqual([{
      projectId: "proj-1",
      sessionId: "session-1",
      body: {
        workId: "aj-1",
        workType: "task",
        stage: undefined,
        runtimeSessionId: "ses_default",
        runtimeEvents: [{ type: "message.delta", payload: { text: "working" } }],
      },
    }])
  })

  it("does not report a binding when the dispatch carries no AgentSessionId", async () => {
    const runtime = makeFakeRuntime()
    const connection = makeFakeConnection()
    const executor = new AgentJobExecutor(connection.connection, runtime.runtime)

    const work = buildAgentJobWork({ agentSessionId: null })
    const result = await executor.execute(work, new AbortController().signal)
    expect(result.status).toBe("completed")
    expect(connection.attachCalls).toHaveLength(0)
  })

  it("attaches the runtimeSessionId from an existing binding on a follow-up dispatch", async () => {
    const runtime = makeFakeRuntime()
    const connection = makeFakeConnection()
    connection.setAgentSession({ runtimeSessionId: "ses_existing" })
    const executor = new AgentJobExecutor(connection.connection, runtime.runtime)

    const work = buildAgentJobWork({ agentSessionId: "session-existing" })
    const result = await executor.execute(work, new AbortController().signal)

    expect(result.status).toBe("completed")
    expect(runtime.runTurnCalls).toHaveLength(1)
    expect(runtime.runTurnCalls[0].target.runtimeSessionId).toBe("ses_existing")
    expect(connection.attachCalls).toHaveLength(1)
    expect(connection.attachCalls[0].body.runtimeSessionId).toMatch(/ses_/)
  })

  it("tolerates an attach failure (best-effort; the runtime turn already settled)", async () => {
    const runtime = makeFakeRuntime()
    const connection: ServerConnection = {
      async attachAgentSession() {
        throw new Error("attach endpoint offline")
      },
      async getAgentSession() {
        return { runtimeSessionId: null } as never
      },
    } as unknown as ServerConnection
    const executor = new AgentJobExecutor(connection, runtime.runtime)

    const work = buildAgentJobWork()
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)
    const result = await executor.execute(work, new AbortController().signal)
    expect(result.status).toBe("completed")
    expect(errorSpy).toHaveBeenCalled()
    errorSpy.mockRestore()
  })
})

describe("AgentJobExecutor materialises the launch-time snapshot", () => {
  it("does not consult the Agent definition; it reads the dispatch payload only", async () => {
    // Editing/archiving the Agent definition while the job is in
    // flight does not change the running turn's inputs. The
    // executor reads only `work.with`; there is no Agent lookup,
    // and `work.with` is the launch-time snapshot that the server
    // already wrote into the dispatch envelope.
    const runtime = makeFakeRuntime()
    const connection = makeFakeConnection()
    const executor = new AgentJobExecutor(connection.connection, runtime.runtime)

    const launchTimeInstructions = "be brief; cite line numbers"
    const launchTimeModel = "openai/gpt-5.5"
    const launchTimeVariant = "high"
    const work = buildAgentJobWork({
      with: {
        prompt: "audit the diff",
        instructions: launchTimeInstructions,
        model: launchTimeModel,
        variant: launchTimeVariant,
      },
    })
    await executor.execute(work, new AbortController().signal)

    expect(runtime.runTurnCalls).toHaveLength(1)
    const request = runtime.runTurnCalls[0]
    expect(request.prompt).toBe(`${launchTimeInstructions}\n\naudit the diff`)
    expect(request.options?.model).toEqual({ providerID: "openai", modelID: "gpt-5.5" })
    expect(request.options?.variant).toBe(launchTimeVariant)
  })

  it("does not mutate state across calls; each invocation reads a fresh dispatch snapshot", async () => {
    const runtime = makeFakeRuntime()
    const connection = makeFakeConnection()
    const executor = new AgentJobExecutor(connection.connection, runtime.runtime)

    // First launch pins one snapshot
    await executor.execute(
      buildAgentJobWork({ with: { prompt: "first", instructions: "original" } }),
      new AbortController().signal,
    )
    // A second call with a different dispatch payload must use the new payload
    await executor.execute(
      buildAgentJobWork({ with: { prompt: "second", instructions: "updated" } }),
      new AbortController().signal,
    )

    expect(runtime.runTurnCalls).toHaveLength(2)
    expect(runtime.runTurnCalls[0].prompt).toBe("original\n\nfirst")
    expect(runtime.runTurnCalls[1].prompt).toBe("updated\n\nsecond")
  })
})

describe("AgentJobExecutor parses the dispatch payload", () => {
  it("rejects a dispatch without a prompt", async () => {
    const runtime = makeFakeRuntime()
    const connection = makeFakeConnection()
    const executor = new AgentJobExecutor(connection.connection, runtime.runtime)

    const work = buildAgentJobWork({ with: { instructions: "no prompt" } })
    const result = await executor.execute(work, new AbortController().signal)
    expect(result.status).toBe("failed")
    expect(result.message).toMatch(/prompt/)
    expect(runtime.runTurnCalls).toHaveLength(0)
  })

  it("rejects a malformed model identifier", async () => {
    const runtime = makeFakeRuntime()
    const connection = makeFakeConnection()
    const executor = new AgentJobExecutor(connection.connection, runtime.runtime)

    const work = buildAgentJobWork({ with: { prompt: "go", model: "not a model id" } })
    const result = await executor.execute(work, new AbortController().signal)
    expect(result.status).toBe("failed")
    expect(result.message).toMatch(/model/)
    expect(runtime.runTurnCalls).toHaveLength(0)
  })

  it("requires a workspace.path variable", async () => {
    const runtime = makeFakeRuntime()
    const connection = makeFakeConnection()
    const executor = new AgentJobExecutor(connection.connection, runtime.runtime)

    const work = buildAgentJobWork({ variables: {} })
    const result = await executor.execute(work, new AbortController().signal)
    expect(result.status).toBe("failed")
    expect(result.message).toMatch(/workspace\.path/)
    expect(runtime.runTurnCalls).toHaveLength(0)
  })
})

describe("AgentJobExecutor surfaces a missing-session turn as a Reset hint", () => {
  it("returns the legacy {kind, status, ..., hint: 'reset'} envelope on a missing session", async () => {
    const runtime = makeFakeRuntime()
    const connection = makeFakeConnection()
    const executor = new AgentJobExecutor(connection.connection, runtime.runtime)

    runtime.setTurnResult({
      ok: false,
      error: {
        kind: "missing-session",
        message: "no physical session",
        diagnostics: [],
      },
      diagnostics: [],
    })

    const work = buildAgentJobWork({ agentSessionId: "session-orphan" })
    const result = await executor.execute(work, new AbortController().signal)
    expect(result.status).toBe("failed")
    const parsed = JSON.parse(result.output ?? "{}")
    expect(parsed.kind).toBe("opencode")
    expect(parsed.status).toBe("failure")
    expect(parsed.runtimeSessionId).toBeNull()
    expect(parsed.error).toMatch(/no physical session/)
    expect(parsed.hint).toBe("reset")
  })
})

describe("legacy AgentJob action-output envelope helper", () => {
  it("buildActionOutputFromTurn emits a drop-in compatible shape", () => {
    const out = JSON.parse(
      buildActionOutputFromTurn(true, "ses_x", "openai/gpt-5.5", "high", "done", null, []),
    )
    expect(out).toMatchObject({
      kind: "opencode",
      status: "success",
      runtimeSessionId: "ses_x",
      model: "openai/gpt-5.5",
      variant: "high",
      text: "done",
      error: null,
      diagnostics: [],
    })
  })

  it("projectTurnToActionResult maps a successful RuntimeResult to a success ActionResult", () => {
    const result: RuntimeResult<RuntimeTurnResult> = {
      ok: true,
      value: {
        facts: {
          finalAssistantText: "yes",
          runtimeSessionId: "ses_a",
          workDir: "/tmp/w",
        } satisfies RuntimeTurnFacts,
        diagnostics: [],
      },
      diagnostics: [],
    }
    const action = projectTurnToActionResult(result, "openai/gpt-5.5", "high")
    expect(action.status).toBe("success")
    expect(action.exitCode).toBe(0)
    const parsed = JSON.parse(action.output ?? "{}")
    expect(parsed.status).toBe("success")
    expect(parsed.runtimeSessionId).toBe("ses_a")
    expect(parsed.text).toBe("yes")
  })

  it("projectTurnToActionResult maps a failed RuntimeResult to a failure ActionResult", () => {
    const result: RuntimeResult<RuntimeTurnResult> = {
      ok: false,
      error: { kind: "turn-failed", message: "boom", diagnostics: [] },
      diagnostics: [],
    }
    const action = projectTurnToActionResult(result, null, null)
    expect(action.status).toBe("failure")
    expect(action.exitCode).toBe(1)
    const parsed = JSON.parse(action.output ?? "{}")
    expect(parsed.status).toBe("failure")
    expect(parsed.error).toBe("boom")
  })
})
