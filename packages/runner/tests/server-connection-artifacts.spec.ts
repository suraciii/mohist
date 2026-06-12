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
  return { serverUrl: "http://localhost:3456", runnerId: "runner-1", runnerRoot: "/tmp", maxConcurrentWorkflows: 1, pollIntervalMs: 100, heartbeatIntervalMs: 60_000 }
}

describe("ServerConnection.uploadArtifact", () => {
  it("sendsMultipartFormDataAndParsesUploadIdFromResponse", async () => {
    const responseBody = JSON.stringify({
      data: {
        uploadId: "artup_abc",
        workflowRunId: "wf-1",
        workId: "work-1",
        taskRunId: "task-1.1",
        path: "review.md",
        contentType: "text/markdown",
        contentHash: "sha256:deadbeef",
        size: 5,
        createdAt: "2026-06-11T00:00:00Z",
        expiresAt: "2026-06-11T00:30:00Z",
        idempotent: false,
      },
    })
    fetchMock.mockResolvedValueOnce(mockResponse({ status: 200, body: responseBody }))
    const connection = new ServerConnection(options())

    const result = await connection.uploadArtifact(
      "wf-1",
      "work-1",
      {
        path: "review.md",
        contentType: "text/markdown",
        contentHash: "sha256:deadbeef",
        size: 5,
        content: new TextEncoder().encode("hello"),
      },
      new AbortController().signal,
    )

    expect(result.uploadId).toBe("artup_abc")
    expect(result.taskRunId).toBe("task-1.1")
    expect(result.idempotent).toBe(false)

    expect(fetchMock).toHaveBeenCalledTimes(1)
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(url).toContain("/api/workflow-runs/wf-1/work/work-1/artifact-uploads")
    expect(init.method).toBe("POST")
    expect(init.body).toBeInstanceOf(FormData)
    const form = init.body as FormData
    expect(form.get("path")).toBe("review.md")
    expect(form.get("contentType")).toBe("text/markdown")
    expect(form.get("contentHash")).toBe("sha256:deadbeef")
    expect(form.get("size")).toBe("5")
    const file = form.get("content")
    expect(file).toBeInstanceOf(Blob)
    expect((file as Blob).type).toBe("text/markdown")
  })

  it("throwsStructuredErrorOnConflict", async () => {
    const responseBody = JSON.stringify({
      code: "artifact_upload_conflict",
      data: { message: "Conflicting upload" },
      details: { existingUploadId: "artup_first", existingContentHash: "sha256:aaa", incomingContentHash: "sha256:bbb" },
    })
    fetchMock.mockResolvedValueOnce(mockResponse({ status: 409, body: responseBody }))
    const connection = new ServerConnection(options())

    await expect(
      connection.uploadArtifact("wf-1", "work-1", { path: "review.md", contentHash: "sha256:bbb", size: 1, content: new Uint8Array([0x01]) }, new AbortController().signal),
    ).rejects.toMatchObject({
      code: "artifact_upload_conflict",
      uploadId: "artup_first",
      status: 409,
    })
  })

  it("throwsWithStatusOnServerError", async () => {
    fetchMock.mockResolvedValueOnce(mockResponse({ status: 500, body: "internal error" }))
    const connection = new ServerConnection(options())

    await expect(
      connection.uploadArtifact("wf-1", "work-1", { path: "review.md", size: 1, content: new Uint8Array([0x01]) }, new AbortController().signal),
    ).rejects.toMatchObject({ status: 500 })
  })
})

describe("ServerConnection.report", () => {
  it("includesArtifactUploadIdsInRequestBody", async () => {
    fetchMock.mockResolvedValueOnce(mockResponse({ status: 200, body: JSON.stringify({ workflowRunId: "wf-1" }) }))
    const connection = new ServerConnection(options())

    const result = await connection.report(
      {
        workflowRunId: "wf-1",
        workId: "work-1",
        workType: "task",
      },
      { status: "completed", artifactUploadIds: ["artup_a", "artup_b"] },
      new AbortController().signal,
    )

    expect(result).toMatchObject({ workflowRunId: "wf-1" })
    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(init.method).toBe("POST")
    expect(JSON.parse(init.body as string)).toEqual(expect.objectContaining({
      workflowRunId: "wf-1",
      workId: "work-1",
      status: "completed",
      artifactUploadIds: ["artup_a", "artup_b"],
    }))
  })

  it("omitsArtifactUploadIdsWhenAbsent", async () => {
    fetchMock.mockResolvedValueOnce(mockResponse({ status: 200, body: JSON.stringify({}) }))
    const connection = new ServerConnection(options())

    await connection.report(
      { workflowRunId: "wf-1", workId: "work-1", workType: "task" },
      { status: "failed", message: "boom" },
      new AbortController().signal,
    )

    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    const body = JSON.parse(init.body as string)
    expect(body.artifactUploadIds).toBeNull()
  })
})
