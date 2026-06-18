import { afterEach, describe, expect, it } from "vitest"
import { prepareAction, setDeliveryGitRunnerForTest } from "../src/actions/registry.js"
import { setRebaseConflictResolverForTest, setRebaseExistsCheckerForTest, setRebaseGitRunnerForTest } from "../src/actions/rebase.js"
import type { ActionContext, JsonObject } from "../src/core/types.js"

type GitResponse = { success: boolean; stdout: string; stderr: string; exitCode: number; combinedOutput: string }

afterEach(() => {
  setDeliveryGitRunnerForTest(null)
  setRebaseGitRunnerForTest(null)
  setRebaseConflictResolverForTest(null)
  setRebaseExistsCheckerForTest(null)
})

function installGit(respond: (workDir: string, args: string[], calls: string[]) => GitResponse | Promise<GitResponse>) {
  const calls: string[] = []
  const respondAndTrack = async (workDir: string, args: string[]) => {
    calls.push(args.join(" "))
    return await respond(workDir, args, calls)
  }
  setRebaseGitRunnerForTest(respondAndTrack)
  setDeliveryGitRunnerForTest(respondAndTrack)
  return calls
}

describe("mohist/prepare", () => {
  it("CleanBranch_FetchesBaseAndRebasesIntoPreparedHead", async () => {
    const calls = installGit(async (_workDir, args, allCalls) => {
      switch (args.join(" ")) {
        case "rev-parse --git-path rebase-merge":
          return ok("/fake/worktree/.git/rebase-merge\n")
        case "rev-parse --git-path rebase-apply":
          return ok("/fake/worktree/.git/rebase-apply\n")
        case "status --porcelain":
          return ok("")
        case "fetch origin master":
          return ok("From https://example.com/repo\n * branch            master     -> FETCH_HEAD")
        case "rev-parse origin/master":
          return ok("base123\n")
        case "rev-parse HEAD":
          return ok(allCalls.filter((c) => c === "rev-parse HEAD").length === 1 ? "before\n" : "after\n")
        case "rebase origin/master":
          return ok("Successfully rebased and updated refs/heads/mo/issue-82.")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })
    setRebaseExistsCheckerForTest(() => false)
    setRebaseConflictResolverForTest(async () => ({ status: "success", message: "noop", output: "" }))

    const result = await prepareAction(context({ baseBranch: "master", maxConflictRetries: 1 }))

    expect(result.status).toBe("success")
    expect(calls).toEqual([
      "rev-parse --git-path rebase-merge",
      "rev-parse --git-path rebase-apply",
      "status --porcelain",
      "fetch origin master",
      "rev-parse origin/master",
      "rev-parse HEAD",
      "rebase origin/master",
      "rev-parse HEAD",
      "status --porcelain",
    ])
    const output = JSON.parse(result.output ?? "{}")
    expect(output).toMatchObject({
      kind: "prepare",
      status: "completed",
      baseBranch: "master",
      preparedBaseSha: "base123",
      preparedHeadSha: "after",
      prepared: true,
      conflicts: [],
      resolveAttempts: 0,
      failureKind: null,
    })
    expect(output).not.toHaveProperty("pushed")
  })

  it("DirtyWorktreeBeforePrepare_FailsWithoutCommittingPendingChanges", async () => {
    const calls = installGit(async (_workDir, args) => {
      switch (args.join(" ")) {
        case "rev-parse --git-path rebase-merge":
          return ok("/fake/worktree/.git/rebase-merge\n")
        case "rev-parse --git-path rebase-apply":
          return ok("/fake/worktree/.git/rebase-apply\n")
        case "status --porcelain":
          return ok(" M packages/runner/src/actions/registry.ts\n")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })
    setRebaseExistsCheckerForTest(() => false)
    setRebaseConflictResolverForTest(async () => ({ status: "success", message: "noop", output: "" }))

    const result = await prepareAction(context({ baseBranch: "master" }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(result.message).toContain("worktree is dirty before rebase")
    expect(calls).toEqual([
      "rev-parse --git-path rebase-merge",
      "rev-parse --git-path rebase-apply",
      "status --porcelain",
    ])
    expect(calls).not.toContain("add .")
    expect(calls).not.toContain("commit -m Prepare rebase onto master")
    expect(output).toMatchObject({
      kind: "prepare",
      status: "failed",
      prepared: false,
      failureKind: "retry-safe",
    })
    expect(output.output).toContain("packages/runner/src/actions/registry.ts")
  })

  it("SuccessfulRebaseLeavesDirtyWorktree_FailsBeforeExecutorCleanup", async () => {
    const calls = installGit(async (_workDir, args, allCalls) => {
      switch (args.join(" ")) {
        case "rev-parse --git-path rebase-merge":
          return ok("/fake/worktree/.git/rebase-merge\n")
        case "rev-parse --git-path rebase-apply":
          return ok("/fake/worktree/.git/rebase-apply\n")
        case "status --porcelain":
          return ok(allCalls.filter((c) => c === "status --porcelain").length === 1
            ? ""
            : " M packages/web/src/pages/settings/ui/WorkflowProfilesSection.tsx\n?? openspec/changes/issue-116/\n")
        case "fetch origin master":
          return ok("")
        case "rev-parse origin/master":
          return ok("base123\n")
        case "rev-parse HEAD":
          return ok(allCalls.filter((c) => c === "rev-parse HEAD").length === 1 ? "before\n" : "after\n")
        case "rebase origin/master":
          return ok("Successfully rebased")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })
    setRebaseExistsCheckerForTest(() => false)

    const result = await prepareAction(context({ baseBranch: "master" }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(result.message).toContain("worktree remained dirty after rebase")
    expect(output).toMatchObject({
      kind: "prepare",
      status: "failed",
      prepared: false,
      preparedBaseSha: "base123",
      preparedHeadSha: "after",
      failureKind: "retry-safe",
    })
    expect(output.output).toContain("Prepare left a dirty worktree after rebase")
    expect(output.output).toContain("openspec/changes/issue-116")
  })

  it("RebaseConflicts_ResolverSucceeds_OutputsConflictAttemptsAndPreparedHead", async () => {
    const calls = installGit(async (_workDir, args) => {
      switch (args.join(" ")) {
        case "rev-parse --git-path rebase-merge":
          return ok("/fake/worktree/.git/rebase-merge\n")
        case "rev-parse --git-path rebase-apply":
          return ok("/fake/worktree/.git/rebase-apply\n")
        case "status --porcelain":
          return ok("")
        case "fetch origin master":
          return ok("")
        case "rev-parse origin/master":
          return ok("base123\n")
        case "rev-parse HEAD":
          return ok("after\n")
        case "rebase origin/master":
          return fail("CONFLICT (content): Merge conflict in packages/runner/src/actions/registry.ts")
        case "diff --name-only --diff-filter=U":
          return ok(resolverRan ? "" : "packages/runner/src/actions/registry.ts\n")
        case "merge-base origin/master HEAD":
          return ok("base123\n")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })
    let resolverRan = false
    setRebaseExistsCheckerForTest(() => false)
    setRebaseConflictResolverForTest(async (resolverContext) => {
      resolverRan = true
      expect(resolverContext.workDir).toBe("/fake/worktree")
      expect(resolverContext.workId).toBe("integrate:prepare.1-conflict-resolve-1")
      return {
        status: "success",
        message: "resolved",
        output: "agent staged resolved files",
      }
    })

    const result = await prepareAction(context({
      baseBranch: "master",
      maxConflictRetries: 1,
      conflictResolver: { with: {} },
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(calls).toEqual([
      "rev-parse --git-path rebase-merge",
      "rev-parse --git-path rebase-apply",
      "status --porcelain",
      "fetch origin master",
      "rev-parse origin/master",
      "rev-parse HEAD",
      "rebase origin/master",
      "diff --name-only --diff-filter=U",
      "rev-parse --git-path rebase-merge",
      "rev-parse --git-path rebase-apply",
      "diff --name-only --diff-filter=U",
      "rev-parse HEAD",
      "rev-parse origin/master",
      "merge-base origin/master HEAD",
      "rev-parse HEAD",
      "status --porcelain",
    ])
    expect(output).toMatchObject({
      kind: "prepare",
      status: "completed",
      baseBranch: "master",
      preparedBaseSha: "base123",
      preparedHeadSha: "after",
      prepared: true,
      conflicts: ["packages/runner/src/actions/registry.ts"],
      resolveAttempts: 1,
      failureKind: null,
    })
    expect(output.output).toContain("agent staged resolved files")
  })

  it("RebaseConflicts_ResolverFails_ReportsConflictFailureKindAndAbortsRebase", async () => {
    const calls = installGit(async (_workDir, args, allCalls) => {
      switch (args.join(" ")) {
        case "rev-parse --git-path rebase-merge":
          return ok("/fake/worktree/.git/rebase-merge\n")
        case "rev-parse --git-path rebase-apply":
          return ok("/fake/worktree/.git/rebase-apply\n")
        case "status --porcelain":
          return ok("")
        case "fetch origin master":
          return ok("")
        case "rev-parse origin/master":
          return ok("base123\n")
        case "rev-parse HEAD":
          return ok("before\n")
        case "rebase origin/master":
          return fail("CONFLICT (content): Merge conflict in packages/runner/src/actions/registry.ts")
        case "diff --name-only --diff-filter=U":
          return ok("packages/runner/src/actions/registry.ts\n")
        case "rebase --abort":
          return ok("")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })
    setRebaseExistsCheckerForTest(() => false)
    setRebaseConflictResolverForTest(async () => {
      return { status: "failure", message: "agent gave up", output: "agent could not resolve" }
    })

    const result = await prepareAction(context({
      baseBranch: "master",
      maxConflictRetries: 1,
      conflictResolver: { with: {} },
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(calls).toContain("rebase --abort")
    expect(calls.indexOf("rebase --abort")).toBeGreaterThan(calls.indexOf("rebase origin/master"))
    expect(output).toMatchObject({
      kind: "prepare",
      status: "failed",
      baseBranch: "master",
      preparedBaseSha: "base123",
      preparedHeadSha: null,
      prepared: false,
      conflicts: ["packages/runner/src/actions/registry.ts"],
      resolveAttempts: 1,
      failureKind: "conflict",
    })
    expect(output.output).toContain("agent could not resolve")
  })

  it("RebaseConflicts_NoResolverConfigured_ReportsConflictFailureKind", async () => {
    const calls = installGit(async (_workDir, args) => {
      switch (args.join(" ")) {
        case "rev-parse --git-path rebase-merge":
          return ok("/fake/worktree/.git/rebase-merge\n")
        case "rev-parse --git-path rebase-apply":
          return ok("/fake/worktree/.git/rebase-apply\n")
        case "status --porcelain":
          return ok("")
        case "fetch origin master":
          return ok("")
        case "rev-parse origin/master":
          return ok("base123\n")
        case "rev-parse HEAD":
          return ok("before\n")
        case "rebase origin/master":
          return fail("CONFLICT (content): Merge conflict in packages/runner/src/actions/registry.ts")
        case "diff --name-only --diff-filter=U":
          return ok("packages/runner/src/actions/registry.ts\n")
        case "rebase --abort":
          return ok("")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })
    setRebaseExistsCheckerForTest(() => false)

    const result = await prepareAction(context({ baseBranch: "master" }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(calls).toContain("rebase --abort")
    expect(output).toMatchObject({
      kind: "prepare",
      failureKind: "conflict",
      conflicts: ["packages/runner/src/actions/registry.ts"],
      prepared: false,
    })
  })

  it("FetchFails_ReportsRetrySafeFailureKind", async () => {
    installGit(async (_workDir, args) => {
      switch (args.join(" ")) {
        case "rev-parse --git-path rebase-merge":
          return ok("/fake/worktree/.git/rebase-merge\n")
        case "rev-parse --git-path rebase-apply":
          return ok("/fake/worktree/.git/rebase-apply\n")
        case "status --porcelain":
          return ok("")
        case "fetch origin master":
          return fail("fatal: could not resolve host")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })
    setRebaseExistsCheckerForTest(() => false)

    const result = await prepareAction(context({ baseBranch: "master" }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(output).toMatchObject({
      kind: "prepare",
      failureKind: "retry-safe",
      prepared: false,
    })
  })

  it("RebaseFailsWithNoConflictFiles_AbortsAndReportsRetrySafe", async () => {
    const calls = installGit(async (_workDir, args) => {
      switch (args.join(" ")) {
        case "rev-parse --git-path rebase-merge":
          return ok("/fake/worktree/.git/rebase-merge\n")
        case "rev-parse --git-path rebase-apply":
          return ok("/fake/worktree/.git/rebase-apply\n")
        case "status --porcelain":
          return ok("")
        case "fetch origin master":
          return ok("")
        case "rev-parse origin/master":
          return ok("base123\n")
        case "rev-parse HEAD":
          return ok("before\n")
        case "rebase origin/master":
          return fail("error: cannot rebase onto multiple branches")
        case "diff --name-only --diff-filter=U":
          return ok("")
        case "rebase --abort":
          return ok("")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })
    setRebaseExistsCheckerForTest(() => false)

    const result = await prepareAction(context({ baseBranch: "master" }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(calls).toContain("rebase --abort")
    expect(output).toMatchObject({
      kind: "prepare",
      failureKind: "retry-safe",
      prepared: false,
    })
  })

  it("StaleRebaseStateBeforePrepare_AbortsStaleRebaseBeforeFetch", async () => {
    const calls = installGit(async (_workDir, args) => {
      switch (args.join(" ")) {
        case "rev-parse --git-path rebase-merge":
          return ok("/fake/worktree/.git/rebase-merge\n")
        case "rev-parse --git-path rebase-apply":
          return ok("/fake/worktree/.git/rebase-apply\n")
        case "rebase --abort":
          return ok("")
        case "status --porcelain":
          return ok("")
        case "fetch origin master":
          return ok("")
        case "rev-parse origin/master":
          return ok("base123\n")
        case "rev-parse HEAD":
          return ok("after\n")
        case "rebase origin/master":
          return ok("Successfully rebased")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })
    setRebaseExistsCheckerForTest((path) => path === "/fake/worktree/.git/rebase-merge")

    const result = await prepareAction(context({ baseBranch: "master" }))

    expect(result.status).toBe("success")
    expect(calls.indexOf("rebase --abort")).toBeLessThan(calls.indexOf("fetch origin master"))
  })
})

function context(withOverrides: JsonObject = {}, variables: JsonObject = {}): ActionContext {
  return {
    workflowRunId: "workflow-1",
    workId: "integrate:prepare.1",
    workType: "task",
    stage: "integrate",
    title: "Prepare branch",
    uses: "mohist/prepare",
    with: withOverrides,
    variables: {
      project: { path: "/repo" },
      issue: { title: "SignalR realtime push", number: 82 },
      ...variables,
    },
    workDir: "/fake/worktree",
    issueNumber: 82,
    signal: new AbortController().signal,
  }
}

function ok(stdout: string) {
  return { success: true, stdout, stderr: "", exitCode: 0, combinedOutput: stdout.trim() }
}

function fail(stderr: string) {
  return { success: false, stdout: "", stderr, exitCode: 1, combinedOutput: stderr }
}
