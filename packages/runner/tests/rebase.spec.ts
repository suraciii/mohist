import { afterEach, describe, expect, it } from "vitest"
import { setIssueFieldCommandRunnerForTest } from "../src/actions/issue-fields.js"
import { rebaseAction, setRebaseExistsCheckerForTest, setRebaseGitRunnerForTest } from "../src/actions/rebase.js"
import type { ActionContext, JsonObject } from "../src/core/types.js"

afterEach(() => {
  setRebaseGitRunnerForTest(null)
  setRebaseExistsCheckerForTest(null)
  setIssueFieldCommandRunnerForTest(null)
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
      errorCode: null,
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
      errorCode: null,
    })
    expect(result.message).toBe("Rebase completed")
  })

  it("MessageFrom_IsIgnoredWhenSquashIsFalse", async () => {
    const calls: string[] = []
    const moCalls: string[] = []
    setRebaseExistsCheckerForTest(() => false)
    setIssueFieldCommandRunnerForTest(async (cmd, args) => {
      moCalls.push([cmd, ...args].join(" "))
      return {
        exitCode: 1,
        stdout: "",
        stderr: "should not run",
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
        case "rev-parse origin/master":
          return ok("baseShaRemote\n")
        case "status --porcelain":
          return ok("")
        case "rev-parse HEAD":
          return ok(calls.filter((call) => call === "rev-parse HEAD").length === 1 ? "before\n" : "after\n")
        case "rebase origin/master":
          return ok("Successfully rebased")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const result = await rebaseAction(context({
      baseBranch: "master",
      remote: "origin",
      squash: false,
      messageFrom: "issue.title",
    }))

    expect(result.status).toBe("success")
    expect(moCalls).toEqual([])
    expect(calls).not.toContain("commit -m")
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
      errorCode: null,
    })
    expect(result.message).toBe("Rebase and squash completed")
  })

  it("SquashOption_MessageFromIssueTitle_ResolvesTitleWithMoIssueShow", async () => {
    const calls: string[] = []
    const moCalls: string[] = []
    setRebaseExistsCheckerForTest(() => false)
    setIssueFieldCommandRunnerForTest(async (cmd, args) => {
      moCalls.push([cmd, ...args].join(" "))
      return {
        exitCode: 0,
        stdout: JSON.stringify({ success: true, data: { title: "Use issue title for squash", body: "ignored" } }),
        stderr: "",
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
        case "commit -m Use issue title for squash":
          return ok("[mo/issue-217 1a2b3c4] Use issue title for squash\n")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const result = await rebaseAction(context({
      baseBranch: "master",
      remote: "origin",
      squash: true,
      messageFrom: "issue.title",
    }))

    expect(result.status).toBe("success")
    expect(moCalls).toEqual(["mo issue show 217 --project-id proj_1 --output json"])
    expect(calls).toContain("commit -m Use issue title for squash")
  })

  it("SquashOption_MessageFromIssueTitleFailure_ReturnsStructuredFailure", async () => {
    const calls: string[] = []
    const moCalls: string[] = []
    setRebaseExistsCheckerForTest(() => false)
    setIssueFieldCommandRunnerForTest(async (cmd, args) => {
      moCalls.push([cmd, ...args].join(" "))
      return {
        exitCode: 1,
        stdout: "",
        stderr: "issue not found",
      }
    })
    setRebaseGitRunnerForTest(async (_workDir, args) => {
      calls.push(args.join(" "))
      return fail(`unexpected git call: ${args.join(" ")}`)
    })

    const result = await rebaseAction(context({
      baseBranch: "master",
      remote: "origin",
      squash: true,
      messageFrom: "issue.title",
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(output.errorCode).toBe("retry-safe")
    expect(output.output).toContain("mo issue show 217 failed")
    expect(output.output).toContain("issue not found")
    expect(calls).toEqual([
      "rev-parse --git-path rebase-merge",
      "rev-parse --git-path rebase-apply",
    ])
    expect(calls).not.toContain("fetch origin master")
    expect(calls).not.toContain("rebase origin/master")
    expect(calls).not.toContain("commit -m Use issue title for squash")
    expect(moCalls).toEqual(["mo issue show 217 --project-id proj_1 --output json"])
  })

  it("SquashOption_UnsupportedMessageFrom_ReturnsStructuredFailure", async () => {
    const calls: string[] = []
    const moCalls: string[] = []
    setRebaseExistsCheckerForTest(() => false)
    setIssueFieldCommandRunnerForTest(async (cmd, args) => {
      moCalls.push([cmd, ...args].join(" "))
      return {
        exitCode: 0,
        stdout: "unexpected",
        stderr: "",
      }
    })
    setRebaseGitRunnerForTest(async (_workDir, args) => {
      calls.push(args.join(" "))
      return fail(`unexpected git call: ${args.join(" ")}`)
    })

    const result = await rebaseAction(context({
      baseBranch: "master",
      remote: "origin",
      squash: true,
      messageFrom: "issue.summary",
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(output.errorCode).toBe("retry-safe")
    expect(output.output).toContain("Unsupported messageFrom source 'issue.summary'")
    expect(calls).toEqual([
      "rev-parse --git-path rebase-merge",
      "rev-parse --git-path rebase-apply",
    ])
    expect(moCalls).toEqual([])
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
      errorCode: "squash-message-missing",
    })
    expect(result.message).toContain("'message' is required")
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
      errorCode: "retry-safe",
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
      errorCode: "retry-safe",
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

  it("Conflict_NoRecovery_AbortsAndReportsConflict", async () => {
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
          return ok("before\n")
        case "rebase master":
          return fail("CONFLICT (content): Merge conflict in packages/runner/src/actions/rebase.ts")
        case "diff --name-only --diff-filter=U":
          return ok("packages/runner/src/actions/rebase.ts\n")
        case "rebase --abort":
          return ok("aborted")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const result = await rebaseAction(context())
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(calls).toContain("rebase --abort")
    expect(output).toMatchObject({
      rebased: false,
      errorCode: "conflict",
      rebaseLeftInProgress: false,
    })
    expect(result.message).toBe("Rebase failed: conflict could not be resolved")
  })

  it("Conflict_WithRecovery_LeavesRebaseInProgressAndReturnsConflict", async () => {
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
          return ok("before\n")
        case "rebase master":
          return fail("CONFLICT (content): Merge conflict in packages/runner/src/actions/rebase.ts")
        case "diff --name-only --diff-filter=U":
          return ok("packages/runner/src/actions/rebase.ts\npackages/runner/src/actions/git.ts\n")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const result = await rebaseAction(context({
      recovery: { budget: 1, handlers: [] },
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(output).toMatchObject({
      kind: "rebase",
      rebased: false,
      conflicts: ["packages/runner/src/actions/rebase.ts", "packages/runner/src/actions/git.ts"],
      resolveAttempts: 0,
      errorCode: "conflict",
      rebaseLeftInProgress: true,
    })
    expect(calls).not.toContain("rebase --abort")
    expect(result.message).toBe("Rebase in progress: conflicts require task-level resolution")
  })

  it("Conflict_WithRecovery_RerunAfterAbandonedInProgress_AbortsPriorRebaseThenStartsFresh", async () => {
    const calls: string[] = []
    let rebaseStatePresent = true
    setRebaseExistsCheckerForTest((path) =>
      path === "/fake/worktree/.git/rebase-merge" && rebaseStatePresent,
    )
    setRebaseGitRunnerForTest(async (_workDir, args) => {
      calls.push(args.join(" "))
      switch (args.join(" ")) {
        case "rev-parse --git-path rebase-merge":
          return ok("/fake/worktree/.git/rebase-merge\n")
        case "rev-parse --git-path rebase-apply":
          return ok("/fake/worktree/.git/rebase-apply\n")
        case "rebase --abort":
          rebaseStatePresent = false
          return ok("aborted")
        case "rev-parse master":
          return ok("baseSha\n")
        case "status --porcelain":
          return ok("")
        case "rev-parse HEAD":
          return ok("before\n")
        case "rebase master":
          return fail("CONFLICT (content): Merge conflict in packages/runner/src/actions/rebase.ts")
        case "diff --name-only --diff-filter=U":
          return ok("packages/runner/src/actions/rebase.ts\n")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const result = await rebaseAction(context({ recovery: { budget: 1, handlers: [] } }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(calls).toContain("rebase --abort")
    expect(calls.indexOf("rebase --abort")).toBeLessThan(calls.indexOf("rebase master"))
    expect(output).toMatchObject({
      errorCode: "conflict",
      rebaseLeftInProgress: true,
    })
  })

  it("Conflict_WithRecovery_SuccessfulRebase_ReportsNormal", async () => {
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

    const result = await rebaseAction(context({ recovery: { budget: 1, handlers: [] } }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(output).toMatchObject({
      rebased: true,
      errorCode: null,
      rebaseLeftInProgress: false,
    })
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
    variables: {
      project: { id: "proj_1" },
      issue: { number: 217 },
      ...variables,
    },
    workDir: "/fake/worktree",
    projectId: "proj_1",
    issueNumber: 217,
    signal: new AbortController().signal,
  }
}

function ok(stdout: string) {
  return { success: true, stdout, stderr: "", exitCode: 0, combinedOutput: stdout.trim() }
}

function fail(stderr: string) {
  return { success: false, stdout: "", stderr, exitCode: 1, combinedOutput: stderr }
}
