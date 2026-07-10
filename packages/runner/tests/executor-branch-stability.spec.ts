import { afterEach, beforeEach, describe, expect, it } from "vitest"
import { ActionRegistry } from "../src/actions/registry.js"
import type { ActionContext, ActionResult, RenderedWorkItem } from "../src/core/types.js"
import { WorkExecutor } from "../src/runtime/executor.js"
import { setExecutorGitRunnerForTest } from "../src/runtime/git-probe.js"
import { setCleanupAgentActionForTest, setExecutorLockHolderProbeForTest } from "../src/runtime/worktree-enforcement.js"
import type { ServerConnection } from "../src/server/connection.js"
import { createTestTempDir } from "./support/temp-dir.js"
import { verifyOnlyWorkspaceManager } from "./support/workspace-mock.js"

const RUN_BRANCH = "mohist/run-workflow-branch"

let workDir: string
let connection: Pick<ServerConnection, "uploadArtifact" | "report">
let worktree: FakeWorktree

beforeEach(async () => {
  workDir = await createTestTempDir("mohist-executor-branch-")
  worktree = createFakeWorktree(workDir)
  installExecutorGit(worktree)
  connection = {
    async report() {
      return {}
    },
    async uploadArtifact() {
      throw new Error("uploadArtifact should not be called in branch stability tests")
    },
  } as unknown as Pick<ServerConnection, "uploadArtifact" | "report">
})

afterEach(() => {
  setCleanupAgentActionForTest(null)
  setExecutorGitRunnerForTest(null)
  setExecutorLockHolderProbeForTest(null)
})

type FakeWorktree = {
  workDir: string
  branch: string | null
  ref: string
  isGit: boolean
  probeFailure: string | null
  staged: string[]
  unstaged: string[]
  untracked: string[]
  calls: string[]
}

function createFakeWorktree(path: string): FakeWorktree {
  return {
    workDir: path,
    branch: RUN_BRANCH,
    ref: RUN_BRANCH,
    isGit: true,
    probeFailure: null,
    staged: [],
    unstaged: [],
    untracked: [],
    calls: [],
  }
}

function installExecutorGit(state: FakeWorktree) {
  setExecutorGitRunnerForTest(async (observedWorkDir, args) => {
    expect(observedWorkDir).toBe(state.workDir)
    const command = args.join(" ")
    state.calls.push(command)

    switch (command) {
      case "rev-parse --abbrev-ref HEAD":
        if (state.probeFailure) return gitFail(state.probeFailure)
        if (!state.isGit) return gitFail("fatal: not a git repository")
        return gitOk(`${state.branch ?? "HEAD"}\n`)
      case "rev-parse HEAD":
        return gitOk(`${state.ref}\n`)
      case "rev-parse --is-inside-work-tree":
        return state.isGit ? gitOk("true\n") : gitFail("fatal: not a git repository")
      case "diff --cached --name-only":
        return gitOk(fileList(state.staged))
      case "diff --name-only":
        return gitOk(fileList(state.unstaged))
      case "ls-files --others --exclude-standard":
        return gitOk(fileList(state.untracked))
      default:
        throw new Error(`unexpected executor git call: ${command}`)
    }
  })
}

function fileList(paths: string[]) {
  return paths.length === 0 ? "" : `${paths.join("\n")}\n`
}

function gitOk(stdout: string) {
  return { success: true, stdout, stderr: "", exitCode: 0, combinedOutput: stdout.trim() }
}

function gitFail(stderr: string) {
  return { success: false, stdout: "", stderr, exitCode: 128, combinedOutput: stderr }
}

function makeRegistry(handler: (ctx: ActionContext) => Promise<ActionResult>): ActionRegistry {
  const registry = new ActionRegistry()
  registry.register("core/script", async (ctx) => handler(ctx))
  registry.register("mohist/acp-agent", async (ctx) => handler(ctx))
  return registry
}

function buildExecutor(registry: ActionRegistry, branch: string | null = RUN_BRANCH): WorkExecutor {
  return new WorkExecutor(
    registry,
    verifyOnlyWorkspaceManager({ path: workDir, branch, changeDir: null }),
    connection as never,
    {} as never,
    null,
    workDir,
  )
}

function buildWork(overrides: Partial<RenderedWorkItem> = {}): RenderedWorkItem {
  return {
    workflowRunId: "workflow-branch",
    workId: "work-branch",
    workType: "task",
    title: "Branch stability task",
    uses: "core/script",
    with: {},
    variables: { workspace: { path: workDir, branch: RUN_BRANCH, changeDir: null } },
    ...overrides,
  }
}

function outputOf(result: { output?: string | null }) {
  return JSON.parse(result.output ?? "{}")
}

describe("WorkExecutor branch stability", () => {
  it("RecordsStartAndEndEvidenceForStableBranch", async () => {
    const executor = buildExecutor(makeRegistry(async () => ({ status: "success", message: "ok" })))

    const result = await executor.execute(buildWork(), new AbortController().signal)

    expect(result.status).toBe("completed")
    expect(outputOf(result).branchStability).toEqual([
      expect.objectContaining({ kind: "branch-stability", boundary: "start", expectedBranch: RUN_BRANCH, observedBranch: RUN_BRANCH }),
      expect.objectContaining({ kind: "branch-stability", boundary: "end", expectedBranch: RUN_BRANCH, observedBranch: RUN_BRANCH }),
    ])
  })

  it("RejectsWrongStartBranchBeforeRunningAction", async () => {
    worktree.branch = "main"
    let actionCalls = 0
    const executor = buildExecutor(makeRegistry(async () => {
      actionCalls += 1
      return { status: "success" }
    }))

    const result = await executor.execute(buildWork(), new AbortController().signal)

    expect(result.status).toBe("failed")
    expect(actionCalls).toBe(0)
    expect(outputOf(result)).toMatchObject({
      kind: "branch-invariant-violation",
      boundary: "start",
      expectedBranch: RUN_BRANCH,
      observedBranch: "main",
    })
  })

  it("ReportsEndBranchViolationBeforeCleanWorktreeProbe", async () => {
    const executor = buildExecutor(makeRegistry(async () => {
      worktree.branch = "feature/dirty"
      worktree.untracked.push("src/leftover.ts")
      return { status: "success" }
    }))

    const result = await executor.execute(buildWork(), new AbortController().signal)

    expect(result.status).toBe("failed")
    expect(outputOf(result)).toMatchObject({
      kind: "branch-invariant-violation",
      boundary: "end",
      expectedBranch: RUN_BRANCH,
      observedBranch: "feature/dirty",
    })
    expect(worktree.calls).toEqual([
      "rev-parse --abbrev-ref HEAD",
      "rev-parse --abbrev-ref HEAD",
    ])
  })

  it("ReportsDirtyWorktreeAfterMatchingEndBranch", async () => {
    const executor = buildExecutor(makeRegistry(async () => {
      worktree.untracked.push("src/leftover.ts")
      return { status: "success" }
    }))

    const result = await executor.execute(buildWork(), new AbortController().signal)

    expect(result.status).toBe("failed")
    const output = outputOf(result)
    expect(output).toMatchObject({ kind: "dirty-worktree", untracked: ["src/leftover.ts"] })
    expect(output.branchStability).toEqual([
      expect.objectContaining({ boundary: "start", observedBranch: RUN_BRANCH }),
      expect.objectContaining({ boundary: "end", observedBranch: RUN_BRANCH }),
    ])
  })

  it("RejectsDetachedHeadAtStartBoundary", async () => {
    worktree.branch = null
    worktree.ref = "4d2c7f9"
    const executor = buildExecutor(makeRegistry(async () => ({ status: "success" })))

    const result = await executor.execute(buildWork(), new AbortController().signal)

    expect(result.status).toBe("failed")
    expect(outputOf(result)).toMatchObject({
      kind: "branch-invariant-violation",
      boundary: "start",
      expectedBranch: RUN_BRANCH,
      observedBranch: "",
      observedRef: "4d2c7f9",
    })
  })

  it("ReportsBranchProbeFailureWithDetail", async () => {
    worktree.probeFailure = "fatal: unable to access '.git': Permission denied"
    const executor = buildExecutor(makeRegistry(async () => ({ status: "success" })))

    const result = await executor.execute(buildWork(), new AbortController().signal)

    expect(result.status).toBe("failed")
    expect(outputOf(result)).toMatchObject({
      kind: "branch-invariant-violation",
      boundary: "start",
      expectedBranch: RUN_BRANCH,
    })
    expect(outputOf(result).detail).toContain("Permission denied")
  })

  it("AttachesStartEvidenceToFailedAction", async () => {
    const executor = buildExecutor(makeRegistry(async () => ({ status: "failed", message: "action failed" })))

    const result = await executor.execute(buildWork(), new AbortController().signal)

    expect(result).toMatchObject({ status: "failed", message: "action failed" })
    expect(outputOf(result).branchStability).toEqual([
      expect.objectContaining({ boundary: "start", observedBranch: RUN_BRANCH }),
    ])
  })

  it("RetriesAfterPrepareRestoresExpectedBranch", async () => {
    const workspaceManager = {
      prepareCalls: 0,
      async prepare() {
        this.prepareCalls += 1
        worktree.branch = RUN_BRANCH
        return { path: workDir, branch: RUN_BRANCH, changeDir: null }
      },
    }
    let actionCalls = 0
    const executor = new WorkExecutor(
      makeRegistry(async () => {
        actionCalls += 1
        worktree.branch = `feature/retry-${actionCalls}`
        return { status: "success" }
      }),
      workspaceManager as never,
      connection as never,
      {} as never,
      null,
      workDir,
    )

    const first = await executor.execute(buildWork(), new AbortController().signal)
    const second = await executor.execute(buildWork(), new AbortController().signal)

    expect(workspaceManager.prepareCalls).toBe(2)
    expect(actionCalls).toBe(2)
    expect(outputOf(first)).toMatchObject({ kind: "branch-invariant-violation", boundary: "end" })
    expect(outputOf(second)).toMatchObject({ kind: "branch-invariant-violation", boundary: "end" })
  })

  it("TreatsNonGitWorkspaceAsBranchStable", async () => {
    worktree.isGit = false
    const executor = buildExecutor(makeRegistry(async () => ({ status: "success" })))

    const result = await executor.execute(buildWork(), new AbortController().signal)

    expect(result.status).toBe("completed")
    expect(outputOf(result).branchStability).toEqual([
      expect.objectContaining({ boundary: "start", expectedBranch: RUN_BRANCH, observedBranch: "" }),
      expect.objectContaining({ boundary: "end", expectedBranch: RUN_BRANCH, observedBranch: "" }),
    ])
  })

  it("CompletesWhenWorkspaceHasNoConfiguredBranch", async () => {
    worktree.branch = "feature/unpinned"
    const executor = buildExecutor(makeRegistry(async () => ({ status: "success" })), null)

    const result = await executor.execute(buildWork(), new AbortController().signal)

    expect(result.status).toBe("completed")
    expect(outputOf(result).branchStability).toEqual([
      expect.objectContaining({ boundary: "start", expectedBranch: "", observedBranch: "feature/unpinned" }),
      expect.objectContaining({ boundary: "end", expectedBranch: "", observedBranch: "feature/unpinned" }),
    ])
  })
})
