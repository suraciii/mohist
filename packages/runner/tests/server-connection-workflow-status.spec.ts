import { describe, expect, it as vitestIt } from "vitest"
import { ServerConnection } from "../src/server/connection.js"
import { transportFetch, withFakeTransport } from "./support/fake-transport.js"

const fetchMock = transportFetch
const it = (name: string, body: () => unknown) => vitestIt(name, () => withFakeTransport(async () => await body()))

// Integration coverage for the ServerConnection.workflowRunsStatus method.
// The endpoint exists at
// POST /api/runner/{runnerId}/workflow-runs/status and returns
// { statuses: { [runId]: status } }. The runner only sends
// workflowRunIds for registry entries still in phase `active`, and the
// server drops unknown ids from the response (omits the key).

describe("ServerConnection.workflowRunsStatus", () => {
  function makeConnection() {
    return new ServerConnection({
      serverUrl: "https://runner.test",
      runnerId: "runner-test",
      projectId: "project-1",
      runnerRoot: "/virtual/runner-test",
      pollIntervalMs: 1000,
      heartbeatIntervalMs: 15_000,
      dispatchLivenessProbeIntervalMs: 10_000,
    })
  }

  it("SendsPostToWorkflowRunsStatus_WithExactRunIdsPayload", async () => {
    const calls: Array<{ url: string; method: string; body: unknown }> = []
    fetchMock.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === "string" ? input : input.toString()
      const method = init?.method ?? "GET"
      const body = init?.body ? JSON.parse(init.body as string) : null
      calls.push({ url, method, body })
      return new Response(JSON.stringify({ statuses: {} }), { status: 200, headers: { "content-type": "application/json" } })
    })

    await makeConnection().workflowRunsStatus(["wr-1", "wr-2"], new AbortController().signal)

    expect(calls).toHaveLength(1)
    expect(calls[0].url).toBe("https://runner.test/api/runner/runner-test/workflow-runs/status")
    expect(calls[0].method).toBe("POST")
    expect(calls[0].body).toEqual({ workflowRunIds: ["wr-1", "wr-2"] })
  })

  it("ReturnsEmptyObject_OnEmptyInput_DoesNotCallServer", async () => {
    const result = await makeConnection().workflowRunsStatus([], new AbortController().signal)
    expect(result).toEqual({})
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it("ReturnsStatusMap_OnServerResponse", async () => {
    fetchMock.mockImplementation(async () => new Response(JSON.stringify({
      statuses: { "wr-1": "Completed", "wr-2": "Running" },
    }), { status: 200, headers: { "content-type": "application/json" } }))

    const result = await makeConnection().workflowRunsStatus(["wr-1", "wr-2"], new AbortController().signal)
    expect(result).toEqual({ "wr-1": "Completed", "wr-2": "Running" })
  })

  it("ReturnsEmptyMap_WhenServerOmitsStatuses", async () => {
    fetchMock.mockImplementation(async () => new Response(JSON.stringify({}), { status: 200, headers: { "content-type": "application/json" } }))

    const result = await makeConnection().workflowRunsStatus(["wr-1"], new AbortController().signal)
    expect(result).toEqual({})
  })

  it("Throws_OnNonOkResponse", async () => {
    fetchMock.mockImplementation(async () => new Response("boom", { status: 500 }))

    await expect(makeConnection().workflowRunsStatus(["wr-1"], new AbortController().signal)).rejects.toThrow(/workflowRunsStatus failed: 500/)
  })

  it("ForwardsAbortSignal", async () => {
    let observedSignal: AbortSignal | undefined
    fetchMock.mockImplementation(async (_input: RequestInfo | URL, init?: RequestInit) => {
      observedSignal = init?.signal ?? undefined
      return new Response(JSON.stringify({ statuses: {} }), { status: 200 })
    })

    const controller = new AbortController()
    await makeConnection().workflowRunsStatus(["wr-1"], controller.signal)

    expect(observedSignal).toBe(controller.signal)
  })
})
