import { afterEach, describe, expect, it } from "vitest"
import { applyWorkflowAgentDefault, rebaseAction, setRebaseConflictResolverForTest, setRebaseExistsCheckerForTest, setRebaseGitRunnerForTest } from "../src/actions/rebase.js"
import type { ActionContext, JsonObject } from "../src/core/types.js"

afterEach(() => {
  setRebaseGitRunnerForTest(null)
  setRebaseExistsCheckerForTest(null)
  setRebaseConflictResolverForTest(null)
})

describe("mohist/rebase", () => {
  it("LocalBasePath_RebasesOntoLocalBaseBranch", async () => {
    const calls: string[] = []
    setRebaseExistsCheckerForTest(() => false)
    setRebaseGitRunnerForTest(async (_workDir, args) => {
      calls.push(args.join(" "))
      switch (args.join(" ")) {
        case "rev-parse --git-path rebase-merge":
          return ok("/fake/worktree/.git/rebase-merge\n")
        case "rev-parse --git-path rebase-apply":
          return ok("/fake/worktree/.git/rebase-apply\n")
        case "rev-parse master":
          return ok("baseSha\n")
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
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(calls).toEqual([
      "rev-parse --git-path rebase-merge",
      "rev-parse --git-path rebase-apply",
      "rev-parse master",
      "status --porcelain",
      "rev-parse HEAD",
      "rebase master",
      "rev-parse HEAD",
    ])
    expect(calls).not.toContain("fetch origin master")
    expect(calls).not.toContain("rebase origin/master")
    expect(calls).not.toContain("reset --soft")
    expect(calls).not.toContain("commit -m Complete issue #217")
    expect(output).toMatchObject({
      kind: "rebase",
      baseBranch: "master",
      remote: null,
      baseRef: "master",
      rebasedOntoSha: "baseSha",
      beforeHeadSha: "before",
      afterHeadSha: "after",
      squashed: false,
      squashedHeadSha: null,
      rebased: true,
      conflicts: [],
      resolveAttempts: 0,
      failureKind: null,
    })
  })

  it("RemoteOption_FetchesAndRebasesOntoRemoteBaseRef", async () => {
    const calls: string[] = []
    setRebaseExistsCheckerForTest(() => false)
    setRebaseGitRunnerForTest(async (_workDir, args) => {
      calls.push(args.join(" "))
      switch (args.join(" ")) {
        case "rev-parse --git-path rebase-merge":
          return ok("/fake/worktree/.git/rebase-merge\n")
        case "rev-parse --git-path rebase-apply":
          return ok("/fake/worktree/.git/rebase-apply\n")
        case "fetch origin master":
          return ok("From https://example.com/repo\n * branch            master     -> FETCH_HEAD")
        case "rev-parse origin/master":
          return ok("baseShaRemote\n")
        case "status --porcelain":
          return ok("")
        case "rev-parse HEAD":
          return ok(calls.filter((call) => call === "rev-parse HEAD").length === 1 ? "before\n" : "after\n")
        case "rebase origin/master":
          return ok("Successfully rebased and updated refs/heads/mo/issue-217.")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const result = await rebaseAction(context({ baseBranch: "master", remote: "origin" }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(calls).toEqual([
      "rev-parse --git-path rebase-merge",
      "rev-parse --git-path rebase-apply",
      "fetch origin master",
      "rev-parse origin/master",
      "status --porcelain",
      "rev-parse HEAD",
      "rebase origin/master",
      "rev-parse HEAD",
    ])
    expect(calls).not.toContain("rebase master")
    expect(output).toMatchObject({
      kind: "rebase",
      baseBranch: "master",
      remote: "origin",
      baseRef: "origin/master",
      rebasedOntoSha: "baseShaRemote",
      beforeHeadSha: "before",
      afterHeadSha: "after",
      squashed: false,
      squashedHeadSha: null,
      rebased: true,
      failureKind: null,
    })
    expect(result.message).toBe("Rebase completed")
  })

  it("SquashOption_FoldsMultipleCommitsIntoOneOnRunBranch", async () => {
    const calls: string[] = []
    setRebaseExistsCheckerForTest(() => false)
    setRebaseGitRunnerForTest(async (_workDir, args) => {
      calls.push(args.join(" "))
      switch (args.join(" ")) {
        case "rev-parse --git-path rebase-merge":
          return ok("/fake/worktree/.git/rebase-merge\n")
        case "rev-parse --git-path rebase-apply":
          return ok("/fake/worktree/.git/rebase-apply\n")
        case "fetch origin master":
          return ok("")
        case "rev-parse origin/master":
          return ok("baseSha\n")
        case "status --porcelain":
          return ok("")
        case "rev-parse HEAD": {
          const index = calls.filter((call) => call === "rev-parse HEAD").length
          if (index === 1) return ok("beforeRebase\n")
          if (index === 2) return ok("afterRebase\n")
          return ok("squashedHead\n")
        }
        case "rebase origin/master":
          return ok("Successfully rebased and updated refs/heads/mo/issue-217.")
        case "reset --soft baseSha":
          return ok("")
        case "commit -m Complete issue #217":
          return ok("[mo/issue-217 1a2b3c4] Complete issue #217\n 3 files changed, 42 insertions(+), 7 deletions(-)")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const result = await rebaseAction(context({
      baseBranch: "master",
      remote: "origin",
      squash: true,
      message: "Complete issue #217",
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(calls).toEqual([
      "rev-parse --git-path rebase-merge",
      "rev-parse --git-path rebase-apply",
      "fetch origin master",
      "rev-parse origin/master",
      "status --porcelain",
      "rev-parse HEAD",
      "rebase origin/master",
      "rev-parse HEAD",
      "reset --soft baseSha",
      "commit -m Complete issue #217",
      "rev-parse HEAD",
    ])
    expect(calls).not.toContain("checkout master")
    expect(calls).not.toContain("checkout origin/master")
    expect(output).toMatchObject({
      kind: "rebase",
      baseBranch: "master",
      remote: "origin",
      baseRef: "origin/master",
      rebasedOntoSha: "baseSha",
      beforeHeadSha: "beforeRebase",
      afterHeadSha: "afterRebase",
      squashed: true,
      squashedHeadSha: "squashedHead",
      rebased: true,
      failureKind: null,
    })
    expect(result.message).toBe("Rebase and squash completed")
  })

  it("SquashOptionWithoutMessage_FailsBeforeSquash", async () => {
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
          return ok("")
        case "rev-parse master":
          return ok("baseSha\n")
        case "rev-parse HEAD":
          return ok(calls.filter((call) => call === "rev-parse HEAD").length === 1 ? "before\n" : "after\n")
        case "rebase master":
          return ok("Successfully rebased")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const result = await rebaseAction(context({ baseBranch: "master", squash: true }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(calls).not.toContain("reset --soft")
    expect(calls).not.toContain("commit -m")
    expect(output).toMatchObject({
      kind: "rebase",
      squashed: false,
      squashedHeadSha: null,
      rebased: false,
      failureKind: "squash-message-missing",
    })
    expect(result.message).toContain("'message' is required")
  })

  it("SquashOption_RunsOnlyAfterSuccessfulRebase", async () => {
    const calls: string[] = []
    let resolverRan = false
    setRebaseExistsCheckerForTest(() => false)
    setRebaseConflictResolverForTest(async (resolverContext) => {
      resolverRan = true
      return {
        status: "success",
        message: "rebase completed",
        output: `agent completed ${resolverContext.workId}`,
      }
    })
    setRebaseGitRunnerForTest(async (_workDir, args) => {
      calls.push(args.join(" "))
      switch (args.join(" ")) {
        case "rev-parse --git-path rebase-merge":
          return ok("/fake/worktree/.git/rebase-merge\n")
        case "rev-parse --git-path rebase-apply":
          return ok("/fake/worktree/.git/rebase-apply\n")
        case "fetch origin master":
          return ok("")
        case "status --porcelain":
          return ok("")
        case "rev-parse origin/master":
          return ok("baseSha\n")
        case "rev-parse HEAD":
          return ok(calls.filter((call) => call === "rev-parse HEAD").length === 1 ? "before\n" : "after\n")
        case "rebase origin/master":
          return fail("CONFLICT (content): Merge conflict in packages/runner/src/actions/rebase.ts")
        case "diff --name-only --diff-filter=U":
          return ok(resolverRan ? "" : "packages/runner/src/actions/rebase.ts\n")
        case "rebase --abort":
          return ok("aborted")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const result = await rebaseAction(context({
      baseBranch: "master",
      remote: "origin",
      squash: true,
      message: "Complete issue #217",
      maxConflictRetries: 1,
      conflictResolver: { with: {} },
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(calls).toContain("rebase --abort")
    expect(calls).not.toContain("reset --soft baseSha")
    expect(calls).not.toContain("commit -m Complete issue #217")
    expect(output).toMatchObject({
      kind: "rebase",
      squashed: false,
      squashedHeadSha: null,
      rebased: false,
      failureKind: "conflict",
    })
  })

  it("RemoteFetchFails_ReportsRetrySafe", async () => {
    const calls: string[] = []
    setRebaseExistsCheckerForTest(() => false)
    setRebaseGitRunnerForTest(async (_workDir, args) => {
      calls.push(args.join(" "))
      switch (args.join(" ")) {
        case "rev-parse --git-path rebase-merge":
          return ok("/fake/worktree/.git/rebase-merge\n")
        case "rev-parse --git-path rebase-apply":
          return ok("/fake/worktree/.git/rebase-apply\n")
        case "fetch origin master":
          return fail("fatal: could not resolve host")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const result = await rebaseAction(context({ baseBranch: "master", remote: "origin" }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(calls).toEqual([
      "rev-parse --git-path rebase-merge",
      "rev-parse --git-path rebase-apply",
      "fetch origin master",
    ])
    expect(calls).not.toContain("rebase origin/master")
    expect(output).toMatchObject({
      kind: "rebase",
      remote: "origin",
      baseRef: "origin/master",
      rebased: false,
      failureKind: "retry-safe",
    })
  })

  it("BaseRefRevParseFails_ReportsRetrySafe", async () => {
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
          return ok("")
        case "rev-parse origin/master":
          return fail("fatal: ambiguous argument 'origin/master'")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const result = await rebaseAction(context({ baseBranch: "master", remote: "origin" }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(calls).not.toContain("rebase origin/master")
    expect(output).toMatchObject({
      kind: "rebase",
      rebased: false,
      failureKind: "retry-safe",
    })
  })

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
        case "rev-parse master":
          return ok("baseSha\n")
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
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(calls).toEqual([
      "rev-parse --git-path rebase-merge",
      "rev-parse --git-path rebase-apply",
      "rev-parse master",
      "status --porcelain",
      "add .",
      "commit -m Prepare rebase onto master",
      "rev-parse HEAD",
      "rebase master",
      "rev-parse HEAD",
    ])
    expect(output).toMatchObject({
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
        case "rev-parse master":
          return ok("baseSha\n")
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

  it("ConflictResolverCompletesFullRebase_VerifiesWithoutContinuingForAgent", async () => {
    const calls: string[] = []
    let resolverRan = false
    setRebaseExistsCheckerForTest(() => false)
    setRebaseConflictResolverForTest(async (resolverContext) => {
      resolverRan = true
      return {
        status: "success",
        message: "rebase completed",
        output: `agent completed ${resolverContext.workId}`,
      }
    })
    setRebaseGitRunnerForTest(async (_workDir, args) => {
      calls.push(args.join(" "))
      switch (args.join(" ")) {
        case "rev-parse --git-path rebase-merge":
          return ok("/fake/worktree/.git/rebase-merge\n")
        case "rev-parse --git-path rebase-apply":
          return ok("/fake/worktree/.git/rebase-apply\n")
        case "status --porcelain":
          return ok("")
        case "branch --show-current":
          return ok("feature-branch\n")
        case "rev-parse master":
          return ok("base\n")
        case "rev-parse HEAD":
          return ok(calls.filter((call) => call === "rev-parse HEAD").length === 1 ? "before\n" : "after\n")
        case "rebase master":
          return fail("CONFLICT (content): Merge conflict in packages/runner/src/actions/acp-agent.ts")
        case "diff --name-only --diff-filter=U":
          return ok(resolverRan ? "" : "packages/runner/src/actions/acp-agent.ts\n")
        case "merge-base master HEAD":
          return ok("base\n")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const result = await rebaseAction(context({ maxConflictRetries: 1, conflictResolver: { with: {} } }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(calls).not.toContain("add .")
    expect(calls).not.toContain("-c core.editor=true rebase --continue")
    expect(output.output).toContain("Merge conflict in packages/runner/src/actions/acp-agent.ts")
    expect(output.output).toContain("agent completed rebase.1-conflict-resolve-1")
    expect(output).toMatchObject({
      beforeHeadSha: "before",
      afterHeadSha: "after",
      rebased: true,
      resolveAttempts: 1,
    })
  })

  it("ConflictResolverReturnsSuccessButRebaseStillInProgress_FailsWithVerificationOutput", async () => {
    const calls: string[] = []
    let resolverRan = false
    setRebaseExistsCheckerForTest((path) => resolverRan && path === "/fake/worktree/.git/rebase-merge")
    setRebaseConflictResolverForTest(async () => {
      resolverRan = true
      return {
        status: "success",
        message: "partial",
        output: "agent stopped before git rebase --continue",
      }
    })
    setRebaseGitRunnerForTest(async (_workDir, args) => {
      calls.push(args.join(" "))
      switch (args.join(" ")) {
        case "rev-parse --git-path rebase-merge":
          return ok("/fake/worktree/.git/rebase-merge\n")
        case "rev-parse --git-path rebase-apply":
          return ok("/fake/worktree/.git/rebase-apply\n")
        case "status --porcelain":
          return ok("")
        case "rev-parse master":
          return ok("base\n")
        case "rev-parse HEAD":
          return ok("before\n")
        case "rebase master":
          return fail("CONFLICT (content): Merge conflict in packages/runner/src/actions/acp-agent.ts")
        case "diff --name-only --diff-filter=U":
          return ok(resolverRan ? "" : "packages/runner/src/actions/acp-agent.ts\n")
        case "merge-base master HEAD":
          return ok("old-base\n")
        case "rebase --abort":
          return ok("aborted")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const result = await rebaseAction(context({ maxConflictRetries: 1, conflictResolver: { with: {} } }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(calls).not.toContain("-c core.editor=true rebase --continue")
    expect(calls).toContain("rebase --abort")
    expect(output.output).toContain("agent stopped before git rebase --continue")
    expect(output.output).toContain("Rebase is still in progress.")
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
