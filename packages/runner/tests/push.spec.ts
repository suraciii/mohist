import { afterEach, describe, expect, it } from "vitest"
import {
  publishAction,
  setDeliveryGitRunnerForTest,
  setDeliveryExistsCheckerForTest,
  setDeliveryWorkspaceManagerForTest,
} from "../src/actions/registry.js"
import type { LandingWorkspaceInfo } from "../src/runtime/workspace.js"
import type { DeliveryWorkspaceManager } from "../src/actions/registry.js"
import type { ActionContext, JsonObject, WorkItem } from "../src/core/types.js"

type WorkspaceCall = { workDir: string; args: string[] }

const WORKSPACE_PATH = "/workspace"
const LANDING_PATH = "/landing/wr-push-1"

afterEach(() => {
  setDeliveryGitRunnerForTest(null)
  setDeliveryExistsCheckerForTest(null)
  setDeliveryWorkspaceManagerForTest(null)
})

function installLandingWorkspace(landingOverrides: Partial<LandingWorkspaceInfo> = {}) {
  const landing: LandingWorkspaceInfo = {
    path: LANDING_PATH,
    runId: "wr-push-1",
    runBranch: "mohist/run-wr-push-1",
    baseBranch: "main",
    gitUrl: "https://example.com/repo.git",
    ...landingOverrides,
  }
  const manager: DeliveryWorkspaceManager = {
    createLandingWorkspace: async (_work, _signal) => landing,
    disposeLandingWorkspace: async (target, _signal) => {
      const path = typeof target === "string" ? target : target.path
      return { path, disposed: true }
    },
  }
  setDeliveryWorkspaceManagerForTest(manager)
  return landing
}

function installGit(respond: (call: WorkspaceCall, history: WorkspaceCall[]) => { success: boolean; stdout: string; stderr: string; exitCode: number; combinedOutput: string } | Promise<{ success: boolean; stdout: string; stderr: string; exitCode: number; combinedOutput: string }>) {
  const calls: WorkspaceCall[] = []
  setDeliveryGitRunnerForTest(async (workDir, args) => {
    const record: WorkspaceCall = { workDir, args: [...args] }
    calls.push(record)
    return await respond(record, calls)
  })
  return calls
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
    workflowRunId: "wr-push-1",
    workId: "integrate:publish.1",
    workType: "task",
    stage: "integrate",
    title: "Publish changes",
    uses: "mohist/publish",
    with: withOverrides,
    variables: {
      project: { path: WORKSPACE_PATH },
      issue: { title: "Push action issue", number: 99 },
      repository: {
        gitUrl: "https://example.com/repo.git",
        baseBranch: "main",
        name: "master",
      },
      mohist: { runId: "wr-push-1" },
      ...variables,
    },
    workDir: WORKSPACE_PATH,
    issueNumber: 99,
    signal: new AbortController().signal,
  }
}

describe("mohist/push (now part of publish, landing-workspace scoped)", () => {
  it("PushHappensInLandingWorkspace_NotInWorkflowWorkspace", async () => {
    installLandingWorkspace()
    const calls = installGit(async (_call, history) => {
      const command = history[history.length - 1].args.join(" ")
      switch (command) {
        case "rev-parse mo/issue-99":
          return ok("source-sha\n")
        case "fetch origin main":
          return ok("")
        case "rev-parse origin/main":
          return ok("remote-head-sha\n")
        case "checkout main":
          return ok("Switched to branch 'main'")
        case "status --porcelain":
          return ok("")
        case "merge --ff-only origin/main":
          return ok("Already up to date.")
        case "merge-base --is-ancestor origin/main mo/issue-99":
          return ok("")
        case "merge --squash mo/issue-99":
          return ok("Squash commit -- not updating HEAD")
        case "commit -m Push action issue (#99) -m mo/issue-99 into main":
          return ok("[main def456] Push action issue (#99)")
        case "rev-parse HEAD":
          return ok("def456\n")
        case "push origin main":
          return ok("To https://example.com/repo.git\n   remote-head-sha..def456  main -> main")
        default:
          return fail(`unexpected git call: ${command}`)
      }
    })

    const result = await publishAction(context({
      source: "mo/issue-99",
      target: "main",
      message: "Complete issue #99",
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    // The push must be issued against the landing workspace path, never
    // the workflow workspace. (The push is the user-visible deliverable;
    // it is now scoped to the landing workspace so the workflow workspace
    // never leaves the run branch.)
    expect(landingCalls(calls)).toContain("push origin main")
    expect(workspaceCalls(calls)).not.toContain("push origin main")
    expect(output).toMatchObject({
      kind: "publish",
      target: "main",
      pushed: true,
      landedCommit: "def456",
      failureKind: null,
    })
  })

  it("PushRejectedAsNonFastForward_ClassifiesAsBaseMovedAndResetsLandingWorkspace", async () => {
    installLandingWorkspace()
    const calls = installGit(async () => fail(""))
    setDeliveryGitRunnerForTest(async (workDir, args) => {
      const command = args.join(" ")
      calls.push({ workDir, args: [...args] })
      switch (command) {
        case "rev-parse mo/issue-99":
          return ok("source-sha\n")
        case "fetch origin main":
          return ok("")
        case "rev-parse origin/main":
          return ok("remote-head-sha\n")
        case "checkout main":
          return ok("Switched to branch 'main'")
        case "status --porcelain":
          return ok("")
        case "merge --ff-only origin/main":
          return ok("Already up to date.")
        case "merge-base --is-ancestor origin/main mo/issue-99":
          return ok("")
        case "merge --squash mo/issue-99":
          return ok("Squash commit -- not updating HEAD")
        case "commit -m Push action issue (#99) -m mo/issue-99 into main":
          return ok("[main def456] Push action issue (#99)")
        case "rev-parse HEAD":
          return ok("def456\n")
        case "push origin main":
          return fail("To https://example.com/repo.git\n ! [rejected]        main -> main (non-fast-forward)\nerror: failed to push some refs")
        case "reset --hard remote-head-sha":
          return ok("HEAD is now at remote-head-sha")
        default:
          return fail(`unexpected git call: ${command}`)
      }
    })

    const result = await publishAction(context({
      source: "mo/issue-99",
      target: "main",
      message: "Complete issue #99",
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    // Push attempted against the landing workspace, then rollback reset
    // hit the landing workspace, never the workflow workspace.
    expect(workspaceCalls(calls)).not.toContain("push origin main")
    expect(workspaceCalls(calls)).not.toContain("reset --hard remote-head-sha")
    expect(landingCalls(calls)).toContain("push origin main")
    expect(landingCalls(calls)).toContain("reset --hard remote-head-sha")
    expect(output).toMatchObject({
      kind: "publish",
      pushed: false,
      landedCommit: "def456",
      failureKind: "base-moved",
    })
  })

  it("PushFailsTransientAuthError_ClassifiesAsRetrySafeNotBaseMoved", async () => {
    installLandingWorkspace()
    const calls = installGit(async () => fail(""))
    setDeliveryGitRunnerForTest(async (workDir, args) => {
      const command = args.join(" ")
      calls.push({ workDir, args: [...args] })
      switch (command) {
        case "rev-parse mo/issue-99":
          return ok("source-sha\n")
        case "fetch origin main":
          return ok("")
        case "rev-parse origin/main":
          return ok("remote-head-sha\n")
        case "checkout main":
          return ok("Switched to branch 'main'")
        case "status --porcelain":
          return ok("")
        case "merge --ff-only origin/main":
          return ok("Already up to date.")
        case "merge-base --is-ancestor origin/main mo/issue-99":
          return ok("")
        case "merge --squash mo/issue-99":
          return ok("Squash commit -- not updating HEAD")
        case "commit -m Push action issue (#99) -m mo/issue-99 into main":
          return ok("[main def456] Push action issue (#99)")
        case "rev-parse HEAD":
          return ok("def456\n")
        case "push origin main":
          return fail("fatal: could not read Username for 'https://example.com': terminal prompts disabled\nfatal: Authentication failed for 'https://example.com/repo.git/'")
        case "reset --hard remote-head-sha":
          return ok("HEAD is now at remote-head-sha")
        default:
          return fail(`unexpected git call: ${command}`)
      }
    })

    const result = await publishAction(context({
      source: "mo/issue-99",
      target: "main",
      message: "Complete issue #99",
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(landingCalls(calls)).toContain("reset --hard remote-head-sha")
    expect(output).toMatchObject({
      kind: "publish",
      pushed: false,
      landedCommit: "def456",
      failureKind: "retry-safe",
    })
  })

  it("PushTargetsConfiguredRemote_NotWorkflowWorkspaceOrigin", async () => {
    installLandingWorkspace()
    const calls = installGit(async () => fail(""))
    setDeliveryGitRunnerForTest(async (workDir, args) => {
      const command = args.join(" ")
      calls.push({ workDir, args: [...args] })
      switch (command) {
        case "rev-parse mo/issue-99":
          return ok("source-sha\n")
        case "fetch upstream main":
          return ok("")
        case "rev-parse upstream/main":
          return ok("remote-head-sha\n")
        case "checkout main":
          return ok("Switched to branch 'main'")
        case "status --porcelain":
          return ok("")
        case "merge --ff-only upstream/main":
          return ok("Already up to date.")
        case "merge-base --is-ancestor upstream/main mo/issue-99":
          return ok("")
        case "merge --squash mo/issue-99":
          return ok("Squash commit -- not updating HEAD")
        case "commit -m Push action issue (#99) -m mo/issue-99 into main":
          return ok("[main def456] Push action issue (#99)")
        case "rev-parse HEAD":
          return ok("def456\n")
        case "push upstream main":
          return ok("To upstream\n   remote-head-sha..def456  main -> main")
        default:
          return fail(`unexpected git call: ${command}`)
      }
    })

    const result = await publishAction(context({
      remote: "upstream",
      source: "mo/issue-99",
      target: "main",
      message: "Complete issue #99",
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    // Push targets the configured `upstream` remote, not the workflow
    // workspace's local origin. The push is issued against the landing
    // workspace path.
    expect(landingCalls(calls)).toContain("push upstream main")
    expect(landingCalls(calls)).toContain("fetch upstream main")
    expect(landingCalls(calls)).toContain("merge --ff-only upstream/main")
    expect(workspaceCalls(calls)).not.toContain("push upstream main")
    expect(output).toMatchObject({
      kind: "publish",
      pushed: true,
      landedCommit: "def456",
      failureKind: null,
    })
  })

  it("NoCheckoutOrPushHitsWorkflowWorkspace_BranchStable", async () => {
    installLandingWorkspace()
    const calls = installGit(async () => fail(""))
    setDeliveryGitRunnerForTest(async (workDir, args) => {
      const command = args.join(" ")
      calls.push({ workDir, args: [...args] })
      switch (command) {
        case "rev-parse mo/issue-99":
          return ok("source-sha\n")
        case "fetch origin main":
          return ok("")
        case "rev-parse origin/main":
          return ok("remote-head-sha\n")
        case "checkout main":
          return ok("Switched to branch 'main'")
        case "status --porcelain":
          return ok("")
        case "merge --ff-only origin/main":
          return ok("Already up to date.")
        case "merge-base --is-ancestor origin/main mo/issue-99":
          return ok("")
        case "merge --squash mo/issue-99":
          return ok("Squash commit -- not updating HEAD")
        case "commit -m Push action issue (#99) -m mo/issue-99 into main":
          return ok("[main def456] Push action issue (#99)")
        case "rev-parse HEAD":
          return ok("def456\n")
        case "push origin main":
          return ok("To https://example.com/repo.git\n   remote-head-sha..def456  main -> main")
        default:
          return fail(`unexpected git call: ${command}`)
      }
    })

    await publishAction(context({
      source: "mo/issue-99",
      target: "main",
      message: "Complete issue #99",
    }))

    // Branch-stability invariant: no `checkout <baseBranch>`,
    // `merge --squash`, `commit`, or `push` was issued against the
    // workflow workspace. The only operation against the workflow
    // workspace is the read-only source-anchor check.
    const workspaceCmdSet = new Set(workspaceCalls(calls))
    expect(workspaceCmdSet.has("checkout main")).toBe(false)
    expect(workspaceCmdSet.has("merge --squash mo/issue-99")).toBe(false)
    expect(workspaceCmdSet.has("commit -m Push action issue (#99) -m mo/issue-99 into main")).toBe(false)
    expect(workspaceCmdSet.has("push origin main")).toBe(false)
  })
})
