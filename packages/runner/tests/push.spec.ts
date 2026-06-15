import { afterEach, describe, expect, it } from "vitest"
import { pushAction, setMergeConflictResolverForTest, setMergeGitRunnerForTest } from "../src/actions/registry.js"
import type { ActionContext, JsonObject } from "../src/core/types.js"

afterEach(() => {
  setMergeGitRunnerForTest(null)
  setMergeConflictResolverForTest(null)
})

describe("mohist/push", () => {
  it("DefaultRemoteAndRepositoryBaseBranch_PushesBaseBranchToOrigin", async () => {
    const calls: string[] = []
    setMergeGitRunnerForTest(async (_workDir, args) => {
      calls.push(args.join(" "))
      switch (args.join(" ")) {
        case "push origin main":
          return ok("To origin\n   abc123..def456  main -> main")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const result = await pushAction(context({}, { repository: { baseBranch: "main" } }))

    expect(result.status).toBe("success")
    expect(calls).toContain("push origin main")
    expect(calls).not.toContain("rev-parse --abbrev-ref HEAD")
  })

  it("ExplicitRemoteAndTarget_PushesConfiguredRef", async () => {
    const calls: string[] = []
    setMergeGitRunnerForTest(async (_workDir, args) => {
      calls.push(args.join(" "))
      switch (args.join(" ")) {
        case "push upstream master":
          return ok("To upstream\n   abc123..def456  master -> master")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const result = await pushAction(context({ remote: "upstream", target: "master" }))

    expect(result.status).toBe("success")
    expect(calls).toEqual(["push upstream master"])
  })

  it("SuccessfulPush_ReturnsSuccess", async () => {
    setMergeGitRunnerForTest(async (_workDir, args) => {
      if (args.join(" ") === "push origin main") {
        return ok("To origin\n   abc123..def456  main -> main")
      }
      return fail(`unexpected git call: ${args.join(" ")}`)
    })

    const result = await pushAction(context({ target: "main" }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(result.message).toBe("Push completed")
    expect(output).toMatchObject({ kind: "push", remote: "origin", target: "main" })
  })

  it("NonFastForwardPush_RebasesOntoRemoteAndRetries", async () => {
    const calls: string[] = []
    let pushAttempts = 0
    setMergeGitRunnerForTest(async (_workDir, args) => {
      calls.push(args.join(" "))
      switch (args.join(" ")) {
        case "push origin main":
          pushAttempts++
          if (pushAttempts === 2) return ok("To origin\n   abc123..def456  main -> main")
          return fail("! [rejected]        main -> main (non-fast-forward)\nerror: failed to push some refs to 'origin'")
        case "fetch origin main":
          return ok("From origin\n * branch main -> FETCH_HEAD")
        case "checkout main":
          return ok("Switched to branch 'main'")
        case "rebase origin/main":
          return ok("Successfully rebased and updated refs/heads/main.")
        case "diff --check":
          return ok("")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const result = await pushAction(context({ target: "main" }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(result.message).toBe("Push completed after rebasing onto remote")
    expect(calls).toEqual([
      "push origin main",
      "fetch origin main",
      "checkout main",
      "rebase origin/main",
      "diff --check",
      "push origin main",
    ])
    expect(output).toMatchObject({ kind: "push", remote: "origin", target: "main" })
    expect(output.status).toBe("remote_advanced_rebased_and_pushed")
    expect(output.output).toContain("Successfully rebased")
  })

  it("NonFastForwardWithRebaseConflict_UsesDefaultResolver", async () => {
    const calls: string[] = []
    let resolverRan = false
    setMergeConflictResolverForTest(async (resolverContext) => {
      resolverRan = true
      expect(resolverContext.workId).toBe("integrate:push.1-push-rebase-resolve-1")
      expect(resolverContext.title).toBe("Resolve push rebase conflicts")
      expect(String(resolverContext.with?.prompt)).toContain("file.txt")
      return { status: "failure", message: "resolver failed", output: "resolver failed", exitCode: 1 }
    })
    setMergeGitRunnerForTest(async (_workDir, args) => {
      calls.push(args.join(" "))
      switch (args.join(" ")) {
        case "push origin main":
          return fail("! [rejected]        main -> main (non-fast-forward)")
        case "fetch origin main":
          return ok("From origin\n * branch main -> FETCH_HEAD")
        case "checkout main":
          return ok("Switched to branch 'main'")
        case "rebase origin/main":
          return fail("CONFLICT (content): Merge conflict in file.txt")
        case "diff --name-only --diff-filter=U":
          return ok("file.txt\n")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const result = await pushAction(context({ target: "main" }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(resolverRan).toBe(true)
    expect(calls).toEqual([
      "push origin main",
      "fetch origin main",
      "checkout main",
      "rebase origin/main",
      "diff --name-only --diff-filter=U",
    ])
    expect(output).toMatchObject({
      kind: "push",
      remote: "origin",
      target: "main",
      status: "remote_advanced_rebase_conflict",
      conflictFiles: ["file.txt"],
      resolveAttempts: 1,
    })
    expect(output.output).toContain("resolver failed")
  })

  it("NonFastForwardWithConfiguredConflictResolver_ResolvesRebaseThenPushes", async () => {
    const calls: string[] = []
    let resolverRan = false
    let pushAttempts = 0
    setMergeConflictResolverForTest(async (resolverContext) => {
      resolverRan = true
      expect(resolverContext.workId).toBe("integrate:push.1-push-rebase-resolve-1")
      expect(resolverContext.title).toBe("Resolve push rebase conflicts")
      expect(String(resolverContext.with?.prompt)).toContain("git rebase origin/main")
      expect(String(resolverContext.with?.prompt)).toContain("file.txt")
      return { status: "success", message: "resolved", output: "resolver completed" }
    })
    setMergeGitRunnerForTest(async (_workDir, args) => {
      calls.push(args.join(" "))
      switch (args.join(" ")) {
        case "push origin main":
          pushAttempts++
          if (pushAttempts === 2) return ok("To origin\n   abc123..def456  main -> main")
          return fail("! [rejected]        main -> main (non-fast-forward)")
        case "fetch origin main":
          return ok("From origin\n * branch main -> FETCH_HEAD")
        case "checkout main":
          return ok("Switched to branch 'main'")
        case "rebase origin/main":
          return fail("CONFLICT (content): Merge conflict in file.txt")
        case "diff --name-only --diff-filter=U":
          return ok(resolverRan ? "" : "file.txt\n")
        case "rev-parse --git-path rebase-merge":
          return ok(".git/rebase-merge\n")
        case "rev-parse --git-path rebase-apply":
          return ok(".git/rebase-apply\n")
        case "diff --check":
          return ok("")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const result = await pushAction(context({
      target: "main",
      conflictResolver: {
        title: "Resolve push rebase conflicts",
        with: {},
      },
      maxConflictRetries: 1,
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(output).toMatchObject({
      kind: "push",
      remote: "origin",
      target: "main",
      status: "remote_advanced_rebased_and_pushed",
      conflictFiles: ["file.txt"],
      resolveAttempts: 1,
    })
    expect(output.output).toContain("resolver completed")
  })

  it("MissingTargetAndRepositoryBaseBranch_ReturnsFailureWithoutInvokingPush", async () => {
    const calls: string[] = []
    setMergeGitRunnerForTest(async (_workDir, args) => {
      calls.push(args.join(" "))
      return fail(`unexpected git call: ${args.join(" ")}`)
    })

    const result = await pushAction(context())

    expect(result.status).toBe("failure")
    expect(result.message).toContain("requires target or repository.baseBranch")
    expect(calls).not.toContain("push origin main")
    expect(calls).toEqual([])
  })
})

function context(withOverrides: JsonObject = {}, variables: JsonObject = {}): ActionContext {
  return {
    workflowRunId: "workflow-1",
    workId: "integrate:push.1",
    workType: "task",
    stage: "integrate",
    title: "Push branch",
    uses: "mohist/push",
    with: withOverrides,
    variables: {
      project: { path: "/repo" },
      issue: { title: "Push action issue", number: 99 },
      ...variables,
    },
    workDir: "/fake/worktree",
    issueNumber: 99,
    signal: new AbortController().signal,
  }
}

function ok(stdout: string) {
  return { success: true, stdout, stderr: "", exitCode: 0, combinedOutput: stdout.trim() }
}

function fail(stderr: string) {
  return { success: false, stdout: "", stderr, exitCode: 1, combinedOutput: stderr }
}
