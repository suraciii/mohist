import { describe, expect, it, vi } from "vitest"
import { piAction, PI_TURN_DURATION_MS } from "./pi.js"
import type { AgentExecutionDefinition, JsonObject, ParentIssueContext } from "../core/types.js"
import type { ServerConnection } from "../server/connection.js"
import type { PiRuntime } from "../runtime/pi/index.js"
import type { ActionHost } from "./host.js"

type ActionContext = {
  workflowRunId: string
  workId: string
  workType: string
  variables: JsonObject
  workDir: string
  signal: AbortSignal
  with?: JsonObject | null
  writeVars: (vars: JsonObject) => Promise<void>
  projectId?: string | null
  parentIssueContext?: ParentIssueContext | null
  piRuntime?: PiRuntime | null
  serverConnection?: ServerConnection | null
  agentDefinition?: AgentExecutionDefinition | null
}

function context(overrides: Partial<ActionContext> = {}): ActionContext {
  return {
    workflowRunId: "run-1",
    workId: "work-1",
    workType: "task",
    variables: {},
    workDir: "/workspace",
    signal: new AbortController().signal,
    with: { prompt: "return <promise>PASS</promise>", session: " shared " },
    writeVars: async () => {},
    ...overrides,
  }
}

function runtime() {
  const turns: unknown[] = []
  return {
    turns,
    ready: () => true,
    diagnostic: () => null,
    createSession: vi.fn(async () => ({ ok: true as const, value: { runtimeSessionId: "/workspace/.pi/session.json", workDir: "/workspace" }, diagnostics: [] })),
    runTurn: vi.fn(async (request: { durationMs?: number }, _signal: AbortSignal, observer?: { onEvent?: (event: unknown) => void }) => {
      turns.push(request)
      observer?.onEvent?.({ id: "assistant-1", type: "assistant.text", runtimeSessionId: "/workspace/.pi/session.json", workDir: "/workspace", payload: { content: "done" } })
      return { ok: true as const, value: { facts: { finalAssistantText: "return <promise>PASS</promise>", runtimeSessionId: "/workspace/.pi/session.json", workDir: "/workspace" }, diagnostics: [] }, diagnostics: [] }
    }),
  }
}

function server() {
  const calls: unknown[] = []
  return {
    calls,
    openWorkflowAgentSession: vi.fn(async () => ({ sessionId: "session-1", runtime: "pi", runtimeSessionId: null, workDir: "/workspace" })),
    recordWorkflowAgentTurn: vi.fn(async (_project: string, _run: string, _name: string, body: { inputId: string; turnId: string }) => ({ sessionId: "session-1", inputId: body.inputId, turnId: body.turnId, status: "queued" })),
    attachWorkflowAgentSession: vi.fn(async () => ({ sessionId: "session-1", runtime: "pi", runtimeSessionId: "/workspace/.pi/session.json", workDir: "/workspace" })),
    workflowAgentSessionRuntimeEvents: vi.fn(async (_project: string, _run: string, _name: string, body: unknown) => { calls.push(body); return [{ id: "accepted" }] }),
    abandonWorkflowAgentTurn: vi.fn(async () => {}),
  }
}

describe("mohist/pi Action", () => {
  it("rejects undeclared top-level input before Session or runtime side effects", async () => {
    const pi = runtime()
    const connection = server()
    const result = await piAction(context({ with: { prompt: "hello", timeout: 10 }, piRuntime: pi as never, serverConnection: connection as never, projectId: "project" }))
    expect(result).toMatchObject({ error: { code: "invalid-input" } })
    expect(connection.openWorkflowAgentSession).not.toHaveBeenCalled()
    expect(pi.createSession).not.toHaveBeenCalled()
  })

  it("binds, accepts input, submits a fixed-duration turn, and returns the Session to idle", async () => {
    const pi = runtime()
    const connection = server()
    const result = await piAction(context({ piRuntime: pi as never, serverConnection: connection as never, projectId: "project" }))
    expect(result).toMatchObject({ output: null, turnFact: { finalAssistantText: "return <promise>PASS</promise>" } })
    expect(connection.attachWorkflowAgentSession).toHaveBeenCalledBefore(connection.workflowAgentSessionRuntimeEvents)
    expect(pi.turns[0]).toMatchObject({ durationMs: PI_TURN_DURATION_MS })
    expect(connection.workflowAgentSessionRuntimeEvents).toHaveBeenCalledTimes(2)
    expect((connection.calls[0] as { runtimeEvents: Array<{ type: string }> }).runtimeEvents[0].type).toBe("session.input")
    expect((connection.calls[1] as { runtimeEvents: Array<{ type: string }> }).runtimeEvents.at(-1)?.type).toBe("session.activity")
  })

  it("preserves an unexpected turn failure when terminal reporting also fails", async () => {
    const pi = runtime()
    const connection = server()
    pi.runTurn.mockRejectedValueOnce(new Error("SDK turn failed"))
    connection.workflowAgentSessionRuntimeEvents
      .mockImplementationOnce(async (_project: string, _run: string, _name: string, body: unknown) => {
        connection.calls.push(body)
        return [{ id: "accepted" }]
      })
      .mockImplementationOnce(async (_project: string, _run: string, _name: string, body: unknown) => {
        connection.calls.push(body)
        throw new Error("terminal report rejected")
      })

    const result = await piAction(context({ piRuntime: pi as never, serverConnection: connection as never, projectId: "project" }))

    expect(result).toMatchObject({
      error: {
        code: "turn-failed",
        message: "SDK turn failed; Session terminal reporting failed and terminal state was not accepted",
      },
    })
    expect(connection.workflowAgentSessionRuntimeEvents).toHaveBeenCalledTimes(2)
    expect((connection.calls[1] as { runtimeEvents: Array<{ type: string }> }).runtimeEvents.at(-1)?.type).toBe("session.activity")
  })

  it("keeps unknown options diagnostic-only", async () => {
    const pi = runtime()
    const connection = server()
    const result = await piAction(context({ with: { prompt: "hello", options: { model: "provider/model", variant: "high", legacy: true } }, piRuntime: pi as never, serverConnection: connection as never, projectId: "project" }))
    expect(result).not.toHaveProperty("error")
    expect(pi.turns[0]).toMatchObject({ options: { model: "provider/model", variant: "high", unknownKeys: ["legacy"] } })
  })

  it("uses the dispatch-only Agent definition without expanding Action options", async () => {
    const pi = runtime()
    const connection = server()
    const definition: AgentExecutionDefinition = {
      instructions: "Review with the configured policy.",
      runtime: "pi",
      model: "provider/configured-model",
      variant: "high",
      skills: [],
    }

    const result = await piAction(context({
      with: { prompt: "review this", options: { model: "provider/caller-model", variant: "low" } },
      agentDefinition: definition,
      piRuntime: pi as never,
      serverConnection: connection as never,
      projectId: "project",
    }))

    expect(result).not.toHaveProperty("error")
    expect(pi.turns[0]).toMatchObject({
      prompt: "Review with the configured policy.\n\nreview this",
      options: { model: "provider/configured-model", variant: "high" },
    })
  })

  it("returns the TaskRun-owned identity and keeps the public input prompt uncomposed", async () => {
    const pi = runtime()
    const connection = server()
    const definition: AgentExecutionDefinition = {
      agentId: "agent-reviewer",
      instructions: "Internal policy that must not enter the public input transcript.",
      runtime: "pi",
      model: "provider/configured-model",
      variant: "high",
      skills: [],
    }

    const result = await piAction(context({
      with: { prompt: "review this", options: { model: "provider/caller-model" } },
      agentDefinition: definition,
      piRuntime: pi as never,
      serverConnection: connection as never,
      projectId: "project",
    }))

    expect(result).toMatchObject({
      turnFact: {
        agentObservation: {
          agentId: "agent-reviewer",
          sessionId: "session-1",
          status: "completed",
          outcome: "completed",
          finalText: "return <promise>PASS</promise>",
        },
      },
    })
    expect(connection.recordWorkflowAgentTurn).toHaveBeenCalledTimes(1)
    const input = (connection.calls[0] as { runtimeEvents: Array<{ payload: { text: string; inputId: string; turnId: string } }> }).runtimeEvents[0].payload
    expect(input.text).toBe("review this")
    expect(input.text).not.toContain("Internal policy")
    expect(input.inputId).toMatch(/^workflow-input-/)
    expect(input.turnId).toMatch(/^workflow-turn-/)
  })

  it("keeps workflow identity and Server connection on the built-in ActionHost path", async () => {
    const pi = runtime()
    const connection = server()
    const definition: AgentExecutionDefinition = {
      agentId: "agent-reviewer",
      instructions: "internal policy",
      runtime: "pi",
      model: "provider/model",
      variant: null,
      skills: [],
    }
    const host: ActionHost = {
      workDir: "/workspace",
      signal: new AbortController().signal,
      log: null,
      workflowRunId: "run-1",
      workId: "work-1",
      workType: "task",
      projectId: "project",
      piRuntime: pi as never,
      serverConnection: connection as never,
      agentDefinition: definition,
      exec: async () => ({ exitCode: 0, stdout: "", stderr: "" }),
    }

    await piAction({ prompt: "review this" }, host)

    expect(connection.recordWorkflowAgentTurn).toHaveBeenCalledTimes(1)
    const reservation = connection.recordWorkflowAgentTurn.mock.calls[0][3] as { inputId: string; turnId: string }
    expect(reservation.inputId).toMatch(/^workflow-input-/)
    expect(reservation.turnId).toMatch(/^workflow-turn-/)
  })

  it("uses the same durable Input/Turn identity when the same work is retried", async () => {
    const pi = runtime()
    const connection = server()
    const definition: AgentExecutionDefinition = {
      agentId: "agent-reviewer",
      instructions: "review",
      runtime: "pi",
      model: "provider/model",
      variant: null,
      skills: [],
    }
    const input = context({ agentDefinition: definition, piRuntime: pi as never, serverConnection: connection as never, projectId: "project" })
    await piAction(input)
    await piAction(input)

    const reservations = connection.recordWorkflowAgentTurn.mock.calls.map((call) => call[3] as { inputId: string; turnId: string })
    expect(reservations[0]).toEqual(reservations[1])
  })

  it("returns a structured unavailable-runtime outcome before creating a Session turn", async () => {
    const connection = server()
    const definition: AgentExecutionDefinition = {
      agentId: "agent-reviewer",
      instructions: "review",
      runtime: "pi",
      model: "provider/model",
      variant: null,
      skills: [],
    }
    const result = await piAction(context({
      agentDefinition: definition,
      piRuntime: null,
      serverConnection: connection as never,
      projectId: "project",
    }))

    expect(result).toMatchObject({
      error: { code: "runtime-unavailable" },
      turnFact: { agentObservation: { agentId: "agent-reviewer", status: "failed", reason: "runtime-unavailable", nextAction: "retry" } },
    })
    expect(connection.openWorkflowAgentSession).not.toHaveBeenCalled()
    expect(connection.recordWorkflowAgentTurn).not.toHaveBeenCalled()
  })

  it("maps an interrupted runtime turn to cancelled with recovery guidance", async () => {
    const pi = runtime()
    pi.runTurn.mockResolvedValueOnce({
      ok: false as const,
      error: { kind: "interrupted", message: "cancelled by workflow" },
      diagnostics: [],
    } as never)
    const connection = server()
    const definition: AgentExecutionDefinition = {
      agentId: "agent-reviewer",
      instructions: "review",
      runtime: "pi",
      model: "provider/model",
      variant: null,
      skills: [],
    }

    const result = await piAction(context({ agentDefinition: definition, piRuntime: pi as never, serverConnection: connection as never, projectId: "project" }))

    expect(result).toMatchObject({
      error: { code: "interrupted" },
      turnFact: { agentObservation: { status: "cancelled", outcome: "cancelled", nextAction: "recover" } },
    })
    expect((connection.calls[1] as { runtimeEvents: Array<{ payload: { status: string } }> }).runtimeEvents.at(-1)?.payload.status).toBe("cancelled")
  })

  it("rolls back the durable reservation when public input is rejected", async () => {
    const pi = runtime()
    const connection = server()
    connection.workflowAgentSessionRuntimeEvents.mockRejectedValueOnce(new Error("input rejected"))
    const definition: AgentExecutionDefinition = {
      agentId: "agent-reviewer",
      instructions: "review",
      runtime: "pi",
      model: "provider/model",
      variant: null,
      skills: [],
    }

    const result = await piAction(context({ agentDefinition: definition, piRuntime: pi as never, serverConnection: connection as never, projectId: "project" }))

    expect(result).toMatchObject({ error: { code: "session-reporting-failed" } })
    expect(connection.abandonWorkflowAgentTurn).toHaveBeenCalledTimes(1)
    expect(pi.runTurn).not.toHaveBeenCalled()
  })
})
