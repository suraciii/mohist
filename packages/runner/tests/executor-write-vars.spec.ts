import { mkdtemp, rm } from "node:fs/promises"
import { tmpdir } from "node:os"
import { join } from "node:path"
import { afterEach, beforeEach, describe, expect, it } from "vitest"
import { ActionRegistry } from "../src/actions/registry.js"
import type { ActionContext, JsonObject, RenderedWorkItem } from "../src/core/types.js"
import { WorkExecutor } from "../src/runtime/executor.js"
import { setExecutorGitRunnerForTest, type GitRunner } from "../src/runtime/git-probe.js"
import type { ServerConnection } from "../src/server/connection.js"
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
  setExecutorGitRunnerForTest(nonGitRunner)
  workDir = await mkdtemp(join(tmpdir(), "mohist-executor-write-vars-"))
})

afterEach(async () => {
  setExecutorGitRunnerForTest(null)
  await rm(workDir, { recursive: true, force: true })
})

describe("WorkExecutor mid-execution variable writes", () => {
  it("passes writeVars through to patchRunVars immediately even when the action fails", async () => {
    const signal = new AbortController().signal
    const events: string[] = []
    const patchCalls: Array<{ workflowRunId: string; vars: JsonObject; signal: AbortSignal }> = []
    const connection = {
      async patchRunVars(workflowRunId: string, vars: JsonObject, patchSignal: AbortSignal) {
        events.push("patchRunVars")
        patchCalls.push({ workflowRunId, vars, signal: patchSignal })
      },
    } as Partial<ServerConnection>

    const registry = new ActionRegistry()
    registry.register("test/write-vars", async (ctx: ActionContext) => {
      events.push("action-start")
      await ctx.writeVars({ checkpoint: "before-failure" })
      events.push("action-after-write")
      return { error: { code: "action-failed", message: "boom" } }
    })

    const executor = new WorkExecutor(
      registry,
      verifyOnlyWorkspaceManager({ path: workDir, branch: null, changeDir: null }),
      connection as ServerConnection,
      workDir,
    )

    const result = await executor.execute(buildWork(), signal)

    expect(result.status).toBe("failed")
    expect(result.message).toBe("boom")
    expect(events).toEqual(["action-start", "patchRunVars", "action-after-write"])
    expect(patchCalls).toEqual([{ workflowRunId: "wf-write-vars", vars: { checkpoint: "before-failure" }, signal }])
  })
})

function buildWork(): RenderedWorkItem {
  return {
    workflowRunId: "wf-write-vars",
    workId: "work-write-vars",
    workType: "task",
    stage: "check",
    title: "Write runtime vars",
    uses: "test/write-vars",
    with: {},
    variables: { workspace: { path: workDir, branch: null, changeDir: null } },
  }
}
