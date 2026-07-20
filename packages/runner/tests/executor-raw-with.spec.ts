import { mkdtemp, rm } from "node:fs/promises"
import { join } from "node:path"
import { tmpdir } from "node:os"
import { afterEach, beforeEach, describe, expect, it } from "vitest"
import { ActionRegistry } from "../src/actions/registry.js"
import type { ActionContext, JsonObject, RenderedWorkItem } from "../src/core/types.js"
import { WorkExecutor } from "../src/runtime/executor.js"
import { setExecutorGitRunnerForTest, type GitRunner } from "../src/runtime/git-probe.js"
import { verifyOnlyWorkspaceManager } from "./support/workspace-mock.js"

let workDir: string

const nonGitRunner: GitRunner = async () => ({
  success: false,
  stdout: "",
  stderr: "not a git repository",
  exitCode: 128,
  combinedOutput: "not a git repository",
})

beforeEach(async () => {
  workDir = await mkdtemp(join(tmpdir(), "mohist-executor-raw-with-"))
  setExecutorGitRunnerForTest(nonGitRunner)
})

afterEach(async () => {
  setExecutorGitRunnerForTest(null)
  await rm(workDir, { recursive: true, force: true })
})

describe("WorkExecutor rawWith", () => {
  it("exposes rawWith as server-expanded form and with as recursively-rendered form", async () => {
    let capturedContext: ActionContext | null = null

    const registry = new ActionRegistry()
    registry.register("test/capture-context", async (ctx) => {
      capturedContext = ctx
      return { output: null }
    })

    const executor = new WorkExecutor(
      registry,
      verifyOnlyWorkspaceManager({ path: workDir, branch: null, changeDir: null }),
      {} as never,
      workDir,
    )

    const agentObject = { type: "opencode", model: "openai/gpt-5.4" }
    const placeholder = "${{ vars.agent }}"

    const workItem: RenderedWorkItem = {
      workflowRunId: "wf-raw-with",
      workId: "work-raw-with",
      workType: "task",
      stage: "build",
      title: "Test rawWith",
      uses: "test/capture-context",
      with: { task: { with: { options: placeholder } } },
      variables: {
        workspace: { path: workDir, branch: null, changeDir: null },
        vars: { agent: agentObject },
      },
    }

    const result = await executor.execute(workItem, new AbortController().signal)

    expect(result.status).toBe("completed")
    expect(capturedContext).not.toBeNull()

    const rawWith = capturedContext!.rawWith as JsonObject
    const renderedWith = capturedContext!.with as JsonObject

    expect((rawWith.task as JsonObject).with).toEqual({ options: placeholder })
    expect((renderedWith.task as JsonObject).with).toEqual({ options: agentObject })
  })

  it("propagates parent issue context only through ActionContext", async () => {
    let capturedContext: ActionContext | null = null
    const registry = new ActionRegistry()
    registry.register("test/capture-parent-context", async (ctx) => {
      capturedContext = ctx
      return { status: "success" }
    })
    const executor = new WorkExecutor(
      registry,
      verifyOnlyWorkspaceManager({ path: workDir, branch: null, changeDir: null }),
      {} as never,
      {} as never,
      null,
      workDir,
    )
    const workItem: RenderedWorkItem = {
      workflowRunId: "wf-parent-context",
      workId: "work-parent-context",
      workType: "task",
      stage: "plan",
      uses: "test/capture-parent-context",
      with: { prompt: "child prompt" },
      variables: { workspace: { path: workDir, branch: null, changeDir: null } },
      parentIssueContext: { title: "Parent", body: "Parent body" },
    }

    const result = await executor.execute(workItem, new AbortController().signal)

    expect(result.status).toBe("completed")
    expect(capturedContext!.parentIssueContext).toEqual({ title: "Parent", body: "Parent body" })
    expect(capturedContext!.with).toEqual({ prompt: "child prompt" })
    expect(capturedContext!.variables).not.toHaveProperty("parentIssueContext")
  })
})
