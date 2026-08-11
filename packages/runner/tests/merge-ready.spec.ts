import { describe, expect, it as vitestIt } from "vitest"
import { mergeReadyAction } from "../src/actions/merge-ready.js"
import type { JsonObject } from "../src/core/types.js"
import type { ActionTestContext as ActionContext } from "./support/action-test-context.js"
import { callAction } from "./support/call-action.js"
import type { RunnerFileSystem, RunnerGitRunner } from "../src/system/filesystem.js"
import { MemoryFileSystem } from "./support/memory-filesystem.js"
import { withTestRunnerResources } from "./support/test-resources.js"

type WorkspaceCall = { workDir: string; args: string[] }

const WORKSPACE_PATH = "/workspace/issue-217"

type MergeReadyTestResources = { fileSystem: RunnerFileSystem; deliveryGitRunner?: RunnerGitRunner }

function it(name: string, body: (resources: MergeReadyTestResources) => Promise<void> | void): void {
  vitestIt(name, async () => {
    const resources: MergeReadyTestResources = { fileSystem: new MemoryFileSystem() }
    await withTestRunnerResources(async () => await body(resources), resources)
  })
}

function installGit(resources: MergeReadyTestResources, respond: (call: WorkspaceCall, history: WorkspaceCall[]) => { success: boolean; stdout: string; stderr: string; exitCode: number; combinedOutput: string } | Promise<{ success: boolean; stdout: string; stderr: string; exitCode: number; combinedOutput: string }>) {
  const calls: WorkspaceCall[] = []
  resources.deliveryGitRunner = async (workDir, args) => {
    const record: WorkspaceCall = { workDir, args: [...args] }
    calls.push(record)
    return await respond(record, calls)
  }
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

function context(withOverrides: JsonObject = {}, variables: JsonObject = {}): ActionContext {
  return {
    workflowRunId: "wr-merge-ready-1",
    workId: "integrate:merge-ready.1",
    workType: "task",
    stage: "integrate",
    title: "Merge readiness check",
    uses: "mohist/merge-ready",
     with: { baseBranch: "main", source: "mohist/run-wr-merge-ready-1", remote: "origin", ...withOverrides },
    variables: {
      project: { path: WORKSPACE_PATH },
      issue: { title: "Merge ready issue", number: 217 },
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
    issueNumber: 217,
    signal: new AbortController().signal,
    writeVars: async () => {},
  }
}

describe("mohist/merge-ready (ref-safe, is-ancestor)", () => {
  it("PreparedCandidate_ReportsCanMergeTrue", async (resources) => {
    const calls = installGit(resources, async (_call, history) => {
      const command = history[history.length - 1].args.join(" ")
      switch (command) {
        case "rev-parse origin/main":
          return ok("base-sha\n")
        case "rev-parse mohist/run-wr-merge-ready-1":
          return ok("candidate-head-sha\n")
        case "merge-base origin/main mohist/run-wr-merge-ready-1":
          return ok("merge-base-sha\n")
        case "merge-base --is-ancestor origin/main mohist/run-wr-merge-ready-1":
          return ok("")
        default:
          return fail(`unexpected git call: ${command}`)
      }
    })

    const result = await callAction(mergeReadyAction, context())
    const output = result.output as Record<string, unknown>

    expect(result.error).toBeUndefined()
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
    expect(new Date(output.checkedAt as string).toString()).not.toBe("Invalid Date")
    expect(output.error ?? null).toBeNull()

    const cmds = workspaceCalls(calls)
    expect(cmds).toContain("rev-parse origin/main")
    expect(cmds).toContain("rev-parse mohist/run-wr-merge-ready-1")
    expect(cmds).toContain("merge-base origin/main mohist/run-wr-merge-ready-1")
    expect(cmds).toContain("merge-base --is-ancestor origin/main mohist/run-wr-merge-ready-1")
  })

  it("BehindBaseCandidate_ReportsCanMergeFalseAndRebaseRequired", async (resources) => {
    const calls = installGit(resources, async (_call, history) => {
      const command = history[history.length - 1].args.join(" ")
      switch (command) {
        case "rev-parse origin/main":
          return ok("base-sha\n")
        case "rev-parse mohist/run-wr-merge-ready-1":
          return ok("candidate-head-sha\n")
        case "merge-base origin/main mohist/run-wr-merge-ready-1":
          return ok("merge-base-sha\n")
        case "merge-base --is-ancestor origin/main mohist/run-wr-merge-ready-1":
          return fail("")
        default:
          return fail(`unexpected git call: ${command}`)
      }
    })

    const result = await callAction(mergeReadyAction, context())
    expect(result.error).toMatchObject({ code: "merge-not-ready" })
    expect(result.error?.message).toContain("does not contain the latest 'origin/main' tip")
    expect(result.error?.message).toContain("rebase is required")

    const cmds = workspaceCalls(calls)
    expect(cmds).toContain("merge-base --is-ancestor origin/main mohist/run-wr-merge-ready-1")
  })

  it("NoCheckoutOrMergeSquashInWorkspace_BranchStable", async (resources) => {
    const calls = installGit(resources, async (_call, history) => {
      const command = history[history.length - 1].args.join(" ")
      switch (command) {
        case "rev-parse origin/main":
          return ok("base-sha\n")
        case "rev-parse mohist/run-wr-merge-ready-1":
          return ok("candidate-head-sha\n")
        case "merge-base origin/main mohist/run-wr-merge-ready-1":
          return ok("merge-base-sha\n")
        case "merge-base --is-ancestor origin/main mohist/run-wr-merge-ready-1":
          return ok("")
        default:
          return fail(`unexpected git call: ${command}`)
      }
    })

    await callAction(mergeReadyAction, context())

    const cmds = new Set(workspaceCalls(calls))
    expect(cmds.has("checkout main")).toBe(false)
    expect(cmds.has("merge --squash --no-commit mohist/run-wr-merge-ready-1")).toBe(false)
    expect(cmds.has("merge --squash --no-commit HEAD")).toBe(false)
    expect(cmds.has("merge --squash mohist/run-wr-merge-ready-1")).toBe(false)
    expect(cmds.has("fetch origin main")).toBe(false)
    expect(cmds.has("reset --hard origin/main")).toBe(false)
  })

  it("BaseRefRevParseFails_ReportsCanMergeFalseWithBaseError", async (resources) => {
    const calls = installGit(resources, async (_call, history) => {
      const command = history[history.length - 1].args.join(" ")
      switch (command) {
        case "rev-parse origin/main":
          return fail("fatal: ambiguous argument 'origin/main'")
        default:
          return fail(`unexpected git call: ${command}`)
      }
    })

    const result = await callAction(mergeReadyAction, context())
    expect(result.error).toMatchObject({ code: "merge-not-ready" })
    expect(result.error?.message).toContain("Could not resolve base branch 'origin/main'")

    const cmds = workspaceCalls(calls)
    expect(cmds).toEqual(["rev-parse origin/main"])
  })

  it("SourceRevParseFails_ReportsCanMergeFalseWithSourceError", async (resources) => {
    const calls = installGit(resources, async (_call, history) => {
      const command = history[history.length - 1].args.join(" ")
      switch (command) {
        case "rev-parse origin/main":
          return ok("base-sha\n")
        case "rev-parse mohist/run-wr-merge-ready-1":
          return fail("fatal: unknown revision")
        default:
          return fail(`unexpected git call: ${command}`)
      }
    })

    const result = await callAction(mergeReadyAction, context())
    expect(result.error).toMatchObject({ code: "merge-not-ready", message: "Could not resolve source" })

    const cmds = workspaceCalls(calls)
    expect(cmds).toContain("rev-parse origin/main")
    expect(cmds).toContain("rev-parse mohist/run-wr-merge-ready-1")
  })

  it("ExplicitSourceOverridesDefault", async (resources) => {
    const calls = installGit(resources, async (_call, history) => {
      const command = history[history.length - 1].args.join(" ")
      switch (command) {
        case "rev-parse origin/main":
          return ok("base-sha\n")
        case "rev-parse custom-source":
          return ok("custom-head-sha\n")
        case "merge-base origin/main custom-source":
          return ok("custom-merge-base\n")
        case "merge-base --is-ancestor origin/main custom-source":
          return ok("")
        default:
          return fail(`unexpected git call: ${command}`)
      }
    })

    const result = await callAction(mergeReadyAction, context(
      { source: "custom-source" },
      { repository: { gitUrl: "https://example.com/repo.git", baseBranch: "main" } },
    ))
    const output = result.output as Record<string, unknown>

    expect(result.error).toBeUndefined()
    expect(output.canMerge).toBe(true)
    expect(output.candidateHeadSha).toBe("custom-head-sha")
    expect(output.mergeBaseSha).toBe("custom-merge-base")

    const cmds = workspaceCalls(calls)
    expect(cmds).toContain("rev-parse custom-source")
    expect(cmds).toContain("merge-base origin/main custom-source")
    expect(cmds).toContain("merge-base --is-ancestor origin/main custom-source")
    expect(cmds).not.toContain("rev-parse mohist/run-wr-merge-ready-1")
  })

  it("ExplicitRemoteOverridesDefault", async (resources) => {
    const calls = installGit(resources, async (_call, history) => {
      const command = history[history.length - 1].args.join(" ")
      switch (command) {
        case "rev-parse upstream/main":
          return ok("upstream-base-sha\n")
        case "rev-parse mohist/run-wr-merge-ready-1":
          return ok("candidate-head-sha\n")
        case "merge-base upstream/main mohist/run-wr-merge-ready-1":
          return ok("merge-base-sha\n")
        case "merge-base --is-ancestor upstream/main mohist/run-wr-merge-ready-1":
          return ok("")
        default:
          return fail(`unexpected git call: ${command}`)
      }
    })

    const result = await callAction(mergeReadyAction, context(
      { remote: "upstream" },
      { repository: { gitUrl: "https://example.com/repo.git", baseBranch: "main" } },
    ))
    const output = result.output as Record<string, unknown>

    expect(result.error).toBeUndefined()
    expect(output.canMerge).toBe(true)
    expect(output.baseSha).toBe("upstream-base-sha")

    const cmds = workspaceCalls(calls)
    expect(cmds).toContain("rev-parse upstream/main")
    expect(cmds).toContain("merge-base --is-ancestor upstream/main mohist/run-wr-merge-ready-1")
    expect(cmds).not.toContain("rev-parse origin/main")
  })

  it("SourceDefaultsToWorkspaceBranchWhenNotProvided", async (resources) => {
    const calls = installGit(resources, async (_call, history) => {
      const command = history[history.length - 1].args.join(" ")
      switch (command) {
        case "rev-parse origin/main":
          return ok("base-sha\n")
        case "rev-parse mohist/run-wr-merge-ready-1":
          return ok("candidate-head-sha\n")
        case "merge-base origin/main mohist/run-wr-merge-ready-1":
          return ok("merge-base-sha\n")
        case "merge-base --is-ancestor origin/main mohist/run-wr-merge-ready-1":
          return ok("")
        default:
          return fail(`unexpected git call: ${command}`)
      }
    })

    await callAction(mergeReadyAction, context())

    const cmds = workspaceCalls(calls)
    expect(cmds).toContain("rev-parse mohist/run-wr-merge-ready-1")
    expect(cmds).toContain("merge-base --is-ancestor origin/main mohist/run-wr-merge-ready-1")
    expect(cmds).not.toContain("rev-parse HEAD")
  })

  it("MissingSource_DoesNotUseWorkspaceVariableOrRunGit", async (resources) => {
    const calls = installGit(resources, async () => { throw new Error("git must not run") })
    const result = await callAction(mergeReadyAction, context({ source: null }, {
      workspace: { path: WORKSPACE_PATH, branch: "different-branch", changeDir: null },
    }))

    expect(result.error).toMatchObject({ code: "invalid-input" })
    expect(result.error?.message).toContain("source")
    expect(calls).toEqual([])
  })

  it("PreflightIsRefOnly_NoWorkingTreeMutation", async (resources) => {
    const calls = installGit(resources, async (_call, history) => {
      const command = history[history.length - 1].args.join(" ")
      switch (command) {
        case "rev-parse origin/main":
          return ok("base-sha\n")
        case "rev-parse mohist/run-wr-merge-ready-1":
          return ok("candidate-head-sha\n")
        case "merge-base origin/main mohist/run-wr-merge-ready-1":
          return ok("merge-base-sha\n")
        case "merge-base --is-ancestor origin/main mohist/run-wr-merge-ready-1":
          return ok("")
        default:
          return fail(`unexpected git call: ${command}`)
      }
    })

    await callAction(mergeReadyAction, context())

    const cmds = workspaceCalls(calls)
    for (const cmd of cmds) {
      expect(cmd).not.toMatch(/^checkout\b/)
      expect(cmd).not.toMatch(/\bmerge --squash\b/)
      expect(cmd).not.toMatch(/^reset\b/)
      expect(cmd).not.toMatch(/^clean\b/)
      expect(cmd).not.toMatch(/^add\b/)
      expect(cmd).not.toMatch(/^commit\b/)
      expect(cmd).not.toMatch(/^fetch\b/)
      expect(cmd).not.toMatch(/^clone\b/)
    }
  })

  it("NoLandingWorkspaceCreated_NoCloneIssued", async (resources) => {
    const calls = installGit(resources, async (_call, history) => {
      const command = history[history.length - 1].args.join(" ")
      switch (command) {
        case "rev-parse origin/main":
          return ok("base-sha\n")
        case "rev-parse mohist/run-wr-merge-ready-1":
          return ok("candidate-head-sha\n")
        case "merge-base origin/main mohist/run-wr-merge-ready-1":
          return ok("merge-base-sha\n")
        case "merge-base --is-ancestor origin/main mohist/run-wr-merge-ready-1":
          return ok("")
        default:
          return fail(`unexpected git call: ${command}`)
      }
    })

    await callAction(mergeReadyAction, context())

    const workDirs = new Set(calls.map((c) => c.workDir))
    expect(workDirs.size).toBe(1)
    expect(workDirs.has(WORKSPACE_PATH)).toBe(true)

    for (const call of calls) {
      expect(call.args[0]).not.toBe("clone")
    }
  })
})
