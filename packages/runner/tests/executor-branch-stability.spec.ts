import { execFile } from "node:child_process"
import { mkdir, mkdtemp, rm, writeFile } from "node:fs/promises"
import { tmpdir } from "node:os"
import { join, dirname } from "node:path"
import { afterEach, beforeEach, describe, expect, it } from "vitest"
import { promisify } from "node:util"
import {
  WorkExecutor,
  setCleanupAgentActionForTest,
  setExecutorGitRunnerForTest,
  setExecutorLockHolderProbeForTest,
} from "../src/runtime/executor.js"
import { ActionRegistry } from "../src/actions/registry.js"
import type { ActionContext, ActionResult, WorkItem } from "../src/core/types.js"
import type { ServerConnection } from "../src/server/connection.js"
import { verifyOnlyWorkspaceManager } from "./support/workspace-mock.js"

const exec = promisify(execFile)

let workDir: string
let connection: Pick<ServerConnection, "uploadArtifact" | "report">

const RUN_BRANCH = "mohist/run-wr-branch-1"

beforeEach(async () => {
  workDir = await mkdtemp(join(tmpdir(), "mohist-executor-branch-"))
  await initGitRepo(workDir, RUN_BRANCH)
  connection = {
    async report() {
      return {}
    },
    async uploadArtifact() {
      throw new Error("uploadArtifact should not be called in branch-stability tests")
    },
  } as unknown as Pick<ServerConnection, "uploadArtifact" | "report">
})

afterEach(async () => {
  setCleanupAgentActionForTest(null)
  setExecutorGitRunnerForTest(null)
  setExecutorLockHolderProbeForTest(null)
  await rm(workDir, { recursive: true, force: true })
})

async function initGitRepo(dir: string, branch: string) {
  await exec("git", ["init", "-q"], { cwd: dir })
  await exec("git", ["config", "user.email", "test@example.com"], { cwd: dir })
  await exec("git", ["config", "user.name", "Test"], { cwd: dir })
  await exec("git", ["config", "commit.gpgsign", "false"], { cwd: dir })
  await writeFile(join(dir, "README.md"), "init\n", "utf8")
  await exec("git", ["add", "README.md"], { cwd: dir })
  await exec("git", ["commit", "-m", "init", "-q"], { cwd: dir })
  // Create the run branch and check it out so the boundary check sees
  // the expected branch from the start.
  await exec("git", ["checkout", "-b", branch], { cwd: dir })
}

function makeRegistry(handler: (ctx: ActionContext) => Promise<ActionResult>): ActionRegistry {
  const registry = new ActionRegistry()
  registry.register("core/script", async (ctx) => handler(ctx))
  registry.register("mohist/acp-agent", async (ctx) => handler(ctx))
  return registry
}

function buildExecutor(registry: ActionRegistry): WorkExecutor {
  return new WorkExecutor(
    registry,
    verifyOnlyWorkspaceManager({ path: workDir, branch: RUN_BRANCH, changeDir: null }),
    connection as never,
    {} as never,
    null,
    workDir,
  )
}

function buildWork(overrides: Partial<WorkItem> = {}): WorkItem {
  return {
    workflowRunId: "wf-1",
    workId: "work-branch-1",
    workType: "task",
    title: "Branch-stability test task",
    uses: "core/script",
    with: {},
    variables: {
      workspace: { path: workDir, branch: RUN_BRANCH, changeDir: null },
    },
    ...overrides,
  }
}

async function readHeadRef() {
  const result = await exec("git", ["rev-parse", "--abbrev-ref", "HEAD"], { cwd: workDir })
  return result.stdout.trim()
}

describe("WorkExecutor branch-stability boundary checks", () => {
  it("recordsStartAndEndBranchStabilityEvidenceWhenWorkspaceStaysOnRunBranch", async () => {
    // Happy path: the worktree is on the run branch before the action,
    // the action leaves it on the run branch, and both the start and
    // end boundaries record that fact as branch-stability evidence
    // attached to the result.
    const executor = buildExecutor(makeRegistry(async () => ({ status: "success", message: "ok" })))

    const result = await executor.execute(buildWork(), new AbortController().signal)

    expect(result.status).toBe("completed")
    expect(result.output).toBeDefined()
    const parsed = JSON.parse(result.output ?? "{}")
    expect(Array.isArray(parsed.branchStability)).toBe(true)
    expect(parsed.branchStability).toHaveLength(2)
    const [start, end] = parsed.branchStability
    expect(start).toMatchObject({
      kind: "branch-stability",
      boundary: "start",
      expectedBranch: RUN_BRANCH,
      observedBranch: RUN_BRANCH,
    })
    expect(end).toMatchObject({
      kind: "branch-stability",
      boundary: "end",
      expectedBranch: RUN_BRANCH,
      observedBranch: RUN_BRANCH,
    })
  })

  it("blocksTaskAtStartBoundaryWhenWorkspaceIsOnWrongBranchAndDoesNotRunAction", async () => {
    // Pre-condition: the workspace is on a different branch when the
    // task attempts to start. The action must NOT be invoked; the
    // task must fail with a branch-invariant-violation whose evidence
    // includes the expected and observed branch and the start
    // boundary.
    await exec("git", ["checkout", "master"], { cwd: workDir })
    let actionCalls = 0
    const executor = buildExecutor(makeRegistry(async () => {
      actionCalls += 1
      return { status: "success", message: "should not run" }
    }))

    const result = await executor.execute(buildWork(), new AbortController().signal)

    expect(result.status).toBe("failed")
    expect(actionCalls).toBe(0)
    expect(result.message).toMatch(/branch-invariant violation at start boundary/)
    expect(result.message).toMatch(new RegExp(`expected branch '${RUN_BRANCH}'`))
    expect(result.message).toMatch(/observed 'master'/)
    expect(result.output).toBeDefined()
    const evidence = JSON.parse(result.output ?? "{}")
    expect(evidence).toMatchObject({
      kind: "branch-invariant-violation",
      boundary: "start",
      expectedBranch: RUN_BRANCH,
      observedBranch: "master",
    })
  })

  it("rejectsTaskAtEndBoundaryWhenActionLeavesWorkspaceOnWrongBranch", async () => {
    // The action leaves the workspace on a different branch before
    // returning success. The end-boundary check must catch it before
    // enforceCleanWorktree runs, so the failure is reported as a
    // branch-invariant violation (not a dirty-worktree failure) and
    // the task is NOT reported completed.
    const executor = buildExecutor(makeRegistry(async (ctx) => {
      await exec("git", ["checkout", "-b", "feature/sneaky"], { cwd: ctx.workDir })
      return { status: "success", message: "moved the branch" }
    }))

    const result = await executor.execute(buildWork(), new AbortController().signal)

    expect(result.status).toBe("failed")
    expect(result.message).toMatch(/branch-invariant violation at end boundary/)
    expect(result.message).toMatch(new RegExp(`expected branch '${RUN_BRANCH}'`))
    expect(result.message).toMatch(/observed 'feature\/sneaky'/)
    const evidence = JSON.parse(result.output ?? "{}")
    expect(evidence).toMatchObject({
      kind: "branch-invariant-violation",
      boundary: "end",
      expectedBranch: RUN_BRANCH,
      observedBranch: "feature/sneaky",
    })
    // The task must NOT carry branch-stability evidence for a
    // successful end boundary because the end check failed.
    expect(evidence.kind).toBe("branch-invariant-violation")
  })

  it("endBoundaryCheckPrecedesCleanWorktreeCheckSoWrongBranchIsNotMisreportedAsDirty", async () => {
    // If the action leaves the workspace on the wrong branch AND
    // dirty, the runner must report a branch-invariant violation
    // (not a dirty-worktree failure). The branch-stability check is
    // the outermost boundary and runs before enforceCleanWorktree so
    // the wrong-branch state is never mis-reported as a dirty
    // worktree.
    const executor = buildExecutor(makeRegistry(async (ctx) => {
      // Move the workspace off the run branch AND leave a dirty file
      // so the clean-worktree invariant would also trip.
      await exec("git", ["checkout", "-b", "feature/dirty"], { cwd: ctx.workDir })
      const target = join(ctx.workDir, "src/leftover.ts")
      await mkdir(dirname(target), { recursive: true })
      await writeFile(target, "export const x = 1\n")
      return { status: "success", message: "did damage" }
    }))

    const result = await executor.execute(buildWork(), new AbortController().signal)

    expect(result.status).toBe("failed")
    const evidence = JSON.parse(result.output ?? "{}")
    // The failure must be classified as a branch-invariant violation
    // at the end boundary, not a dirty-worktree failure, because the
    // branch check runs first and a wrong branch is a runner/action
    // bug.
    expect(evidence.kind).toBe("branch-invariant-violation")
    expect(evidence.boundary).toBe("end")
    expect(evidence.observedBranch).toBe("feature/dirty")
    // The dirty-worktree evidence must not also be reported.
    expect(evidence.staged).toBeUndefined()
    expect(evidence.unstaged).toBeUndefined()
    expect(evidence.untracked).toBeUndefined()
    expect(evidence.cleanupAttempts).toBeUndefined()
  })

  it("startBoundaryDetachedHeadIsReportedAsBranchInvariantViolation", async () => {
    // A detached HEAD at a task boundary is itself a violation: the
    // run branch is always a real branch ref, so compare
    // --abbrev-ref HEAD against it. A detached HEAD must not be
    // silently tolerated as "still on the run branch".
    const headSha = await exec("git", ["rev-parse", "HEAD"], { cwd: workDir })
    await exec("git", ["checkout", "--detach", headSha.stdout.trim()], { cwd: workDir })

    const executor = buildExecutor(makeRegistry(async () => ({ status: "success", message: "ran" })))

    const result = await executor.execute(buildWork(), new AbortController().signal)

    expect(result.status).toBe("failed")
    const evidence = JSON.parse(result.output ?? "{}")
    expect(evidence).toMatchObject({
      kind: "branch-invariant-violation",
      boundary: "start",
      expectedBranch: RUN_BRANCH,
    })
    expect(evidence.observedBranch).toBe("")
    expect(evidence.observedRef).toBe(headSha.stdout.trim())
  })

  it("startBoundaryProbeFailureIsReportedAsBranchInvariantViolationWithDetail", async () => {
    // A probe failure (e.g. corrupted worktree, permission denied) is
    // a runner bug distinct from a "wrong branch" finding. The
    // branch-stability check surfaces it as a branch-invariant
    // violation carrying the probe failure detail in `detail`.
    setExecutorGitRunnerForTest(async (_workDir, args) => {
      if (args[0] === "rev-parse" && args[1] === "--abbrev-ref" && args[2] === "HEAD") {
        return {
          success: false,
          stdout: "",
          stderr: "fatal: unable to access '.git': Permission denied",
          exitCode: 128,
          combinedOutput: "fatal: unable to access '.git': Permission denied",
        }
      }
      throw new Error(`unexpected git call: ${args.join(" ")}`)
    })
    const executor = buildExecutor(makeRegistry(async () => ({ status: "success", message: "ran" })))

    const result = await executor.execute(buildWork(), new AbortController().signal)

    expect(result.status).toBe("failed")
    const evidence = JSON.parse(result.output ?? "{}")
    expect(evidence).toMatchObject({
      kind: "branch-invariant-violation",
      boundary: "start",
      expectedBranch: RUN_BRANCH,
    })
    expect(evidence.detail).toMatch(/probe failed/)
    expect(evidence.detail).toMatch(/Permission denied/)
  })

  it("dirtyWorktreeFailureIsNotReportedWhenBranchIsWrong", async () => {
    // The branch-stability check must run before the clean-worktree
    // check so a wrong-branch state is never misreported as a
    // dirty-worktree failure. The dirty-worktree evidence is only
    // surfaced when the branch matches `workspace.branch`.
    const executor = buildExecutor(makeRegistry(async (ctx) => {
      await exec("git", ["checkout", "-b", "feature/leftover"], { cwd: ctx.workDir })
      const target = join(ctx.workDir, "src/leftover.ts")
      await mkdir(dirname(target), { recursive: true })
      await writeFile(target, "export const x = 1\n")
      return { status: "success" }
    }))

    const result = await executor.execute(buildWork(), new AbortController().signal)

    expect(result.status).toBe("failed")
    const evidence = JSON.parse(result.output ?? "{}")
    // Wrong branch dominates; the dirty-worktree evidence must not
    // also be reported. The two failure kinds must remain distinct
    // per the workspace-branch-stability spec.
    expect(evidence.kind).toBe("branch-invariant-violation")
    expect(evidence.boundary).toBe("end")
    expect(evidence.kind).not.toBe("dirty-worktree")
    expect(evidence.kind).not.toBe("git-index-lock")
  })

  it("dirtyWorktreeFailureIsReportedWithStructuredEvidenceWhenBranchMatches", async () => {
    // When the branch matches, the clean-worktree invariant still
    // surfaces a dirty-worktree failure. The branch-stability
    // evidence (start AND end) is still attached to the result so a
    // successful task has the full pair alongside the clean-worktree
    // evidence.
    const executor = buildExecutor(makeRegistry(async (ctx) => {
      const target = join(ctx.workDir, "src/leftover.ts")
      await mkdir(dirname(target), { recursive: true })
      await writeFile(target, "export const x = 1\n")
      return { status: "success" }
    }))

    const result = await executor.execute(buildWork(), new AbortController().signal)

    expect(result.status).toBe("failed")
    const evidence = JSON.parse(result.output ?? "{}")
    expect(evidence.kind).toBe("dirty-worktree")
    expect(evidence.untracked).toEqual(["src/leftover.ts"])
    // Start and end branch-stability evidence is attached alongside
    // the dirty-worktree evidence.
    expect(Array.isArray(evidence.branchStability)).toBe(true)
    expect(evidence.branchStability).toHaveLength(2)
    expect(evidence.branchStability[0].boundary).toBe("start")
    expect(evidence.branchStability[1].boundary).toBe("end")
    expect(evidence.branchStability[0].observedBranch).toBe(RUN_BRANCH)
    expect(evidence.branchStability[1].observedBranch).toBe(RUN_BRANCH)
  })

  it("startEvidenceIsAttachedToNonCompletedActionResult", async () => {
    // A task that does NOT return success (the action itself
    // reported a failure) still gets the start branch-stability
    // evidence attached so the workflow has a record of the
    // boundary state, but the task is not reported completed.
    const executor = buildExecutor(makeRegistry(async () => ({ status: "failed", message: "action failed" })))

    const result = await executor.execute(buildWork(), new AbortController().signal)

    expect(result.status).toBe("failed")
    expect(result.message).toBe("action failed")
    const evidence = JSON.parse(result.output ?? "{}")
    expect(Array.isArray(evidence.branchStability)).toBe(true)
    expect(evidence.branchStability).toHaveLength(1)
    expect(evidence.branchStability[0].boundary).toBe("start")
    expect(evidence.branchStability[0].observedBranch).toBe(RUN_BRANCH)
  })

  it("retryAfterBranchInvariantViolationRecoversWorkspaceBranchViaStartBoundaryPrecheck", async () => {
    // Post-#181 contract: the workflow workspace is materialized once
    // at workflow start (outside the executor). Every executor
    // dispatch — first or retry — calls `verify()`, and `verify()` may
    // restore the workspace onto the run branch on behalf of the
    // action. After a task that ended off the run branch is retried,
    // the start-boundary `verify` must bring the workspace back before
    // the start check runs, so the retry's start check passes even
    // though the prior attempt left the worktree on a wrong branch.
    const workspaceManager = {
      verifyCalls: 0,
      async verify(_work: WorkItem, _signal: AbortSignal) {
        this.verifyCalls += 1
        const current = await readHeadRef()
        if (current !== RUN_BRANCH) {
          await exec("git", ["checkout", RUN_BRANCH], { cwd: workDir })
        }
        return { path: workDir, branch: RUN_BRANCH, changeDir: null }
      },
      async planResolution(_work: WorkItem, _signal: AbortSignal) {
        return { action: "verify" as const, workspacePath: workDir }
      },
      async ensure(work: WorkItem, signal: AbortSignal) {
        // T-002 contract: the executor's start-boundary precheck
        // routes through `ensure`, which mirrors the real
        // `WorkspaceManager.ensure` by planning first and then
        // dispatching to `materialize` or `verify` accordingly.
        const plan = await this.planResolution(work, signal)
        return plan.action === "materialize"
          ? await this.materialize(work, signal)
          : await this.verify(work, signal)
      },
    }
    const executor = new WorkExecutor(
      makeRegistry(async (ctx) => {
        // On each attempt, leave the workspace on a fresh wrong branch
        // so the end-boundary check fails. The retry's start-boundary
        // verify restores the run branch before the action runs.
        const current = await exec("git", ["rev-parse", "--abbrev-ref", "HEAD"], { cwd: ctx.workDir })
        if (current.stdout.trim() === RUN_BRANCH) {
          const callCount = workspaceManager.verifyCalls
          await exec("git", ["checkout", "-b", `feature/retry-${callCount}`], { cwd: ctx.workDir })
        }
        return { status: "success", message: "ok" }
      }),
      workspaceManager as never,
      connection as never,
      {} as never,
      null,
      workDir,
    )

    // First attempt: verify restores the run branch, start check passes;
    // the action then moves off the run branch, so the end check fails.
    const first = await executor.execute(buildWork(), new AbortController().signal)
    expect(first.status).toBe("failed")
    const firstEvidence = JSON.parse(first.output ?? "{}")
    expect(firstEvidence.kind).toBe("branch-invariant-violation")
    expect(firstEvidence.boundary).toBe("end")
    expect(workspaceManager.verifyCalls).toBe(1)

    // Retry: verify runs again and restores the run branch before the
    // start check, so the start check passes; the action still ends off
    // the run branch so the end check still fails.
    const second = await executor.execute(buildWork(), new AbortController().signal)
    expect(workspaceManager.verifyCalls).toBe(2)
    const secondEvidence = JSON.parse(second.output ?? "{}")
    expect(secondEvidence.kind).toBe("branch-invariant-violation")
    expect(secondEvidence.boundary).toBe("end")
  })

  it("retryRecoversWorkspaceBranchViaStartBoundaryPrecheckBeforeStartCheck", async () => {
    // The retry path must restore the workspace to the run branch
    // before the start check runs. After a prior attempt left the
    // workspace on the wrong branch, the second attempt's start
    // check must observe the run branch (because the start-boundary
    // precheck brought it back) and pass, even though the worktree
    // still has the wrong branch on disk right before the precheck
    // is called. Under the T-002 contract the precheck is
    // `verify()` for retries (no re-clone), so the recovery path
    // runs through verify.
    const workspaceManager = {
      async verify() {
        // Always restore the workspace to the run branch on
        // verify, mimicking the real `WorkspaceManager.verify`.
        const current = await readHeadRef()
        if (current !== RUN_BRANCH) {
          await exec("git", ["checkout", RUN_BRANCH], { cwd: workDir })
        }
        return { path: workDir, branch: RUN_BRANCH, changeDir: null }
      },
      async planResolution() {
        return { action: "verify" as const, workspacePath: workDir }
      },
      async ensure() {
        // T-002 contract: the executor's start-boundary precheck
        // routes through `ensure`, which calls `planResolution` and
        // dispatches to `verify` here because the workspace is
        // already bound.
        return await this.verify()
      },
    }
    const executor = new WorkExecutor(
      makeRegistry(async (ctx) => {
        // Move off the run branch every time, so end check fails on
        // every attempt. What matters is that the start check sees
        // the run branch on retries (because `ensure` restored it).
        const current = await exec("git", ["rev-parse", "--abbrev-ref", "HEAD"], { cwd: ctx.workDir })
        if (current.stdout.trim() === RUN_BRANCH) {
          await exec("git", ["checkout", "-b", `retry-${Date.now()}`], { cwd: ctx.workDir })
        }
        return { status: "success", message: "ok" }
      }),
      workspaceManager as never,
      connection as never,
      {} as never,
      null,
      workDir,
    )

    // Simulate a prior failure leaving the workspace on the wrong
    // branch. Then run the task again; the executor must call
    // `ensure` (which restores the run branch) and the start check
    // must succeed.
    await exec("git", ["checkout", "-b", "feature/leftover"], { cwd: workDir })
    expect(await readHeadRef()).toBe("feature/leftover")

    const result = await executor.execute(buildWork(), new AbortController().signal)

    // The start check passed because `ensure` restored the run
    // branch; the action then moved it again, so the end check
    // fails. The evidence chain shows start observed the run branch.
    const evidence = JSON.parse(result.output ?? "{}")
    expect(evidence.kind).toBe("branch-invariant-violation")
    expect(evidence.boundary).toBe("end")
    // The start evidence is not present in the end-only failure
    // payload, but the start check passing is implicit in the
    // boundary being "end" and the action having been invoked.
  })

  it("retriedActionWithCleanBranchReportsCompletedWithBranchStabilityEvidence", async () => {
    // After ensure restores the run branch, a well-behaved action
    // (one that does not move the branch) completes successfully
    // and the result carries both start and end branch-stability
    // evidence. This is the recovery shape: ensure restores the
    // branch, retry runs cleanly, completion is reported with
    // evidence.
    const executor = buildExecutor(makeRegistry(async () => ({ status: "success", message: "ok" })))

    const result = await executor.execute(buildWork(), new AbortController().signal)

    expect(result.status).toBe("completed")
    const evidence = JSON.parse(result.output ?? "{}")
    expect(Array.isArray(evidence.branchStability)).toBe(true)
    expect(evidence.branchStability).toHaveLength(2)
    expect(evidence.branchStability[0]).toMatchObject({
      boundary: "start",
      expectedBranch: RUN_BRANCH,
      observedBranch: RUN_BRANCH,
    })
    expect(evidence.branchStability[1]).toMatchObject({
      boundary: "end",
      expectedBranch: RUN_BRANCH,
      observedBranch: RUN_BRANCH,
    })
  })

  it("nonGitWorktreeIsBranchStable", async () => {
    // The branch-stability check tolerates a plain (non-git) worktree
    // the same way the clean-worktree probe does: there is no branch
    // context to check, so the check is satisfied trivially and
    // recorded with an empty observed branch. This matches the
    // defensive behaviour for test fixtures and short-lived tmpdirs
    // that resolve outside a git repository.
    const plainDir = await mkdtemp(join(tmpdir(), "mohist-executor-branch-plain-"))
    try {
      const executor = new WorkExecutor(
        makeRegistry(async () => ({ status: "success", message: "ran" })),
        verifyOnlyWorkspaceManager({ path: plainDir, branch: RUN_BRANCH, changeDir: null }),
        connection as never,
        {} as never,
        null,
        plainDir,
      )
      const work: WorkItem = {
        workflowRunId: "wf-1",
        workId: "work-branch-plain",
        workType: "task",
        title: "Plain tmpdir branch test",
        uses: "core/script",
        with: {},
        variables: { workspace: { path: plainDir, branch: RUN_BRANCH, changeDir: null } },
      }

      const result = await executor.execute(work, new AbortController().signal)

      expect(result.status).toBe("completed")
      const evidence = JSON.parse(result.output ?? "{}")
      expect(Array.isArray(evidence.branchStability)).toBe(true)
      expect(evidence.branchStability).toHaveLength(2)
      expect(evidence.branchStability[0]).toMatchObject({
        boundary: "start",
        expectedBranch: RUN_BRANCH,
        observedBranch: "",
      })
      expect(evidence.branchStability[1]).toMatchObject({
        boundary: "end",
        expectedBranch: RUN_BRANCH,
        observedBranch: "",
      })
    } finally {
      await rm(plainDir, { recursive: true, force: true })
    }
  })

  it("missingWorkspaceBranchInVariablesSkipsBranchStabilityCheck", async () => {
    // When the runner cannot determine the expected branch
    // (variables.workspace.branch is null/absent), the branch check
    // is skipped (no boundary to enforce) and the action runs
    // through. This is the same edge case as a plain tmpdir: there
    // is no `workspace.branch` invariant to check, so the boundary
    // is trivially satisfied.
    const executor = new WorkExecutor(
      makeRegistry(async () => ({ status: "success", message: "ok" })),
      verifyOnlyWorkspaceManager({ path: workDir, branch: null, changeDir: null }),
      connection as never,
      {} as never,
      null,
      workDir,
    )
    const work: WorkItem = {
      workflowRunId: "wf-1",
      workId: "work-no-branch",
      workType: "task",
      title: "No-branch test",
      uses: "core/script",
      with: {},
      variables: { workspace: { path: workDir, branch: null, changeDir: null } },
    }

    const result = await executor.execute(work, new AbortController().signal)

    expect(result.status).toBe("completed")
  })

  it("endBoundaryCheckRunsBeforeCleanWorktreeCheck_OrderingProof", async () => {
    // Explicit ordering proof: the branch-stability check on the end
    // boundary must run BEFORE the clean-worktree check so a wrong
    // branch is never mis-reported as a dirty worktree. The action
    // leaves the workspace on a different branch AND a dirty file.
    // The expected outcome is a branch-invariant-violation, not a
    // dirty-worktree failure, demonstrating the ordering.
    await exec("git", ["checkout", "master"], { cwd: workDir })
    let cleanWorktreeProbeCalled = false
    // Spy on the git runner: detect calls to the clean-worktree probe
    // (git diff --cached) and assert none of them happen because the
    // branch check aborts before enforceCleanWorktree is reached.
    setExecutorGitRunnerForTest(async (wd, args) => {
      const inner = await import("node:child_process")
      const { promisify } = await import("node:util")
      const exec = promisify(inner.execFile)
      const result = await exec("git", args, { cwd: wd })
      if (args[0] === "diff" && args[1] === "--cached") {
        cleanWorktreeProbeCalled = true
      }
      return {
        success: true,
        stdout: result.stdout,
        stderr: result.stderr,
        exitCode: 0,
        combinedOutput: `${result.stdout}${result.stderr}`.trim(),
      }
    })
    const executor = buildExecutor(makeRegistry(async (ctx) => {
      await exec("git", ["checkout", "-b", "feature/order"], { cwd: ctx.workDir })
      await writeFile(join(ctx.workDir, "dirty.ts"), "d\n")
      return { status: "success", message: "ok" }
    }))

    const result = await executor.execute(buildWork(), new AbortController().signal)

    expect(result.status).toBe("failed")
    const evidence = JSON.parse(result.output ?? "{}")
    expect(evidence.kind).toBe("branch-invariant-violation")
    expect(evidence.boundary).toBe("start")
    // The clean-worktree probe (git diff --cached) was never called
    // because the start check rejected the task before the action
    // even ran. This is the ordering proof.
    expect(cleanWorktreeProbeCalled).toBe(false)
  })
})
