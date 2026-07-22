import { mkdir, mkdtemp, rm, writeFile } from "node:fs/promises"
import { join } from "node:path"
import { tmpdir } from "node:os"
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { WorkExecutor } from "../src/runtime/executor.js"
import { AgentJobExecutor } from "../src/runtime/agent-job-executor.js"
import { setExecutorGitRunnerForTest } from "../src/runtime/git-probe.js"
import type { ActionResult, JsonObject, DispatchWorkItem } from "../src/core/types.js"
import type { ActionHost } from "../src/actions/host.js"
import type { ServerConnection, ArtifactUploadResponse } from "../src/server/connection.js"
import { defineTestActions, type ActionRegistry } from "./support/action-registry-test.js"
import type { CapturedArtifact } from "../src/runtime/artifact-capture.js"
import type { OpenCodeRuntime } from "../src/runtime/opencode/index.js"
import type { RuntimeResult, RuntimeTurnResult } from "../src/runtime/opencode/types.js"
import { verifyOnlyWorkspaceManager } from "./support/workspace-mock.js"

class FakeServerConnection implements Pick<ServerConnection, "uploadArtifact" | "report"> {
  public uploads: Array<{ ownerId: string; ownerKind?: string; workId: string; path: string; size: number; contentType: string | null; content: Uint8Array }> = []
  public uploadFailures = new Map<string, Error>()
  public nextUploadId = 0

  async uploadArtifact(
    ownerId: string,
    workId: string,
    upload: { path: string; contentType?: string | null; contentHash?: string | null; size: number; content: Uint8Array; filename?: string },
    _signal?: AbortSignal,
    ownerKind?: string,
  ): Promise<ArtifactUploadResponse> {
    this.uploads.push({ ownerId, ownerKind, workId, path: upload.path, size: upload.size, contentType: upload.contentType ?? null, content: upload.content })
    const failure = this.uploadFailures.get(upload.path)
    if (failure) throw failure
    this.nextUploadId += 1
    return {
      uploadId: `artup_${this.nextUploadId}`,
      workflowRunId: ownerId,
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

function makeRegistry(handler: (inputs: JsonObject, host: ActionHost) => Promise<ActionResult>, errors: { code: string }[] = []): ActionRegistry {
  return defineTestActions({
    "test/action": {
      run: handler,
      errors,
    },
  })
}

let workDir: string

beforeEach(async () => {
  workDir = await mkdtemp(join(tmpdir(), "mohist-exec-artifacts-"))
  setExecutorGitRunnerForTest(async () => ({
    success: false,
    stdout: "",
    stderr: "fatal: not a git repository",
    exitCode: 128,
    combinedOutput: "fatal: not a git repository",
  }))
})

afterEach(async () => {
  setExecutorGitRunnerForTest(null)
  await rm(workDir, { recursive: true, force: true })
})

function buildWork(artifacts: JsonObject | null, uses = "test/action"): DispatchWorkItem {
  return {
    workflowRunId: "wf-1",
    workId: "work-1",
    workType: "task",
    title: "Test task",
    uses,
    with: {},
    variables: { workspace: { path: workDir, branch: null, changeDir: null } },
    artifacts,
  }
}

describe("WorkExecutor artifact capture", () => {
  it("taskRecoveryPreservesNestedRecoveryOnAddedTasks", async () => {
    const connection = new FakeServerConnection()
    const executor = new WorkExecutor(
      makeRegistry(async () => ({ error: { code: "base-moved", message: "base moved" } }), [{ code: "base-moved" }]),
      verifyOnlyWorkspaceManager({ path: workDir, branch: null, changeDir: null }),
      connection as never,
      workDir,
    )

    const result = await executor.execute({
      ...buildWork(null),
      title: "Merge PR",
      recovery: {
        budget: 2,
        handlers: [
          {
            when: "error.code=base-moved",
            tasks: [
              {
                id: "recover:rebase",
                title: "Rebase after base moved",
                uses: "mohist/rebase",
                with: { baseBranch: "master" },
                recovery: {
                  budget: 1,
                  handlers: [
                    {
                      when: "error.code=conflict",
                      tasks: [
                        {
                          id: "recover:resolve-rebase-conflicts",
                          title: "Resolve rebase conflicts",
                          uses: "mohist/opencode",
                          with: { session: "integrate" },
                        },
                      ],
                      retrySelf: true,
                    },
                  ],
                },
              },
            ],
            retrySelf: true,
          },
        ],
      },
      recoveryRemaining: null,
    }, new AbortController().signal)

    expect(result.status).toBe("completed")
    expect(result.addTasks?.[0]).toMatchObject({
      id: "recover:rebase",
      recovery: {
        budget: 1,
        handlers: [
          {
            when: "error.code=conflict",
            retrySelf: true,
          },
        ],
      },
    })
    expect(result.addTasks?.[1]).toMatchObject({
      id: "work-1",
      recovery: {
        budget: 2,
      },
      recoveryRemaining: 1,
    })
  })

  it("completesTaskAndIncludesUploadIdsWhenAllDeclaredArtifactsExist", async () => {
    await writeFile(join(workDir, "review.md"), "looks good", "utf8")
    await writeFile(join(workDir, "design.md"), "the design", "utf8")
    const connection = new FakeServerConnection()
    const executor = new WorkExecutor(
      makeRegistry(async () => ({ output: { done: true } })),
      verifyOnlyWorkspaceManager({ path: workDir, branch: null, changeDir: null }),
      connection as never,
      workDir,
    )

    const result = await executor.execute(buildWork({ files: [{ path: "review.md" }, { path: "design.md" }] }), new AbortController().signal)

    expect(result.status).toBe("completed")
    expect(result.output).toEqual({ done: true })
    expect(result.artifactUploadIds).toEqual(["artup_1", "artup_2"])
    expect(connection.uploads).toHaveLength(2)
  })

  it("declaredArtifactMissingIsNonFatalWarning", async () => {
    const connection = new FakeServerConnection()
    const executor = new WorkExecutor(
      makeRegistry(async () => ({ output: { done: true } })),
      verifyOnlyWorkspaceManager({ path: workDir, branch: null, changeDir: null }),
      connection as never,
      workDir,
    )

    const result = await executor.execute(buildWork({ files: [{ path: "missing.md" }] }), new AbortController().signal)

    expect(result.status).toBe("completed")
    expect(result.message).toMatch(/artifact capture warnings/)
    expect(result.message).toMatch(/missing\.md/)
    expect(connection.uploads).toEqual([])
  })

  it("declaredArtifactUploadFailureIsNonFatalWarning", async () => {
    await writeFile(join(workDir, "review.md"), "content", "utf8")
    const connection = new FakeServerConnection()
    connection.uploadFailures.set("review.md", new Error("server 503"))
    const executor = new WorkExecutor(
      makeRegistry(async () => ({ output: { done: true } })),
      verifyOnlyWorkspaceManager({ path: workDir, branch: null, changeDir: null }),
      connection as never,
      workDir,
    )

    const result = await executor.execute(buildWork({ files: [{ path: "review.md" }] }), new AbortController().signal)

    expect(result.status).toBe("completed")
    expect(result.message).toMatch(/artifact warnings/)
    expect(result.message).toMatch(/server 503/)
  })

  it("declaredDirectoryExceedsLimitsIsNonFatalWarning", async () => {
    const specs = join(workDir, "specs")
    await mkdir(specs, { recursive: true })
    for (let i = 0; i < 250; i += 1) {
      await writeFile(join(specs, `f${i}.md`), "x".repeat(300), "utf8")
    }
    const connection = new FakeServerConnection()
    const executor = new WorkExecutor(
      makeRegistry(async () => ({ output: null })),
      verifyOnlyWorkspaceManager({ path: workDir, branch: null, changeDir: null }),
      connection as never,
      workDir,
    )

    const result = await executor.execute(buildWork({ files: [{ path: "specs" }] }), new AbortController().signal)

    expect(result.status).toBe("completed")
    expect(result.message).toMatch(/artifact capture warnings/)
  })

  it("capturesDynamicArtifactsFromActionOutput", async () => {
    await writeFile(join(workDir, "review.md"), "review content", "utf8")
    await writeFile(join(workDir, "diagnostic.log"), "log content", "utf8")
    const connection = new FakeServerConnection()
    const executor = new WorkExecutor(
      makeRegistry(async () => ({
        status: "success",
        output: { producedArtifacts: [{ path: "diagnostic.log" }] },
      })),
      verifyOnlyWorkspaceManager({ path: workDir, branch: null, changeDir: null }),
      connection as never,
      workDir,
    )

    const result = await executor.execute(buildWork({ files: [{ path: "review.md" }] }), new AbortController().signal)

    expect(result.status).toBe("completed")
    expect(result.artifactUploadIds).toEqual(["artup_1", "artup_2"])
    const uploadedPaths = connection.uploads.map((u) => u.path).sort()
    expect(uploadedPaths).toEqual(["diagnostic.log", "review.md"])
  })

  it("dynamicArtifactUploadFailureDoesNotFailTask", async () => {
    await writeFile(join(workDir, "review.md"), "review", "utf8")
    await writeFile(join(workDir, "diagnostic.log"), "log", "utf8")
    const connection = new FakeServerConnection()
    connection.uploadFailures.set("diagnostic.log", new Error("server 503"))
    const executor = new WorkExecutor(
      makeRegistry(async () => ({
        status: "success",
        output: { producedArtifacts: [{ path: "diagnostic.log" }] },
      })),
      verifyOnlyWorkspaceManager({ path: workDir, branch: null, changeDir: null }),
      connection as never,
      workDir,
    )

    const result = await executor.execute(buildWork({ files: [{ path: "review.md" }] }), new AbortController().signal)

    expect(result.status).toBe("completed")
    expect(result.artifactUploadIds).toEqual(["artup_1"])
    expect(result.message).toMatch(/artifact warnings/)
  })

  it("actionFailureShortCircuitsArtifactCapture", async () => {
    const connection = new FakeServerConnection()
    const executor = new WorkExecutor(
      makeRegistry(async () => ({ error: { code: "action-failed", message: "agent crashed" } })),
      verifyOnlyWorkspaceManager({ path: workDir, branch: null, changeDir: null }),
      connection as never,
      workDir,
    )

    const result = await executor.execute(buildWork({ files: [{ path: "review.md" }] }), new AbortController().signal)

    expect(result.status).toBe("failed")
    expect(result.message).toBe("agent crashed")
    expect(connection.uploads).toEqual([])
    expect(result.artifactUploadIds).toBeUndefined()
  })

  it("skipsArtifactCaptureWhenNoDeclaredOrDynamicArtifacts", async () => {
    const connection = new FakeServerConnection()
    const executor = new WorkExecutor(
      makeRegistry(async () => ({ output: { ok: true } })),
      verifyOnlyWorkspaceManager({ path: workDir, branch: null, changeDir: null }),
      connection as never,
      workDir,
    )

    const result = await executor.execute(buildWork(null), new AbortController().signal)

    expect(result.status).toBe("completed")
    expect(connection.uploads).toEqual([])
    expect(result.artifactUploadIds).toBeUndefined()
  })

  it("rendersTemplateVariablesInDeclaredArtifactPathsBeforeCapture", async () => {
    // The default workflow declares every artifact `path` as a
    // `${{ openspecChangeDir }}`-prefixed template. The runner must
    // substitute that variable (against `work.variables`) so the
    // capture layer reads from the resolved workspace-relative
    // path, not a literal `${{ openspecChangeDir }}` directory.
    const changeDir = "openspec/changes/issue-55"
    const reviewPath = `${changeDir}/review.md`
    const reviewAbsolute = join(workDir, changeDir, "review.md")
    await mkdir(join(workDir, changeDir), { recursive: true })
    await writeFile(reviewAbsolute, "looks good", "utf8")

    const work = buildWork({ files: [{ path: "${{ openspecChangeDir }}/review.md" }] })
    work.variables = { ...(work.variables ?? {}), openspecChangeDir: changeDir }

    const connection = new FakeServerConnection()
    const executor = new WorkExecutor(
      makeRegistry(async () => ({ output: { done: true } })),
      verifyOnlyWorkspaceManager({ path: workDir, branch: null, changeDir: null }),
      connection as never,
      workDir,
    )

    const result = await executor.execute(work, new AbortController().signal)

    expect(result.status).toBe("completed")
    expect(result.artifactUploadIds).toEqual(["artup_1"])
    // The upload must report the rendered path, not the raw
    // `${{ openspecChangeDir }}/review.md` literal.
    expect(connection.uploads).toHaveLength(1)
    expect(connection.uploads[0].path).toBe(reviewPath)
  })

  it("rendersTemplateVariablesInDeclaredDirectoryArtifactPathsBeforeCapture", async () => {
    // Same template-substitution contract for a directory artifact:
    // the runner resolves `${{ openspecChangeDir }}` before the
    // capture layer walks the directory.
    const changeDir = "openspec/changes/issue-55"
    const specsPath = `${changeDir}/specs`
    const specsAbsolute = join(workDir, changeDir, "specs")
    await mkdir(join(specsAbsolute, "sub"), { recursive: true })
    await writeFile(join(specsAbsolute, "a.md"), "alpha", "utf8")
    await writeFile(join(specsAbsolute, "sub", "b.md"), "beta", "utf8")

    const work = buildWork({ files: [{ path: "${{ openspecChangeDir }}/specs" }] })
    work.variables = { ...(work.variables ?? {}), openspecChangeDir: changeDir }

    const connection = new FakeServerConnection()
    const executor = new WorkExecutor(
      makeRegistry(async () => ({ output: null })),
      verifyOnlyWorkspaceManager({ path: workDir, branch: null, changeDir: null }),
      connection as never,
      workDir,
    )

    const result = await executor.execute(work, new AbortController().signal)

    expect(result.status).toBe("completed")
    expect(connection.uploads).toHaveLength(1)
    expect(connection.uploads[0].path).toBe(specsPath)
    expect(connection.uploads[0].contentType).toBe("application/x-mohist-artifact-directory")
  })

  it("capturesDeclaredArtifactsFromWorkspaceRootWhenActionUsesSubdirectoryWorkingDirectory", async () => {
    await mkdir(join(workDir, "subdir"), { recursive: true })
    await writeFile(join(workDir, "review.md"), "workspace review", "utf8")

    const work = buildWork({ files: [{ path: "review.md" }] })
    work.with = { "working-directory": "subdir" }

    let actionWorkDir = ""
    const connection = new FakeServerConnection()
    const executor = new WorkExecutor(
      makeRegistry(async (_inputs, host) => {
        actionWorkDir = host.workDir
        return { output: null }
      }),
      verifyOnlyWorkspaceManager({ path: workDir, branch: null, changeDir: null }),
      connection as never,
      workDir,
    )

    const result = await executor.execute(work, new AbortController().signal)

    expect(result.status).toBe("completed")
    expect(actionWorkDir).toBe(join(workDir, "subdir"))
    expect(connection.uploads).toHaveLength(1)
    expect(connection.uploads[0].path).toBe("review.md")
  })

  it("failsTaskWhenWorkingDirectoryEscapesWorkspace", async () => {
    const work = buildWork(null)
    work.with = { "working-directory": "../outside" }

    const connection = new FakeServerConnection()
    const executor = new WorkExecutor(
      makeRegistry(async () => ({ output: null })),
      verifyOnlyWorkspaceManager({ path: workDir, branch: null, changeDir: null }),
      connection as never,
      workDir,
    )

    const result = await executor.execute(work, new AbortController().signal)

    expect(result.status).toBe("failed")
    expect(result.message).toMatch(/escapes workspace\.path/)
  })

  it("failsTaskThroughNormalFailureWhenDeclaredArtifactTemplateVariableIsMissing", async () => {
    // Whole-string unresolvable reference in a declared artifact
    // path: the runner should surface a clean error rather than
    // attempt to capture from a literal `${{ ... }}` directory.
    const work = buildWork({ files: [{ path: "${{ openspecChangeDir }}/review.md" }] })

    const connection = new FakeServerConnection()
    const executor = new WorkExecutor(
      makeRegistry(async () => ({ output: { ok: true } })),
      verifyOnlyWorkspaceManager({ path: workDir, branch: null, changeDir: null }),
      connection as never,
      workDir,
    )

    const result = await executor.execute(work, new AbortController().signal)

    expect(result.status).toBe("failed")
    expect(result.message).toMatch(/artifact declaration references undefined variable/)
    expect(result.message).toMatch(/openspecChangeDir/)
    expect(connection.uploads).toEqual([])
    expect(result.artifactUploadIds).toBeUndefined()
  })

  it("AgentJob dispatches drive the AgentJobExecutor and never reach the action registry", async () => {
    // After #410 T-001, an AgentJob dispatch is routed to the
    // AgentJobExecutor (which drives `OpenCodeRuntime.runTurn`
    // directly) — it never reaches the action registry. Stand up a
    // fake runtime that returns a `completed` turn so the executor's
    // result-report flow runs against the AgentJob owner identity.
    const connection = new FakeServerConnection()
    const fakeRuntime = makeFakeRuntimeReturningCompleted()
    let registryInvoked = false
    const executor = new WorkExecutor(
      makeRegistry(async () => {
        registryInvoked = true
        return { output: { reached: false } }
      }),
      verifyOnlyWorkspaceManager({ path: workDir, branch: null, changeDir: null }),
      connection as never,
      workDir,
      undefined,
      fakeRuntime as never,
      new AgentJobExecutor(connection as never, fakeRuntime as never),
    )

    const work = buildWork({ files: [{ path: "review.md" }] })
    work.workflowRunId = ""
    work.ownerKind = "agent-job"
    work.agentJobId = "agent-job-1"
    work.with = { prompt: "review this" }

    const result = await executor.execute(work, new AbortController().signal)

    expect(result.status).toBe("completed")
    expect(registryInvoked).toBe(false)
    // The AgentJob path does not upload declared artifacts through
    // the work-executor artifact pipeline — the AgentJobExecutor
    // owns the runtime turn and reports the result directly. The
    // legacy test (uploadsArtifactsForAgentJobWorkUsingAgentJobOwner)
    // existed because the ACP Action ran the executor's artifact
    // tail; the new path short-circuits before that.
    expect(connection.uploads).toHaveLength(0)
  })
})

function makeFakeRuntimeReturningCompleted(): OpenCodeRuntime {
  const runtime: Partial<OpenCodeRuntime> = {
    ready: () => true,
    diagnostic: () => null,
    async runTurn(_request, _signal): Promise<RuntimeResult<RuntimeTurnResult>> {
      return {
        ok: true,
        value: {
          facts: {
            finalAssistantText: "agent done",
            runtimeSessionId: "ses_fake",
            workDir: workDir,
          },
          diagnostics: [],
        },
        diagnostics: [],
      }
    },
  }
  return runtime as OpenCodeRuntime
}
