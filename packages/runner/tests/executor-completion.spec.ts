import { describe, expect, it as vitestIt } from "vitest"
import { succeed, fail, validateActionOutputShape } from "../src/actions/action-result.js"
import { WorkExecutor } from "../src/runtime/executor.js"
import type { GitRunner } from "../src/runtime/git-probe.js"
import type { ActionResult, DispatchWorkItem } from "../src/core/types.js"
import { verifyOnlyWorkspaceManager } from "./support/workspace-mock.js"
import { defineTestActions } from "./support/action-registry-test.js"
import { withTestRunnerResources } from "./support/test-resources.js"

const nonGitRunner: GitRunner = async () => ({ success: false, exitCode: 128, stdout: "", stderr: "not a git repository", combinedOutput: "not a git repository" })

const withExecutorResources = <T>(body: (workDir: string) => Promise<T>) =>
  withTestRunnerResources(async () => await body("/virtual/executor-completion"), { gitRunner: nonGitRunner })

function execute(result: ActionResult, workDir: string) {
  const actions = defineTestActions({
    "test/action": async () => result,
  })
  const executor = new WorkExecutor(
    actions,
     verifyOnlyWorkspaceManager({ path: workDir, branch: null }),
    {} as never,
    workDir,
  )
  const work: DispatchWorkItem = {
    workflowRunId: "wf-completion", workId: "review.1", workType: "task", stage: "check",
     title: "Review", uses: "test/action", with: {}, variables: { workspace: { path: workDir, branch: null } },
  }
  return executor.execute(work, new AbortController().signal)
}

describe("Action result boundary", () => {
  const it = Object.assign(
    (name: string, body: (workDir: string) => Promise<void> | void) => vitestIt(name, () => withExecutorResources(async (workDir) => await body(workDir))),
    { each: vitestIt.each.bind(vitestIt) },
  )

  it("preserves successful output", async (workDir) => {
    await expect(execute(succeed({ promise: "PASS" }), workDir)).resolves.toMatchObject({ status: "completed", output: { promise: "PASS" } })
  })

  it("preserves an Action timeout without evaluating completion", async (workDir) => {
    await expect(execute(fail("timeout", "OpenCode turn timed out after 60s"), workDir)).resolves.toMatchObject({
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
    await withExecutorResources(async (workDir) => await expect(execute({ output } as unknown as ActionResult, workDir)).resolves.toMatchObject({
      status: "failed",
      error: { code: "unexpected-error" },
    }))
  })

  it("rejects non-JSON object values", () => {
    expect(validateActionOutputShape({ completedAt: new Date("2026-01-01T00:00:00Z") })).toContain("successful Action output must be a JSON object or null")
  })

  it("accepts repeated JSON object references", () => {
    const shared = { value: 1 }
    expect(validateActionOutputShape({ first: shared, second: shared })).toBeNull()
  })
})
