import { afterEach, describe, expect, it } from "vitest"
import { applyWorkflowAgentDefault, rebaseAction, setRebaseConflictResolverForTest, setRebaseExistsCheckerForTest, setRebaseGitRunnerForTest } from "../src/actions/rebase.js"
import type { ActionContext, JsonObject } from "../src/core/types.js"

afterEach(() => {
  setRebaseGitRunnerForTest(null)
  setRebaseExistsCheckerForTest(null)
  setRebaseConflictResolverForTest(null)
})

describe("mohist/rebase", () => {
  it("DirtyWorktreeBeforeRebase_CommitsPendingChangesThenRebases", async () => {
    const calls: string[] = []
    setRebaseExistsCheckerForTest(() => false)
    setRebaseGitRunnerForTest(async (_workDir, args) => {
      calls.push(args.join(" "))
      switch (args.join(" ")) {
        case "rev-parse --git-path rebase-merge":
          return ok("/fake/worktree/.git/rebase-merge\n")
        case "rev-parse --git-path rebase-apply":
          return ok("/fake/worktree/.git/rebase-apply\n")
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
      "rev-parse --git-path rebase-merge",
      "rev-parse --git-path rebase-apply",
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

  it("StaleRebaseStateBeforeRebase_AbortsBeforeStartingFreshRebase", async () => {
    const calls: string[] = []
    setRebaseExistsCheckerForTest((path) => path === "/fake/worktree/.git/rebase-merge")
    setRebaseGitRunnerForTest(async (_workDir, args) => {
      calls.push(args.join(" "))
      switch (args.join(" ")) {
        case "rev-parse --git-path rebase-merge":
          return ok("/fake/worktree/.git/rebase-merge\n")
        case "rev-parse --git-path rebase-apply":
          return ok("/fake/worktree/.git/rebase-apply\n")
        case "rebase --abort":
          return ok("aborted")
        case "status --porcelain":
          return ok("")
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
    expect(calls).toContain("rebase --abort")
    expect(calls.indexOf("rebase --abort")).toBeLessThan(calls.indexOf("rebase master"))
  })

  it("ConflictResolverWithoutAgentConfig_InheritsWorkflowAgentConfig", () => {
    const withInput: JsonObject = { description: "resolve" }

    applyWorkflowAgentDefault(withInput, {
      vars: { agent: { type: "opencode", model: "openai/gpt-5.4" } },
    })

    expect(withInput.agent).toEqual({ type: "opencode", model: "openai/gpt-5.4" })
  })

  it("ConflictResolverSucceedsButRebaseContinueFails_IncludesContinueOutput", async () => {
    const calls: string[] = []
    setRebaseExistsCheckerForTest(() => false)
    setRebaseConflictResolverForTest(async () => ({
      status: "success",
      message: "resolved",
      output: "resolved markers",
    }))
    setRebaseGitRunnerForTest(async (_workDir, args) => {
      calls.push(args.join(" "))
      switch (args.join(" ")) {
        case "rev-parse --git-path rebase-merge":
          return ok("/fake/worktree/.git/rebase-merge\n")
        case "rev-parse --git-path rebase-apply":
          return ok("/fake/worktree/.git/rebase-apply\n")
        case "status --porcelain":
          return ok("")
        case "rev-parse HEAD":
          return ok("before\n")
        case "rebase master":
          return fail("CONFLICT (content): Merge conflict in packages/runner/src/actions/acp-agent.ts")
        case "diff --name-only --diff-filter=U":
          return ok("packages/runner/src/actions/acp-agent.ts\n")
        case "add .":
          return ok("")
        case "-c core.editor=true rebase --continue":
          return fail("error: could not apply next-commit\nCONFLICT (content): Merge conflict in packages/runner/tests/acp-agent.spec.ts")
        case "rebase --abort":
          return ok("aborted")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const result = await rebaseAction(context({ maxConflictRetries: 1, conflictResolver: { with: {} } }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(calls).toContain("-c core.editor=true rebase --continue")
    expect(output.output).toContain("Merge conflict in packages/runner/src/actions/acp-agent.ts")
    expect(output.output).toContain("error: could not apply next-commit")
    expect(output.output).toContain("Merge conflict in packages/runner/tests/acp-agent.spec.ts")
  })
})

function context(withOverrides: JsonObject = {}, variables: JsonObject = {}): ActionContext {
  return {
    workflowRunId: "workflow-1",
    workId: "rebase.1",
    workType: "task",
    stage: "check",
    title: "Rebase onto master",
    uses: "mohist/rebase",
    with: { baseBranch: "master", ...withOverrides },
    variables,
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
