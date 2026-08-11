import { describe, expect, it as vitestIt } from "vitest"
import { ServerConnection } from "../src/server/connection.js"
import { transportFetch, withFakeTransport } from "./support/fake-transport.js"

const fetchMock = transportFetch
const it = (name: string, body: () => unknown) => vitestIt(name, () => withFakeTransport(async () => await body()))

// Coverage for the dedicated runner config channel
// `GET /api/runner/{runnerId}/config`. The
// runner fetches the config on every cleanup-loop tick instead of
// reading a cached value from the work dispatch envelope.
//
// The contract:
//   - Plain GET to `/api/runner/{runnerId}/config` — no body, no
//     ETag / If-None-Match / version negotiation.
//   - 2xx response is parsed; the unwrapped `cleanupPolicy` field is
//     returned (null when absent).
//   - Non-2xx / network errors throw so the cleanup loop's existing
//     try/catch can swallow them per design D4 (best-effort, no
//     stale-policy fallback).
//   - The caller's AbortSignal is forwarded verbatim — matches the
//     existing poll / report / workflowRunsStatus helpers.

describe("ServerConnection.fetchConfig", () => {
  function makeConnection(runnerId = "runner-test") {
    return new ServerConnection({
      serverUrl: "https://runner.test",
      runnerId,
      projectId: "project-1",
      runnerRoot: "/virtual/runner-test",
      pollIntervalMs: 1000,
      heartbeatIntervalMs: 15_000,
      dispatchLivenessProbeIntervalMs: 10_000,
    })
  }

  it("SendsPlainGetToRunnerConfigEndpoint_NoRequestBody", async () => {
    const calls: Array<{ url: string; method: string; body: unknown; headers: Record<string, string> }> = []
    fetchMock.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === "string" ? input : input.toString()
      const method = init?.method ?? "GET"
      const body = init?.body ? JSON.parse(init.body as string) : null
      const headers: Record<string, string> = {}
      if (init?.headers) {
        for (const [k, v] of Object.entries(init.headers as Record<string, string>)) {
          headers[k.toLowerCase()] = v
        }
      }
      calls.push({ url, method, body, headers })
      return new Response(JSON.stringify({ cleanupPolicy: null }), { status: 200, headers: { "content-type": "application/json" } })
    })

    await makeConnection("runner-cfg-1").fetchConfig(new AbortController().signal)

    expect(calls).toHaveLength(1)
    expect(calls[0].url).toBe("https://runner.test/api/runner/runner-cfg-1/config")
    expect(calls[0].method).toBe("GET")
    expect(calls[0].body).toBeNull()
    // No conditional-fetch headers leaked into the request.
    expect(calls[0].headers).not.toHaveProperty("if-none-match")
    expect(calls[0].headers).not.toHaveProperty("if-modified-since")
  })

  it("ReturnsUnwrappedCleanupPolicy_FromResponseBody", async () => {
    fetchMock.mockImplementation(async () => new Response(JSON.stringify({
      cleanupPolicy: { retentionDays: 14, storageBudgetBytes: 1_073_741_824, storageTargetWatermarkBytes: 536_870_912 },
    }), { status: 200, headers: { "content-type": "application/json" } }))

    const result = await makeConnection().fetchConfig(new AbortController().signal)

    expect(result).toEqual({
      retentionDays: 14,
      storageBudgetBytes: 1_073_741_824,
      storageTargetWatermarkBytes: 536_870_912,
    })
  })

  it("ReturnsNull_WhenCleanupPolicyFieldIsAbsent", async () => {
    fetchMock.mockImplementation(async () => new Response(JSON.stringify({}), { status: 200, headers: { "content-type": "application/json" } }))

    const result = await makeConnection().fetchConfig(new AbortController().signal)

    expect(result).toBeNull()
  })

  it("ReturnsNull_WhenCleanupPolicyIsExplicitNull", async () => {
    fetchMock.mockImplementation(async () => new Response(JSON.stringify({ cleanupPolicy: null }), { status: 200, headers: { "content-type": "application/json" } }))

    const result = await makeConnection().fetchConfig(new AbortController().signal)

    expect(result).toBeNull()
  })

  it("ReturnsPolicyWithAllNullSentinels_WhenServerReturnsFullyUnconfigured", async () => {
    fetchMock.mockImplementation(async () => new Response(JSON.stringify({
      cleanupPolicy: { retentionDays: null, storageBudgetBytes: null, storageTargetWatermarkBytes: null },
    }), { status: 200, headers: { "content-type": "application/json" } }))

    const result = await makeConnection().fetchConfig(new AbortController().signal)

    expect(result).toEqual({
      retentionDays: null,
      storageBudgetBytes: null,
      storageTargetWatermarkBytes: null,
    })
  })

  it("Throws_OnNonOkResponse_BestEffortCallerHandles", async () => {
    fetchMock.mockImplementation(async () => new Response("not found", { status: 404 }))

    // Per design D4: fetchConfig throws on non-2xx / network error;
    // the caller's existing try/catch in runCleanupOnce logs and
    // skips this tick. The contract is "throw", not "swallow".
    await expect(makeConnection().fetchConfig(new AbortController().signal)).rejects.toThrow(/fetchConfig failed: 404/)
  })

  it("Throws_OnServerError500", async () => {
    fetchMock.mockImplementation(async () => new Response("oops", { status: 500 }))

    await expect(makeConnection().fetchConfig(new AbortController().signal)).rejects.toThrow(/fetchConfig failed: 500/)
  })

  it("Throws_OnNetworkError", async () => {
    fetchMock.mockImplementation(async () => {
      throw new Error("ECONNREFUSED")
    })

    await expect(makeConnection().fetchConfig(new AbortController().signal)).rejects.toThrow(/ECONNREFUSED/)
  })

  it("ForwardsAbortSignal_ToFetch", async () => {
    let observedSignal: AbortSignal | undefined
    fetchMock.mockImplementation(async (_input: RequestInfo | URL, init?: RequestInit) => {
      observedSignal = init?.signal ?? undefined
      return new Response(JSON.stringify({ cleanupPolicy: null }), { status: 200 })
    })

    const controller = new AbortController()
    await makeConnection().fetchConfig(controller.signal)

    expect(observedSignal).toBe(controller.signal)
  })
})
