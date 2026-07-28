import { describe, expect, it, vi } from "vitest"
import { piAction, PI_TURN_DURATION_MS } from "./pi.js"
import type { AgentExecutionDefinition, JsonObject, ParentIssueContext } from "../core/types.js"
import type { ServerConnection } from "../server/connection.js"
import type { PiRuntime } from "../runtime/pi/index.js"

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
    openWorkflowAgentSession: vi.fn(async () => ({ runtime: "pi", runtimeSessionId: null, workDir: "/workspace" })),
    attachWorkflowAgentSession: vi.fn(async () => ({ runtime: "pi", runtimeSessionId: "/workspace/.pi/session.json", workDir: "/workspace" })),
    workflowAgentSessionRuntimeEvents: vi.fn(async (_project: string, _run: string, _name: string, body: unknown) => { calls.push(body); return [{ id: "accepted" }] }),
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
})
