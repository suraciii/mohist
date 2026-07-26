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

  it("usesAgentJobArtifactEndpointWhenOwnerKindIsAgentJob", async () => {
    fetchMock.mockResolvedValueOnce(mockResponse({
      status: 200,
      body: JSON.stringify({ data: { uploadId: "artup_agent", workflowRunId: "agent-job-1", workId: "agent-work-1", path: "review.md", size: 5 } }),
    }))
    const connection = new ServerConnection(options())

    const result = await connection.uploadArtifact(
      "agent-job-1",
      "agent-work-1",
      { path: "review.md", size: 5, content: new TextEncoder().encode("hello") },
      new AbortController().signal,
      "agent-job",
    )

    expect(result.uploadId).toBe("artup_agent")
    const [url] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(url).toContain("/api/agent-jobs/agent-job-1/work/agent-work-1/artifact-uploads")
    expect(url).not.toContain("/api/workflow-runs//")
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

  it("sendsOwnerKindAndAgentJobIdForAgentJobWork", async () => {
    fetchMock.mockResolvedValueOnce(mockResponse({ status: 200, body: JSON.stringify({}) }))
    const connection = new ServerConnection(options())

    await connection.report(
      {
        workflowRunId: "",
        workId: "agent-work-1",
        workType: "agent-job",
        ownerKind: "agent-job",
        agentJobId: "agent-job-abc",
      },
      { status: "completed" },
      new AbortController().signal,
    )

    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    const body = JSON.parse(init.body as string)
    expect(body.ownerKind).toBe("agent-job")
    expect(body.agentJobId).toBe("agent-job-abc")
    expect(body.workflowRunId).toBeUndefined()
  })

  it("normalizesAgentJobOwnerKindBeforeReporting", async () => {
    fetchMock.mockResolvedValueOnce(mockResponse({ status: 200, body: JSON.stringify({}) }))
    const connection = new ServerConnection(options())

    await connection.report(
      {
        workflowRunId: "",
        workId: "agent-work-uppercase",
        workType: "agent-job",
        ownerKind: "AGENT-JOB",
        agentJobId: "agent-job-uppercase",
      },
      { status: "completed" },
      new AbortController().signal,
    )

    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    const body = JSON.parse(init.body as string)
    expect(body.ownerKind).toBe("agent-job")
    expect(body.agentJobId).toBe("agent-job-uppercase")
    expect(body.workflowRunId).toBeUndefined()
  })

  it("sendsWorkflowRunIdForWorkflowWork", async () => {
    fetchMock.mockResolvedValueOnce(mockResponse({ status: 200, body: JSON.stringify({}) }))
    const connection = new ServerConnection(options())

    await connection.report(
      {
        workflowRunId: "wf-1",
        workId: "work-1",
        workType: "task",
        ownerKind: "workflow",
      },
      { status: "completed" },
      new AbortController().signal,
    )

    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    const body = JSON.parse(init.body as string)
    expect(body.ownerKind).toBe("workflow")
    expect(body.workflowRunId).toBe("wf-1")
  })
})

describe("ServerConnection.poll", () => {
  it("preservesOwnerKindAndAgentJobIdFromDispatchResponse", async () => {
    fetchMock.mockResolvedValueOnce(
      mockResponse({
        status: 200,
        body: JSON.stringify({ dispatches: [{
          workflowRunId: "",
          workId: "agent-work-1",
          workType: "agent-job",
          uses: "mohist/opencode",
          with: "{\"prompt\":\"hi\"}",
          variables: "{\"workspace\":{\"path\":\"/tmp/agent\"}}",
          stage: "agent",
          title: "Agent Job",
          ownerKind: "agent-job",
          agentJobId: "agent-job-abc",
        }] }),
      }),
    )
    const connection = new ServerConnection(options())

    const item = (await connection.poll(new AbortController().signal))[0]

    expect(item).not.toBeNull()
    expect(item!.ownerKind).toBe("agent-job")
    expect(item!.agentJobId).toBe("agent-job-abc")
    expect(item!.workId).toBe("agent-work-1")
    expect(item!.workflowRunId).toBe("")
  })

  it("preservesProjectIdAndAgentSessionIdFromDispatchResponse_ForAgentJob", async () => {
    fetchMock.mockResolvedValueOnce(
      mockResponse({
        status: 200,
        body: JSON.stringify({ dispatches: [{
          workflowRunId: "",
          workId: "agent-work-2",
          workType: "agent-job",
          uses: "mohist/opencode",
          with: "{\"prompt\":{\"agent-launch\":{\"instructions\":\"be brief\",\"prompt\":\"hi\"}}}",
          variables: "{\"workspace\":{\"path\":\"/tmp/agent-2\"}}",
          stage: "agent",
          title: "Agent Job",
          ownerKind: "agent-job",
          agentJobId: "agent-job-xyz",
          projectId: "project-launch",
          agentSessionId: "session-abc",
        }] }),
      }),
    )
    const connection = new ServerConnection(options())

    const item = (await connection.poll(new AbortController().signal))[0]

    expect(item).not.toBeNull()
    expect(item!.ownerKind).toBe("agent-job")
    expect(item!.projectId).toBe("project-launch")
    expect(item!.agentSessionId).toBe("session-abc")
    expect(item!.agentJobId).toBe("agent-job-xyz")
  })

  it("preservesProjectIdFromIssue_ForWorkflowDispatch", async () => {
    fetchMock.mockResolvedValueOnce(
      mockResponse({
        status: 200,
        body: JSON.stringify({ dispatches: [{
          workflowRunId: "wf-1",
          workId: "work-1",
          workType: "task",
          uses: "mohist/opencode",
          with: "{\"prompt\":\"hi\"}",
          variables: "{}",
          stage: "build",
          title: "Workflow task",
          ownerKind: "workflow",
          projectId: "project-wf",
          issueNumber: 7,
        }] }),
      }),
    )
    const connection = new ServerConnection(options())

    const item = (await connection.poll(new AbortController().signal))[0]

    expect(item).not.toBeNull()
    expect(item!.ownerKind).toBe("workflow")
    expect(item!.projectId).toBe("project-wf")
    expect(item!.issueNumber).toBe(7)
    expect(item!.workflowRunId).toBe("wf-1")
    expect(item!.agentSessionId).toBeUndefined()
  })

  it("leavesAgentSessionIdUndefined_WhenDispatchOmitsField", async () => {
    fetchMock.mockResolvedValueOnce(
      mockResponse({
        status: 200,
        body: JSON.stringify({ dispatches: [{
          workflowRunId: "",
          workId: "agent-work-3",
          workType: "agent-job",
          uses: "mohist/opencode",
          with: "{\"prompt\":\"raw\"}",
          variables: "{\"workspace\":{\"path\":\"/tmp/agent-3\"}}",
          stage: "agent",
          title: "Agent Job",
          ownerKind: "agent-job",
          agentJobId: "agent-job-no-session",
          projectId: "project-raw",
        }] }),
      }),
    )
    const connection = new ServerConnection(options())

    const item = (await connection.poll(new AbortController().signal))[0]

    expect(item).not.toBeNull()
    expect(item!.ownerKind).toBe("agent-job")
    expect(item!.projectId).toBe("project-raw")
    expect(item!.agentSessionId).toBeUndefined()
  })
})

describe("ServerConnection.buildGitHash", () => {
  it("registerRequestIncludesBuildGitHash", async () => {
    fetchMock.mockResolvedValueOnce(mockResponse({ status: 200, body: "" }))
    const hash = "abcdef1234567890abcdef1234567890abcdef12"
    const connection = new ServerConnection(options(), hash)

    await connection.connect(
      {
        capabilities: ["spec/*"],
        actionCatalog: { actions: [], tombstones: [] },
        projectId: "project-1",
        coderModels: ["openai/gpt-4"],
        buildGitHash: hash,
      },
      new AbortController().signal,
    )

    expect(fetchMock).toHaveBeenCalledTimes(1)
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(url).toContain("/api/runner/runner-1/register")
    expect(init.method).toBe("POST")
    const body = JSON.parse(init.body as string)
    expect(body.buildGitHash).toBe(hash)
  })

  it("heartbeatIncludesBuildGitHashAndState", async () => {
    fetchMock.mockResolvedValueOnce(mockResponse({ status: 200, body: "" }))
    const hash = "abcdef1234567890abcdef1234567890abcdef12"
    const connection = new ServerConnection(options(), hash)

    await connection.heartbeat(
      { capabilities: ["spec/*"], actionCatalog: { actions: [], tombstones: [] }, projectId: "project-1", coderModels: ["openai/gpt-4"] },
      new AbortController().signal,
    )

    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    const body = JSON.parse(init.body as string)
    expect(body.buildGitHash).toBe(hash)
    expect(body.coderModels).toEqual(["openai/gpt-4"])
  })

  it("heartbeatStillSendsStateWhenBuildGitHashUnknown", async () => {
    fetchMock.mockResolvedValueOnce(mockResponse({ status: 200, body: "" }))
    const connection = new ServerConnection(options(), null)

    await connection.heartbeat(
      { capabilities: ["spec/*"], actionCatalog: { actions: [], tombstones: [] }, projectId: "project-1" },
      new AbortController().signal,
    )

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(url).toContain("/api/runner/runner-1/heartbeat")
    const body = JSON.parse(init.body as string)
    expect(body.buildGitHash).toBeNull()
  })
})
