import { afterEach, describe, expect, it } from "vitest"
import { pushAction, setMergeGitRunnerForTest } from "../src/actions/registry.js"
import type { ActionContext, JsonObject } from "../src/core/types.js"

afterEach(() => {
  setMergeGitRunnerForTest(null)
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

  it("NonFastForwardWithRebaseConflict_ReturnsStructuredFailure", async () => {
    const calls: string[] = []
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
    expect(result.message.toLowerCase()).toContain("remote branch advanced")
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
    })
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
