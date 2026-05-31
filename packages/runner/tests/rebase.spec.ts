import { afterEach, describe, expect, it } from "vitest"
import { applyWorkflowAgentDefault, rebaseAction, setRebaseGitRunnerForTest } from "../src/actions/rebase.js"
import type { ActionContext, JsonObject } from "../src/core/types.js"

afterEach(() => setRebaseGitRunnerForTest(null))

describe("mohist/rebase", () => {
  it("DirtyWorktreeBeforeRebase_CommitsPendingChangesThenRebases", async () => {
    const calls: string[] = []
    setRebaseGitRunnerForTest(async (_workDir, args) => {
      calls.push(args.join(" "))
      switch (args.join(" ")) {
        case "status --porcelain":
          return ok(" M packages/runner/src/actions/acp-agent.ts\n")
        case "add .":
          return ok("")
        case "commit -m Prepare rebase onto master":
          return ok("[issue abc123] Prepare rebase onto master")
        case "rev-parse HEAD":
          return ok(calls.filter((call) => call === "rev-parse HEAD").length === 1 ? "before\n" : "after\n")
        case "rebase master":
          return ok("Successfully rebased")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const result = await rebaseAction(context())

    expect(result.status).toBe("success")
    expect(calls).toEqual([
      "status --porcelain",
      "add .",
      "commit -m Prepare rebase onto master",
      "rev-parse HEAD",
      "rebase master",
      "rev-parse HEAD",
    ])
    expect(JSON.parse(result.output ?? "{}")).toMatchObject({
      beforeHeadSha: "before",
      afterHeadSha: "after",
      rebased: true,
    })
  })

  it("ConflictResolverWithoutAgentConfig_InheritsWorkflowAgentConfig", () => {
    const withInput: JsonObject = { description: "resolve" }

    applyWorkflowAgentDefault(withInput, {
      vars: { agent: { type: "opencode", model: "openai/gpt-5.4" } },
    })

    expect(withInput.agent).toEqual({ type: "opencode", model: "openai/gpt-5.4" })
  })
})

function context(): ActionContext {
  return {
    workflowRunId: "workflow-1",
    workId: "rebase.1",
    workType: "task",
    stage: "check",
    title: "Rebase onto master",
    uses: "mohist/rebase",
    with: { baseBranch: "master" },
    variables: {},
    workDir: "/fake/worktree",
    signal: new AbortController().signal,
  }
}

function ok(stdout: string) {
  return { success: true, stdout, stderr: "", exitCode: 0, combinedOutput: stdout.trim() }
}

function fail(stderr: string) {
  return { success: false, stdout: "", stderr, exitCode: 1, combinedOutput: stderr }
}
