import { afterEach, describe, expect, it } from "vitest"
import { mergeAction, mergeReadyAction, setMergeConflictResolverForTest, setMergeGitRunnerForTest } from "../src/actions/registry.js"
import type { ActionContext, JsonObject } from "../src/core/types.js"

afterEach(() => {
  setMergeGitRunnerForTest(null)
  setMergeConflictResolverForTest(null)
})

describe("mohist/merge", () => {
  it("SourceDirty_FailsWithPhaseSourceCleanup_BeforeAnyFetchOrRebase", async () => {
    const calls: string[] = []
    setMergeGitRunnerForTest(async (_workDir, args) => {
      calls.push(args.join(" "))
      switch (args.join(" ")) {
        case "status --porcelain":
          return ok(" M packages/runner/src/actions/registry.ts\n?? untracked.ts\n")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const result = await mergeAction(context({
      source: "mo/issue-112",
      target: "master",
      strategy: "squash",
      push: true,
      remote: "origin",
      message: "Complete issue #112",
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(output.phase).toBe("source-cleanup")
    expect(output.dirty).toEqual({
      staged: [],
      unstaged: ["packages/runner/src/actions/registry.ts"],
      untracked: ["untracked.ts"],
    })
    expect(calls).toEqual(["status --porcelain"])
    expect(calls).not.toContain("fetch origin master")
    expect(calls).not.toContain("rebase origin/master")
    expect(calls).not.toContain("push")
  })

  it("FetchFailure_FailsWithPhaseFetch_StructuredEvidence", async () => {
    setMergeGitRunnerForTest(async (_workDir, args) => {
      switch (args.join(" ")) {
        case "status --porcelain":
          return ok("")
        case "fetch origin master":
          return fail("fatal: could not resolve host origin\n")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const result = await mergeAction(context({
      source: "mo/issue-112",
      target: "master",
      strategy: "squash",
      push: true,
      remote: "origin",
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(output.phase).toBe("fetch")
    expect(output.message).toContain("Fetch from 'origin' failed")
    expect(output.output).toContain("could not resolve host origin")
  })

  it("CleanRebase_ProceedsToLandingPush_VerifiesRemoteRef", async () => {
    const calls: string[] = []
    let headCalls = 0
    setMergeGitRunnerForTest(async (_workDir, args) => {
      calls.push(args.join(" "))
      switch (args.join(" ")) {
        case "status --porcelain":
          return ok("")
        case "fetch origin master":
          return ok("From https://example.com/repo\n * branch            master     -> FETCH_HEAD")
        case "rev-parse origin/master":
          return ok("base-sha\n")
        case "checkout mo/issue-112":
          return ok("Switched to branch 'mo/issue-112'")
        case "checkout mo/issue-112":
          return ok("Switched to branch 'mo/issue-112'")
        case "rebase origin/master":
          return ok("Successfully rebased and updated refs/heads/mo/issue-112.")
        case "rev-parse HEAD":
          headCalls++
          return ok(headCalls === 1 ? "rebased-sha\n" : "landing-sha\n")
        case "checkout --detach base-sha":
          return ok("HEAD is now at base-sha base commit")
        case "merge --squash mo/issue-112":
          return ok("Squash commit -- not updating HEAD")
        case "log --format=* %s base-sha..mo/issue-112":
          return ok("* T-002 commit")
        case "commit -m SignalR push (#112) -m * T-002 commit":
          return ok("[detached HEAD landing-sha] SignalR push (#112)")
        case "log -1 --format=%P landing-sha":
          return ok("base-sha\n")
        case "push origin landing-sha:refs/heads/master":
          return ok("To https://example.com/repo\n   base-sha..landing-sha  master -> master")
        case "ls-remote origin refs/heads/master":
          return ok("landing-sha\trefs/heads/master\n")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const result = await mergeAction(context({
      source: "mo/issue-112",
      target: "master",
      strategy: "squash",
      push: true,
      remote: "origin",
      message: "Complete issue #112",
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(output.phase).toBeUndefined()
    expect(output).toMatchObject({
      kind: "merge",
      source: "mo/issue-112",
      target: "master",
      remote: "origin",
      strategy: "squash",
      pushEnabled: true,
      baseSha: "base-sha",
      rebasedSha: "rebased-sha",
      landingSha: "landing-sha",
      remoteRef: "landing-sha",
      pushRetryAttempts: 1,
      lastRemoteSha: "base-sha",
    })
    expect(calls).toContain("fetch origin master")
    expect(calls).toContain("rebase origin/master")
    expect(calls).toContain("push origin landing-sha:refs/heads/master")
    expect(calls).toContain("ls-remote origin refs/heads/master")
  })

  it("RebaseConflict_InvokesAgentResolver_ResolvesAndContinues", async () => {
    const calls: string[] = []
    let resolverRan = false
    let headCalls = 0
    setMergeConflictResolverForTest(async (resolverContext) => {
      resolverRan = true
      expect(resolverContext.workDir).toBe("/repo")
      expect(resolverContext.workId).toBe("integrate:merge.1-conflict-resolve-1")
      expect(String(resolverContext.with?.prompt)).toContain("Complete Git Rebase Conflict Resolution (attempt 1)")
      expect(String(resolverContext.with?.prompt)).toContain("packages/registry.ts")
      return {
        status: "success",
        message: "rebase conflicts resolved",
        output: "agent resolved rebase conflicts",
      }
    })
    setMergeGitRunnerForTest(async (_workDir, args) => {
      calls.push(args.join(" "))
      switch (args.join(" ")) {
        case "status --porcelain":
          return ok("")
        case "fetch origin master":
          return ok("From https://example.com/repo")
        case "rev-parse origin/master":
          return ok("base-sha\n")
        case "checkout mo/issue-112":
          return ok("Switched to branch 'mo/issue-112'")
        case "rebase origin/master":
          return fail("CONFLICT (content): Merge conflict in packages/registry.ts")
        case "diff --name-only --diff-filter=U":
          return ok(resolverRan ? "" : "packages/registry.ts\n")
        case "rev-parse --git-path rebase-merge":
          return ok("/repo/.git/rebase-merge\n")
        case "rev-parse --git-path rebase-apply":
          return ok("/repo/.git/rebase-apply\n")
        case "rev-parse --git-path MERGE_HEAD":
          return ok("/repo/.git/MERGE_HEAD\n")
        case "rev-parse HEAD":
          headCalls++
          return ok(headCalls === 1 ? "rebased-sha\n" : "landing-sha\n")
        case "checkout --detach base-sha":
          return ok("HEAD is now at base-sha")
        case "merge --squash mo/issue-112":
          return ok("Squash commit -- not updating HEAD")
        case "log --format=* %s base-sha..mo/issue-112":
          return ok("")
        case "commit -m SignalR push (#112)":
          return ok("[detached HEAD landing-sha] SignalR push (#112)")
        case "log -1 --format=%P landing-sha":
          return ok("base-sha\n")
        case "push origin landing-sha:refs/heads/master":
          return ok("To https://example.com/repo\n   base-sha..landing-sha  master -> master")
        case "ls-remote origin refs/heads/master":
          return ok("landing-sha\trefs/heads/master\n")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const result = await mergeAction(context({
      source: "mo/issue-112",
      target: "master",
      strategy: "squash",
      push: true,
      remote: "origin",
      maxConflictRetries: 1,
      conflictResolver: { with: {} },
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(resolverRan).toBe(true)
    expect(output.resolveAttempts).toBe(1)
    expect(output.conflicts).toEqual(["packages/registry.ts"])
    expect(output.landingSha).toBe("landing-sha")
    expect(calls).toContain("rebase origin/master")
    expect(calls).toContain("push origin landing-sha:refs/heads/master")
  })

  it("RebaseConflictResolutionExhausted_FailsWithPhaseRebaseConflict", async () => {
    setMergeConflictResolverForTest(async () => ({
      status: "success",
      message: "agent gave up",
      output: "agent did not finish",
    }))
    let resolverAttempt = 0
    setMergeGitRunnerForTest(async (_workDir, args) => {
      switch (args.join(" ")) {
        case "status --porcelain":
          return ok("")
        case "fetch origin master":
          return ok("From https://example.com/repo")
        case "rev-parse origin/master":
          return ok("base-sha\n")
        case "checkout mo/issue-112":
          return ok("Switched to branch 'mo/issue-112'")
        case "rebase origin/master":
          return fail("CONFLICT (content): Merge conflict in packages/registry.ts")
        case "diff --name-only --diff-filter=U":
          return ok("packages/registry.ts\n")
        case "rev-parse --git-path rebase-merge":
          return ok("/repo/.git/rebase-merge\n")
        case "rev-parse --git-path rebase-apply":
          return ok("/repo/.git/rebase-apply\n")
        case "rev-parse --git-path MERGE_HEAD":
          return ok("/repo/.git/MERGE_HEAD\n")
        case "rebase --abort":
          resolverAttempt++
          return ok("aborted")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const result = await mergeAction(context({
      source: "mo/issue-112",
      target: "master",
      strategy: "squash",
      push: true,
      remote: "origin",
      maxConflictRetries: 2,
      conflictResolver: { with: {} },
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(output.phase).toBe("rebase-conflict")
    expect(output.conflicts).toEqual(["packages/registry.ts"])
    expect(output.resolveAttempts).toBe(2)
    expect(output.message).toContain("Rebase conflicts could not be resolved")
    expect(resolverAttempt).toBe(1)
  })

  it("LandingParentMismatch_FailsWithPhaseLandingValidation", async () => {
    let headCalls = 0
    setMergeGitRunnerForTest(async (_workDir, args) => {
      switch (args.join(" ")) {
        case "status --porcelain":
          return ok("")
        case "fetch origin master":
          return ok("From https://example.com/repo")
        case "rev-parse origin/master":
          return ok("base-sha\n")
        case "checkout mo/issue-112":
          return ok("Switched to branch 'mo/issue-112'")
        case "rebase origin/master":
          return ok("rebased")
        case "rev-parse HEAD":
          headCalls++
          return ok(headCalls === 1 ? "rebased-sha\n" : "landing-sha\n")
        case "checkout --detach base-sha":
          return ok("HEAD is now at base-sha")
        case "merge --squash mo/issue-112":
          return ok("Squash commit -- not updating HEAD")
        case "log --format=* %s base-sha..mo/issue-112":
          return ok("")
        case "commit -m SignalR push (#112)":
          return ok("[detached HEAD landing-sha] SignalR push (#112)")
        case "log -1 --format=%P landing-sha":
          return ok("unrelated-parent\n")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const result = await mergeAction(context({
      source: "mo/issue-112",
      target: "master",
      strategy: "squash",
      push: true,
      remote: "origin",
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(output.phase).toBe("landing-validation")
    expect(output.landingSha).toBe("landing-sha")
    expect(output.message).toContain("Landing commit parent mismatch")
  })

  it("PostRebaseWorktreeDirty_FailsWithPhaseRebaseConflict_StructuredEvidence", async () => {
    let statusCalls = 0
    setMergeGitRunnerForTest(async (_workDir, args) => {
      switch (args.join(" ")) {
        case "status --porcelain":
          statusCalls++
          if (statusCalls === 1) return ok("")
          return ok("?? extra.ts\n")
        case "fetch origin master":
          return ok("From https://example.com/repo")
        case "rev-parse origin/master":
          return ok("base-sha\n")
        case "checkout mo/issue-112":
          return ok("Switched to branch 'mo/issue-112'")
        case "rebase origin/master":
          return ok("rebased")
        case "rev-parse HEAD":
          return ok("rebased-sha\n")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const result = await mergeAction(context({
      source: "mo/issue-112",
      target: "master",
      strategy: "squash",
      push: true,
      remote: "origin",
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(output.phase).toBe("rebase-conflict")
    expect(output.message).toContain("Source worktree is dirty after rebase")
    expect(output.dirty.untracked).toEqual(["extra.ts"])
  })

  it("PushSkippedWhenPushNotConfigured_DeliveryFactsIncludeLandingShaNoRemoteRef", async () => {
    let headCalls = 0
    setMergeGitRunnerForTest(async (_workDir, args) => {
      switch (args.join(" ")) {
        case "status --porcelain":
          return ok("")
        case "fetch origin master":
          return ok("From https://example.com/repo")
        case "rev-parse origin/master":
          return ok("base-sha\n")
        case "checkout mo/issue-112":
          return ok("Switched to branch 'mo/issue-112'")
        case "rebase origin/master":
          return ok("rebased")
        case "rev-parse HEAD":
          headCalls++
          return ok(headCalls === 1 ? "rebased-sha\n" : "landing-sha\n")
        case "checkout --detach base-sha":
          return ok("HEAD is now at base-sha")
        case "merge --squash mo/issue-112":
          return ok("Squash commit -- not updating HEAD")
        case "log --format=* %s base-sha..mo/issue-112":
          return ok("")
        case "commit -m SignalR push (#112)":
          return ok("[detached HEAD landing-sha] SignalR push (#112)")
        case "log -1 --format=%P landing-sha":
          return ok("base-sha\n")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const result = await mergeAction(context({
      source: "mo/issue-112",
      target: "master",
      strategy: "squash",
      push: false,
      remote: "origin",
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(output.pushEnabled).toBe(false)
    expect(output.landingSha).toBe("landing-sha")
    expect(output.remoteRef).toBeNull()
    expect(output.pushRetryAttempts).toBe(0)
  })

  it("PushRejectedAsRemoteAdvanced_RefetchesRebasesRegeneratesAndRetries", async () => {
    const calls: string[] = []
    let fetchAttempt = 0
    let headCalls = 0
    let baseShaAt = 0
    let lsRemoteCalls = 0
    let commitCount = 0
    setMergeGitRunnerForTest(async (_workDir, args) => {
      calls.push(args.join(" "))
      const cmd = args.join(" ")
      switch (cmd) {
        case "status --porcelain":
          return ok("")
        case "fetch origin master":
          fetchAttempt++
          return ok(`From https://example.com/repo\n * branch            master     -> FETCH_HEAD (attempt ${fetchAttempt})`)
        case "rev-parse origin/master":
          baseShaAt++
          return ok(baseShaAt === 1 ? "base-sha-1\n" : "base-sha-2\n")
        case "checkout mo/issue-112":
          return ok("Switched to branch 'mo/issue-112'")
        case "rebase origin/master":
          return ok("Successfully rebased")
        case "rev-parse HEAD":
          headCalls++
          if (headCalls === 1) return ok("rebased-sha-1\n")
          if (headCalls === 2) return ok("landing-sha-1\n")
          if (headCalls === 3) return ok("rebased-sha-2\n")
          return ok("landing-sha-2\n")
        case "checkout --detach base-sha-1":
          return ok("HEAD is now at base-sha-1")
        case "checkout --detach base-sha-2":
          return ok("HEAD is now at base-sha-2")
        case "merge --squash mo/issue-112":
          return ok("Squash commit -- not updating HEAD")
        case "log --format=* %s base-sha..mo/issue-112":
          return ok("")
        case "commit -m SignalR push (#112)":
          commitCount++
          return ok(`[detached HEAD landing-sha-${commitCount}] SignalR push (#112)`)
        case "log -1 --format=%P landing-sha-1":
          return ok("base-sha-1\n")
        case "log -1 --format=%P landing-sha-2":
          return ok("base-sha-2\n")
        case "push origin landing-sha-1:refs/heads/master":
          return fail("To https://example.com/repo\n ! [rejected] master -> master (non-fast-forward)\n")
        case "push origin landing-sha-2:refs/heads/master":
          return ok("To https://example.com/repo\n   base-sha-2..landing-sha-2  master -> master")
        case "ls-remote origin refs/heads/master":
          lsRemoteCalls++
          if (lsRemoteCalls === 1) return ok("new-remote-sha\trefs/heads/master\n")
          return ok("landing-sha-2\trefs/heads/master\n")
        default:
          return fail(`unexpected git call: ${cmd}`)
      }
    })

    const result = await mergeAction(context({
      source: "mo/issue-112",
      target: "master",
      strategy: "squash",
      push: true,
      remote: "origin",
      maxPushRetry: 5,
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(output.pushRetryAttempts).toBe(2)
    expect(output.lastRemoteSha).toBe("base-sha-2")
    expect(output.landingSha).toBe("landing-sha-2")
    expect(output.remoteRef).toBe("landing-sha-2")
    expect(fetchAttempt).toBe(2)
  })

  it("PushRejectedRetryExhausted_FailsWithPhasePush_LastRemoteShaRecorded", async () => {
    let headCalls = 0
    setMergeGitRunnerForTest(async (_workDir, args) => {
      switch (args.join(" ")) {
        case "status --porcelain":
          return ok("")
        case "fetch origin master":
          return ok("From https://example.com/repo")
        case "rev-parse origin/master":
          return ok("base-sha\n")
        case "checkout mo/issue-112":
          return ok("Switched to branch 'mo/issue-112'")
        case "rebase origin/master":
          return ok("rebased")
        case "rev-parse HEAD":
          headCalls++
          return ok(headCalls === 1 ? "rebased-sha\n" : "landing-sha\n")
        case "checkout --detach base-sha":
          return ok("HEAD is now at base-sha")
        case "merge --squash mo/issue-112":
          return ok("Squash commit -- not updating HEAD")
        case "log --format=* %s base-sha..mo/issue-112":
          return ok("")
        case "commit -m SignalR push (#112)":
          return ok("[detached HEAD landing-sha] SignalR push (#112)")
        case "log -1 --format=%P landing-sha":
          return ok("base-sha\n")
        case "push origin landing-sha:refs/heads/master":
          return fail("To https://example.com/repo\n ! [rejected] master -> master (non-fast-forward)\n")
        case "ls-remote origin refs/heads/master":
          return ok("new-remote-sha\trefs/heads/master\n")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const result = await mergeAction(context({
      source: "mo/issue-112",
      target: "master",
      strategy: "squash",
      push: true,
      remote: "origin",
      maxPushRetry: 1,
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(output.phase).toBe("push")
    expect(output.pushRetryAttempts).toBe(1)
    expect(output.lastRemoteSha).toBe("new-remote-sha")
    expect(output.message).toContain("Remote-advanced push retry exhausted after 1 attempt(s)")
  })

  it("RemoteRefVerificationFailsAfterPush_FailsWithPhasePush", async () => {
    let headCalls = 0
    setMergeGitRunnerForTest(async (_workDir, args) => {
      switch (args.join(" ")) {
        case "status --porcelain":
          return ok("")
        case "fetch origin master":
          return ok("From https://example.com/repo")
        case "rev-parse origin/master":
          return ok("base-sha\n")
        case "checkout mo/issue-112":
          return ok("Switched to branch 'mo/issue-112'")
        case "rebase origin/master":
          return ok("rebased")
        case "rev-parse HEAD":
          headCalls++
          return ok(headCalls === 1 ? "rebased-sha\n" : "landing-sha\n")
        case "checkout --detach base-sha":
          return ok("HEAD is now at base-sha")
        case "merge --squash mo/issue-112":
          return ok("Squash commit -- not updating HEAD")
        case "log --format=* %s base-sha..mo/issue-112":
          return ok("")
        case "commit -m SignalR push (#112)":
          return ok("[detached HEAD landing-sha] SignalR push (#112)")
        case "log -1 --format=%P landing-sha":
          return ok("base-sha\n")
        case "push origin landing-sha:refs/heads/master":
          return ok("To https://example.com/repo")
        case "ls-remote origin refs/heads/master":
          return fail("fatal: not a git repository\n")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const result = await mergeAction(context({
      source: "mo/issue-112",
      target: "master",
      strategy: "squash",
      push: true,
      remote: "origin",
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(output.phase).toBe("push")
    expect(output.message).toContain("Remote ref verification failed")
  })

  it("RemoteRefDoesNotMatchLandingSha_FailsWithPhasePush", async () => {
    let headCalls = 0
    setMergeGitRunnerForTest(async (_workDir, args) => {
      switch (args.join(" ")) {
        case "status --porcelain":
          return ok("")
        case "fetch origin master":
          return ok("From https://example.com/repo")
        case "rev-parse origin/master":
          return ok("base-sha\n")
        case "checkout mo/issue-112":
          return ok("Switched to branch 'mo/issue-112'")
        case "rebase origin/master":
          return ok("rebased")
        case "rev-parse HEAD":
          headCalls++
          return ok(headCalls === 1 ? "rebased-sha\n" : "landing-sha\n")
        case "checkout --detach base-sha":
          return ok("HEAD is now at base-sha")
        case "merge --squash mo/issue-112":
          return ok("Squash commit -- not updating HEAD")
        case "log --format=* %s base-sha..mo/issue-112":
          return ok("")
        case "commit -m SignalR push (#112)":
          return ok("[detached HEAD landing-sha] SignalR push (#112)")
        case "log -1 --format=%P landing-sha":
          return ok("base-sha\n")
        case "push origin landing-sha:refs/heads/master":
          return ok("To https://example.com/repo")
        case "ls-remote origin refs/heads/master":
          return ok("different-sha\trefs/heads/master\n")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const result = await mergeAction(context({
      source: "mo/issue-112",
      target: "master",
      strategy: "squash",
      push: true,
      remote: "origin",
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(output.phase).toBe("push")
    expect(output.message).toContain("Remote ref points at 'different-sha' but expected landing commit 'landing-sha'")
  })

  it("LandingCommitInProgressMerge_FailsWithPhaseLandingValidation", async () => {
    const { mkdir, writeFile, rm } = await import("node:fs/promises")
    const { tmpdir } = await import("node:os")
    const { join } = await import("node:path")
    const tmpMergeHead = join(tmpdir(), `mohist-merge-head-${Date.now()}`)
    await mkdir(tmpMergeHead, { recursive: true })
    await writeFile(join(tmpMergeHead, "MERGE_HEAD"), "abc123\n")

    let headCalls = 0
    setMergeGitRunnerForTest(async (_workDir, args) => {
      switch (args.join(" ")) {
        case "status --porcelain":
          return ok("")
        case "fetch origin master":
          return ok("From https://example.com/repo")
        case "rev-parse origin/master":
          return ok("base-sha\n")
        case "checkout mo/issue-112":
          return ok("Switched to branch 'mo/issue-112'")
        case "rebase origin/master":
          return ok("rebased")
        case "rev-parse HEAD":
          headCalls++
          return ok(headCalls === 1 ? "rebased-sha\n" : "landing-sha\n")
        case "checkout --detach base-sha":
          return ok("HEAD is now at base-sha")
        case "merge --squash mo/issue-112":
          return ok("Squash commit -- not updating HEAD")
        case "log --format=* %s base-sha..mo/issue-112":
          return ok("")
        case "commit -m SignalR push (#112)":
          return ok("[detached HEAD landing-sha] SignalR push (#112)")
        case "log -1 --format=%P landing-sha":
          return ok("base-sha\n")
        case "rev-parse --git-path MERGE_HEAD":
          return ok(join(tmpMergeHead, "MERGE_HEAD") + "\n")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    try {
      const result = await mergeAction(context({
        source: "mo/issue-112",
        target: "master",
        strategy: "squash",
        push: true,
        remote: "origin",
      }))
      const output = JSON.parse(result.output ?? "{}")

      expect(result.status).toBe("failure")
      expect(output.phase).toBe("landing-validation")
      expect(output.message).toContain("Merge is still in progress")
    } finally {
      await rm(tmpMergeHead, { recursive: true, force: true })
    }
  })

  it("MissingTarget_FailsImmediately_BeforeAnyGitOperations", async () => {
    const calls: string[] = []
    setMergeGitRunnerForTest(async (_workDir, args) => {
      calls.push(args.join(" "))
      return fail(`unexpected git call: ${args.join(" ")}`)
    })

    const result = await mergeAction(context({
      source: "mo/issue-112",
      strategy: "squash",
      push: true,
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(output.message).toContain("merge action requires 'target'")
    expect(calls).toEqual([])
  })

  it("LongSourceHistory_BodyIsCappedTo50BulletLinesWithTruncationMarker", async () => {
    // The landing commit body is built from `log target..source`. On a
    // no-op rebase this can be the entire source-branch history, which
    // can be very long. The merge action caps the body at 50 bullet
    // lines and appends a truncation marker so the commit message does
    // not silently grow without bound.
    const logLines = Array.from({ length: 200 }, (_, i) => `* commit number ${i + 1}`).join("\n")
    let headCalls = 0
    const gitCalls: string[] = []
    setMergeGitRunnerForTest(async (_workDir, args) => {
      const cmd = args.join(" ")
      gitCalls.push(cmd)
      switch (cmd) {
        case "status --porcelain":
          return ok("")
        case "fetch origin master":
          return ok("From origin")
        case "rev-parse origin/master":
          return ok("base-sha\n")
        case "checkout mo/issue-112":
          return ok("Switched to branch 'mo/issue-112'")
        case "rebase origin/master":
          return ok("rebased")
        case "rev-parse HEAD":
          headCalls++
          return ok(headCalls === 1 ? "rebased-sha\n" : "landing-sha\n")
        case "checkout --detach base-sha":
          return ok("HEAD is now at base-sha")
        case "merge --squash mo/issue-112":
          return ok("Squash commit -- not updating HEAD")
        case "log --format=* %s base-sha..mo/issue-112":
          return ok(logLines)
        case "log -1 --format=%P landing-sha":
          return ok("base-sha\n")
        case "push origin landing-sha:refs/heads/master":
          return ok("To origin")
        case "ls-remote origin refs/heads/master":
          return ok("landing-sha\trefs/heads/master\n")
        default:
          // The exact `commit -m` argument list is hard to predict
          // because the body is built dynamically; accept any commit
          // invocation and record what was sent so the test can assert
          // on the body length.
          if (cmd.startsWith("commit -m")) return ok("[detached HEAD landing-sha] SignalR push (#112)")
          return fail(`unexpected git call: ${cmd}`)
      }
    })

    const result = await mergeAction(context({
      source: "mo/issue-112",
      target: "master",
      strategy: "squash",
      push: true,
      remote: "origin",
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    // The commit body is built by joining the first 50 log lines; the
    // remaining 150 lines are replaced by a truncation marker.
    const commitCall = gitCalls.find((c) => c.startsWith("commit -m"))
    expect(commitCall).toBeDefined()
    expect(commitCall).toMatch(/\* commit number 1\b/)
    expect(commitCall).toMatch(/\* commit number 50\b/)
    expect(commitCall).not.toMatch(/\* commit number 51\b/)
    expect(commitCall).toMatch(/more commit\(s\)/)
    expect(output.landingSha).toBe("landing-sha")
  })
})

describe("mohist/merge-ready", () => {
  it("CleanCandidate_ReturnsStructuredMergeability", async () => {
    const calls: string[] = []
    const workDirs: string[] = []
    setMergeGitRunnerForTest(async (workDir, args) => {
      calls.push(args.join(" "))
      workDirs.push(workDir)
      switch (args.join(" ")) {
        case "rev-parse master":
          return ok("base-sha\n")
        case "rev-parse HEAD":
          return ok("head-sha\n")
        case "merge-base master HEAD":
          return ok("merge-base-sha\n")
        case "rev-parse --abbrev-ref HEAD":
          return ok("issue-branch\n")
        case "checkout master":
          return ok("Switched to branch 'master'")
        case "merge --squash --no-commit HEAD":
          return ok("")
        case "reset --hard":
          return ok("HEAD is now at base-sha")
        case "checkout issue-branch":
          return ok("Switched to branch 'issue-branch'")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const result = await mergeReadyAction(context({ baseBranch: "master" }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(workDirs.every((d) => d === "/fake/worktree")).toBe(true)
    expect(output).toMatchObject({
      kind: "merge-ready",
      targetBranch: "master",
      strategy: "squash",
      baseSha: "base-sha",
      candidateHeadSha: "head-sha",
      mergeBaseSha: "merge-base-sha",
      canMerge: true,
      conflictFiles: [],
    })
    expect(output.checkedAt).toBeDefined()
  })

  it("ConflictingCandidate_ReturnsConflictFiles", async () => {
    const calls: string[] = []
    setMergeGitRunnerForTest(async (_workDir, args) => {
      calls.push(args.join(" "))
      switch (args.join(" ")) {
        case "rev-parse master":
          return ok("base-sha\n")
        case "rev-parse HEAD":
          return ok("head-sha\n")
        case "merge-base master HEAD":
          return ok("merge-base-sha\n")
        case "rev-parse --abbrev-ref HEAD":
          return ok("issue-branch\n")
        case "checkout master":
          return ok("Switched to branch 'master'")
        case "merge --squash --no-commit HEAD":
          return fail("CONFLICT (content): Merge conflict in file.txt")
        case "diff --name-only --diff-filter=U":
          return ok("file.txt\n")
        case "reset --hard":
          return ok("HEAD is now at base-sha")
        case "checkout issue-branch":
          return ok("Switched to branch 'issue-branch'")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const result = await mergeReadyAction(context({ baseBranch: "master" }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(output).toMatchObject({
      kind: "merge-ready",
      targetBranch: "master",
      strategy: "squash",
      baseSha: "base-sha",
      candidateHeadSha: "head-sha",
      mergeBaseSha: "merge-base-sha",
      canMerge: false,
      conflictFiles: ["file.txt"],
    })
  })

  it("PreflightRestoresOriginalBranch_AfterTemporaryMerge", async () => {
    const calls: string[] = []
    setMergeGitRunnerForTest(async (_workDir, args) => {
      calls.push(args.join(" "))
      switch (args.join(" ")) {
        case "rev-parse master":
          return ok("base-sha\n")
        case "rev-parse HEAD":
          return ok("head-sha\n")
        case "merge-base master HEAD":
          return ok("merge-base-sha\n")
        case "rev-parse --abbrev-ref HEAD":
          return ok("issue-branch\n")
        case "checkout master":
          return ok("Switched to branch 'master'")
        case "merge --squash --no-commit HEAD":
          return ok("")
        case "reset --hard":
          return ok("HEAD is now at base-sha")
        case "checkout issue-branch":
          return ok("Switched to branch 'issue-branch'")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    await mergeReadyAction(context({ baseBranch: "master" }))

    expect(calls).toContain("checkout master")
    expect(calls[calls.length - 1]).toBe("checkout issue-branch")
  })
})

function context(withOverrides: JsonObject = {}, variables: JsonObject = {}): ActionContext {
  return {
    workflowRunId: "workflow-1",
    workId: "integrate:merge.1",
    workType: "task",
    stage: "integrate",
    title: "Merge branch",
    uses: "mohist/merge",
    with: withOverrides,
    variables: {
      project: { path: "/repo" },
      issue: { title: "SignalR push", number: 112 },
      ...variables,
    },
    workDir: "/fake/worktree",
    issueNumber: 112,
    signal: new AbortController().signal,
  }
}

function ok(stdout: string) {
  return { success: true, stdout, stderr: "", exitCode: 0, combinedOutput: stdout.trim() }
}

function fail(stderr: string) {
  return { success: false, stdout: "", stderr, exitCode: 1, combinedOutput: stderr }
}
