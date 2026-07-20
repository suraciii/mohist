import { mkdtemp, rm } from "node:fs/promises"
import { join } from "node:path"
import { tmpdir } from "node:os"
import { afterEach, beforeEach, describe, expect, it } from "vitest"
import { ActionRegistry } from "../src/actions/registry.js"
import { succeed, fail, validateActionOutputShape } from "../src/actions/action-result.js"
import { WorkExecutor } from "../src/runtime/executor.js"
import { setExecutorGitRunnerForTest } from "../src/runtime/git-probe.js"
import type { ActionResult, RenderedWorkItem } from "../src/core/types.js"
import { verifyOnlyWorkspaceManager } from "./support/workspace-mock.js"

let workDir: string

beforeEach(async () => {
  workDir = await mkdtemp(join(tmpdir(), "mohist-executor-completion-"))
  setExecutorGitRunnerForTest(async () => ({ success: false, exitCode: 128, stdout: "", stderr: "not a git repository", combinedOutput: "not a git repository" }))
})

afterEach(async () => {
  setExecutorGitRunnerForTest(null)
  await rm(workDir, { recursive: true, force: true })
})

function execute(result: ActionResult) {
  const actions = new ActionRegistry()
  actions.register("test/action", async () => result)
  const executor = new WorkExecutor(
    actions,
    verifyOnlyWorkspaceManager({ path: workDir, branch: null, changeDir: null }),
    {} as never,
    workDir,
  )
  const work: RenderedWorkItem = {
    workflowRunId: "wf-completion", workId: "review.1", workType: "task", stage: "check",
    title: "Review", uses: "test/action", with: {}, variables: { workspace: { path: workDir, branch: null, changeDir: null } },
  }
  return executor.execute(work, new AbortController().signal)
}

describe("Action result boundary", () => {
  it("preserves successful output", async () => {
    await expect(execute(succeed({ promise: "PASS" }))).resolves.toMatchObject({ status: "completed", output: { promise: "PASS" } })
  })

  it("preserves an Action timeout without evaluating completion", async () => {
    await expect(execute(fail("timeout", "OpenCode turn timed out after 60s"))).resolves.toMatchObject({
      status: "failed",
      error: { code: "timeout", message: "OpenCode turn timed out after 60s" },
    })
  })

  it.each([
    ["serialized object", '{"answer":42}'],
    ["array", []],
    ["number", 42],
    ["boolean", true],
  ])("rejects a successful %s output before task completion", async (_name, output) => {
    await expect(execute({ output } as unknown as ActionResult)).resolves.toMatchObject({
      status: "failed",
      error: { code: "unexpected-error" },
    })
  })

  it("rejects non-JSON object values", () => {
    expect(validateActionOutputShape({ completedAt: new Date("2026-01-01T00:00:00Z") })).toContain("successful Action output must be a JSON object or null")
  })
})
