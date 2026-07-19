import { mkdtemp, rm } from "node:fs/promises"
import { tmpdir } from "node:os"
import { join } from "node:path"
import { afterEach, beforeEach, describe, expect, it } from "vitest"
import { ActionRegistry } from "../src/actions/registry.js"
import { WorkExecutor } from "../src/runtime/executor.js"
import { setExecutorGitRunnerForTest } from "../src/runtime/git-probe.js"
import type { ActionResult, RenderedWorkItem } from "../src/core/types.js"
import type { ServerConnection } from "../src/server/connection.js"
import { verifyOnlyWorkspaceManager } from "./support/workspace-mock.js"

let workDir: string

const nonGitRunner = async () => ({
  success: false,
  exitCode: 128,
  stdout: "",
  stderr: "not a git repository",
  combinedOutput: "not a git repository",
})

beforeEach(async () => {
  workDir = await mkdtemp(join(tmpdir(), "mohist-executor-removed-action-"))
  setExecutorGitRunnerForTest(nonGitRunner)
})

afterEach(async () => {
  setExecutorGitRunnerForTest(null)
  await rm(workDir, { recursive: true, force: true })
})

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

function executorFor(registry: ActionRegistry): WorkExecutor {
  return new WorkExecutor(
    registry,
    verifyOnlyWorkspaceManager({ path: workDir, branch: null, changeDir: null }),
    silentConnection(),
    workDir,
  )
}

function buildWork(overrides: Partial<RenderedWorkItem>): RenderedWorkItem {
  return {
    workflowRunId: "wf-removed-action",
    workId: "review.1",
    workType: "task",
    stage: "check",
    title: "Removed Action test",
    uses: "mohist/acp-agent",
    with: {},
    variables: { workspace: { path: workDir, branch: null, changeDir: null } },
    ...overrides,
  }
}

describe("WorkExecutor removed-action rejection", () => {
  it("returns an actionable error when uses is the removed 'mohist/acp-agent' Action", async () => {
    // Issue-410 T-004 / design D6: a pre-cutover WorkflowRun that
    // persisted `uses: mohist/acp-agent` fails with a named, actionable
    // message that points the user to rerun the affected stage with a
    // `mohist/opencode` profile. The generic "No action found" miss is
    // replaced with this richer message.
    const registry = new ActionRegistry()
    const executor = executorFor(registry)
    const result = await executor.execute(buildWork({}), new AbortController().signal)
    expect(result.status).toBe("failed")
    expect(result.message).toContain("removed Action")
    expect(result.message).toContain("mohist/acp-agent")
    expect(result.message).toContain("mohist/opencode")
    expect(result.message).toMatch(/rerun/i)
  })

  it("returns an actionable error when a custom Action is also recognized as removed (case-insensitive)", async () => {
    const registry = new ActionRegistry()
    const executor = executorFor(registry)
    const result = await executor.execute(buildWork({ uses: "MOHIST/ACP-AGENT" }), new AbortController().signal)
    expect(result.status).toBe("failed")
    expect(result.message).toContain("MOHIST/ACP-AGENT")
    expect(result.message).toMatch(/rerun/i)
  })

  it("falls back to the generic 'No action found' miss for unknown Actions that are not removed", async () => {
    const registry = new ActionRegistry()
    const executor = executorFor(registry)
    const result = await executor.execute(buildWork({ uses: "unknown/action" }), new AbortController().signal)
    expect(result.status).toBe("failed")
    expect(result.message).toBe("No action found for 'unknown/action'")
  })
})
