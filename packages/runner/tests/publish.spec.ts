import { afterEach, describe, expect, it } from "vitest"
import { publishAction, setDeliveryExistsCheckerForTest, setDeliveryGitRunnerForTest } from "../src/actions/registry.js"
import type { ActionContext, JsonObject } from "../src/core/types.js"

type GitResponse = { success: boolean; stdout: string; stderr: string; exitCode: number; combinedOutput: string }

afterEach(() => {
  setDeliveryGitRunnerForTest(null)
  setDeliveryExistsCheckerForTest(null)
})

function installGit(respond: (workDir: string, args: string[], calls: string[]) => GitResponse | Promise<GitResponse>) {
  const calls: string[] = []
  const respondAndTrack = async (workDir: string, args: string[]) => {
    calls.push(args.join(" "))
    const result = await respond(workDir, args, calls)
    if (!result.success && result.combinedOutput.startsWith("unexpected git call: ")) {
      const fallback = defaultPublishGitResponse(args)
      if (fallback) return fallback
    }
    return result
  }
  setDeliveryGitRunnerForTest(respondAndTrack)
  return calls
}

function defaultPublishGitResponse(args: string[]) {
  const command = args.join(" ")
  if (command === "fetch origin master" || command === "fetch origin main") return ok("")
  if (command === "rev-parse origin/master" || command === "rev-parse origin/main") return ok("pre-publish-sha\n")
  if (command === "merge --ff-only origin/master" || command === "merge --ff-only origin/main") return ok("Already up to date.")
  if (command.startsWith("merge-base --is-ancestor origin/master ") || command.startsWith("merge-base --is-ancestor origin/main ")) return ok("")
  return null
}

describe("mohist/publish", () => {
  it("PublishReady_OperatesInProjectPath_SquashLandsAndPushes", async () => {
    const calls = installGit(async (workDir, args) => {
      expect(workDir).toBe("/repo")
      switch (args.join(" ")) {
        case "rev-parse master":
          return ok("pre-publish-sha\n")
        case "status --porcelain":
          return ok("")
        case "checkout master":
          return ok("Switched to branch 'master'")
        case "merge --squash mo/issue-82":
          return ok("Squash commit -- not updating HEAD")
        case "commit -m SignalR realtime push (#82) -m mo/issue-82 into master":
          return ok("[master abc123] SignalR realtime push (#82)")
        case "rev-parse HEAD":
          return ok("abc123\n")
        case "push origin master":
          return ok("To https://example.com/repo.git\n   pre-publish-sha..abc123  master -> master")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const result = await publishAction(context({
      source: "mo/issue-82",
      target: "master",
      message: "Complete issue #82",
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(calls).toEqual([
      "rev-parse master",
      "status --porcelain",
      "fetch origin master",
      "rev-parse origin/master",
      "checkout master",
      "merge --ff-only origin/master",
      "merge-base --is-ancestor origin/master mo/issue-82",
      "merge --squash mo/issue-82",
      "commit -m SignalR realtime push (#82) -m mo/issue-82 into master",
      "rev-parse HEAD",
      "push origin master",
    ])
    expect(output).toMatchObject({
      kind: "publish",
      status: "completed",
      source: "mo/issue-82",
      target: "master",
      workDir: "/repo",
      landedCommit: "abc123",
      pushed: true,
      failureKind: null,
    })
    expect(output).not.toHaveProperty("resolveAttempts")
  })

  it("StaleLocalTarget_FastForwardsToRemoteBeforeSquashAndPush", async () => {
    const calls = installGit(async (workDir, args) => {
      expect(workDir).toBe("/repo")
      switch (args.join(" ")) {
        case "rev-parse master":
          return ok("stale-local-sha\n")
        case "status --porcelain":
          return ok("")
        case "fetch origin master":
          return ok("From https://example.com/repo.git\n * branch            master     -> FETCH_HEAD")
        case "rev-parse origin/master":
          return ok("remote-head-sha\n")
        case "checkout master":
          return ok("Switched to branch 'master'")
        case "merge --ff-only origin/master":
          return ok("Updating stale-local-sha..remote-head-sha\nFast-forward")
        case "merge-base --is-ancestor origin/master mo/issue-82":
          return ok("")
        case "merge --squash mo/issue-82":
          return ok("Squash commit -- not updating HEAD")
        case "commit -m SignalR realtime push (#82) -m mo/issue-82 into master":
          return ok("[master abc123] SignalR realtime push (#82)")
        case "rev-parse HEAD":
          return ok("abc123\n")
        case "push origin master":
          return ok("To https://example.com/repo.git\n   remote-head-sha..abc123  master -> master")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const result = await publishAction(context({
      source: "mo/issue-82",
      target: "master",
      message: "Complete issue #82",
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(calls.indexOf("merge --ff-only origin/master")).toBeLessThan(calls.indexOf("merge --squash mo/issue-82"))
    expect(output).toMatchObject({
      kind: "publish",
      status: "completed",
      landedCommit: "abc123",
      pushed: true,
      failureKind: null,
    })
  })

  it("SourceBehindRemoteTarget_StopsBeforeSquashAndReportsBaseMoved", async () => {
    const calls = installGit(async (workDir, args) => {
      expect(workDir).toBe("/repo")
      switch (args.join(" ")) {
        case "rev-parse master":
          return ok("stale-local-sha\n")
        case "status --porcelain":
          return ok("")
        case "fetch origin master":
          return ok("")
        case "rev-parse origin/master":
          return ok("remote-head-sha\n")
        case "checkout master":
          return ok("Switched to branch 'master'")
        case "merge --ff-only origin/master":
          return ok("Updating stale-local-sha..remote-head-sha\nFast-forward")
        case "merge-base --is-ancestor origin/master mo/issue-82":
          return fail("")
        case "reset --hard remote-head-sha":
          return ok("HEAD is now at remote-head-sha")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const result = await publishAction(context({
      source: "mo/issue-82",
      target: "master",
      message: "Complete issue #82",
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(calls).toContain("reset --hard remote-head-sha")
    expect(calls).not.toContain("merge --squash mo/issue-82")
    expect(calls).not.toContain("push origin master")
    expect(output).toMatchObject({
      kind: "publish",
      status: "failed",
      landedCommit: null,
      pushed: false,
      failureKind: "base-moved",
    })
    expect(output.output).toContain("Re-run prepare")
  })

  it("SquashConflicts_BaseMoved_ReportsBaseMovedFailureKindAndRestoresBase", async () => {
    const calls = installGit(async (workDir, args) => {
      expect(workDir).toBe("/repo")
      switch (args.join(" ")) {
        case "rev-parse master":
          return ok("pre-publish-sha\n")
        case "status --porcelain":
          return ok("")
        case "checkout master":
          return ok("Switched to branch 'master'")
        case "merge --squash mo/issue-82":
          return fail("CONFLICT (content): Merge conflict in specs/web-ui/spec.md")
        case "merge --abort":
          return ok("")
        case "reset --hard pre-publish-sha":
          return ok("HEAD is now at pre-publish-sha")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const result = await publishAction(context({
      source: "mo/issue-82",
      target: "master",
      message: "Complete issue #82",
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(calls).toContain("merge --abort")
    expect(calls).toContain("reset --hard pre-publish-sha")
    expect(calls).not.toContain("commit -m SignalR realtime push (#82) -m mo/issue-82 into master")
    expect(calls).not.toContain("push origin master")
    expect(output).toMatchObject({
      kind: "publish",
      status: "failed",
      source: "mo/issue-82",
      target: "master",
      workDir: "/repo",
      landedCommit: null,
      pushed: false,
      failureKind: "base-moved",
    })
  })

  it("PushRejectedNonFastForward_ReportsBaseMovedAndRestoresBase", async () => {
    const calls = installGit(async (workDir, args) => {
      expect(workDir).toBe("/repo")
      switch (args.join(" ")) {
        case "rev-parse master":
          return ok("pre-publish-sha\n")
        case "status --porcelain":
          return ok("")
        case "checkout master":
          return ok("Switched to branch 'master'")
        case "merge --squash mo/issue-82":
          return ok("Squash commit -- not updating HEAD")
        case "commit -m SignalR realtime push (#82) -m mo/issue-82 into master":
          return ok("[master abc123] SignalR realtime push (#82)")
        case "rev-parse HEAD":
          return ok("abc123\n")
        case "push origin master":
          return fail("To https://example.com/repo.git\n ! [rejected]        master -> master (non-fast-forward)\nerror: failed to push some refs")
        case "reset --hard pre-publish-sha":
          return ok("HEAD is now at pre-publish-sha")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const result = await publishAction(context({
      source: "mo/issue-82",
      target: "master",
      message: "Complete issue #82",
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(calls).not.toContain("merge --abort")
    expect(calls).toContain("reset --hard pre-publish-sha")
    expect(calls.indexOf("reset --hard pre-publish-sha")).toBeGreaterThan(calls.indexOf("push origin master"))
    expect(output).toMatchObject({
      kind: "publish",
      status: "failed",
      source: "mo/issue-82",
      target: "master",
      landedCommit: "abc123",
      pushed: false,
      failureKind: "base-moved",
    })
  })

  it("PushFailsTransient_ReportsRetrySafeAndRestoresBase", async () => {
    const calls = installGit(async (workDir, args) => {
      expect(workDir).toBe("/repo")
      switch (args.join(" ")) {
        case "rev-parse master":
          return ok("pre-publish-sha\n")
        case "status --porcelain":
          return ok("")
        case "checkout master":
          return ok("Switched to branch 'master'")
        case "merge --squash mo/issue-82":
          return ok("Squash commit -- not updating HEAD")
        case "commit -m SignalR realtime push (#82) -m mo/issue-82 into master":
          return ok("[master abc123] SignalR realtime push (#82)")
        case "rev-parse HEAD":
          return ok("abc123\n")
        case "push origin master":
          return fail("fatal: unable to access: could not resolve host")
        case "reset --hard pre-publish-sha":
          return ok("HEAD is now at pre-publish-sha")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const result = await publishAction(context({
      source: "mo/issue-82",
      target: "master",
      message: "Complete issue #82",
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(calls).toContain("reset --hard pre-publish-sha")
    expect(output).toMatchObject({
      kind: "publish",
      landedCommit: "abc123",
      pushed: false,
      failureKind: "retry-safe",
    })
  })

  it("CommitFails_ReportsRetrySafeAndRestoresBase", async () => {
    const calls = installGit(async (workDir, args) => {
      expect(workDir).toBe("/repo")
      switch (args.join(" ")) {
        case "rev-parse master":
          return ok("pre-publish-sha\n")
        case "status --porcelain":
          return ok("")
        case "checkout master":
          return ok("Switched to branch 'master'")
        case "merge --squash mo/issue-82":
          return ok("Squash commit -- not updating HEAD")
        case "commit -m SignalR realtime push (#82) -m mo/issue-82 into master":
          return fail("error: unable to commit without author identity")
        case "reset --hard pre-publish-sha":
          return ok("HEAD is now at pre-publish-sha")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const result = await publishAction(context({
      source: "mo/issue-82",
      target: "master",
      message: "Complete issue #82",
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(calls).toContain("reset --hard pre-publish-sha")
    expect(output).toMatchObject({
      kind: "publish",
      landedCommit: null,
      pushed: false,
      failureKind: "retry-safe",
    })
  })

  it("DirtyWorkingTreeBeforePublish_ReportsRetrySafeWithoutAnyMergeOrPush", async () => {
    const calls = installGit(async (workDir, args) => {
      expect(workDir).toBe("/repo")
      switch (args.join(" ")) {
        case "rev-parse master":
          return ok("pre-publish-sha\n")
        case "status --porcelain":
          return ok(" M packages/server/src/Server.cs\n")
        case "reset --hard pre-publish-sha":
          return ok("HEAD is now at pre-publish-sha")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const result = await publishAction(context({
      source: "mo/issue-82",
      target: "master",
      message: "Complete issue #82",
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(calls).not.toContain("merge --squash mo/issue-82")
    expect(calls).not.toContain("push origin master")
    expect(output).toMatchObject({
      kind: "publish",
      landedCommit: null,
      pushed: false,
      failureKind: "retry-safe",
    })
    expect(output.output).toContain("dirty working tree")
  })

  it("CheckoutFailsAfterDirtyTree_ReportsRetrySafeAndRestoresBase", async () => {
    const calls = installGit(async (workDir, args) => {
      expect(workDir).toBe("/repo")
      switch (args.join(" ")) {
        case "rev-parse master":
          return ok("pre-publish-sha\n")
        case "status --porcelain":
          return ok("")
        case "checkout master":
          return fail("error: Your local changes to the following files would be overwritten by checkout:\n\tpackages/server/src/Server.cs\nPlease commit your changes or stash them before you switch branches.")
        case "reset --hard pre-publish-sha":
          return ok("HEAD is now at pre-publish-sha")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const result = await publishAction(context({
      source: "mo/issue-82",
      target: "master",
      message: "Complete issue #82",
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(calls).toContain("reset --hard pre-publish-sha")
    expect(calls).not.toContain("merge --squash mo/issue-82")
    expect(calls).not.toContain("push origin master")
    expect(output).toMatchObject({
      kind: "publish",
      landedCommit: null,
      pushed: false,
      failureKind: "retry-safe",
    })
  })

  it("CheckoutFailsWithStaleRebaseMerge_AbortsRebaseNotMerge", async () => {
    const calls = installGit(async (workDir, args) => {
      expect(workDir).toBe("/repo")
      switch (args.join(" ")) {
        case "rev-parse master":
          return ok("pre-publish-sha\n")
        case "status --porcelain":
          return ok("")
        case "checkout master":
          return fail("error: pathspec 'master' did not match any file(s) known to git")
        case "rev-parse --git-path rebase-merge":
          return ok(".git/rebase-merge\n")
        case "rev-parse --git-path rebase-apply":
          return ok(".git/rebase-apply\n")
        case "rev-parse --git-path MERGE_HEAD":
          return ok(".git/MERGE_HEAD\n")
        case "rebase --abort":
          return ok("")
        case "reset --hard pre-publish-sha":
          return ok("HEAD is now at pre-publish-sha")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })
    setDeliveryExistsCheckerForTest((path) => path === "/repo/.git/rebase-merge")

    const result = await publishAction(context({
      source: "mo/issue-82",
      target: "master",
      message: "Complete issue #82",
    }))

    expect(result.status).toBe("failure")
    expect(calls).toContain("rebase --abort")
    expect(calls).not.toContain("merge --abort")
    expect(calls).toContain("reset --hard pre-publish-sha")
  })

  it("CheckoutFailsWithPendingMerge_AbortsMergeAndRestoresBase", async () => {
    const calls = installGit(async (workDir, args) => {
      expect(workDir).toBe("/repo")
      switch (args.join(" ")) {
        case "rev-parse master":
          return ok("pre-publish-sha\n")
        case "status --porcelain":
          return ok("")
        case "checkout master":
          return fail("error: Your local changes would be overwritten")
        case "rev-parse --git-path rebase-merge":
          return ok(".git/rebase-merge\n")
        case "rev-parse --git-path rebase-apply":
          return ok(".git/rebase-apply\n")
        case "rev-parse --git-path MERGE_HEAD":
          return ok(".git/MERGE_HEAD\n")
        case "merge --abort":
          return ok("")
        case "reset --hard pre-publish-sha":
          return ok("HEAD is now at pre-publish-sha")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })
    setDeliveryExistsCheckerForTest((path) => path === "/repo/.git/MERGE_HEAD")

    const result = await publishAction(context({
      source: "mo/issue-82",
      target: "master",
      message: "Complete issue #82",
    }))

    expect(result.status).toBe("failure")
    expect(calls).toContain("merge --abort")
    expect(calls).not.toContain("rebase --abort")
    expect(calls).toContain("reset --hard pre-publish-sha")
  })

  it("PushRejectedAsNonFastForward_StandardGitShape_ClassifiesAsBaseMoved", async () => {
    const calls = installGit(async (workDir, args) => {
      expect(workDir).toBe("/repo")
      switch (args.join(" ")) {
        case "rev-parse master":
          return ok("pre-publish-sha\n")
        case "status --porcelain":
          return ok("")
        case "checkout master":
          return ok("Switched to branch 'master'")
        case "merge --squash mo/issue-82":
          return ok("Squash commit -- not updating HEAD")
        case "commit -m SignalR realtime push (#82) -m mo/issue-82 into master":
          return ok("[master abc123] SignalR realtime push (#82)")
        case "rev-parse HEAD":
          return ok("abc123\n")
        case "push origin master":
          return fail("To https://example.com/repo.git\n ! [rejected]        master -> master (non-fast-forward)\nerror: failed to push some refs")
        case "reset --hard pre-publish-sha":
          return ok("HEAD is now at pre-publish-sha")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const result = await publishAction(context({
      source: "mo/issue-82",
      target: "master",
      message: "Complete issue #82",
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(calls).toContain("reset --hard pre-publish-sha")
    expect(output).toMatchObject({
      kind: "publish",
      landedCommit: "abc123",
      pushed: false,
      failureKind: "base-moved",
    })
  })

  it("PushRejectedTransientAuthError_DoesNotMisclassifyAsBaseMoved", async () => {
    const calls = installGit(async (workDir, args) => {
      expect(workDir).toBe("/repo")
      switch (args.join(" ")) {
        case "rev-parse master":
          return ok("pre-publish-sha\n")
        case "status --porcelain":
          return ok("")
        case "checkout master":
          return ok("Switched to branch 'master'")
        case "merge --squash mo/issue-82":
          return ok("Squash commit -- not updating HEAD")
        case "commit -m SignalR realtime push (#82) -m mo/issue-82 into master":
          return ok("[master abc123] SignalR realtime push (#82)")
        case "rev-parse HEAD":
          return ok("abc123\n")
        case "push origin master":
          return fail("fatal: could not read Username for 'https://example.com': terminal prompts disabled\nfatal: Authentication failed for 'https://example.com/repo.git/'")
        case "reset --hard pre-publish-sha":
          return ok("HEAD is now at pre-publish-sha")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const result = await publishAction(context({
      source: "mo/issue-82",
      target: "master",
      message: "Complete issue #82",
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(calls).toContain("reset --hard pre-publish-sha")
    expect(output).toMatchObject({
      kind: "publish",
      landedCommit: "abc123",
      pushed: false,
      failureKind: "retry-safe",
    })
  })

  it("DirtyWorkingTree_ReportsRetrySafeAndIncludesDiscardedFilesInOutput", async () => {
    const calls = installGit(async (workDir, args) => {
      expect(workDir).toBe("/repo")
      switch (args.join(" ")) {
        case "rev-parse master":
          return ok("pre-publish-sha\n")
        case "status --porcelain":
          return ok(" M packages/server/src/Server.cs\n?? notes.txt\n")
        case "reset --hard pre-publish-sha":
          return ok("HEAD is now at pre-publish-sha")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const result = await publishAction(context({
      source: "mo/issue-82",
      target: "master",
      message: "Complete issue #82",
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(calls).not.toContain("merge --squash mo/issue-82")
    expect(calls).not.toContain("push origin master")
    expect(output).toMatchObject({
      kind: "publish",
      landedCommit: null,
      pushed: false,
      failureKind: "retry-safe",
    })
    expect(output.output).toContain("Destructive 'git reset --hard pre-publish-sha'")
    expect(output.output).toContain("M packages/server/src/Server.cs")
    expect(output.output).toContain("?? notes.txt")
  })

  it("ReadsTargetBranchFromProjectVariables_WhenTargetUnset", async () => {
    const calls = installGit(async (workDir, args) => {
      expect(workDir).toBe("/repo")
      switch (args.join(" ")) {
        case "rev-parse main":
          return ok("pre-publish-sha\n")
        case "status --porcelain":
          return ok("")
        case "checkout main":
          return ok("Switched to branch 'main'")
        case "merge --squash mo/issue-82":
          return ok("Squash commit -- not updating HEAD")
        case "commit -m SignalR realtime push (#82) -m mo/issue-82 into main":
          return ok("[main def456] SignalR realtime push (#82)")
        case "rev-parse HEAD":
          return ok("def456\n")
        case "push origin main":
          return ok("To https://example.com/repo.git\n   pre-publish-sha..def456  main -> main")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const result = await publishAction(context(
      { source: "mo/issue-82", message: "Complete issue #82" },
      { project: { path: "/repo", baseBranch: "main" } },
    ))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(calls).toContain("checkout main")
    expect(calls).toContain("push origin main")
    expect(output).toMatchObject({
      kind: "publish",
      target: "main",
      landedCommit: "def456",
      pushed: true,
    })
  })

  it("Publish_SeesPreparedRefFromSharedWorktreeRefs", async () => {
    const calls = installGit(async (workDir, args) => {
      expect(workDir).toBe("/repo")
      switch (args.join(" ")) {
        case "rev-parse master":
          return ok("pre-publish-sha\n")
        case "status --porcelain":
          return ok("")
        case "checkout master":
          return ok("Switched to branch 'master'")
        case "merge --squash mo/issue-82":
          return ok("Squash commit -- not updating HEAD")
        case "commit -m SignalR realtime push (#82) -m mo/issue-82 into master":
          return ok("[master abc123] SignalR realtime push (#82)")
        case "rev-parse HEAD":
          return ok("abc123\n")
        case "push origin master":
          return ok("To https://example.com/repo.git\n   pre-publish-sha..abc123  master -> master")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const result = await publishAction(context({
      source: "mo/issue-82",
      target: "master",
      message: "Complete issue #82",
    }))

    expect(result.status).toBe("success")
    expect(calls).toContain("merge --squash mo/issue-82")
  })
})

function context(withOverrides: JsonObject = {}, variables: JsonObject = {}): ActionContext {
  return {
    workflowRunId: "workflow-1",
    workId: "integrate:publish.1",
    workType: "task",
    stage: "integrate",
    title: "Publish changes",
    uses: "mohist/publish",
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
