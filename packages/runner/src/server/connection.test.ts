import { afterEach, describe, expect, it, vi } from "vitest"
import { ServerConnection } from "./connection.js"

const options = {
  serverUrl: "http://server",
  runnerId: "runner-1",
  runnerRoot: "/tmp/runner",
  pollIntervalMs: 1,
  heartbeatIntervalMs: 1,
  dispatchLivenessProbeIntervalMs: 1,
}

const signal = new AbortController().signal

describe("ServerConnection AgentSession reconciliation", () => {
  afterEach(() => vi.restoreAllMocks())

  it("reads the runner-scoped binding list", async () => {
    const fetchSpy = vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response(JSON.stringify([{
      sessionId: "session-1",
      runtime: "opencode",
      runtimeSessionId: "runtime-1",
      workDir: "/work",
    }]), { status: 200, headers: { "content-type": "application/json" } }))

    const bindings = await new ServerConnection(options).listAgentSessionsForReconcile(signal)

    expect(bindings).toEqual([{
      sessionId: "session-1",
      runtime: "opencode",
      runtimeSessionId: "runtime-1",
      workDir: "/work",
    }])
    expect(fetchSpy.mock.calls[0]?.[0]).toBe("http://server/api/runner/runner-1/agent-sessions/reconcile")
  })

  it.each([
    {},
    [{ sessionId: "session-1", runtime: "unknown", runtimeSessionId: "runtime-1", workDir: "/work" }],
    [{ sessionId: "session-1", runtime: "opencode", runtimeSessionId: "", workDir: "/work" }],
  ])("rejects corrupt reconcile responses", async (payload) => {
    vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response(JSON.stringify(payload), { status: 200 }))

    await expect(new ServerConnection(options).listAgentSessionsForReconcile(signal)).rejects.toThrow("malformed")
  })
})

describe("ServerConnection workflow runtime events", () => {
  afterEach(() => vi.restoreAllMocks())

  it("returns accepted entries when every submitted fact is accepted", async () => {
    vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response(JSON.stringify([
      { id: "1", type: "session.input" },
      { id: "2", type: "message.delta" },
    ]), { status: 200, headers: { "content-type": "application/json" } }))

    const accepted = await new ServerConnection(options).workflowAgentSessionRuntimeEvents(
      "project", "run", "session", { runtimeSessionId: "runtime", runtimeEvents: [{ type: "session.input" }, { type: "message.delta" }] }, signal)

    expect(accepted).toHaveLength(2)
  })

  it("surfaces malformed and count-mismatched acceptance responses", async () => {
    vi.spyOn(globalThis, "fetch").mockResolvedValueOnce(new Response("not-json", { status: 200 }))
    await expect(new ServerConnection(options).workflowAgentSessionRuntimeEvents(
      "project", "run", "session", { runtimeEvents: [{ type: "session.input" }] }, signal)).rejects.toThrow("malformed JSON")

    vi.spyOn(globalThis, "fetch").mockResolvedValueOnce(new Response("[]", { status: 200 }))
    await expect(new ServerConnection(options).workflowAgentSessionRuntimeEvents(
      "project", "run", "session", { runtimeEvents: [{ type: "session.input" }] }, signal)).rejects.toThrow("acceptance mismatch")
  })
})

describe("ServerConnection agent-input attachments", () => {
  afterEach(() => vi.restoreAllMocks())

  it("fetches bytes only through the owning input scoped route", async () => {
    const fetchSpy = vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response("content", {
      status: 200,
      headers: {
        "content-type": "text/plain",
        "content-disposition": "attachment; filename=notes.txt",
      },
    }))

    const content = await new ServerConnection(options).openAgentInputAttachment(
      "project/1",
      "session/1",
      "input/1",
      "attachment/1",
      signal,
    )

    expect(new TextDecoder().decode(content?.bytes)).toBe("content")
    expect(fetchSpy.mock.calls[0]?.[0]).toBe(
      "http://server/api/projects/project%2F1/agent-sessions/session%2F1/inputs/input%2F1/attachments/attachment%2F1/content",
    )
    expect(JSON.stringify(fetchSpy.mock.calls[0]?.[1])).not.toContain("temp")
    expect(JSON.stringify(fetchSpy.mock.calls[0]?.[1])).not.toContain("token")
  })
})
