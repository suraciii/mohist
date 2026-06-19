import { afterEach, describe, expect, it } from "vitest"
import {
  publishAction,
  setDeliveryExistsCheckerForTest,
  setDeliveryGitRunnerForTest,
  setDeliveryWorkspaceManagerForTest,
} from "../src/actions/registry.js"
import type { LandingWorkspaceInfo } from "../src/runtime/workspace.js"
import type { DeliveryWorkspaceManager } from "../src/actions/registry.js"
import type { ActionContext, JsonObject, WorkItem } from "../src/core/types.js"

type GitResponse = { success: boolean; stdout: string; stderr: string; exitCode: number; combinedOutput: string }

type WorkspaceCall = { workDir: string; args: string[] }

const WORKSPACE_PATH = "/workspace"
const LANDING_PATH = "/landing/wr-publish-1"

afterEach(() => {
  setDeliveryGitRunnerForTest(null)
  setDeliveryExistsCheckerForTest(null)
  setDeliveryWorkspaceManagerForTest(null)
})

function installGitAndLanding(respond: (calls: WorkspaceCall[]) => GitResponse | Promise<GitResponse>) {
  const calls: WorkspaceCall[] = []
  const respondAndTrack = async (workDir: string, args: string[]) => {
    const record: WorkspaceCall = { workDir, args: [...args] }
    calls.push(record)
    return await respond(calls)
  }
  setDeliveryGitRunnerForTest(respondAndTrack)
  const landing: LandingWorkspaceInfo = {
    path: LANDING_PATH,
    runId: "wr-publish-1",
    runBranch: "mohist/run-wr-publish-1",
    baseBranch: "master",
    gitUrl: "https://example.com/repo.git",
  }
  const manager: DeliveryWorkspaceManager = {
    createLandingWorkspace: async (_work, _signal) => landing,
    disposeLandingWorkspace: async (target, _signal) => {
      const path = typeof target === "string" ? target : target.path
      return { path, disposed: true }
    },
  }
  setDeliveryWorkspaceManagerForTest(manager)
  return { calls, landing }
}

function ok(stdout: string) {
  return { success: true, stdout, stderr: "", exitCode: 0, combinedOutput: stdout.trim() }
}

function fail(stderr: string) {
  return { success: false, stdout: "", stderr, exitCode: 1, combinedOutput: stderr }
}

function workspaceCalls(calls: WorkspaceCall[]) {
  return calls.filter((c) => c.workDir === WORKSPACE_PATH).map((c) => c.args.join(" "))
}

function landingCalls(calls: WorkspaceCall[]) {
  return calls.filter((c) => c.workDir === LANDING_PATH).map((c) => c.args.join(" "))
}

function context(withOverrides: JsonObject = {}, variables: JsonObject = {}): ActionContext {
  return {
    workflowRunId: "wr-publish-1",
    workId: "integrate:publish.1",
    workType: "task",
    stage: "integrate",
    title: "Publish changes",
    uses: "mohist/publish",
    with: withOverrides,
    variables: {
      project: { path: WORKSPACE_PATH },
      issue: { title: "SignalR realtime push", number: 82 },
      repository: {
        gitUrl: "https://example.com/repo.git",
        baseBranch: "master",
        name: "master",
      },
      mohist: { runId: "wr-publish-1" },
      ...variables,
    },
    workDir: WORKSPACE_PATH,
    issueNumber: 82,
    signal: new AbortController().signal,
  }
}

describe("mohist/publish (branch-stable)", () => {
  it("PublishReady_LandsInIsolatedLandingWorkspaceAndPushes", async () => {
    const { calls } = installGitAndLanding(async () => {
      return fail(`unexpected git call`)
    })

    const workspaceSeen: string[] = []
    const landingSeen: string[] = []
    setDeliveryGitRunnerForTest(async (workDir, args) => {
      const command = args.join(" ")
      calls.push({ workDir, args: [...args] })
      if (workDir === WORKSPACE_PATH) workspaceSeen.push(command)
      else if (workDir === LANDING_PATH) landingSeen.push(command)

      switch (command) {
        case "rev-parse mo/issue-82":
          return ok("source-sha\n")
        case "fetch origin master":
          return ok("")
        case "rev-parse origin/master":
          return ok("remote-head-sha\n")
        case "checkout -B master origin/master":
          return ok("Switched to branch 'master'")
        case "status --porcelain":
          return ok("")
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
          return fail(`unexpected git call: ${command}`)
      }
    })

    const result = await publishAction(context({
      source: "mo/issue-82",
      target: "master",
      message: "Complete issue #82",
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    // Workflow workspace only sees the read-only source-anchor check.
    expect(workspaceSeen).toEqual(["rev-parse mo/issue-82"])
    expect(workspaceCalls(calls)).toEqual(["rev-parse mo/issue-82"])
    // No checkout/merge/commit/push against the workflow workspace.
    expect(workspaceSeen.some((c) => c.startsWith("checkout "))).toBe(false)
    expect(workspaceSeen.some((c) => c.startsWith("merge --squash "))).toBe(false)
    expect(workspaceSeen.some((c) => c.startsWith("commit "))).toBe(false)
    expect(workspaceSeen.some((c) => c.startsWith("push "))).toBe(false)
    expect(workspaceSeen.some((c) => c === "checkout -B master origin/master")).toBe(false)

    // Landing workspace has the full landing sequence.
    expect(landingSeen).toEqual([
      "fetch origin master",
      "rev-parse origin/master",
      "checkout -B master origin/master",
      "status --porcelain",
      "merge-base --is-ancestor origin/master mo/issue-82",
      "merge --squash mo/issue-82",
      "commit -m SignalR realtime push (#82) -m mo/issue-82 into master",
      "rev-parse HEAD",
      "push origin master",
    ])
    expect(landingCalls(calls)).toEqual(landingSeen)

    expect(output).toMatchObject({
      kind: "publish",
      status: "completed",
      source: "mo/issue-82",
      target: "master",
      workDir: WORKSPACE_PATH,
      landedCommit: "abc123",
      pushed: true,
      failureKind: null,
    })
    expect(output).not.toHaveProperty("resolveAttempts")
  })

  it("StaleLocalTarget_ResetToRemoteBeforeSquashAndPush", async () => {
    const { calls } = installGitAndLanding(async () => fail("unexpected"))

    const landingSeen: string[] = []
    setDeliveryGitRunnerForTest(async (workDir, args) => {
      const command = args.join(" ")
      calls.push({ workDir, args: [...args] })
      if (workDir === LANDING_PATH) landingSeen.push(command)
      switch (command) {
        case "rev-parse mo/issue-82":
          return ok("source-sha\n")
        case "fetch origin master":
          return ok("From https://example.com/repo.git\n * branch            master     -> FETCH_HEAD")
        case "rev-parse origin/master":
          return ok("remote-head-sha\n")
        case "checkout -B master origin/master":
          return ok("Switched to branch 'master'")
        case "status --porcelain":
          return ok("")
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
          return fail(`unexpected git call: ${command}`)
      }
    })

    const result = await publishAction(context({
      source: "mo/issue-82",
      target: "master",
      message: "Complete issue #82",
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(landingSeen).toContain("checkout -B master origin/master")
    expect(landingSeen).not.toContain("merge --ff-only origin/master")
    expect(landingSeen.indexOf("checkout -B master origin/master")).toBeLessThan(landingSeen.indexOf("merge --squash mo/issue-82"))
    expect(workspaceCalls(calls)).toEqual(["rev-parse mo/issue-82"])
    expect(output).toMatchObject({
      kind: "publish",
      landedCommit: "abc123",
      pushed: true,
      failureKind: null,
    })
  })

  it("SourceBehindRemoteTarget_ReportsBaseMovedWithoutTouchingWorkspace", async () => {
    const { calls } = installGitAndLanding(async () => fail("unexpected"))

    setDeliveryGitRunnerForTest(async (workDir, args) => {
      const command = args.join(" ")
      calls.push({ workDir, args: [...args] })
      switch (command) {
        case "rev-parse mo/issue-82":
          return ok("source-sha\n")
        case "fetch origin master":
          return ok("")
        case "rev-parse origin/master":
          return ok("remote-head-sha\n")
        case "checkout -B master origin/master":
          return ok("Switched to branch 'master'")
        case "status --porcelain":
          return ok("")
        case "merge-base --is-ancestor origin/master mo/issue-82":
          return fail("")
        case "reset --hard remote-head-sha":
          return ok("HEAD is now at remote-head-sha")
        default:
          return fail(`unexpected git call: ${command}`)
      }
    })

    const result = await publishAction(context({
      source: "mo/issue-82",
      target: "master",
      message: "Complete issue #82",
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(workspaceCalls(calls)).toEqual(["rev-parse mo/issue-82"])
    // Rollback targets only the landing workspace.
    expect(landingCalls(calls)).toContain("reset --hard remote-head-sha")
    expect(landingCalls(calls)).not.toContain("merge --squash mo/issue-82")
    expect(landingCalls(calls)).not.toContain("push origin master")
    expect(output).toMatchObject({
      kind: "publish",
      status: "failed",
      landedCommit: null,
      pushed: false,
      failureKind: "base-moved",
    })
    expect(output.output).toContain("Re-run prepare")
  })

  it("SquashConflicts_BaseMoved_ReportsBaseMovedAndResetsLandingWorkspace", async () => {
    const { calls } = installGitAndLanding(async () => fail("unexpected"))

    setDeliveryGitRunnerForTest(async (workDir, args) => {
      const command = args.join(" ")
      calls.push({ workDir, args: [...args] })
      switch (command) {
        case "rev-parse mo/issue-82":
          return ok("source-sha\n")
        case "fetch origin master":
          return ok("")
        case "rev-parse origin/master":
          return ok("remote-head-sha\n")
        case "checkout -B master origin/master":
          return ok("Switched to branch 'master'")
        case "status --porcelain":
          return ok("")
        case "merge-base --is-ancestor origin/master mo/issue-82":
          return ok("")
        case "merge --squash mo/issue-82":
          return fail("CONFLICT (content): Merge conflict in specs/web-ui/spec.md")
        case "merge --abort":
          return ok("")
        case "reset --hard remote-head-sha":
          return ok("HEAD is now at remote-head-sha")
        default:
          return fail(`unexpected git call: ${command}`)
      }
    })

    const result = await publishAction(context({
      source: "mo/issue-82",
      target: "master",
      message: "Complete issue #82",
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(workspaceCalls(calls)).toEqual(["rev-parse mo/issue-82"])
    expect(landingCalls(calls)).toContain("merge --abort")
    expect(landingCalls(calls)).toContain("reset --hard remote-head-sha")
    expect(landingCalls(calls)).not.toContain("commit -m SignalR realtime push (#82) -m mo/issue-82 into master")
    expect(landingCalls(calls)).not.toContain("push origin master")
    expect(output).toMatchObject({
      kind: "publish",
      status: "failed",
      source: "mo/issue-82",
      target: "master",
      workDir: WORKSPACE_PATH,
      landedCommit: null,
      pushed: false,
      failureKind: "base-moved",
    })
  })

  it("PushRejectedNonFastForward_ReportsBaseMovedAndResetsLandingWorkspace", async () => {
    const { calls } = installGitAndLanding(async () => fail("unexpected"))

    setDeliveryGitRunnerForTest(async (workDir, args) => {
      const command = args.join(" ")
      calls.push({ workDir, args: [...args] })
      switch (command) {
        case "rev-parse mo/issue-82":
          return ok("source-sha\n")
        case "fetch origin master":
          return ok("")
        case "rev-parse origin/master":
          return ok("remote-head-sha\n")
        case "checkout -B master origin/master":
          return ok("Switched to branch 'master'")
        case "status --porcelain":
          return ok("")
        case "merge-base --is-ancestor origin/master mo/issue-82":
          return ok("")
        case "merge --squash mo/issue-82":
          return ok("Squash commit -- not updating HEAD")
        case "commit -m SignalR realtime push (#82) -m mo/issue-82 into master":
          return ok("[master abc123] SignalR realtime push (#82)")
        case "rev-parse HEAD":
          return ok("abc123\n")
        case "push origin master":
          return fail("To https://example.com/repo.git\n ! [rejected]        master -> master (non-fast-forward)\nerror: failed to push some refs")
        case "reset --hard remote-head-sha":
          return ok("HEAD is now at remote-head-sha")
        default:
          return fail(`unexpected git call: ${command}`)
      }
    })

    const result = await publishAction(context({
      source: "mo/issue-82",
      target: "master",
      message: "Complete issue #82",
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(workspaceCalls(calls)).toEqual(["rev-parse mo/issue-82"])
    expect(landingCalls(calls)).not.toContain("merge --abort")
    expect(landingCalls(calls)).toContain("reset --hard remote-head-sha")
    expect(landingCalls(calls).indexOf("reset --hard remote-head-sha"))
      .toBeGreaterThan(landingCalls(calls).indexOf("push origin master"))
    expect(output).toMatchObject({
      kind: "publish",
      status: "failed",
      source: "mo/issue-82",
      target: "master",
      workDir: WORKSPACE_PATH,
      landedCommit: "abc123",
      pushed: false,
      failureKind: "base-moved",
    })
  })

  it("PushFailsTransient_ReportsRetrySafeAndResetsLandingWorkspace", async () => {
    const { calls } = installGitAndLanding(async () => fail("unexpected"))

    setDeliveryGitRunnerForTest(async (workDir, args) => {
      const command = args.join(" ")
      calls.push({ workDir, args: [...args] })
      switch (command) {
        case "rev-parse mo/issue-82":
          return ok("source-sha\n")
        case "fetch origin master":
          return ok("")
        case "rev-parse origin/master":
          return ok("remote-head-sha\n")
        case "checkout -B master origin/master":
          return ok("Switched to branch 'master'")
        case "status --porcelain":
          return ok("")
        case "merge-base --is-ancestor origin/master mo/issue-82":
          return ok("")
        case "merge --squash mo/issue-82":
          return ok("Squash commit -- not updating HEAD")
        case "commit -m SignalR realtime push (#82) -m mo/issue-82 into master":
          return ok("[master abc123] SignalR realtime push (#82)")
        case "rev-parse HEAD":
          return ok("abc123\n")
        case "push origin master":
          return fail("fatal: unable to access: could not resolve host")
        case "reset --hard remote-head-sha":
          return ok("HEAD is now at remote-head-sha")
        default:
          return fail(`unexpected git call: ${command}`)
      }
    })

    const result = await publishAction(context({
      source: "mo/issue-82",
      target: "master",
      message: "Complete issue #82",
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(workspaceCalls(calls)).toEqual(["rev-parse mo/issue-82"])
    expect(landingCalls(calls)).toContain("reset --hard remote-head-sha")
    expect(output).toMatchObject({
      kind: "publish",
      landedCommit: "abc123",
      pushed: false,
      failureKind: "retry-safe",
    })
  })

  it("CommitFails_ReportsRetrySafeAndResetsLandingWorkspace", async () => {
    const { calls } = installGitAndLanding(async () => fail("unexpected"))

    setDeliveryGitRunnerForTest(async (workDir, args) => {
      const command = args.join(" ")
      calls.push({ workDir, args: [...args] })
      switch (command) {
        case "rev-parse mo/issue-82":
          return ok("source-sha\n")
        case "fetch origin master":
          return ok("")
        case "rev-parse origin/master":
          return ok("remote-head-sha\n")
        case "checkout -B master origin/master":
          return ok("Switched to branch 'master'")
        case "status --porcelain":
          return ok("")
        case "merge-base --is-ancestor origin/master mo/issue-82":
          return ok("")
        case "merge --squash mo/issue-82":
          return ok("Squash commit -- not updating HEAD")
        case "commit -m SignalR realtime push (#82) -m mo/issue-82 into master":
          return fail("error: unable to commit without author identity")
        case "reset --hard remote-head-sha":
          return ok("HEAD is now at remote-head-sha")
        default:
          return fail(`unexpected git call: ${command}`)
      }
    })

    const result = await publishAction(context({
      source: "mo/issue-82",
      target: "master",
      message: "Complete issue #82",
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(workspaceCalls(calls)).toEqual(["rev-parse mo/issue-82"])
    expect(landingCalls(calls)).toContain("reset --hard remote-head-sha")
    expect(output).toMatchObject({
      kind: "publish",
      landedCommit: null,
      pushed: false,
      failureKind: "retry-safe",
    })
  })

  it("DirtyLandingWorkspace_ReportsRetrySafeWithoutSquashOrPush", async () => {
    const { calls } = installGitAndLanding(async () => fail("unexpected"))

    setDeliveryGitRunnerForTest(async (workDir, args) => {
      const command = args.join(" ")
      calls.push({ workDir, args: [...args] })
      switch (command) {
        case "rev-parse mo/issue-82":
          return ok("source-sha\n")
        case "fetch origin master":
          return ok("")
        case "rev-parse origin/master":
          return ok("remote-head-sha\n")
        case "checkout -B master origin/master":
          return ok("Switched to branch 'master'")
        case "status --porcelain":
          return ok(" M packages/server/src/Server.cs\n")
        case "reset --hard remote-head-sha":
          return ok("HEAD is now at remote-head-sha")
        default:
          return fail(`unexpected git call: ${command}`)
      }
    })

    const result = await publishAction(context({
      source: "mo/issue-82",
      target: "master",
      message: "Complete issue #82",
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(workspaceCalls(calls)).toEqual(["rev-parse mo/issue-82"])
    expect(landingCalls(calls)).not.toContain("merge --squash mo/issue-82")
    expect(landingCalls(calls)).not.toContain("push origin master")
    expect(output).toMatchObject({
      kind: "publish",
      landedCommit: null,
      pushed: false,
      failureKind: "retry-safe",
    })
    expect(output.output).toContain("landing workspace")
  })

  it("CheckoutFails_AbortsRebaseOrMergeAndResetsLandingWorkspace", async () => {
    const { calls } = installGitAndLanding(async () => fail("unexpected"))

    setDeliveryGitRunnerForTest(async (workDir, args) => {
      const command = args.join(" ")
      calls.push({ workDir, args: [...args] })
      switch (command) {
        case "rev-parse mo/issue-82":
          return ok("source-sha\n")
        case "fetch origin master":
          return ok("")
        case "rev-parse origin/master":
          return ok("remote-head-sha\n")
        case "checkout -B master origin/master":
          return fail("error: pathspec 'master' did not match any file(s) known to git")
        case "rev-parse --git-path rebase-merge":
          return ok(".git/rebase-merge\n")
        case "rev-parse --git-path rebase-apply":
          return ok(".git/rebase-apply\n")
        case "rev-parse --git-path MERGE_HEAD":
          return ok(".git/MERGE_HEAD\n")
        case "rebase --abort":
          return ok("")
        case "reset --hard remote-head-sha":
          return ok("HEAD is now at remote-head-sha")
        default:
          return fail(`unexpected git call: ${command}`)
      }
    })
    setDeliveryExistsCheckerForTest((path) => path === `${LANDING_PATH}/.git/rebase-merge`)

    const result = await publishAction(context({
      source: "mo/issue-82",
      target: "master",
      message: "Complete issue #82",
    }))

    expect(result.status).toBe("failure")
    expect(workspaceCalls(calls)).toEqual(["rev-parse mo/issue-82"])
    expect(landingCalls(calls)).toContain("rebase --abort")
    expect(landingCalls(calls)).not.toContain("merge --abort")
    expect(landingCalls(calls)).toContain("reset --hard remote-head-sha")
  })

  it("CheckoutFailsWithPendingMerge_AbortsMergeAndResetsLandingWorkspace", async () => {
    const { calls } = installGitAndLanding(async () => fail("unexpected"))

    setDeliveryGitRunnerForTest(async (workDir, args) => {
      const command = args.join(" ")
      calls.push({ workDir, args: [...args] })
      switch (command) {
        case "rev-parse mo/issue-82":
          return ok("source-sha\n")
        case "fetch origin master":
          return ok("")
        case "rev-parse origin/master":
          return ok("remote-head-sha\n")
        case "checkout -B master origin/master":
          return fail("error: Your local changes would be overwritten")
        case "rev-parse --git-path rebase-merge":
          return ok(".git/rebase-merge\n")
        case "rev-parse --git-path rebase-apply":
          return ok(".git/rebase-apply\n")
        case "rev-parse --git-path MERGE_HEAD":
          return ok(".git/MERGE_HEAD\n")
        case "merge --abort":
          return ok("")
        case "reset --hard remote-head-sha":
          return ok("HEAD is now at remote-head-sha")
        default:
          return fail(`unexpected git call: ${command}`)
      }
    })
    setDeliveryExistsCheckerForTest((path) => path === `${LANDING_PATH}/.git/MERGE_HEAD`)

    const result = await publishAction(context({
      source: "mo/issue-82",
      target: "master",
      message: "Complete issue #82",
    }))

    expect(result.status).toBe("failure")
    expect(workspaceCalls(calls)).toEqual(["rev-parse mo/issue-82"])
    expect(landingCalls(calls)).toContain("merge --abort")
    expect(landingCalls(calls)).not.toContain("rebase --abort")
    expect(landingCalls(calls)).toContain("reset --hard remote-head-sha")
  })

  it("ReadsTargetBranchFromProjectVariables_WhenTargetUnset", async () => {
    const { calls } = installGitAndLanding(async () => fail("unexpected"))

    const landingSeen: string[] = []
    setDeliveryGitRunnerForTest(async (workDir, args) => {
      const command = args.join(" ")
      calls.push({ workDir, args: [...args] })
      if (workDir === LANDING_PATH) landingSeen.push(command)
      switch (command) {
        case "rev-parse mo/issue-82":
          return ok("source-sha\n")
        case "fetch origin main":
          return ok("")
        case "rev-parse origin/main":
          return ok("remote-head-sha\n")
        case "checkout -B main origin/main":
          return ok("Switched to branch 'main'")
        case "status --porcelain":
          return ok("")
        case "merge-base --is-ancestor origin/main mo/issue-82":
          return ok("")
        case "merge --squash mo/issue-82":
          return ok("Squash commit -- not updating HEAD")
        case "commit -m SignalR realtime push (#82) -m mo/issue-82 into main":
          return ok("[main def456] SignalR realtime push (#82)")
        case "rev-parse HEAD":
          return ok("def456\n")
        case "push origin main":
          return ok("To https://example.com/repo.git\n   remote-head-sha..def456  main -> main")
        default:
          return fail(`unexpected git call: ${command}`)
      }
    })

    const result = await publishAction(context(
      { source: "mo/issue-82", message: "Complete issue #82" },
      { project: { baseBranch: "main" }, repository: { gitUrl: "https://example.com/repo.git", baseBranch: "main", name: "master" } },
    ))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(workspaceCalls(calls)).toEqual(["rev-parse mo/issue-82"])
    expect(landingSeen).toContain("checkout -B main origin/main")
    expect(landingSeen).toContain("push origin main")
    expect(output).toMatchObject({
      kind: "publish",
      target: "main",
      landedCommit: "def456",
      pushed: true,
    })
  })

  it("WorkspaceBranchInvariant_NeverInvokesBaseBranchOpsInWorkflowWorkspace", async () => {
    const { calls } = installGitAndLanding(async () => fail("unexpected"))

    setDeliveryGitRunnerForTest(async (workDir, args) => {
      const command = args.join(" ")
      calls.push({ workDir, args: [...args] })
      switch (command) {
        case "rev-parse mo/issue-82":
          return ok("source-sha\n")
        case "fetch origin master":
          return ok("")
        case "rev-parse origin/master":
          return ok("remote-head-sha\n")
        case "checkout -B master origin/master":
          return ok("Switched to branch 'master'")
        case "status --porcelain":
          return ok("")
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
          return fail(`unexpected git call: ${command}`)
      }
    })

    await publishAction(context({
      source: "mo/issue-82",
      target: "master",
      message: "Complete issue #82",
    }))

    const workspaceCmdSet = new Set(workspaceCalls(calls))
    // The workflow workspace must NOT see any base-branch mutating
    // operation. The only operation against the workflow workspace is
    // the read-only source-anchor resolution; everything else lives in
    // the landing workspace.
    for (const forbidden of ["checkout -B master origin/master", "merge --squash mo/issue-82", "commit -m SignalR realtime push (#82) -m mo/issue-82 into master", "push origin master"]) {
      expect(workspaceCmdSet.has(forbidden)).toBe(false)
    }
  })

  it("LandingWorkspaceCreationFails_ReportsRetrySafeAndDoesNotMutateWorkspace", async () => {
    const calls: WorkspaceCall[] = []
    setDeliveryGitRunnerForTest(async (workDir, args) => {
      calls.push({ workDir, args: [...args] })
      switch (args.join(" ")) {
        case "rev-parse mo/issue-82":
          return ok("source-sha\n")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })
    setDeliveryWorkspaceManagerForTest({
      createLandingWorkspace: async () => { throw new Error("boom: clone failed") },
      disposeLandingWorkspace: async (target) => {
        const path = typeof target === "string" ? target : target.path
        return { path, disposed: true }
      },
    })

    const result = await publishAction(context({
      source: "mo/issue-82",
      target: "master",
      message: "Complete issue #82",
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    // Only the read-only rev-parse should have hit the workflow workspace.
    expect(workspaceCalls(calls)).toEqual(["rev-parse mo/issue-82"])
    expect(output).toMatchObject({
      kind: "publish",
      landedCommit: null,
      pushed: false,
      failureKind: "retry-safe",
    })
    expect(output.output).toContain("landing workspace")
  })
})
