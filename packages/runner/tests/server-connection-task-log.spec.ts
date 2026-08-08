import { describe, expect, it as vitestIt } from "vitest"
import { ServerConnection } from "../src/server/connection.js"
import type { TaskLogBatch } from "../src/runtime/task-log.js"
import { transportFetch, withFakeTransport } from "./support/fake-transport.js"

const fetchMock = transportFetch
const it = (name: string, body: () => unknown) => vitestIt(name, () => withFakeTransport(async () => await body()))

function mockResponse({ status, body = "{}" }: { status: number; body?: string }): Response {
  return new Response(body, { status, headers: { "content-type": "application/json" } })
}

function options() {
  return {
    serverUrl: "https://runner.test",
    runnerId: "runner-1",
    runnerRoot: "/virtual/runner",
    pollIntervalMs: 100,
    heartbeatIntervalMs: 60_000,
    dispatchLivenessProbeIntervalMs: 60_000,
  }
}

function sampleBatch(): TaskLogBatch {
  return {
    truncated: false,
    entries: [
      { seq: 1, timestamp: new Date("2026-07-01T00:00:00.000Z"), source: "workspace-prep", text: "Cloning" },
      { seq: 2, timestamp: new Date("2026-07-01T00:00:01.000Z"), source: "branch-check", text: "Stable" },
    ],
  }
}

describe("ServerConnection.uploadTaskLog", () => {
  it("PostsJsonBodyToWorkflowRunTaskLogEndpoint", async () => {
    fetchMock.mockResolvedValueOnce(mockResponse({ status: 200, body: JSON.stringify({ data: { accepted: 2, truncated: false } }) }))
    const connection = new ServerConnection(options())

    const result = await connection.uploadTaskLog("wf-1", "work-1", sampleBatch(), new AbortController().signal)

    expect(result.accepted).toBe(2)
    expect(result.truncated).toBe(false)

    expect(fetchMock).toHaveBeenCalledTimes(1)
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(url).toContain("/api/workflow-runs/wf-1/work/work-1/task-log")
    expect(init.method).toBe("POST")
    expect(new Headers(init.headers).get("content-type")).toBe("application/json")

    const body = JSON.parse(init.body as string)
    expect(body.truncated).toBe(false)
    expect(body.entries).toHaveLength(2)
    expect(body.entries[0]).toEqual({
      seq: 1,
      timestamp: "2026-07-01T00:00:00.000Z",
      source: "workspace-prep",
      text: "Cloning",
    })
  })

  it("RoutesToAgentJobTaskLogEndpointWhenOwnerKindIsAgentJob", async () => {
    fetchMock.mockResolvedValueOnce(mockResponse({ status: 200, body: JSON.stringify({ data: { accepted: 1, truncated: true } }) }))
    const connection = new ServerConnection(options())

    const result = await connection.uploadTaskLog(
      "aj-1",
      "work-1",
      { truncated: true, entries: [{ seq: 1, timestamp: new Date("2026-07-01T00:00:00.000Z"), source: "action", text: "x" }] },
      new AbortController().signal,
      "agent-job",
    )

    expect(result.accepted).toBe(1)
    expect(result.truncated).toBe(true)
    const [url] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(url).toContain("/api/agent-jobs/aj-1/work/work-1/task-log")
    expect(url).not.toContain("/api/workflow-runs/")
  })

  it("DefaultsToWorkflowOwnerKindWhenNotSpecified", async () => {
    fetchMock.mockResolvedValueOnce(mockResponse({ status: 200, body: JSON.stringify({ data: { accepted: 0, truncated: false } }) }))
    const connection = new ServerConnection(options())

    await connection.uploadTaskLog("wf-1", "work-1", { truncated: false, entries: [] }, new AbortController().signal)

    const [url] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(url).toContain("/api/workflow-runs/wf-1/work/work-1/task-log")
  })

  it("EncodesOwnerIdAndWorkIdInUrl", async () => {
    fetchMock.mockResolvedValueOnce(mockResponse({ status: 200, body: JSON.stringify({ data: { accepted: 0 } }) }))
    const connection = new ServerConnection(options())

    await connection.uploadTaskLog("wf with space", "work/slash", { truncated: false, entries: [] }, new AbortController().signal)

    const [url] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(url).toContain("/api/workflow-runs/wf%20with%20space/work/work%2Fslash/task-log")
  })

  it("SerializesTimestampAsIso8601", async () => {
    fetchMock.mockResolvedValueOnce(mockResponse({ status: 200, body: JSON.stringify({ data: { accepted: 1 } }) }))
    const connection = new ServerConnection(options())

    await connection.uploadTaskLog(
      "wf-1",
      "work-1",
      {
        truncated: false,
        entries: [
          { seq: 1, timestamp: new Date("2026-07-01T05:30:45.123Z"), source: "action", text: "x" },
        ],
      },
      new AbortController().signal,
    )

    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    const body = JSON.parse(init.body as string)
    expect(body.entries[0].timestamp).toBe("2026-07-01T05:30:45.123Z")
  })

  it("ThrowsStructuredErrorOnNonOkResponse", async () => {
    fetchMock.mockResolvedValueOnce(
      mockResponse({ status: 400, body: JSON.stringify({ code: "bad_request", error: "Too many entries" }) }),
    )
    const connection = new ServerConnection(options())

    await expect(
      connection.uploadTaskLog("wf-1", "work-1", sampleBatch(), new AbortController().signal),
    ).rejects.toMatchObject({
      status: 400,
      code: "bad_request",
    })
  })

  it("ThrowsGenericErrorWhenResponseBodyIsEmpty", async () => {
    fetchMock.mockResolvedValueOnce(mockResponse({ status: 500, body: "" }))
    const connection = new ServerConnection(options())

    await expect(
      connection.uploadTaskLog("wf-1", "work-1", sampleBatch(), new AbortController().signal),
    ).rejects.toMatchObject({ status: 500 })
  })

  it("CarriesTruncatedFlagThroughToRequestBody", async () => {
    fetchMock.mockResolvedValueOnce(mockResponse({ status: 200, body: JSON.stringify({ data: { accepted: 1, truncated: true } }) }))
    const connection = new ServerConnection(options())

    await connection.uploadTaskLog(
      "wf-1",
      "work-1",
      { truncated: true, entries: [{ seq: 3, timestamp: new Date("2026-07-01T00:00:00.000Z"), source: "action", text: "tail" }] },
      new AbortController().signal,
    )

    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    const body = JSON.parse(init.body as string)
    expect(body.truncated).toBe(true)
  })

  it("EmptyEntriesArraySucceeds", async () => {
    fetchMock.mockResolvedValueOnce(mockResponse({ status: 200, body: JSON.stringify({ data: { accepted: 0, truncated: false } }) }))
    const connection = new ServerConnection(options())

    const result = await connection.uploadTaskLog("wf-1", "work-1", { truncated: false, entries: [] }, new AbortController().signal)
    expect(result.accepted).toBe(0)
  })

  it("PropagatesAbortSignal", async () => {
    fetchMock.mockImplementationOnce((_url: string, init: RequestInit) => {
      return new Promise((_resolve, reject) => {
        init.signal?.addEventListener("abort", () => reject(new Error("aborted")))
      })
    })
    const connection = new ServerConnection(options())
    const controller = new AbortController()

    const promise = connection.uploadTaskLog("wf-1", "work-1", sampleBatch(), controller.signal)
    controller.abort()
    await expect(promise).rejects.toThrow(/aborted/i)
  })
})
