import { mkdtemp, rm } from "node:fs/promises"
import { join } from "node:path"
import { tmpdir } from "node:os"
import { afterEach, beforeEach, describe, expect, it } from "vitest"
import { ActionRegistry } from "../src/actions/registry.js"
import type { ActionResult, RenderedWorkItem } from "../src/core/types.js"
import { setExecutorGitRunnerForTest } from "../src/runtime/git-probe.js"
import { WorkExecutor } from "../src/runtime/executor.js"
import { verifyOnlyWorkspaceManager } from "./support/workspace-mock.js"

let workDir: string

beforeEach(async () => {
  workDir = await mkdtemp(join(tmpdir(), "mohist-executor-recovery-"))
  setExecutorGitRunnerForTest(async () => ({ success: false, exitCode: 128, stdout: "", stderr: "not a git repository", combinedOutput: "not a git repository" }))
})

afterEach(async () => {
  setExecutorGitRunnerForTest(null)
  await rm(workDir, { recursive: true, force: true })
})

function executorFor(result: ActionResult): WorkExecutor {
  const registry = new ActionRegistry()
  registry.register("test/action", async () => result)
  return new WorkExecutor(
    registry,
    verifyOnlyWorkspaceManager({ path: workDir, branch: null, changeDir: null }),
    {} as never,
    {} as never,
    null,
    workDir,
  )
}

function work(overrides: Partial<RenderedWorkItem> = {}): RenderedWorkItem {
  return {
    workflowRunId: "wf-recovery",
    workId: "integrate:rebase.2",
    workType: "task",
    stage: "integrate",
    title: "Rebase branch",
    uses: "test/action",
    with: { baseBranch: "master" },
    variables: { workspace: { path: workDir, branch: null, changeDir: null } },
    ...overrides,
  }
}

describe("WorkExecutor recovery", () => {
  it("schedules handler tasks and trimmed retry self with decremented budget", async () => {
    const executor = executorFor({
      status: "failure",
      message: "conflict",
      output: JSON.stringify({ errorCode: "rebase-conflict" }),
    })

    const result = await executor.execute(work({
      recovery: {
        budget: 2,
        handlers: [
          {
            when: "errorCode=rebase-conflict",
            tasks: [{ id: "resolve-conflicts", title: "Resolve conflicts", uses: "mohist/acp-agent", with: { session: "integrate" } }],
            retrySelf: true,
          },
        ],
      },
    }), new AbortController().signal)

    expect(result).toMatchObject({
      status: "completed",
      message: "Rebase branch failed (errorCode=rebase-conflict); recovery scheduled",
      addTasks: [
        { id: "resolve-conflicts", title: "Resolve conflicts", uses: "mohist/acp-agent", with: { session: "integrate" } },
        { id: "integrate:rebase", title: "Rebase branch", uses: "test/action", with: { baseBranch: "master" }, recovery: { budget: 1 } },
      ],
    })
  })

  it("leaves unmatched failure output failed", async () => {
    const executor = executorFor({
      status: "failure",
      message: "network failed",
      output: JSON.stringify({ errorCode: "network" }),
    })

    const result = await executor.execute(work({
      recovery: {
        budget: 1,
        handlers: [{ when: "errorCode=rebase-conflict", tasks: [{ id: "resolve-conflicts", title: "Resolve conflicts" }], retrySelf: true }],
      },
    }), new AbortController().signal)

    expect(result).toMatchObject({ status: "failed", message: "network failed" })
    expect(result.addTasks).toBeUndefined()
  })
})
