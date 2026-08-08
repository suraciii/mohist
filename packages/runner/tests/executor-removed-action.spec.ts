import { describe, expect, it as vitestIt } from "vitest"
import { WorkExecutor } from "../src/runtime/executor.js"
import type { GitRunner } from "../src/runtime/git-probe.js"
import type { DispatchWorkItem } from "../src/core/types.js"
import type { ServerConnection } from "../src/server/connection.js"
import { verifyOnlyWorkspaceManager } from "./support/workspace-mock.js"
import { ActionRegistry } from "../src/actions/registry.js"
import { ACP_AGENT_TOMBSTONE } from "../src/actions/built-ins.js"
import { withTestRunnerResources } from "./support/test-resources.js"

const nonGitRunner: GitRunner = async () => ({
  success: false,
  exitCode: 128,
  stdout: "",
  stderr: "not a git repository",
  combinedOutput: "not a git repository",
})

const withExecutorResources = <T>(body: (workDir: string) => Promise<T>) =>
  withTestRunnerResources(async () => await body("/virtual/executor-removed-action"), { gitRunner: nonGitRunner })

function silentConnection(): ServerConnection {
  return {
    async report() {
      return {}
    },
    async uploadArtifact() {
      throw new Error("uploadArtifact should not be called in removed-action tests")
    },
  } as unknown as ServerConnection
}

function executorFor(registry: ActionRegistry, workDir: string): WorkExecutor {
  return new WorkExecutor(
    registry,
     verifyOnlyWorkspaceManager({ path: workDir, branch: null }),
    silentConnection(),
    workDir,
  )
}

function buildWork(workDir: string, overrides: Partial<DispatchWorkItem>): DispatchWorkItem {
  return {
    workflowRunId: "wf-removed-action",
    workId: "review.1",
    workType: "task",
    stage: "check",
    title: "Removed Action test",
    uses: "mohist/acp-agent",
    with: {},
     variables: { workspace: { path: workDir, branch: null } },
    ...overrides,
  }
}

describe("WorkExecutor removed-action rejection", () => {
  const it = (name: string, body: (workDir: string) => Promise<void>) => vitestIt(name, () => withExecutorResources(body))

  it("returns an actionable error when uses is the removed 'mohist/acp-agent' Action", async (workDir) => {
    const registry = new ActionRegistry([], [ACP_AGENT_TOMBSTONE])
    const executor = executorFor(registry, workDir)
    const result = await executor.execute(buildWork(workDir, {}), new AbortController().signal)
    expect(result.status).toBe("failed")
    expect(result.message).toContain("removed Action")
    expect(result.message).toContain("mohist/acp-agent")
    expect(result.message).toContain("mohist/opencode")
    expect(result.message).toMatch(/rerun/i)
  })

  it("returns an actionable error when a custom Action is also recognized as removed (case-insensitive)", async (workDir) => {
    const registry = new ActionRegistry([], [ACP_AGENT_TOMBSTONE])
    const executor = executorFor(registry, workDir)
    const result = await executor.execute(buildWork(workDir, { uses: "MOHIST/ACP-AGENT" }), new AbortController().signal)
    expect(result.status).toBe("failed")
    expect(result.message).toContain("MOHIST/ACP-AGENT")
    expect(result.message).toMatch(/rerun/i)
  })

  it("falls back to the generic 'No action found' miss for unknown Actions that are not removed", async (workDir) => {
    const registry = new ActionRegistry([], [])
    const executor = executorFor(registry, workDir)
    const result = await executor.execute(buildWork(workDir, { uses: "unknown/action" }), new AbortController().signal)
    expect(result.status).toBe("failed")
    expect(result.message).toBe("No action found for 'unknown/action'")
  })
})
