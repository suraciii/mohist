import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { ServerConnection } from "../src/server/connection.js"

interface MockResponseInit {
  status: number
  contentType?: string
  body?: string | Buffer
}

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

function mockResponse({ status, contentType = "application/json", body = "{}" }: MockResponseInit): Response {
  return new Response(typeof body === "string" ? body : new Uint8Array(body), { status, headers: { "content-type": contentType } })
}

function options() {
  return { serverUrl: "http://localhost:3456", runnerId: "runner-1", runnerRoot: "/tmp", pollIntervalMs: 100, heartbeatIntervalMs: 60_000, dispatchLivenessProbeIntervalMs: 60_000 }
}

describe("ServerConnection.report", () => {
  it("forwardsCleanupAttemptsToServerWhenResultIncludesThem", async () => {
    fetchMock.mockResolvedValueOnce(mockResponse({ status: 200, body: "{}" }))
    const connection = new ServerConnection(options())
    const work = { workflowRunId: "wf-1", workId: "work-1", workType: "task" }
    await connection.report(work, { status: "failed", message: "dirty", output: "{}", cleanupAttempts: 3 }, new AbortController().signal)
    expect(fetchMock).toHaveBeenCalledTimes(1)
    const init = fetchMock.mock.calls[0][1] as RequestInit
    const body = JSON.parse(init.body as string)
    expect(body.cleanupAttempts).toBe(3)
  })

  it("sendsNullCleanupAttemptsWhenResultOmitsThem", async () => {
    fetchMock.mockResolvedValueOnce(mockResponse({ status: 200, body: "{}" }))
    const connection = new ServerConnection(options())
    const work = { workflowRunId: "wf-1", workId: "work-1", workType: "task" }
    await connection.report(work, { status: "completed", message: "ok", output: "{}" }, new AbortController().signal)
    expect(fetchMock).toHaveBeenCalledTimes(1)
    const init = fetchMock.mock.calls[0][1] as RequestInit
    const body = JSON.parse(init.body as string)
    expect(body.cleanupAttempts).toBeNull()
  })

  it("forwardsRecoveryRemainingOnReportedFollowUps", async () => {
    fetchMock.mockResolvedValueOnce(mockResponse({ status: 200, body: "{}" }))
    const connection = new ServerConnection(options())
    const work = { workflowRunId: "wf-1", workId: "work-1", workType: "task" }
    await connection.report(work, {
      status: "completed",
      output: "{}",
      addTasks: [{
        id: "work-1",
        title: "Work",
        recovery: { budget: 2, handlers: [] },
        recoveryRemaining: 1,
      }],
    }, new AbortController().signal)

    const init = fetchMock.mock.calls[0][1] as RequestInit
    const body = JSON.parse(init.body as string)
    expect(body.addTasks[0].recoveryRemaining).toBe(1)
  })
})

describe("ServerConnection.poll recovery state", () => {
  it("preserves explicit null and numeric state while keeping an absent state absent", async () => {
    fetchMock.mockResolvedValueOnce(mockResponse({
      status: 200,
      body: JSON.stringify({
        dispatches: [
          { workflowRunId: "wf-1", workId: "work-1", workType: "task", recoveryRemaining: null },
          { workflowRunId: "wf-1", workId: "work-2", workType: "task", recoveryRemaining: 1 },
          { workflowRunId: "wf-1", workId: "work-3", workType: "task" },
        ],
      }),
    }))

    const connection = new ServerConnection(options())
    const works = await connection.poll(new AbortController().signal)

    expect(works[0]?.recoveryRemaining).toBeNull()
    expect(Object.prototype.hasOwnProperty.call(works[0], "recoveryRemaining")).toBe(true)
    expect(works[1]?.recoveryRemaining).toBe(1)
    expect(Object.prototype.hasOwnProperty.call(works[1], "recoveryRemaining")).toBe(true)
    expect(Object.prototype.hasOwnProperty.call(works[2], "recoveryRemaining")).toBe(false)
  })
})

describe("ServerConnection.patchRunVars", () => {
  it("patchesWorkflowRunProfileVariablesWithVariableBundleShape", async () => {
    fetchMock.mockResolvedValueOnce(mockResponse({ status: 200, body: "{}" }))
    const connection = new ServerConnection(options())

    await connection.patchRunVars(
      "wf-1",
      { github: { pr: { number: 249 } } },
      new AbortController().signal,
    )

    expect(fetchMock).toHaveBeenCalledTimes(1)
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(url).toBe("http://localhost:3456/api/workflow-runs/wf-1/workflow-profile/variables")
    expect(init.method).toBe("PATCH")
    expect(JSON.parse(init.body as string)).toEqual({
      vars: {
        github: {
          pr: {
            number: 249,
          },
        },
      },
    })
  })
})
