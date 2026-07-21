import { afterEach, describe, expect, it } from "vitest"
import { createDefaultRegistry } from "../src/actions/registry.js"
import {
  setWorkspacePrepareExistsCheckerForTest,
  setWorkspacePrepareGitRunnerForTest,
  workspacePrepareAction,
} from "../src/actions/workspace-prepare.js"
import { callAction } from "./support/call-action.js"
import type { ActionContext, JsonObject } from "../src/core/types.js"

type GitCall = { workDir: string; args: string[] }

const WORKSPACE_PATH = "/workspace"
const EXPECTED_BRANCH = "mohist/run-wr-prepare-1"

afterEach(() => {
  setWorkspacePrepareGitRunnerForTest(null)
  setWorkspacePrepareExistsCheckerForTest(null)
})

function ok(stdout: string) {
  return { success: true, stdout, stderr: "", exitCode: 0, combinedOutput: stdout.trim() }
}

function fail(stderr: string) {
  return { success: false, stdout: "", stderr, exitCode: 1, combinedOutput: stderr }
}

function installGit(respond: (call: GitCall, history: GitCall[]) => { success: boolean; stdout: string; stderr: string; exitCode: number; combinedOutput: string } | Promise<{ success: boolean; stdout: string; stderr: string; exitCode: number; combinedOutput: string }>) {
  const calls: GitCall[] = []
  setWorkspacePrepareGitRunnerForTest(async (workDir, args) => {
    const record: GitCall = { workDir, args: [...args] }
    calls.push(record)
    return await respond(record, calls)
  })
  return calls
}

function commandOf(call: GitCall): string {
  return call.args.join(" ")
}

function hasCommand(calls: GitCall[], command: string): boolean {
  return calls.some((call) => commandOf(call) === command)
}

function hasCommandStartingWith(calls: GitCall[], prefix: string): boolean {
  return calls.some((call) => commandOf(call).startsWith(prefix))
}

function context(variables: JsonObject = {}): ActionContext {
  return {
    workflowRunId: "wr-prepare-1",
    workId: "workspace-prepare",
    workType: "task",
    stage: "build",
    title: "Prepare workspace",
    uses: "mohist/workspace-prepare",
     with: { expectedBranch: EXPECTED_BRANCH },
    variables: {
      workspace: { path: WORKSPACE_PATH, branch: EXPECTED_BRANCH, changeDir: null },
      ...variables,
    },
    workDir: WORKSPACE_PATH,
    signal: new AbortController().signal,
    writeVars: async () => {},
  }
}

function cleanProbeResponses(extra: (call: GitCall, history: GitCall[]) => { success: boolean; stdout: string; stderr: string; exitCode: number; combinedOutput: string } | null = () => null) {
  return async (call: GitCall, history: GitCall[]) => {
    const command = commandOf(call)
    const override = extra(call, history)
    if (override) return override
    switch (command) {
      case "rev-parse --git-path rebase-merge":
        return ok("/workspace/.git/rebase-merge\n")
      case "rev-parse --git-path rebase-apply":
        return ok("/workspace/.git/rebase-apply\n")
      case "rev-parse --git-path MERGE_HEAD":
        return ok("/workspace/.git/MERGE_HEAD\n")
      case "rev-parse --git-path CHERRY_PICK_HEAD":
        return ok("/workspace/.git/CHERRY_PICK_HEAD\n")
      case "rev-parse HEAD":
        return ok("clean-head-sha\n")
      case "rev-parse --abbrev-ref HEAD":
        return ok(`${EXPECTED_BRANCH}\n`)
      case "status --porcelain":
        return ok("")
      default:
        return fail(`unexpected git call: ${command}`)
    }
  }
}

describe("mohist/workspace-prepare", () => {
  it("registers workspacePrepareAction under mohist/workspace-prepare", () => {
    const registry = createDefaultRegistry()
    const resolved = registry.resolve("mohist/workspace-prepare")
    expect(resolved.kind).toBe("definition")
    if (resolved.kind === "definition") {
      expect(resolved.definition.manifest.name).toBe("mohist/workspace-prepare")
    }
  })

  it("FastPass_CleanWorkspace_IssuesNoMutationCommands", async () => {
    setWorkspacePrepareExistsCheckerForTest(() => false)
    const calls = installGit(cleanProbeResponses())

    const result = await callAction(workspacePrepareAction, context())
    const output = result.output as Record<string, unknown>

    expect(result.error).toBeUndefined()
    expect(output).toMatchObject({
      kind: "workspace-prepare",
      status: "success",
      expectedBranch: EXPECTED_BRANCH,
      head: { commit: "clean-head-sha", ref: EXPECTED_BRANCH },
      residual: { rebaseMerge: false, rebaseApply: false, mergeHead: false, cherryPickHead: false },
      porcelain: "",
      step: null,
    })

    expect(hasCommand(calls, "rebase --abort")).toBe(false)
    expect(hasCommand(calls, "merge --abort")).toBe(false)
    expect(hasCommand(calls, "cherry-pick --abort")).toBe(false)
    expect(hasCommandStartingWith(calls, "checkout")).toBe(false)
    expect(hasCommand(calls, "reset --hard HEAD")).toBe(false)
    expect(hasCommand(calls, "clean -fd")).toBe(false)
  })

  it("UsesHostWorkDirAndExplicitBranchWhenVariablesDisagree", async () => {
    setWorkspacePrepareExistsCheckerForTest(() => false)
    const calls = installGit(cleanProbeResponses())

    const contextWithHiddenVariables: ActionContext = {
      ...context(),
      workDir: "/host-workspace",
      with: { expectedBranch: EXPECTED_BRANCH },
      variables: { workspace: { path: "/hidden-workspace", branch: "hidden-branch" } },
    }
    const result = await callAction(workspacePrepareAction, contextWithHiddenVariables)

    expect(result.error).toBeUndefined()
    expect(calls.every((call) => call.workDir === "/host-workspace")).toBe(true)
  })

  it("InitialStatusProbeFails_ReportsWorkspaceSetupFailure", async () => {
    setWorkspacePrepareExistsCheckerForTest(() => false)
    const calls = installGit(cleanProbeResponses((call) => {
      if (commandOf(call) === "status --porcelain") return fail("fatal: status unavailable")
      return null
    }))

    const result = await callAction(workspacePrepareAction, context())
    const output = result.output as Record<string, unknown>

    expect(result.error).toBeDefined()
    expect(result.error?.message).toContain("git status --porcelain failed")
    expect(hasCommand(calls, "reset --hard HEAD")).toBe(false)
    expect(hasCommandStartingWith(calls, "checkout")).toBe(false)
  })

  it("InitialHeadProbeFails_ReportsWorkspaceSetupFailure", async () => {
    setWorkspacePrepareExistsCheckerForTest(() => false)
    const calls = installGit(cleanProbeResponses((call) => {
      if (commandOf(call) === "rev-parse HEAD") return fail("fatal: bad HEAD")
      return null
    }))

    const result = await callAction(workspacePrepareAction, context())
    const output = result.output as Record<string, unknown>

    expect(result.error).toBeDefined()
    expect(result.error?.message).toContain("git rev-parse HEAD failed")
    expect(hasCommand(calls, "reset --hard HEAD")).toBe(false)
    expect(hasCommandStartingWith(calls, "checkout")).toBe(false)
  })

  it("InitialHeadRefProbeFails_ReportsWorkspaceSetupFailure", async () => {
    setWorkspacePrepareExistsCheckerForTest(() => false)
    const calls = installGit(cleanProbeResponses((call) => {
      if (commandOf(call) === "rev-parse --abbrev-ref HEAD") return fail("fatal: cannot resolve ref")
      return null
    }))

    const result = await callAction(workspacePrepareAction, context())
    const output = result.output as Record<string, unknown>

    expect(result.error).toBeDefined()
    expect(result.error?.message).toContain("git rev-parse --abbrev-ref HEAD failed")
    expect(hasCommand(calls, "reset --hard HEAD")).toBe(false)
    expect(hasCommandStartingWith(calls, "checkout")).toBe(false)
  })

  it("InitialResidualProbeFails_ReportsWorkspaceSetupFailure", async () => {
    setWorkspacePrepareExistsCheckerForTest(() => false)
    const calls = installGit(cleanProbeResponses((call) => {
      if (commandOf(call) === "rev-parse --git-path rebase-merge") return fail("fatal: git dir unreadable")
      return null
    }))

    const result = await callAction(workspacePrepareAction, context())
    const output = result.output as Record<string, unknown>

    expect(result.error).toBeDefined()
    expect(result.error?.message).toContain("git rev-parse --git-path rebase-merge failed")
    expect(hasCommand(calls, "reset --hard HEAD")).toBe(false)
    expect(hasCommandStartingWith(calls, "checkout")).toBe(false)
  })

  it("RebaseInProgress_AbortsAndReprobesBeforeCheckout", async () => {
    let rebaseStatePresent = true
    setWorkspacePrepareExistsCheckerForTest((path) => path.endsWith("rebase-merge") && rebaseStatePresent)
    const calls = installGit(cleanProbeResponses((call) => {
      if (commandOf(call) === "rebase --abort") {
        rebaseStatePresent = false
        return ok("")
      }
      return null
    }))

    const result = await callAction(workspacePrepareAction, context())
    const output = result.output as Record<string, unknown>

    expect(result.error).toBeUndefined()
    expect(hasCommand(calls, "rebase --abort")).toBe(true)
    expect(hasCommand(calls, "merge --abort")).toBe(false)
    expect(hasCommand(calls, "cherry-pick --abort")).toBe(false)
    expect(hasCommandStartingWith(calls, "checkout")).toBe(false)
    expect(hasCommand(calls, "reset --hard HEAD")).toBe(false)
    expect(hasCommand(calls, "clean -fd")).toBe(false)

    const abortIndex = calls.findIndex((c) => commandOf(c) === "rebase --abort")
    expect(abortIndex).toBeGreaterThanOrEqual(0)
    const resetIdx = calls.findIndex((c) => commandOf(c) === "reset --hard HEAD")
    const checkoutIdx = calls.findIndex((c) => commandOf(c).startsWith("checkout"))
    if (resetIdx >= 0) expect(abortIndex).toBeLessThan(resetIdx)
    if (checkoutIdx >= 0) expect(abortIndex).toBeLessThan(checkoutIdx)
    expect(output.residual as Record<string, unknown>).toMatchObject({ rebaseMerge: false, rebaseApply: false })
  })

  it("RebaseAbortFails_ReportsWorkspaceSetupFailure", async () => {
    setWorkspacePrepareExistsCheckerForTest((path) => path.endsWith("rebase-merge"))
    const calls = installGit(cleanProbeResponses((call) => {
      if (commandOf(call) === "rebase --abort") return fail("fatal: could not abort rebase")
      return null
    }))

    const result = await callAction(workspacePrepareAction, context())
    const output = result.output as Record<string, unknown>

    expect(result.error).toBeDefined()
    expect(result.error?.message).toContain("rebase --abort failed")
    expect(hasCommand(calls, "merge --abort")).toBe(false)
    expect(hasCommand(calls, "cherry-pick --abort")).toBe(false)
    expect(hasCommandStartingWith(calls, "checkout")).toBe(false)
    expect(hasCommand(calls, "reset --hard HEAD")).toBe(false)
    expect(hasCommand(calls, "clean -fd")).toBe(false)
  })

  it("RebaseStillInProgressAfterAbort_ReportsWorkspaceSetupFailure", async () => {
    setWorkspacePrepareExistsCheckerForTest((path) => path.endsWith("rebase-merge"))
    const calls = installGit(cleanProbeResponses((call) => {
      if (commandOf(call) === "rebase --abort") return ok("")
      return null
    }))

    const result = await callAction(workspacePrepareAction, context())
    const output = result.output as Record<string, unknown>

    expect(result.error).toBeDefined()
    expect(result.error?.message).toContain("still in progress")
    expect(hasCommand(calls, "merge --abort")).toBe(false)
  })

  it("MergeInProgress_AbortsMergeAndReprobes", async () => {
    let mergeStatePresent = true
    setWorkspacePrepareExistsCheckerForTest((path) => path.endsWith("MERGE_HEAD") && mergeStatePresent)
    const calls = installGit(cleanProbeResponses((call) => {
      if (commandOf(call) === "merge --abort") {
        mergeStatePresent = false
        return ok("")
      }
      return null
    }))

    const result = await callAction(workspacePrepareAction, context())
    const output = result.output as Record<string, unknown>

    expect(result.error).toBeUndefined()
    expect(hasCommand(calls, "merge --abort")).toBe(true)
    expect(hasCommand(calls, "rebase --abort")).toBe(false)
    expect(hasCommand(calls, "cherry-pick --abort")).toBe(false)
    expect((output.residual as Record<string, unknown>).mergeHead).toBe(false)
  })

  it("CherryPickInProgress_AbortsCherryPickAndReprobes", async () => {
    let cherryPickStatePresent = true
    setWorkspacePrepareExistsCheckerForTest((path) => path.endsWith("CHERRY_PICK_HEAD") && cherryPickStatePresent)
    const calls = installGit(cleanProbeResponses((call) => {
      if (commandOf(call) === "cherry-pick --abort") {
        cherryPickStatePresent = false
        return ok("")
      }
      return null
    }))

    const result = await callAction(workspacePrepareAction, context())
    const output = result.output as Record<string, unknown>

    expect(result.error).toBeUndefined()
    expect(hasCommand(calls, "cherry-pick --abort")).toBe(true)
    expect(hasCommand(calls, "rebase --abort")).toBe(false)
    expect(hasCommand(calls, "merge --abort")).toBe(false)
    expect((output.residual as Record<string, unknown>).cherryPickHead).toBe(false)
  })

  it("MergeAbortFails_ReportsWorkspaceSetupFailure", async () => {
    setWorkspacePrepareExistsCheckerForTest((path) => path.endsWith("MERGE_HEAD"))
    const calls = installGit(cleanProbeResponses((call) => {
      if (commandOf(call) === "merge --abort") return fail("fatal: could not abort merge")
      return null
    }))

    const result = await callAction(workspacePrepareAction, context())
    const output = result.output as Record<string, unknown>

    expect(result.error).toBeDefined()
    expect(hasCommand(calls, "rebase --abort")).toBe(false)
    expect(hasCommand(calls, "cherry-pick --abort")).toBe(false)
  })

  it("CherryPickAbortFails_ReportsWorkspaceSetupFailure", async () => {
    setWorkspacePrepareExistsCheckerForTest((path) => path.endsWith("CHERRY_PICK_HEAD"))
    const calls = installGit(cleanProbeResponses((call) => {
      if (commandOf(call) === "cherry-pick --abort") return fail("fatal: could not abort cherry-pick")
      return null
    }))

    const result = await callAction(workspacePrepareAction, context())
    const output = result.output as Record<string, unknown>

    expect(result.error).toBeDefined()
  })

  it("DetachedHead_ChecksOutExpectedBranch", async () => {
    setWorkspacePrepareExistsCheckerForTest(() => false)
    let checkoutIssued = false
    const calls = installGit(cleanProbeResponses((call) => {
      const command = commandOf(call)
      if (command === "rev-parse --abbrev-ref HEAD") {
        return ok(checkoutIssued ? `${EXPECTED_BRANCH}\n` : "HEAD\n")
      }
      if (command === `checkout ${EXPECTED_BRANCH}`) {
        checkoutIssued = true
        return ok(`Switched to branch '${EXPECTED_BRANCH}'\n`)
      }
      return null
    }))

    const result = await callAction(workspacePrepareAction, context())
    const output = result.output as Record<string, unknown>

    expect(result.error).toBeUndefined()
    expect(hasCommand(calls, `checkout ${EXPECTED_BRANCH}`)).toBe(true)
    expect((output.head as Record<string, unknown>).ref).toBe(EXPECTED_BRANCH)
    expect(hasCommand(calls, "reset --hard HEAD")).toBe(false)
    expect(hasCommand(calls, "clean -fd")).toBe(false)
  })

  it("DifferentBranch_ChecksOutExpectedBranch", async () => {
    setWorkspacePrepareExistsCheckerForTest(() => false)
    let checkoutIssued = false
    const calls = installGit(cleanProbeResponses((call) => {
      const command = commandOf(call)
      if (command === "rev-parse --abbrev-ref HEAD") {
        return ok(checkoutIssued ? `${EXPECTED_BRANCH}\n` : "feature/other\n")
      }
      if (command === `checkout ${EXPECTED_BRANCH}`) {
        checkoutIssued = true
        return ok(`Switched to branch '${EXPECTED_BRANCH}'\n`)
      }
      return null
    }))

    const result = await callAction(workspacePrepareAction, context())

    expect(result.error).toBeUndefined()
    expect(hasCommand(calls, `checkout ${EXPECTED_BRANCH}`)).toBe(true)
  })

  it("CheckoutFails_ReportsWorkspaceSetupFailure", async () => {
    setWorkspacePrepareExistsCheckerForTest(() => false)
    const calls = installGit(cleanProbeResponses((call) => {
      const command = commandOf(call)
      if (command === "rev-parse --abbrev-ref HEAD") return ok("HEAD\n")
      if (command === `checkout ${EXPECTED_BRANCH}`) return fail("fatal: path not found")
      return null
    }))

    const result = await callAction(workspacePrepareAction, context())
    const output = result.output as Record<string, unknown>

    expect(result.error).toBeDefined()
    expect(hasCommand(calls, "reset --hard HEAD")).toBe(false)
    expect(hasCommand(calls, "clean -fd")).toBe(false)
  })

  it("DirtyTreeOnDifferentBranch_ResetsAndCleansBeforeCheckout", async () => {
    setWorkspacePrepareExistsCheckerForTest(() => false)
    let cleaned = false
    let checkoutIssued = false
    const calls = installGit(cleanProbeResponses((call) => {
      const command = commandOf(call)
      if (command === "status --porcelain") return cleaned ? ok("") : ok(" M dirty-file.txt\n?? untracked.txt\n")
      if (command === "rev-parse --abbrev-ref HEAD") return ok(checkoutIssued ? `${EXPECTED_BRANCH}\n` : "feature/other\n")
      if (command === "reset --hard HEAD") return ok("HEAD is now clean\n")
      if (command === "clean -fd") {
        cleaned = true
        return ok("Removing untracked.txt\n")
      }
      if (command === `checkout ${EXPECTED_BRANCH}`) {
        if (!cleaned) return fail("error: Your local changes would be overwritten by checkout")
        checkoutIssued = true
        return ok(`Switched to branch '${EXPECTED_BRANCH}'\n`)
      }
      return null
    }))

    const result = await callAction(workspacePrepareAction, context())
    const output = result.output as Record<string, unknown>

    expect(result.error).toBeUndefined()
    expect((output.head as Record<string, unknown>).ref).toBe(EXPECTED_BRANCH)
    const resetIdx = calls.findIndex((c) => commandOf(c) === "reset --hard HEAD")
    const cleanIdx = calls.findIndex((c) => commandOf(c) === "clean -fd")
    const checkoutIdx = calls.findIndex((c) => commandOf(c) === `checkout ${EXPECTED_BRANCH}`)
    expect(resetIdx).toBeGreaterThanOrEqual(0)
    expect(cleanIdx).toBeGreaterThanOrEqual(0)
    expect(checkoutIdx).toBeGreaterThanOrEqual(0)
    expect(resetIdx).toBeLessThan(cleanIdx)
    expect(cleanIdx).toBeLessThan(checkoutIdx)
  })

  it("DirtyTree_ResetsAndCleans", async () => {
    setWorkspacePrepareExistsCheckerForTest(() => false)
    let porcelainDirty = true
    const calls = installGit(cleanProbeResponses((call) => {
      const command = commandOf(call)
      if (command === "status --porcelain") {
        return porcelainDirty ? ok(" M dirty-file.txt\n?? untracked.txt\n") : ok("")
      }
      if (command === "reset --hard HEAD") return ok("HEAD is now at clean-head-sha\n")
      if (command === "clean -fd") {
        porcelainDirty = false
        return ok("Removing dirty-file.txt\nRemoving untracked.txt\n")
      }
      return null
    }))

    const result = await callAction(workspacePrepareAction, context())
    const output = result.output as Record<string, unknown>

    expect(result.error).toBeUndefined()
    expect(hasCommand(calls, "reset --hard HEAD")).toBe(true)
    expect(hasCommand(calls, "clean -fd")).toBe(true)
    const resetIdx = calls.findIndex((c) => commandOf(c) === "reset --hard HEAD")
    const cleanIdx = calls.findIndex((c) => commandOf(c) === "clean -fd")
    expect(resetIdx).toBeGreaterThanOrEqual(0)
    expect(cleanIdx).toBeGreaterThanOrEqual(0)
    expect(resetIdx).toBeLessThan(cleanIdx)
    expect(output.porcelain).toBe("")
  })

  it("ResetFails_ReportsWorkspaceSetupFailure", async () => {
    setWorkspacePrepareExistsCheckerForTest(() => false)
    const calls = installGit(cleanProbeResponses((call) => {
      const command = commandOf(call)
      if (command === "status --porcelain") return ok(" M dirty-file.txt\n")
      if (command === "reset --hard HEAD") return fail("fatal: reset failed")
      return null
    }))

    const result = await callAction(workspacePrepareAction, context())
    const output = result.output as Record<string, unknown>

    expect(result.error).toBeDefined()
    expect(hasCommand(calls, "clean -fd")).toBe(false)
  })

  it("CleanFails_ReportsWorkspaceSetupFailure", async () => {
    setWorkspacePrepareExistsCheckerForTest(() => false)
    const calls = installGit(cleanProbeResponses((call) => {
      const command = commandOf(call)
      if (command === "status --porcelain") return ok(" M dirty-file.txt\n")
      if (command === "reset --hard HEAD") return ok("")
      if (command === "clean -fd") return fail("fatal: clean failed")
      return null
    }))

    const result = await callAction(workspacePrepareAction, context())
    const output = result.output as Record<string, unknown>

    expect(result.error).toBeDefined()
  })

  it("HealthVerifyFailure_DirtyTreeAfterCleanup_ReportsWorkspaceSetupFailure", async () => {
    setWorkspacePrepareExistsCheckerForTest(() => false)
    let resetIssued = false
    const calls = installGit(cleanProbeResponses((call) => {
      const command = commandOf(call)
      if (command === "status --porcelain") {
        return resetIssued ? ok(" M still-dirty.txt\n") : ok(" M dirty-file.txt\n")
      }
      if (command === "reset --hard HEAD") {
        resetIssued = true
        return ok("")
      }
      if (command === "clean -fd") return ok("")
      return null
    }))

    const result = await callAction(workspacePrepareAction, context())
    const output = result.output as Record<string, unknown>

    expect(result.error).toBeDefined()
  })

  it("HealthVerifyFailure_WrongBranchAfterCleanup_ReportsWorkspaceSetupFailure", async () => {
    setWorkspacePrepareExistsCheckerForTest(() => false)
    const calls = installGit(cleanProbeResponses((call) => {
      const command = commandOf(call)
      if (command === "rev-parse --abbrev-ref HEAD") return ok("feature/other\n")
      if (command === `checkout ${EXPECTED_BRANCH}`) return ok(`Switched to branch '${EXPECTED_BRANCH}'\n`)
      return null
    }))

    const result = await callAction(workspacePrepareAction, context())
    const output = result.output as Record<string, unknown>

    expect(result.error).toBeDefined()
    expect(hasCommand(calls, `checkout ${EXPECTED_BRANCH}`)).toBe(true)
  })

  it("HealthVerifyFailure_RebaseReappearsAfterCleanup_ReportsWorkspaceSetupFailure", async () => {
    let rebaseProbeCount = 0
    setWorkspacePrepareExistsCheckerForTest((path) => {
      if (!path.endsWith("rebase-merge")) return false
      rebaseProbeCount++
      return rebaseProbeCount !== 2
    })
    const calls = installGit(cleanProbeResponses((call) => {
      if (commandOf(call) === "rebase --abort") return ok("")
      return null
    }))

    const result = await callAction(workspacePrepareAction, context())
    const output = result.output as Record<string, unknown>

    expect(result.error).toBeDefined()
    expect(hasCommand(calls, "rebase --abort")).toBe(true)
  })

  it("NoNetworkOperations_NoFetchPullPush", async () => {
    setWorkspacePrepareExistsCheckerForTest(() => false)
    const calls = installGit(cleanProbeResponses())

    await callAction(workspacePrepareAction, context())

    for (const forbidden of ["fetch", "pull", "push", "clone", "remote", "ls-remote"]) {
      const hasNetwork = calls.some((call) => call.args[0] === forbidden)
      expect(hasNetwork).toBe(false)
    }
  })

  it("MissingExpectedBranch_ReportsResolveFailure", async () => {
    setWorkspacePrepareExistsCheckerForTest(() => false)
    const calls = installGit(cleanProbeResponses())

    const contextWithHiddenVariables: ActionContext = {
      ...context(),
      with: {},
      variables: { workspace: { path: WORKSPACE_PATH, branch: EXPECTED_BRANCH, changeDir: null } },
    }
    const result = await callAction(workspacePrepareAction, contextWithHiddenVariables)
    const output = result.output as Record<string, unknown>

    expect(result.error).toBeDefined()
    expect(hasCommandStartingWith(calls, "checkout")).toBe(false)
    expect(hasCommand(calls, "rebase --abort")).toBe(false)
    expect(hasCommand(calls, "reset --hard HEAD")).toBe(false)
    expect(hasCommand(calls, "clean -fd")).toBe(false)
  })

  it("FullPipeline_RebaseAbortThenResetClean_ProducesExpectedCallOrder", async () => {
    let rebaseStatePresent = true
    setWorkspacePrepareExistsCheckerForTest((path) => path.endsWith("rebase-merge") && rebaseStatePresent)
    let porcelainDirty = true
    const calls = installGit(cleanProbeResponses((call) => {
      const command = commandOf(call)
      if (command === "rebase --abort") {
        rebaseStatePresent = false
        return ok("")
      }
      if (command === "status --porcelain") {
        return porcelainDirty ? ok(" M dirty.txt\n") : ok("")
      }
      if (command === "reset --hard HEAD") return ok("")
      if (command === "clean -fd") {
        porcelainDirty = false
        return ok("")
      }
      return null
    }))

    const result = await callAction(workspacePrepareAction, context())
    const output = result.output as Record<string, unknown>

    expect(result.error).toBeUndefined()
    expect(output).toMatchObject({
      kind: "workspace-prepare",
      status: "success",
      expectedBranch: EXPECTED_BRANCH,
      head: { ref: EXPECTED_BRANCH },
      residual: { rebaseMerge: false, rebaseApply: false, mergeHead: false, cherryPickHead: false },
      porcelain: "",
    })

    const abortIdx = calls.findIndex((c) => commandOf(c) === "rebase --abort")
    const resetIdx = calls.findIndex((c) => commandOf(c) === "reset --hard HEAD")
    const cleanIdx = calls.findIndex((c) => commandOf(c) === "clean -fd")
    expect(abortIdx).toBeGreaterThanOrEqual(0)
    expect(resetIdx).toBeGreaterThanOrEqual(0)
    expect(cleanIdx).toBeGreaterThanOrEqual(0)
    expect(abortIdx).toBeLessThan(resetIdx)
    expect(resetIdx).toBeLessThan(cleanIdx)
  })

  it("LocalProbes_NoCommandTimeoutIsApplied", async () => {
    type RecordingGitCall = { workDir: string; args: string[]; timeoutMs: number | undefined }
    const calls: RecordingGitCall[] = []
    setWorkspacePrepareExistsCheckerForTest(() => false)
    setWorkspacePrepareGitRunnerForTest(async (workDir, args, _signal, options) => {
      calls.push({ workDir, args: [...args], timeoutMs: options?.timeoutMs })
      const command = args.join(" ")
      switch (command) {
        case "rev-parse --git-path rebase-merge":
          return ok("/workspace/.git/rebase-merge\n")
        case "rev-parse --git-path rebase-apply":
          return ok("/workspace/.git/rebase-apply\n")
        case "rev-parse --git-path MERGE_HEAD":
          return ok("/workspace/.git/MERGE_HEAD\n")
        case "rev-parse --git-path CHERRY_PICK_HEAD":
          return ok("/workspace/.git/CHERRY_PICK_HEAD\n")
        case "rev-parse HEAD":
          return ok("clean-head-sha\n")
        case "rev-parse --abbrev-ref HEAD":
          return ok(`${EXPECTED_BRANCH}\n`)
        case "status --porcelain":
          return ok("")
        default:
          return fail(`unexpected git call: ${command}`)
      }
    })

    await callAction(workspacePrepareAction, context())

    // All local-only git probes must keep no per-command timeout —
    // they cannot hang on the network and so run under the work-level
    // signal only.
    for (const call of calls) {
      expect(call.timeoutMs, `git call ${call.args.join(" ")} should have no timeoutMs`).toBeUndefined()
    }
    expect(calls.length).toBeGreaterThan(0)
  })
})
