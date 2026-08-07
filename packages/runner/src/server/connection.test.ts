import { afterEach, describe, expect, it, vi } from "vitest"
import { ServerConnection } from "./connection.js"
import { WorkspaceHomeClaimedError } from "../runtime/workspace-entity.js"

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

describe("ServerConnection named workspace materialization report", () => {
  afterEach(() => vi.restoreAllMocks())

  it("posts the materialized path and parses the recorded home", async () => {
    const fetchSpy = vi.spyOn(globalThis, "fetch").mockResolvedValue(
      new Response(JSON.stringify({ runnerId: "runner-1", path: "/tmp/ws/pay" }), { status: 200, headers: { "content-type": "application/json" } }),
    )

    const report = await new ServerConnection(options).reportWorkspaceMaterialized("project-1", "pay", "/tmp/ws/pay", signal)

    expect(report).toEqual({ runnerId: "runner-1", path: "/tmp/ws/pay" })
    expect(fetchSpy.mock.calls[0]?.[0]).toBe("http://server/api/runner/runner-1/workspaces/project-1/pay/materialized")
    const init = fetchSpy.mock.calls[0]?.[1] as RequestInit | undefined
    expect(init?.method).toBe("POST")
    expect(JSON.parse(String(init?.body))).toEqual({ path: "/tmp/ws/pay" })
  })

  it("throws WorkspaceHomeClaimedError on a 409 workspace_home_claimed answer", async () => {
    vi.spyOn(globalThis, "fetch").mockResolvedValue(
      new Response(JSON.stringify({ ok: false, code: "workspace_home_claimed", error: "already materialized" }), { status: 409, headers: { "content-type": "application/json" } }),
    )

    await expect(
      new ServerConnection(options).reportWorkspaceMaterialized("project-1", "pay", "/tmp/ws/pay", signal),
    ).rejects.toBeInstanceOf(WorkspaceHomeClaimedError)
  })

  it("throws a plain error on other non-2xx answers", async () => {
    vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response("bad", { status: 400 }))
    await expect(
      new ServerConnection(options).reportWorkspaceMaterialized("project-1", "pay", "/tmp/ws/pay", signal),
    ).rejects.toThrow("workspace materialization failed: 400")
  })
})

describe("ServerConnection workspace reclaimability", () => {
  afterEach(() => vi.restoreAllMocks())

  it.each([
    ["active with no bound sessions", { status: "active", activeBoundSessions: 0 }, { status: "active", activeBoundSessions: 0 }],
    ["active with bound sessions", { status: "active", activeBoundSessions: 2 }, { status: "active", activeBoundSessions: 2 }],
    ["archived", { status: "archived", activeBoundSessions: 0 }, { status: "archived", activeBoundSessions: 0 }],
  ] as const)("parses the %s answer", async (_label, payload, expected) => {
    const fetchSpy = vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response(JSON.stringify(payload), { status: 200, headers: { "content-type": "application/json" } }))

    const info = await new ServerConnection(options).getWorkspaceReclaimability("project-1", "pay", signal)

    expect(info).toEqual(expected)
    expect(fetchSpy.mock.calls[0]?.[0]).toBe("http://server/api/runner/runner-1/workspaces/project-1/pay/reclaimable")
    const init = fetchSpy.mock.calls[0]?.[1] as RequestInit | undefined
    expect(init?.method).toBe("GET")
  })

  it("throws on non-2xx", async () => {
    vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response("gone", { status: 404 }))
    await expect(new ServerConnection(options).getWorkspaceReclaimability("project-1", "pay", signal)).rejects.toThrow("workspace reclaimability failed: 404")
  })

  it.each([
    ["an unknown status", JSON.stringify({ status: "suspended", activeBoundSessions: 0 }), "unknown status"],
    ["a malformed count", JSON.stringify({ status: "active", activeBoundSessions: -1 }), "invalid session count"],
    ["malformed JSON", "not-json", "malformed JSON"],
  ])("rejects %s", async (_label, body, message) => {
    vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response(body, { status: 200, headers: { "content-type": "application/json" } }))
    await expect(new ServerConnection(options).getWorkspaceReclaimability("project-1", "pay", signal)).rejects.toThrow(message)
  })
})
