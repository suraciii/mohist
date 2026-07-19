import { afterEach, beforeEach, describe, expect, it } from "vitest"
import { ActionRegistry } from "../src/actions/registry.js"
import { NETWORK_COMMAND_TIMEOUT_MS } from "../src/actions/git.js"
import type { ActionContext, ActionResult, RenderedWorkItem } from "../src/core/types.js"
import { WorkExecutor, baseContext } from "../src/runtime/executor.js"
import { AgentJobExecutor } from "../src/runtime/agent-job-executor.js"
import { setExecutorGitRunnerForTest, type GitRunner } from "../src/runtime/git-probe.js"
import { WorkspaceManager, WorkspaceNetworkTimeoutError } from "../src/runtime/workspace.js"
import type { ServerConnection } from "../src/server/connection.js"
import type { OpenCodeRuntime } from "../src/runtime/opencode/index.js"
import type { RuntimeResult, RuntimeTurnResult } from "../src/runtime/opencode/types.js"
import { createTestTempDir } from "./support/temp-dir.js"

const nonGitRunner: GitRunner = async () => ({
  success: false,
  stdout: "",
  stderr: "not a git repository",
  exitCode: 128,
  combinedOutput: "not a git repository",
})

beforeEach(() => setExecutorGitRunnerForTest(nonGitRunner))
afterEach(() => setExecutorGitRunnerForTest(null))

describe("workspace preparation across stages", () => {
  it("skips workspace preparation for agent jobs", async () => {
    const workspacePath = await createTestTempDir("mohist-agent-job-workspace-")
    const recorded = { prepare: 0 }
    const recordingManager = {
      async prepare() {
        recorded.prepare += 1
        throw new Error("prepare must not be called for agent-job dispatches")
      },
    } as unknown as WorkspaceManager

    const executor = new WorkExecutor(
      buildRegistry(async () => ({ output: "should-not-reach" })),
      recordingManager,
      connection() as never,
      {} as never,
      null,
      "/runner",
      undefined,
      fakeRuntime() as never,
      new AgentJobExecutor(connection() as never, fakeRuntime() as never),
    )

    const result = await executor.execute(
      buildAgentJobWork(workspacePath, "workflow-agent", "agent-job"),
      new AbortController().signal,
    )

    expect(result.status).toBe("completed")
    expect(recorded).toEqual({ prepare: 0 })
  })

  it("serializes a workspace network timeout as a retry-safe failure", async () => {
    const timeout = new WorkspaceNetworkTimeoutError(
      "Workspace preparation network command timed out: git-ls-remote after 120s",
      {
        name: "git-ls-remote",
        command: "ls-remote --heads https://example.test/repository.git master",
        exitCode: 124,
        output: `Command timed out after ${NETWORK_COMMAND_TIMEOUT_MS / 1000}s`,
        status: "timeout",
        timeoutMs: NETWORK_COMMAND_TIMEOUT_MS,
      },
    )
    const failingManager = {
      async prepare() {
        throw timeout
      },
    } as unknown as WorkspaceManager
    const executor = new WorkExecutor(
      buildRegistry(async () => ({ output: "should not run" })),
      failingManager,
      connection() as never,
      {} as never,
      null,
      "/runner",
    )

    const result = await executor.execute(
      buildWork("https://example.test/repository.git", "workflow-timeout", "plan", "plan:write"),
      new AbortController().signal,
    )
    expect(result.status).toBe("failed")
    expect(result.message).toContain("workspace preparation timed out")
  })
})

describe("execution context runtime wiring", () => {
  it("passes the OpenCode runtime to AgentJob contexts", () => {
    const runtime = fakeRuntime()
    const context = baseContext(
      {
        workflowRunId: "",
        workId: "agent-work",
        workType: "task",
        ownerKind: "agent-job",
      },
      {},
      new AbortController().signal,
      {} as never,
      null,
      {} as never,
      null,
      runtime,
    )

    expect(context.openCodeRuntime).toBe(runtime)
  })
})

function connection(): Pick<ServerConnection, "uploadArtifact" | "report"> {
  return {
    async report() {
      return {}
    },
    async uploadArtifact() {
      throw new Error("uploadArtifact should not be called in workspace boundary tests")
    },
  } as unknown as Pick<ServerConnection, "uploadArtifact" | "report">
}

function buildWork(repo: string, workflowRunId: string, stage: string, workId: string): RenderedWorkItem {
  return {
    workflowRunId,
    workId,
    workType: "task",
    stage,
    title: `${stage} task`,
    uses: "core/script",
    with: { run: "echo ok" },
    variables: {
      mohist: { runId: workflowRunId },
      issue: { number: 9 },
      project: { id: "project-1", name: "Mohist Local" },
      repository: { name: "master", gitUrl: repo, baseBranch: "master" },
      openspecChangeDir: "openspec/changes/issue-9",
    },
  }
}

function buildAgentJobWork(suppliedPath: string, workflowRunId: string, agentJobId: string): RenderedWorkItem {
  return {
    workflowRunId,
    workId: "agent:job.1",
    workType: "task",
    stage: "agent-job",
    title: "agent-job dispatch",
    // After #410 T-001, AgentJob dispatches carry a flat
    // `{ prompt, instructions?, model?, variant? }` payload — no
    // `Uses` selector and no `core/script` Action shape.
    with: { prompt: "echo ok" },
    variables: {
      mohist: { runId: workflowRunId },
      workspace: { path: suppliedPath, branch: null, changeDir: null },
      project: { id: "project-1", name: "Mohist Local" },
      repository: { name: "master", gitUrl: "https://example.test/repository.git", baseBranch: "master" },
    },
    ownerKind: "agent-job",
    agentJobId,
  }
}

function buildRegistry(handler: (ctx: ActionContext) => Promise<ActionResult>): ActionRegistry {
  const registry = new ActionRegistry()
  registry.register("core/script", async (ctx) => handler(ctx))
  registry.register("mohist/rebase", async (ctx) => handler(ctx))
  return registry
}

function fakeRuntime(): OpenCodeRuntime {
  const runtime: Partial<OpenCodeRuntime> = {
    ready: () => true,
    diagnostic: () => null,
    async runTurn(_request, _signal): Promise<RuntimeResult<RuntimeTurnResult>> {
      return {
        ok: true,
        value: {
          facts: {
            finalAssistantText: "agent ran",
            runtimeSessionId: "ses_fake",
            workDir: "/runner",
          },
          diagnostics: [],
        },
        diagnostics: [],
      }
    },
  }
  return runtime as OpenCodeRuntime
}
