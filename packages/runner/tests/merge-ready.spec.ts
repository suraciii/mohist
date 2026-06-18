import { afterEach, describe, expect, it } from "vitest"
import {
  mergeReadyAction,
  setDeliveryGitRunnerForTest,
  setDeliveryWorkspaceManagerForTest,
} from "../src/actions/registry.js"
import type { LandingWorkspaceInfo } from "../src/runtime/workspace.js"
import type { DeliveryWorkspaceManager } from "../src/actions/registry.js"
import type { ActionContext, JsonObject } from "../src/core/types.js"

type WorkspaceCall = { workDir: string; args: string[] }

const WORKSPACE_PATH = "/workspace/issue-150"
const LANDING_PATH = "/landing/wr-merge-ready-1"

afterEach(() => {
  setDeliveryGitRunnerForTest(null)
  setDeliveryWorkspaceManagerForTest(null)
})

function installLandingWorkspace(landingOverrides: Partial<LandingWorkspaceInfo> = {}) {
  const landing: LandingWorkspaceInfo = {
    path: LANDING_PATH,
    runId: "wr-merge-ready-1",
    runBranch: "mohist/run-wr-merge-ready-1",
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
    workflowRunId: "wr-merge-ready-1",
    workId: "integrate:merge-ready.1",
    workType: "task",
    stage: "integrate",
    title: "Merge readiness check",
    uses: "mohist/merge-ready",
    with: withOverrides,
    variables: {
      project: { path: WORKSPACE_PATH },
      issue: { title: "Merge ready issue", number: 150 },
      repository: {
        gitUrl: "https://example.com/repo.git",
        baseBranch: "main",
        name: "master",
      },
      mohist: { runId: "wr-merge-ready-1" },
      workspace: {
        path: WORKSPACE_PATH,
        branch: "mohist/run-wr-merge-ready-1",
        changeDir: null,
      },
      ...variables,
    },
    workDir: WORKSPACE_PATH,
    issueNumber: 150,
    signal: new AbortController().signal,
  }
}

describe("mohist/merge-ready (ref-safe, landing-workspace scoped)", () => {
  it("CleanCandidate_ReportsCanMergeTrueAndKeepsWorkflowWorkspaceUntouched", async () => {
    installLandingWorkspace()
    const calls = installGit(async (_call, history) => {
      const command = history[history.length - 1].args.join(" ")
      switch (command) {
        case "rev-parse main":
          return ok("base-sha\n")
        case "rev-parse HEAD":
          return ok("candidate-head-sha\n")
        case "merge-base main HEAD":
          return ok("merge-base-sha\n")
        case "fetch origin main":
          return ok("From https://example.com/repo\n * branch            main       -> FETCH_HEAD")
        case "checkout main":
          return ok("Switched to branch 'main'")
        case "merge --squash --no-commit HEAD":
          return ok("Squash commit -- not updating HEAD")
        case "reset --hard origin/main":
          return ok("HEAD is now at origin/main")
        default:
          return fail(`unexpected git call: ${command}`)
      }
    })

    const result = await mergeReadyAction(context({ baseBranch: "main", source: "HEAD" }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(output).toMatchObject({
      kind: "merge-ready",
      strategy: "squash",
      targetBranch: "main",
      baseSha: "base-sha",
      candidateHeadSha: "candidate-head-sha",
      mergeBaseSha: "merge-base-sha",
      canMerge: true,
      conflictFiles: [],
    })
    expect(typeof output.checkedAt).toBe("string")
    expect(new Date(output.checkedAt).toString()).not.toBe("Invalid Date")

    // The squash-merge probe and the base-branch checkout run only in
    // the landing workspace — never in the workflow workspace.
    expect(landingCalls(calls)).toContain("checkout main")
    expect(landingCalls(calls)).toContain("merge --squash --no-commit HEAD")
    expect(workspaceCalls(calls)).not.toContain("checkout main")
    expect(workspaceCalls(calls)).not.toContain("merge --squash --no-commit HEAD")

    // The workflow workspace's only git calls are read-only rev-parse/merge-base.
    const workflowCmdSet = new Set(workspaceCalls(calls))
    expect(workflowCmdSet.has("rev-parse main")).toBe(true)
    expect(workflowCmdSet.has("rev-parse HEAD")).toBe(true)
    expect(workflowCmdSet.has("merge-base main HEAD")).toBe(true)
  })

  it("ConflictingCandidate_ReportsConflictFilesAndCanMergeFalse", async () => {
    installLandingWorkspace()
    const calls = installGit(async (_call, history) => {
      const command = history[history.length - 1].args.join(" ")
      switch (command) {
        case "rev-parse main":
          return ok("base-sha\n")
        case "rev-parse HEAD":
          return ok("candidate-head-sha\n")
        case "merge-base main HEAD":
          return ok("merge-base-sha\n")
        case "fetch origin main":
          return ok("")
        case "checkout main":
          return ok("Switched to branch 'main'")
        case "merge --squash --no-commit HEAD":
          return fail("CONFLICT (content): Merge conflict in packages/runner/src/actions/registry.ts\nCONFLICT (content): Merge conflict in packages/runner/src/runtime/workspace.ts\nAutomatic merge failed; fix conflicts and then commit the result.")
        case "diff --name-only --diff-filter=U":
          return ok("packages/runner/src/actions/registry.ts\npackages/runner/src/runtime/workspace.ts\n")
        case "reset --hard origin/main":
          return ok("HEAD is now at origin/main")
        default:
          return fail(`unexpected git call: ${command}`)
      }
    })

    const result = await mergeReadyAction(context({ baseBranch: "main", source: "HEAD" }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(output).toMatchObject({
      kind: "merge-ready",
      strategy: "squash",
      targetBranch: "main",
      baseSha: "base-sha",
      candidateHeadSha: "candidate-head-sha",
      mergeBaseSha: "merge-base-sha",
      canMerge: false,
      conflictFiles: [
        "packages/runner/src/actions/registry.ts",
        "packages/runner/src/runtime/workspace.ts",
      ],
    })
    expect(output.error).toContain("CONFLICT")

    // Conflict files captured before the landing-workspace reset.
    const diffIndex = landingCalls(calls).indexOf("diff --name-only --diff-filter=U")
    const resetIndex = landingCalls(calls).indexOf("reset --hard origin/main")
    expect(diffIndex).toBeGreaterThan(-1)
    expect(resetIndex).toBeGreaterThan(-1)
    expect(diffIndex).toBeLessThan(resetIndex)

    // No base-branch checkout or merge --squash in the workflow workspace.
    expect(workspaceCalls(calls)).not.toContain("checkout main")
    expect(workspaceCalls(calls)).not.toContain("merge --squash --no-commit HEAD")
  })

  it("CleanupFailure_DoesNotFlipDetectedConflictIntoPassingResult", async () => {
    installLandingWorkspace()
    const calls = installGit(async (_call, history) => {
      const command = history[history.length - 1].args.join(" ")
      switch (command) {
        case "rev-parse main":
          return ok("base-sha\n")
        case "rev-parse HEAD":
          return ok("candidate-head-sha\n")
        case "merge-base main HEAD":
          return ok("merge-base-sha\n")
        case "fetch origin main":
          return ok("")
        case "checkout main":
          return ok("Switched to branch 'main'")
        case "merge --squash --no-commit HEAD":
          return fail("CONFLICT (content): Merge conflict in foo.txt\nAutomatic merge failed; fix conflicts and then commit the result.")
        case "diff --name-only --diff-filter=U":
          return ok("foo.txt\n")
        case "reset --hard origin/main":
          return ok("HEAD is now at origin/main")
        default:
          return fail(`unexpected git call: ${command}`)
      }
    })
    // Dispose fails — but the preflight outcome was already captured.
    setDeliveryWorkspaceManagerForTest({
      createLandingWorkspace: async () => ({
        path: LANDING_PATH,
        runId: "wr-merge-ready-1",
        runBranch: "mohist/run-wr-merge-ready-1",
        baseBranch: "main",
        gitUrl: "https://example.com/repo.git",
      }),
      disposeLandingWorkspace: async () => ({ path: LANDING_PATH, disposed: false, error: "rm -rf failed" }),
    })

    const result = await mergeReadyAction(context({ baseBranch: "main", source: "HEAD" }))
    const output = JSON.parse(result.output ?? "{}")

    // The detected conflict is preserved: canMerge must still be false
    // and the conflict file list must still be populated.
    expect(result.status).toBe("failure")
    expect(output.canMerge).toBe(false)
    expect(output.conflictFiles).toEqual(["foo.txt"])
    expect(output.error).toContain("CONFLICT")

    // Sanity: the merge probe ran in the landing workspace and not in
    // the workflow workspace.
    expect(landingCalls(calls)).toContain("merge --squash --no-commit HEAD")
    expect(workspaceCalls(calls)).not.toContain("merge --squash --no-commit HEAD")
  })

  it("NoCheckoutOrMergeSquashInWorkflowWorkspace_BranchStable", async () => {
    installLandingWorkspace()
    const calls = installGit(async (_call, history) => {
      const command = history[history.length - 1].args.join(" ")
      switch (command) {
        case "rev-parse main":
          return ok("base-sha\n")
        case "rev-parse HEAD":
          return ok("candidate-head-sha\n")
        case "merge-base main HEAD":
          return ok("merge-base-sha\n")
        case "fetch origin main":
          return ok("")
        case "checkout main":
          return ok("Switched to branch 'main'")
        case "merge --squash --no-commit HEAD":
          return ok("Squash commit -- not updating HEAD")
        case "reset --hard origin/main":
          return ok("HEAD is now at origin/main")
        default:
          return fail(`unexpected git call: ${command}`)
      }
    })

    await mergeReadyAction(context({ baseBranch: "main", source: "HEAD" }))

    // Branch-stability invariant: the workflow workspace never sees
    // `checkout <baseBranch>` or `merge --squash ...`.
    const workflowCmdSet = new Set(workspaceCalls(calls))
    expect(workflowCmdSet.has("checkout main")).toBe(false)
    expect(workflowCmdSet.has("merge --squash --no-commit HEAD")).toBe(false)
  })

  it("LandingWorkspaceCreationFails_ReportsCanMergeFalseWithLandingError", async () => {
    setDeliveryWorkspaceManagerForTest({
      createLandingWorkspace: async () => {
        throw new Error("clone --shared refused")
      },
      disposeLandingWorkspace: async () => ({ path: LANDING_PATH, disposed: true }),
    })
    const calls = installGit(async (_call, history) => {
      const command = history[history.length - 1].args.join(" ")
      switch (command) {
        case "rev-parse main":
          return ok("base-sha\n")
        case "rev-parse HEAD":
          return ok("candidate-head-sha\n")
        case "merge-base main HEAD":
          return ok("merge-base-sha\n")
        default:
          return fail(`unexpected git call: ${command}`)
      }
    })

    const result = await mergeReadyAction(context({ baseBranch: "main", source: "HEAD" }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(output.canMerge).toBe(false)
    expect(output.error).toContain("clone --shared refused")
    // No base-branch checkout or merge --squash was attempted in any
    // workspace — the preflight aborted before the landing workspace
    // could be used.
    expect(landingCalls(calls)).toEqual([])
    expect(workspaceCalls(calls)).not.toContain("checkout main")
    expect(workspaceCalls(calls)).not.toContain("merge --squash --no-commit HEAD")
  })

  it("FetchFailsInLandingWorkspace_ReportsCanMergeFalseWithFetchError", async () => {
    installLandingWorkspace()
    const calls = installGit(async (_call, history) => {
      const command = history[history.length - 1].args.join(" ")
      switch (command) {
        case "rev-parse main":
          return ok("base-sha\n")
        case "rev-parse HEAD":
          return ok("candidate-head-sha\n")
        case "merge-base main HEAD":
          return ok("merge-base-sha\n")
        case "fetch origin main":
          return fail("fatal: could not resolve host example.com")
        default:
          return fail(`unexpected git call: ${command}`)
      }
    })

    const result = await mergeReadyAction(context({ baseBranch: "main", source: "HEAD" }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(output.canMerge).toBe(false)
    expect(output.error).toContain("could not resolve host example.com")
    expect(landingCalls(calls)).toContain("fetch origin main")
    expect(landingCalls(calls)).not.toContain("checkout main")
    expect(landingCalls(calls)).not.toContain("merge --squash --no-commit HEAD")
  })

  it("PreflightRunsInLandingWorkspace_NotInWorkflowWorkspace", async () => {
    installLandingWorkspace()
    const calls = installGit(async (_call, history) => {
      const command = history[history.length - 1].args.join(" ")
      switch (command) {
        case "rev-parse main":
          return ok("base-sha\n")
        case "rev-parse HEAD":
          return ok("candidate-head-sha\n")
        case "merge-base main HEAD":
          return ok("merge-base-sha\n")
        case "fetch origin main":
          return ok("")
        case "checkout main":
          return ok("Switched to branch 'main'")
        case "merge --squash --no-commit HEAD":
          return ok("Squash commit -- not updating HEAD")
        case "reset --hard origin/main":
          return ok("HEAD is now at origin/main")
        default:
          return fail(`unexpected git call: ${command}`)
      }
    })

    await mergeReadyAction(context({ baseBranch: "main", source: "HEAD" }))

    // Every preflight git call lands on the landing workspace path.
    const landingWorkdirs = new Set(
      calls.filter((c) => c.workDir === LANDING_PATH).map((c) => c.args.join(" "))
    )
    expect(landingWorkdirs.has("fetch origin main")).toBe(true)
    expect(landingWorkdirs.has("checkout main")).toBe(true)
    expect(landingWorkdirs.has("merge --squash --no-commit HEAD")).toBe(true)
    expect(landingWorkdirs.has("reset --hard origin/main")).toBe(true)

    // And none of those calls touch the workflow workspace.
    expect(workspaceCalls(calls)).not.toContain("fetch origin main")
    expect(workspaceCalls(calls)).not.toContain("checkout main")
    expect(workspaceCalls(calls)).not.toContain("merge --squash --no-commit HEAD")
    expect(workspaceCalls(calls)).not.toContain("reset --hard origin/main")
  })
})