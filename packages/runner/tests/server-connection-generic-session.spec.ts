import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { ServerConnection } from "../src/server/connection.js"

const originalFetch = globalThis.fetch
let fetchMock: ReturnType<typeof vi.fn>

beforeEach(() => {
  fetchMock = vi.fn()
  globalThis.fetch = fetchMock as unknown as typeof fetch
})

afterEach(() => {
  globalThis.fetch = originalFetch
  vi.restoreAllMocks()
})

function mockResponse({ status, contentType = "application/json", body = "{}" }: { status: number; contentType?: string; body?: string | Buffer }): Response {
  return new Response(typeof body === "string" ? body : new Uint8Array(body), { status, headers: { "content-type": contentType } })
}

function options() {
  return { serverUrl: "http://localhost:3456", runnerId: "runner-1", runnerRoot: "/tmp", pollIntervalMs: 100, heartbeatIntervalMs: 60_000, dispatchLivenessProbeIntervalMs: 60_000 }
}

describe("ServerConnection.getAgentSession (generic)", () => {
  it("GetAgentSession_HitsGenericSessionUrl", async () => {
    fetchMock.mockResolvedValueOnce(mockResponse({ status: 200, body: JSON.stringify({ runtimeSessionId: "acp-1", runtime: "opencode", workDir: "D:/work" }) }))
    const connection = new ServerConnection(options())

    const result = await connection.getAgentSession("project-1", "session-abc", new AbortController().signal)

    expect(result).toEqual({ runtimeSessionId: "acp-1", runtime: "opencode", workDir: "D:/work" })
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(url).toMatch(/\/api\/runner\/runner-1\/agent-sessions\/project-1\/session-abc$/)
    expect(init.method).toBe("GET")
    expect(url).not.toMatch(/\/api\/runner\/runner-1\/sessions\/project-1\//)
  })

  it("GetAgentSession_ReturnsNull_On404", async () => {
    fetchMock.mockResolvedValueOnce(mockResponse({ status: 404, body: "not found" }))
    const connection = new ServerConnection(options())

    const result = await connection.getAgentSession("project-1", "missing-session", new AbortController().signal)

    expect(result).toBeNull()
  })

  it("GetAgentSession_ThrowsOnOtherErrors", async () => {
    fetchMock.mockResolvedValueOnce(mockResponse({ status: 500, body: "boom" }))
    const connection = new ServerConnection(options())

    await expect(connection.getAgentSession("project-1", "session-1", new AbortController().signal))
      .rejects.toThrow(/agent session lookup failed/)
  })
})

describe("ServerConnection.openAgentSession (generic)", () => {
  it("OpenAgentSession_PostsToGenericOpenUrl_AndReturnsSession", async () => {
    fetchMock.mockResolvedValueOnce(mockResponse({ status: 200, body: JSON.stringify({ runtimeSessionId: "acp-new", runtime: "opencode", workDir: "D:/work", model: "openai/gpt-4.1" }) }))
    const connection = new ServerConnection(options())

    const result = await connection.openAgentSession("project-1", "session-abc", { workId: "work-1", workType: "agent-job" }, new AbortController().signal)

    expect(result).toEqual({ runtimeSessionId: "acp-new", runtime: "opencode", workDir: "D:/work", model: "openai/gpt-4.1" })
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(url).toMatch(/\/api\/runner\/runner-1\/agent-sessions\/project-1\/session-abc\/open$/)
    expect(init.method).toBe("POST")
    expect(JSON.parse(init.body as string)).toEqual({ workId: "work-1", workType: "agent-job" })
    expect(url).not.toMatch(/\/api\/runner\/runner-1\/sessions\/project-1\//)
  })

  it("OpenAgentSession_ThrowsOnServerError", async () => {
    fetchMock.mockResolvedValueOnce(mockResponse({ status: 500, body: "boom" }))
    const connection = new ServerConnection(options())

    await expect(connection.openAgentSession("project-1", "session-abc", {}, new AbortController().signal))
      .rejects.toThrow(/agent session open failed/)
  })
})

describe("ServerConnection.attachAgentSession (generic)", () => {
  it("AttachAgentSession_PostsToGenericAttachUrl", async () => {
    fetchMock.mockResolvedValueOnce(mockResponse({ status: 200, body: "" }))
    const connection = new ServerConnection(options())

    await connection.attachAgentSession("project-1", "session-abc", { agentSessionId: "acp-1", workDir: "D:/work" }, new AbortController().signal)

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(url).toMatch(/\/api\/runner\/runner-1\/agent-sessions\/project-1\/session-abc\/attach$/)
    expect(init.method).toBe("POST")
    expect(JSON.parse(init.body as string)).toEqual({ agentSessionId: "acp-1", workDir: "D:/work" })
    expect(url).not.toMatch(/\/api\/runner\/runner-1\/sessions\/project-1\//)
  })
})

describe("ServerConnection.agentSessionRuntimeEvents (generic)", () => {
  it("AgentSessionRuntimeEvents_PostsToGenericRuntimeEventsUrl", async () => {
    fetchMock.mockResolvedValueOnce(mockResponse({ status: 200, body: "" }))
    const connection = new ServerConnection(options())

    await connection.agentSessionRuntimeEvents("project-1", "session-abc", { workId: "work-1", workType: "agent-job", stage: "agent", runtimeEvents: [{ type: "session.input", payload: { text: "hi" } }] }, new AbortController().signal)

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(url).toMatch(/\/api\/runner\/runner-1\/agent-sessions\/project-1\/session-abc\/runtime-events$/)
    expect(init.method).toBe("POST")
    const body = JSON.parse(init.body as string)
    expect(body.runtimeEvents).toEqual([{ type: "session.input", payload: { text: "hi" } }])
    expect(url).not.toMatch(/\/api\/runner\/runner-1\/sessions\/project-1\//)
  })
})

describe("ServerConnection generic vs workflow URL segregation", () => {
  it("GenericUrls_AreDistinctFrom_WorkflowUrls", async () => {
    fetchMock.mockResolvedValueOnce(mockResponse({ status: 200, body: "{}" }))
    await new ServerConnection(options()).getAgentSession("project-1", "session-abc", new AbortController().signal)
    const genericGet = (fetchMock.mock.calls[0] as [string, RequestInit])[0]

    fetchMock.mockResolvedValueOnce(mockResponse({ status: 200, body: "{}" }))
    await new ServerConnection(options()).getWorkflowAgentSession("project-1", "wf-1", "build", new AbortController().signal)
    const workflowGet = (fetchMock.mock.calls[1] as [string, RequestInit])[0]

    expect(genericGet).toMatch(/\/api\/runner\/runner-1\/agent-sessions\/project-1\/session-abc$/)
    expect(workflowGet).toMatch(/\/api\/runner\/runner-1\/sessions\/project-1\/wf-1\/build$/)
    expect(genericGet).not.toBe(workflowGet)
  })

  it("GenericOpenUrl_ContainsSlashOpen_WorkflowOpenUrl_AlsoContainsSlashOpen", async () => {
    fetchMock.mockResolvedValueOnce(mockResponse({ status: 200, body: "{}" }))
    await new ServerConnection(options()).openAgentSession("project-1", "session-abc", {}, new AbortController().signal)
    const genericOpen = (fetchMock.mock.calls[0] as [string, RequestInit])[0]

    fetchMock.mockResolvedValueOnce(mockResponse({ status: 200, body: "{}" }))
    await new ServerConnection(options()).openWorkflowAgentSession("project-1", "wf-1", "build", {}, new AbortController().signal)
    const workflowOpen = (fetchMock.mock.calls[1] as [string, RequestInit])[0]

    expect(genericOpen).toMatch(/\/api\/runner\/runner-1\/agent-sessions\/project-1\/session-abc\/open$/)
    expect(workflowOpen).toMatch(/\/api\/runner\/runner-1\/sessions\/project-1\/wf-1\/build\/open$/)
    expect(genericOpen).not.toBe(workflowOpen)
  })

  it("GenericAttachUrl_ContainsAgentSessionsPrefix", async () => {
    fetchMock.mockResolvedValueOnce(mockResponse({ status: 200, body: "" }))
    await new ServerConnection(options()).attachAgentSession("project-1", "session-abc", {}, new AbortController().signal)
    const genericAttach = (fetchMock.mock.calls[0] as [string, RequestInit])[0]

    expect(genericAttach).toMatch(/\/api\/runner\/runner-1\/agent-sessions\/project-1\/session-abc\/attach$/)
  })

  it("GenericRuntimeEventsUrl_ContainsAgentSessionsPrefix", async () => {
    fetchMock.mockResolvedValueOnce(mockResponse({ status: 200, body: "" }))
    await new ServerConnection(options()).agentSessionRuntimeEvents("project-1", "session-abc", { runtimeEvents: [] }, new AbortController().signal)
    const genericRuntime = (fetchMock.mock.calls[0] as [string, RequestInit])[0]

    expect(genericRuntime).toMatch(/\/api\/runner\/runner-1\/agent-sessions\/project-1\/session-abc\/runtime-events$/)
  })
})
