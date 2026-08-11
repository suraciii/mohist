import { describe, expect, it as vitestIt } from "vitest"
import { NETWORK_COMMAND_TIMEOUT_MS } from "../src/actions/git.js"
import type { ActionResult, JsonObject, DispatchWorkItem } from "../src/core/types.js"
import type { ActionHost } from "../src/actions/host.js"
import { WorkExecutor } from "../src/runtime/executor.js"
import { AgentJobExecutor } from "../src/runtime/agent-job-executor.js"
import { WorkspaceManager, WorkspaceNetworkTimeoutError } from "../src/runtime/workspace.js"
import type { ServerConnection } from "../src/server/connection.js"
import type { OpenCodeRuntime } from "../src/runtime/opencode/index.js"
import type { RuntimeResult, RuntimeTurnResult } from "../src/runtime/opencode/types.js"
import { createTestTempDir } from "./support/temp-dir.js"
import { defineTestActions, type ActionRegistry } from "./support/action-registry-test.js"
import { verifyOnlyWorkspaceManager } from "./support/workspace-mock.js"
import { withTestRunnerResources } from "./support/test-resources.js"

const it = Object.assign(
  (name: string, body: () => unknown) => vitestIt(name, () => withTestRunnerResources(async () => await body())),
  { each: vitestIt.each.bind(vitestIt) },
) as typeof vitestIt

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
      buildRegistry(async () => ({ output: { reached: false } })),
      recordingManager,
      connection() as never,
      "/runner",
      undefined,
      fakeRuntime() as never,
      new AgentJobExecutor(connection() as never, { openCode: fakeRuntime() as never, pi: null }),
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
      buildRegistry(async () => ({ output: { reached: false } })),
      failingManager,
      connection() as never,
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

describe("execution host boundary", () => {
  it("does not expose the runtime through an Action host", async () => {
    let observed: Record<string, unknown> | null = null
    const registry = buildRegistry(async (_inputs, host) => {
      observed = host as unknown as Record<string, unknown>
      return { output: null }
    })
    const definition = registry.resolve("core/script")
    if (definition.kind !== "definition") throw new Error("test action missing")
    await definition.definition.run({ run: "echo ok" }, {
      workDir: "/tmp/agent-host",
      signal: new AbortController().signal,
      log: null,
      exec: async () => ({ exitCode: 0, stdout: "", stderr: "" }),
    })
    expect(observed).not.toBeNull()
    expect(observed).not.toHaveProperty("openCodeRuntime")
    expect(observed).not.toHaveProperty("serverConnection")
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

function buildWork(repo: string, workflowRunId: string, stage: string, workId: string): DispatchWorkItem {
  return {
    workflowRunId,
    workId,
    workType: "task",
    stage,
    title: `${stage} task`,
    uses: "core/script",
    with: { run: "echo ok" },
    variables: {
      workflow: { runId: workflowRunId },
      issue: { number: 9, projectId: "project-1" },
      repository: { name: "master", gitUrl: repo, baseBranch: "master" },
    },
  }
}

function buildAgentJobWork(suppliedPath: string, workflowRunId: string, agentJobId: string): DispatchWorkItem {
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

function buildRegistry(handler: (inputs: JsonObject, host: ActionHost) => Promise<ActionResult>): ActionRegistry {
  return defineTestActions({
    "core/script": handler,
    "mohist/rebase": handler,
  })
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
