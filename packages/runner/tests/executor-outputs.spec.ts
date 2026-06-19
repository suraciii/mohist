import { mkdtemp, rm } from "node:fs/promises"
import { join } from "node:path"
import { tmpdir } from "node:os"
import { afterEach, beforeEach, describe, expect, it } from "vitest"
import { WorkExecutor } from "../src/runtime/executor.js"
import { ActionRegistry } from "../src/actions/registry.js"
import type { ActionContext, WorkItem, WorkItemResult } from "../src/core/types.js"
import type { ServerConnection, ArtifactUploadResponse } from "../src/server/connection.js"
import { verifyOnlyWorkspaceManager } from "./support/workspace-mock.js"

class FakeServerConnection implements Pick<ServerConnection, "uploadArtifact" | "report"> {
  public uploads: Array<{ path: string; size: number; contentType: string | null; content: Uint8Array }> = []
  public nextUploadId = 0

  async uploadArtifact(
    workflowRunId: string,
    workId: string,
    upload: { path: string; contentType?: string | null; contentHash?: string | null; size: number; content: Uint8Array; filename?: string },
  ): Promise<ArtifactUploadResponse> {
    this.uploads.push({ path: upload.path, size: upload.size, contentType: upload.contentType ?? null, content: upload.content })
    this.nextUploadId += 1
    return {
      uploadId: `artup_${this.nextUploadId}`,
      workflowRunId,
      workId,
      taskRunId: "task-run-1",
      path: upload.path,
      contentType: upload.contentType ?? null,
      contentHash: upload.contentHash ?? null,
      size: upload.size,
      createdAt: new Date().toISOString(),
      expiresAt: new Date(Date.now() + 60_000).toISOString(),
      idempotent: false,
    }
  }

  async report(): Promise<Record<string, unknown>> {
    return {}
  }
}

function makeRegistry(handler: (ctx: ActionContext) => Promise<WorkItemResult>): ActionRegistry {
  const registry = new ActionRegistry()
  registry.register("test/action", async (ctx) => handler(ctx))
  return registry
}

let workDir: string

beforeEach(async () => {
  workDir = await mkdtemp(join(tmpdir(), "mohist-exec-outputs-"))
})

afterEach(async () => {
  await rm(workDir, { recursive: true, force: true })
})

function buildWork(outputs?: WorkItem["outputs"]): WorkItem {
  return {
    workflowRunId: "wf-1",
    workId: "work-1",
    workType: "task",
    title: "Test task",
    uses: "test/action",
    with: {},
    variables: { workspace: { path: workDir, branch: null, changeDir: null } },
    outputs,
  }
}

describe("WorkExecutor output capture", () => {
  it("populates capturedOutputs on successful task with declared outputs", async () => {
    const connection = new FakeServerConnection()
    const executor = new WorkExecutor(
      makeRegistry(async () => ({
        status: "success",
        output: JSON.stringify({ openspecName: "issue-97", changeDir: "openspec/changes/issue-97" }),
      })),
      verifyOnlyWorkspaceManager({ path: workDir, branch: null, changeDir: null }),
      connection as never,
      {} as never,
      null,
      workDir,
    )

    const result = await executor.execute(
      buildWork([
        { name: "openspecName", from: "output.openspecName" },
        { name: "changeDir", from: "output.changeDir" },
      ]),
      new AbortController().signal,
    )

    expect(result.status).toBe("completed")
    expect(result.capturedOutputs).toEqual({
      openspecName: "issue-97",
      changeDir: "openspec/changes/issue-97",
    })
  })

  it("produces no capturedOutputs for failed tasks", async () => {
    const connection = new FakeServerConnection()
    const executor = new WorkExecutor(
      makeRegistry(async () => ({
        status: "failure",
        message: "agent crashed",
        output: JSON.stringify({ openspecName: "issue-97" }),
      })),
      verifyOnlyWorkspaceManager({ path: workDir, branch: null, changeDir: null }),
      connection as never,
      {} as never,
      null,
      workDir,
    )

    const result = await executor.execute(
      buildWork([{ name: "openspecName", from: "output.openspecName" }]),
      new AbortController().signal,
    )

    expect(result.status).toBe("failed")
    expect(result.capturedOutputs).toBeUndefined()
  })

  it("skips missing from fields without failing", async () => {
    const connection = new FakeServerConnection()
    const executor = new WorkExecutor(
      makeRegistry(async () => ({
        status: "success",
        output: JSON.stringify({ openspecName: "issue-97" }),
      })),
      verifyOnlyWorkspaceManager({ path: workDir, branch: null, changeDir: null }),
      connection as never,
      {} as never,
      null,
      workDir,
    )

    const result = await executor.execute(
      buildWork([
        { name: "openspecName", from: "output.openspecName" },
        { name: "missing", from: "output.missing" },
      ]),
      new AbortController().signal,
    )

    expect(result.status).toBe("completed")
    expect(result.capturedOutputs).toEqual({ openspecName: "issue-97" })
  })

  it("omits capturedOutputs when no outputs are declared", async () => {
    const connection = new FakeServerConnection()
    const executor = new WorkExecutor(
      makeRegistry(async () => ({
        status: "success",
        output: JSON.stringify({ openspecName: "issue-97" }),
      })),
      verifyOnlyWorkspaceManager({ path: workDir, branch: null, changeDir: null }),
      connection as never,
      {} as never,
      null,
      workDir,
    )

    const result = await executor.execute(buildWork(undefined), new AbortController().signal)

    expect(result.status).toBe("completed")
    expect(result.capturedOutputs).toBeUndefined()
  })

  it("does not produce capturedOutputs when artifact capture fails", async () => {
    const connection = new FakeServerConnection()
    const executor = new WorkExecutor(
      makeRegistry(async () => ({
        status: "success",
        output: JSON.stringify({ openspecName: "issue-97" }),
      })),
      verifyOnlyWorkspaceManager({ path: workDir, branch: null, changeDir: null }),
      connection as never,
      {} as never,
      null,
      workDir,
    )

    const result = await executor.execute(
      {
        ...buildWork([{ name: "openspecName", from: "output.openspecName" }]),
        artifacts: { files: [{ path: "missing.md" }] },
      },
      new AbortController().signal,
    )

    expect(result.status).toBe("failed")
    expect(result.capturedOutputs).toBeUndefined()
  })
})
