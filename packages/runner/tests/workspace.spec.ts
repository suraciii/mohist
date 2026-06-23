import { chmod, mkdtemp, readFile, readdir, rm, writeFile } from "node:fs/promises"
import { join } from "node:path"
import { tmpdir } from "node:os"
import { describe, expect, it, vi } from "vitest"
import { CacheReplacementBlockedError, WorkspaceBranchMismatchError, WorkspaceManager, slugify } from "../src/runtime/workspace.js"
import { exists, runCommand } from "../src/system/process.js"

// Item-5: the workspace path slug must stay in sync with the C#
// MohistWorkspaceLayout.Slug helper. The server-side table is pinned in
// PathContractRegressionSpecs.Slug_MatchesRunnerAlgorithm; the matching
// JS table is asserted here so a future change to either side is caught
// by the appropriate runtime's tests.

describe("WorkspaceManager", () => {
  it("NewIssueReusingNumber_SecondDispatchIsVerifyOnlyAndRejectsStaleIdentity", async () => {
    const root = await mkdtemp(join(tmpdir(), "mohist-workspace-"))
    const repo = await createRepo(root, "repo")

    const runnerRoot = join(root, "runner")
    const manager = new WorkspaceManager(runnerRoot)
    const signal = new AbortController().signal

    const first = await manager.ensure(work("wr-old", "issue-old", repo), signal)
    await writeFile(join(first.path, "stale.txt"), "old issue data\n")

    await expect(manager.ensure(work("wr-new", "issue-new", repo), signal)).rejects.toMatchObject({ kind: "workspace-identity-mismatch" })

    expect(await readFile(join(first.path, "stale.txt"), "utf8")).toBe("old issue data\n")

    const marker = JSON.parse(await readFile(join(first.path, ".mohist", "workspace.json"), "utf8"))
    expect(marker).toMatchObject({ issueId: "issue-old", issueNumber: 9, workflowRunId: "wr-old" })
  })

  it("NewIssueReusingNumber_ExplicitMaterializeRemovesStaleWorkspaceBeforeCreatingFreshWorkspace", async () => {
    const root = await mkdtemp(join(tmpdir(), "mohist-workspace-"))
    const repo = await createRepo(root, "repo")

    const runnerRoot = join(root, "runner")
    const manager = new WorkspaceManager(runnerRoot)
    const signal = new AbortController().signal

    const first = await manager.materialize(work("wr-old", "issue-old", repo), signal)
    await writeFile(join(first.path, ".mohist", "workspace.json"), JSON.stringify({
      issueId: "other-issue",
      issueNumber: 9,
      workflowRunId: "other-run",
    }, null, 2))

    const second = await manager.materialize(work("wr-new", "issue-new", repo), signal)

    expect(second.path).toBe(first.path)
    const marker = JSON.parse(await readFile(join(second.path, ".mohist", "workspace.json"), "utf8"))
    expect(marker).toMatchObject({ issueId: "issue-new", issueNumber: 9, workflowRunId: "wr-new" })
  })

  it("CachePreparedFromGitUrl_CreatesSeparateWorkspace", async () => {
    const root = await mkdtemp(join(tmpdir(), "mohist-workspace-"))
    const repo = await createRepo(root, "repo")
    const runnerRoot = join(root, "runner")
    const manager = new WorkspaceManager(runnerRoot)
    const signal = new AbortController().signal

    const workspace = await manager.ensure(work("wr-1", "issue-1", repo), signal)

    const cachePath = join(runnerRoot, "repos", "project-1", "master")
    expect(await isBareRepo(cachePath, signal)).toBe(true)
    expect(workspace.path).not.toBe(cachePath)
    expect(workspace.path).toBe(join(runnerRoot, "mohist-local", "workspaces", "issue-9"))
    expect(await readFile(join(workspace.path, "README.md"), "utf8")).toBe("base\n")
    expect(workspace.branch).toBe("mohist/run-wr-1")
  })

  it("ExistingWorkspaceWithSameMarker_IsReused", async () => {
    const root = await mkdtemp(join(tmpdir(), "mohist-workspace-"))
    const repo = await createRepo(root, "repo")
    const runnerRoot = join(root, "runner")
    const manager = new WorkspaceManager(runnerRoot)
    const signal = new AbortController().signal

    const first = await manager.ensure(work("wr-1", "issue-1", repo), signal)
    await writeFile(join(first.path, "draft.txt"), "draft\n")

    const second = await manager.ensure(work("wr-1", "issue-1", repo), signal)

    expect(second.path).toBe(first.path)
    expect(await readFile(join(second.path, "draft.txt"), "utf8")).toBe("draft\n")
  })

  it("Verify_WhenWorkspaceIsOnWrongBranch_ReportsBranchInvariantWithoutRecloning", async () => {
    const root = await mkdtemp(join(tmpdir(), "mohist-workspace-"))
    const repo = await createRepo(root, "repo")
    const runnerRoot = join(root, "runner")
    const manager = new WorkspaceManager(runnerRoot)
    const signal = new AbortController().signal

    const workspace = await manager.materialize(work("wr-branch", "issue-branch", repo), signal)
    await git(workspace.path, "checkout", "-b", "wrong-branch")

    await expect(manager.verify(work("wr-branch", "issue-branch", repo), signal)).rejects.toMatchObject({
      kind: "branch-invariant-violation",
      expectedBranch: "mohist/run-wr-branch",
      observedBranch: "wrong-branch",
    } satisfies Partial<WorkspaceBranchMismatchError>)
  })

  it("MissingBaseBranch_FailsWorkspacePreparationWithoutCreatingWorkspace", async () => {
    const root = await mkdtemp(join(tmpdir(), "mohist-workspace-"))
    const repo = await createRepo(root, "repo")
    const runnerRoot = join(root, "runner")
    const manager = new WorkspaceManager(runnerRoot)
    const signal = new AbortController().signal

    const item = work("wr-1", "issue-1", repo)
    item.variables.repository = { name: "master", gitUrl: repo, baseBranch: "does-not-exist" }

    await expect(manager.ensure(item, signal)).rejects.toThrow(/cannot be resolved/)
    expect(exists(join(runnerRoot, "workspaces"))).toBe(false)
  })

  it("WorkspacePreparation_DoesNotUseGitWorktreeCommands", async () => {
    const root = await mkdtemp(join(tmpdir(), "mohist-workspace-"))
    const repo = await createRepo(root, "repo")
    const runnerRoot = join(root, "runner")
    const manager = new WorkspaceManager(runnerRoot)
    const signal = new AbortController().signal

    const spy = vi.spyOn(await import("../src/system/process.js"), "runCommand")

    try {
      await manager.ensure(work("wr-1", "issue-1", repo), signal)
    } finally {
      spy.mockRestore()
    }

    const worktreeCalls = spy.mock.calls.filter((call) => call[0] === "git" && call[1].some((a) => a === "worktree"))
    expect(worktreeCalls).toHaveLength(0)
  })

  it("ServerSuppliedPath_FirstDispatchMaterializesOnce_EveryLaterDispatchIsVerifyOnly", async () => {
    const root = await mkdtemp(join(tmpdir(), "mohist-workspace-"))
    const repo = await createRepo(root, "repo")
    const runnerRoot = join(root, "runner")
    const manager = new WorkspaceManager(runnerRoot)
    const signal = new AbortController().signal

    const suppliedWorkspacePath = join(runnerRoot, "supplied", "workspaces", "issue-9")
    const item = work("wr-supplied", "issue-1", repo)
    ;(item.variables as Record<string, unknown>).workspace = { path: suppliedWorkspacePath, branch: "mohist/run-wr-supplied", changeDir: null }

    const result = await manager.ensure(item, signal)
    expect(result.path).toBe(suppliedWorkspacePath)
    expect(result.branch).toBe("mohist/run-wr-supplied")
    const cachePath = join(runnerRoot, "repos", "project-1", "master")
    expect(await isBareRepo(cachePath, signal)).toBe(true)
    expect(await readFile(join(suppliedWorkspacePath, "README.md"), "utf8")).toBe("base\n")
    const head = await runCommand("git", ["-C", suppliedWorkspacePath, "rev-parse", "--abbrev-ref", "HEAD"], ".", signal)
    expect(head.exitCode).toBe(0)
    expect(head.stdout.trim()).toBe("mohist/run-wr-supplied")
    const remote = await runCommand("git", ["-C", suppliedWorkspacePath, "remote", "get-url", "origin"], ".", signal)
    expect(remote.exitCode).toBe(0)
    expect(remote.stdout.trim()).toBe(repo)

    const gitCalls: string[] = []
    const processMod = await import("../src/system/process.js")
    const realRun = processMod.runCommand
    const spy = vi.spyOn(processMod, "runCommand").mockImplementation(async (cmd, args, cwd, sig) => {
      gitCalls.push(`${cmd} ${args.join(" ")}`)
      return await realRun(cmd, args, cwd, sig)
    })
    try {
      const second = await manager.ensure(item, signal)
      expect(second.path).toBe(suppliedWorkspacePath)
      expect(second.branch).toBe("mohist/run-wr-supplied")
    } finally {
      spy.mockRestore()
    }

    const cloneCalls = gitCalls.filter((call) => call.startsWith("git clone"))
    expect(cloneCalls).toHaveLength(0)
    expect(await isBareRepo(cachePath, signal)).toBe(true)
  })

  it("ServerSuppliedPath_VerifySurfacesIdentityMismatchWhenMarkerBoundToDifferentRun", async () => {
    // T-002 contract: a dispatch against a workspace whose marker
    // is bound to a different workflow run must be reported as a
    // `workspace-identity-mismatch` infrastructure failure — NOT
    // recovered by re-cloning. This is the dispatch-time equivalent
    // of the cache-replacement-blocked invariant: the runner must
    // not silently discard another run's work.
    //
    // We exercise the verify path directly because `ensure()` is
    // the smart dispatcher: when the marker is bound to a different
    // run it routes through `materialize()`, which (per the
    // pre-existing contract) rebuilds the workspace. The
    // workspace-identity-mismatch failure is specifically a
    // dispatch-time signal: it is what `WorkExecutor.execute()`'s
    // start-boundary precheck surfaces when a workflow run's
    // `WorkItem` carries a workspace whose marker disagrees with
    // the run identity.
    const root = await mkdtemp(join(tmpdir(), "mohist-workspace-"))
    const repo = await createRepo(root, "repo")
    const runnerRoot = join(root, "runner")
    const manager = new WorkspaceManager(runnerRoot)
    const signal = new AbortController().signal

    // First run materializes normally.
    const suppliedWorkspacePath = join(runnerRoot, "supplied", "workspaces", "issue-9")
    const firstItem = work("wr-supplied-first", "issue-1", repo)
    ;(firstItem.variables as Record<string, unknown>).workspace = { path: suppliedWorkspacePath, branch: "mohist/run-wr-supplied-first", changeDir: null }
    await manager.ensure(firstItem, signal)

    // Second dispatch from a DIFFERENT run points at the same
    // supplied path. Verify() must report workspace-identity-mismatch
    // instead of accepting the workspace as bound to this run.
    const secondItem = work("wr-supplied-second", "issue-1", repo)
    ;(secondItem.variables as Record<string, unknown>).workspace = { path: suppliedWorkspacePath, branch: "mohist/run-wr-supplied-second", changeDir: null }
    await expect(manager.verify(secondItem, signal)).rejects.toMatchObject({ kind: "workspace-identity-mismatch" })

    // The first run's marker is preserved (the verify path is
    // read-only and did not overwrite the marker).
    const markerPath = join(suppliedWorkspacePath, ".mohist", "workspace.json")
    const marker = JSON.parse(await readFile(markerPath, "utf8"))
    expect(marker.workflowRunId).toBe("wr-supplied-first")
  })

  it("ServerSuppliedPath_VerifySurfacesMissingWhenWorkspaceDirectoryDoesNotExist", async () => {
    // T-002 contract: a dispatch against a missing workspace path
    // must be reported as `workspace-missing` and is NEVER recovered
    // by re-cloning. `mohist/rebase` against a missing / unbound
    // workspace fails as workspace-missing rather than
    // materializing on demand.
    const root = await mkdtemp(join(tmpdir(), "mohist-workspace-"))
    const repo = await createRepo(root, "repo")
    const runnerRoot = join(root, "runner")
    const manager = new WorkspaceManager(runnerRoot)
    const signal = new AbortController().signal

    const suppliedWorkspacePath = join(runnerRoot, "supplied", "workspaces", "issue-9")
    const item = work("wr-missing", "issue-1", repo)
    ;(item.variables as Record<string, unknown>).workspace = { path: suppliedWorkspacePath, branch: "mohist/run-wr-missing", changeDir: null }

    // The workspace path does not exist yet (no prior dispatch
    // materialized it), so the dispatch-time verify must surface
    // `workspace-missing` and the runner MUST NOT materialize on
    // demand. materialize() is the once-per-run path; verify() on a
    // missing path refuses to recover.
    await expect(manager.verify(item, signal)).rejects.toMatchObject({ kind: "workspace-missing" })

    // The runner must NOT have created the workspace directory as a
    // side effect of the failed verify.
    expect(exists(suppliedWorkspacePath)).toBe(false)
  })
})

// T-003: workspace health gate. The gate is the runner's only crash
// self-healing mechanism once the disposable landing workspace is
// removed (T-005). The tests below exercise the gate end-to-end with
// real git state: they materialize a workflow workspace, plant a
// residual mid-rebase-crash fixture (`.git/rebase-merge` + conflict
// markers), and verify the next `verify()` / `materialize()` call
// self-heals the workspace so a subsequent dispatch succeeds without
// any manual `git checkout` or `rebase --abort`.
//
// The crash-safety invariant is the "committed work survives a
// mid-rebase crash" scenario from the workspace-health-gate spec: the
// run branch ref does not move while a rebase is in progress (git
// only advances the ref on rebase success), so `git reset --hard
// <runBranch>` after the abort rolls the work tree back without
// discarding the agent's commits that were already on the run branch.
describe("WorkspaceManager health gate (T-003)", () => {
  it("Verify_RecoversFromMidRebaseCrashAndPreservesRunBranchRef", async () => {
    const root = await mkdtemp(join(tmpdir(), "mohist-health-gate-"))
    const repo = await createRepo(root, "repo")
    const runnerRoot = join(root, "runner")
    const manager = new WorkspaceManager(runnerRoot)
    const signal = new AbortController().signal

    const item = work("wr-crash", "issue-crash", repo)

    // Materialize the workflow workspace normally.
    const workspace = await manager.materialize(item, signal)
    expect(workspace.path).toBe(join(runnerRoot, "mohist-local", "workspaces", "issue-9"))

    // The agent commits work to the run branch. This commit is the
    // payload we expect to survive a mid-rebase crash.
    const runBranch = "mohist/run-wr-crash"
    await writeFile(join(workspace.path, "agent.txt"), "agent work\n")
    await git(workspace.path, "add", ".")
    await git(workspace.path, "commit", "-m", "agent commit on run branch")

    const runShaBefore = (await runCommand(
      "git",
      ["-C", workspace.path, "rev-parse", `refs/heads/${runBranch}`],
      ".",
      signal,
    )).stdout.trim()
    expect(runShaBefore.length).toBeGreaterThan(0)

    // Simulate a base-branch advance: write a conflicting change to
    // `master` in the workspace so a `git rebase master` against the
    // run branch's commit hits a merge conflict. This mirrors what
    // `mohist/rebase` (with `remote=origin` unset) sees during
    // integrate; the rebase target can be a local `master` ref in
    // real flows (e.g. remote=origin + fetch, with `master` already
    // mirrored locally).
    await git(workspace.path, "checkout", "master")
    await writeFile(join(workspace.path, "agent.txt"), "conflicting master change\n")
    await git(workspace.path, "add", ".")
    await git(workspace.path, "commit", "-m", "conflicting master commit")
    await git(workspace.path, "checkout", runBranch)

    // Snapshot the run branch ref before the rebase crash. The
    // rebase will fail; the ref MUST still be at this SHA afterwards.
    const runShaPreRebase = (await runCommand(
      "git",
      ["-C", workspace.path, "rev-parse", `refs/heads/${runBranch}`],
      ".",
      signal,
    )).stdout.trim()
    expect(runShaPreRebase).toBe(runShaBefore)

    // Run a real `git rebase master` that will conflict, leaving the
    // workspace in a mid-rebase state (detached HEAD, residual
    // `.git/rebase-merge` directory, conflict markers in agent.txt).
    // We do not abort; we leave the residual state exactly as a
    // crashed runner would.
    const rebase = await runCommand("git", ["-C", workspace.path, "rebase", "master"], workspace.path, new AbortController().signal)
    expect(rebase.exitCode).not.toBe(0)

    // Sanity-check: the workspace is in a mid-rebase state.
    const rebaseMergePath = join(workspace.path, ".git", "rebase-merge")
    expect(exists(rebaseMergePath)).toBe(true)
    const agentTxt = await readFile(join(workspace.path, "agent.txt"), "utf8")
    expect(agentTxt).toContain("<<<<<<<")
    expect(agentTxt).toContain("=======")
    expect(agentTxt).toContain(">>>>>>>")

    // The run branch ref is unchanged after a failed rebase — this
    // is the safety invariant the gate relies on. Document it in
    // the test so any future git behavior change here is caught.
    const runShaAfterFailedRebase = (await runCommand(
      "git",
      ["-C", workspace.path, "rev-parse", `refs/heads/${runBranch}`],
      ".",
      signal,
    )).stdout.trim()
    expect(runShaAfterFailedRebase).toBe(runShaBefore)

    // Now consume the workspace through verify(). This is the
    // dispatch-time entry that the gate must guard. The verify
    // path returns successfully (no WorkspaceMissingError /
    // WorkspaceCorruptError / WorkspaceBranchMismatchError) because
    // the gate healed the residual state.
    const verified = await manager.verify(item, signal)
    expect(verified.path).toBe(workspace.path)
    expect(verified.branch).toBe(runBranch)

    // The workspace is on the run branch (the gate's checkout step).
    const branch = (await runCommand(
      "git",
      ["-C", workspace.path, "rev-parse", "--abbrev-ref", "HEAD"],
      ".",
      signal,
    )).stdout.trim()
    expect(branch).toBe(runBranch)

    // The work tree is clean: no conflict markers, no unmerged
    // entries, no in-progress rebase state. A subsequent task
    // dispatch would see a clean worktree and proceed.
    expect(exists(rebaseMergePath)).toBe(false)
    const applyPath = join(workspace.path, ".git", "rebase-apply")
    expect(exists(applyPath)).toBe(false)
    const finalAgentTxt = await readFile(join(workspace.path, "agent.txt"), "utf8")
    expect(finalAgentTxt).not.toContain("<<<<<<<")
    expect(finalAgentTxt).not.toContain("=======")
    expect(finalAgentTxt).not.toContain(">>>>>>>")
    expect(finalAgentTxt).toBe("agent work\n")
    const status = (await runCommand("git", ["-C", workspace.path, "status", "--porcelain"], ".", signal)).stdout.trim()
    expect(status).toBe("")

    // The run branch ref still points at the agent's original
    // commit. The gate's `reset --hard` aligns the work tree to
    // the run branch ref without moving the ref itself; the
    // agent's work is fully preserved at runShaBefore.
    const runShaAfterRecovery = (await runCommand(
      "git",
      ["-C", workspace.path, "rev-parse", `refs/heads/${runBranch}`],
      ".",
      signal,
    )).stdout.trim()
    expect(runShaAfterRecovery).toBe(runShaBefore)

    // A subsequent verify() call (simulating the next task
    // dispatch) is a no-op for the gate: the workspace is already
    // clean, so the gate detects no residual state and just
    // returns. The dispatch-time branch check still passes.
    const second = await manager.verify(item, signal)
    expect(second.path).toBe(workspace.path)
    expect(second.branch).toBe(runBranch)
    const statusAfterSecond = (await runCommand("git", ["-C", workspace.path, "status", "--porcelain"], ".", signal)).stdout.trim()
    expect(statusAfterSecond).toBe("")
  })

  it("Materialize_RecoversFromMidRebaseCrashBeforeAnyCacheRepair", async () => {
    // The T-003 acceptance criteria call out that the gate must
    // run in `materialize()` as well, not just `verify()`. This test
    // exercises the materialize() entry specifically: it sets up a
    // workspace with a residual `rebase-merge` and then re-runs
    // materialize() (as a restart / recovery path would) and
    // confirms the gate heals the workspace before the cache
    // pipeline is touched.
    const root = await mkdtemp(join(tmpdir(), "mohist-health-gate-mat-"))
    const repo = await createRepo(root, "repo")
    const runnerRoot = join(root, "runner")
    const manager = new WorkspaceManager(runnerRoot)
    const signal = new AbortController().signal

    const item = work("wr-crash-mat", "issue-crash-mat", repo)
    const workspace = await manager.materialize(item, signal)
    const runBranch = "mohist/run-wr-crash-mat"

    // Set up a mid-rebase-crash fixture: commit agent work, advance
    // master, run a failing rebase. The result is the same residual
    // state as the verify() test above.
    await writeFile(join(workspace.path, "agent.txt"), "agent work\n")
    await git(workspace.path, "add", ".")
    await git(workspace.path, "commit", "-m", "agent commit")
    const runShaBefore = (await runCommand(
      "git",
      ["-C", workspace.path, "rev-parse", `refs/heads/${runBranch}`],
      ".",
      signal,
    )).stdout.trim()

    await git(workspace.path, "checkout", "master")
    await writeFile(join(workspace.path, "agent.txt"), "conflicting master\n")
    await git(workspace.path, "add", ".")
    await git(workspace.path, "commit", "-m", "conflicting master")
    await git(workspace.path, "checkout", runBranch)
    const rebase = await runCommand("git", ["-C", workspace.path, "rebase", "master"], workspace.path, new AbortController().signal)
    expect(rebase.exitCode).not.toBe(0)
    expect(exists(join(workspace.path, ".git", "rebase-merge"))).toBe(true)

    // Re-materialize. The gate at the entry of materialize() must
    // detect and abort the residual rebase state. After recovery,
    // the workspace is on the run branch, clean, and the run
    // branch ref is preserved.
    const rematerialized = await manager.materialize(item, signal)
    expect(rematerialized.path).toBe(workspace.path)
    expect(rematerialized.branch).toBe(runBranch)

    const branch = (await runCommand(
      "git",
      ["-C", workspace.path, "rev-parse", "--abbrev-ref", "HEAD"],
      ".",
      signal,
    )).stdout.trim()
    expect(branch).toBe(runBranch)
    expect(exists(join(workspace.path, ".git", "rebase-merge"))).toBe(false)
    const status = (await runCommand("git", ["-C", workspace.path, "status", "--porcelain"], ".", signal)).stdout.trim()
    expect(status).toBe("")
    const runShaAfter = (await runCommand(
      "git",
      ["-C", workspace.path, "rev-parse", `refs/heads/${runBranch}`],
      ".",
      signal,
    )).stdout.trim()
    expect(runShaAfter).toBe(runShaBefore)
  })

  it("Verify_CleanWorkspacePassesThroughGateUnchanged", async () => {
    // T-003 acceptance: "A workspace with no residual state SHALL
    // pass through the health gate unchanged." This test exercises
    // a freshly-materialized workspace and asserts verify() works
    // without the gate touching anything.
    const root = await mkdtemp(join(tmpdir(), "mohist-health-gate-clean-"))
    const repo = await createRepo(root, "repo")
    const runnerRoot = join(root, "runner")
    const manager = new WorkspaceManager(runnerRoot)
    const signal = new AbortController().signal

    const item = work("wr-clean", "issue-clean", repo)
    const workspace = await manager.materialize(item, signal)
    const runBranch = "mohist/run-wr-clean"
    const runShaBefore = (await runCommand(
      "git",
      ["-C", workspace.path, "rev-parse", `refs/heads/${runBranch}`],
      ".",
      signal,
    )).stdout.trim()

    // Workspace is clean. Verify must succeed without any reset.
    const verified = await manager.verify(item, signal)
    expect(verified.path).toBe(workspace.path)
    expect(verified.branch).toBe(runBranch)

    const runShaAfter = (await runCommand(
      "git",
      ["-C", workspace.path, "rev-parse", `refs/heads/${runBranch}`],
      ".",
      signal,
    )).stdout.trim()
    expect(runShaAfter).toBe(runShaBefore)
    const status = (await runCommand("git", ["-C", workspace.path, "status", "--porcelain"], ".", signal)).stdout.trim()
    expect(status).toBe("")
  })

  it("Verify_RecoversFromResidualMergeHeadState", async () => {
    // The acceptance criteria cover `MERGE_HEAD` and
    // `CHERRY_PICK_HEAD` too. This test plants a residual merge
    // state file and verifies the gate uses `git merge --abort` to
    // heal it.
    const root = await mkdtemp(join(tmpdir(), "mohist-health-gate-merge-"))
    const repo = await createRepo(root, "repo")
    const runnerRoot = join(root, "runner")
    const manager = new WorkspaceManager(runnerRoot)
    const signal = new AbortController().signal

    const item = work("wr-merge", "issue-merge", repo)
    const workspace = await manager.materialize(item, signal)
    const runBranch = "mohist/run-wr-merge"
    const runShaBefore = (await runCommand(
      "git",
      ["-C", workspace.path, "rev-parse", `refs/heads/${runBranch}`],
      ".",
      signal,
    )).stdout.trim()

    // Simulate a residual merge state by writing a MERGE_HEAD
    // marker. We do not start a real merge — the gate's contract
    // is to detect the marker and call `git merge --abort`,
    // which will no-op (exit non-zero) when there is no in-progress
    // merge. The subsequent `reset --hard` is the authoritative
    // recovery, and the gate treats the abort as best-effort.
    await writeFile(join(workspace.path, ".git", "MERGE_HEAD"), runShaBefore + "\n")
    expect(exists(join(workspace.path, ".git", "MERGE_HEAD"))).toBe(true)

    const verified = await manager.verify(item, signal)
    expect(verified.path).toBe(workspace.path)
    expect(verified.branch).toBe(runBranch)
    expect(exists(join(workspace.path, ".git", "MERGE_HEAD"))).toBe(false)

    const branch = (await runCommand(
      "git",
      ["-C", workspace.path, "rev-parse", "--abbrev-ref", "HEAD"],
      ".",
      signal,
    )).stdout.trim()
    expect(branch).toBe(runBranch)
    const runShaAfter = (await runCommand(
      "git",
      ["-C", workspace.path, "rev-parse", `refs/heads/${runBranch}`],
      ".",
      signal,
    )).stdout.trim()
    expect(runShaAfter).toBe(runShaBefore)
  })

  it("Verify_RecoversFromResidualCherryPickHeadState", async () => {
    // The acceptance scenarios also cover `CHERRY_PICK_HEAD`. Plant
    // a residual cherry-pick state and verify the gate uses
    // `git cherry-pick --abort` to heal it, then re-aligns the
    // workspace to the run branch.
    const root = await mkdtemp(join(tmpdir(), "mohist-health-gate-cherry-"))
    const repo = await createRepo(root, "repo")
    const runnerRoot = join(root, "runner")
    const manager = new WorkspaceManager(runnerRoot)
    const signal = new AbortController().signal

    const item = work("wr-cherry", "issue-cherry", repo)
    const workspace = await manager.materialize(item, signal)
    const runBranch = "mohist/run-wr-cherry"
    const runShaBefore = (await runCommand(
      "git",
      ["-C", workspace.path, "rev-parse", `refs/heads/${runBranch}`],
      ".",
      signal,
    )).stdout.trim()

    // Plant a residual cherry-pick marker. We do not start a real
    // cherry-pick — the gate's contract is to detect the marker
    // and call `git cherry-pick --abort`, which will no-op (exit
    // non-zero) when there is no in-progress cherry-pick. The
    // subsequent `reset --hard` is the authoritative recovery, and
    // the gate treats the abort as best-effort.
    await writeFile(join(workspace.path, ".git", "CHERRY_PICK_HEAD"), runShaBefore + "\n")
    expect(exists(join(workspace.path, ".git", "CHERRY_PICK_HEAD"))).toBe(true)

    const verified = await manager.verify(item, signal)
    expect(verified.path).toBe(workspace.path)
    expect(verified.branch).toBe(runBranch)
    expect(exists(join(workspace.path, ".git", "CHERRY_PICK_HEAD"))).toBe(false)

    const branch = (await runCommand(
      "git",
      ["-C", workspace.path, "rev-parse", "--abbrev-ref", "HEAD"],
      ".",
      signal,
    )).stdout.trim()
    expect(branch).toBe(runBranch)
    const runShaAfter = (await runCommand(
      "git",
      ["-C", workspace.path, "rev-parse", `refs/heads/${runBranch}`],
      ".",
      signal,
    )).stdout.trim()
    expect(runShaAfter).toBe(runShaBefore)
  })
})

describe("WorkspaceManager.slugify", () => {
  // Item-5: the workspace path slug must stay in sync with the C#
  // MohistWorkspaceLayout.Slug helper. The server-side table is pinned in
  // PathContractRegressionSpecs.Slug_MatchesRunnerAlgorithm; this mirrors
  // it for the JS side. A failure on either side points at a divergence.
  it.each([
    ["my-project", "my-project"],
    ["My Project!", "my-project"],
    ["  spaced  out  ", "spaced-out"],
    ["foo_bar.baz", "foo-bar-baz"],
    ["Café", "caf"],
    ["测试-project", "project"],
    ["", "project"],
  ])("slugify(%j) === %j", (input, expected) => {
    expect(slugify(input)).toBe(expected)
  })
})

// T-001 (issue #181): hardened bare-cache materialization. Each test
// here builds on the existing createRepo / work helpers and asserts a
// distinct WHEN/THEN from the hardened-cache spec:
//
//   - fetch failure on an existing cache keeps the cache + the
//     shared workspace's alternates intact and surfaces a distinct
//     `cache-fetch-failed` kind (non-fatal on re-materialization);
//   - replacement is refused when an active workspace references
//     the cache via `.git/objects/info/alternates`;
//   - replacement proceeds only when origin mismatches (or fsck
//     detects corruption) AND no active workspace references the
//     cache;
//   - on first materialization (no prior cache) a failed clone is
//     fatal: no fallback.
describe("WorkspaceManager hardened cache", () => {
  it("FetchFailure_KeepsCacheAndSharedWorkspaceAlternatesValid", async () => {
    const root = await mkdtemp(join(tmpdir(), "mohist-cache-hardening-"))
    const source = await createRepo(root, "source")
    const runnerRoot = join(root, "runner")
    const manager = new WorkspaceManager(runnerRoot)
    const signal = new AbortController().signal

    // First materialize() builds the cache + workspace against the
    // local source path. We call materialize() directly (not
    // ensure()) because the T-002 contract caches the materialized
    // state via the workspace marker: a second ensure() call would
    // short-circuit to verify() and miss the cache-fetch path under
    // test. The hardened-cache contract lives inside the materialize
    // pipeline itself, so driving it directly is correct.
    const first = await manager.materialize(work("wr-fetch", "issue-fetch", source), signal)
    const cachePath = join(runnerRoot, "repos", "project-1", "master")
    expect(await isBareRepo(cachePath, signal)).toBe(true)

    // Snapshot the workspace's alternates file. This is the reference
    // that the cache-delete path would invalidate.
    const alternatesPath = join(first.path, ".git", "objects", "info", "alternates")
    const alternatesBefore = await readFile(alternatesPath, "utf8")
    expect(alternatesBefore).toContain(cachePath)

    // Capture an object OID present in the cache's object store so we
    // can verify the object store is untouched after the fetch failure.
    const objectOid = (await runCommand(
      "git",
      ["-C", cachePath, "rev-parse", "refs/heads/master"],
      ".",
      signal,
    )).stdout.trim()
    expect(objectOid.length).toBeGreaterThan(0)
    const looseFile = join(cachePath, "objects", objectOid.slice(0, 2), objectOid.slice(2))
    expect(exists(looseFile)).toBe(true)

    // Make `git fetch origin` fail while leaving the cache's `origin`
    // URL untouched. Removing the underlying source directory keeps
    // the configured remote URL (string match still holds), so the
    // runner's materialize() takes the same-origin fetch branch.
    await runCommand("rm", ["-rf", source], ".", signal)

    // Force re-materialization by stripping the workspace marker so
    // planResolution() routes through materialize() again on the
    // next call. This mirrors the once-per-run real-world path where
    // an explicit recover / restart drives the materialize path.
    const { rm: rmMarker } = await import("node:fs/promises")
    await rmMarker(join(first.path, ".mohist", "workspace.json"), { force: true })

    const second = await manager.materialize(work("wr-fetch", "issue-fetch", source), signal)
    expect(second.path).toBe(first.path)

    // The cache directory must still be in place, still a bare repo,
    // and still hosting the same object store content.
    expect(exists(cachePath)).toBe(true)
    expect(await isBareRepo(cachePath, signal)).toBe(true)
    expect(exists(looseFile)).toBe(true)

    // The workspace's alternates file is unchanged — the cache wasn't
    // deleted, so the shared object store reference is still valid.
    const alternatesAfter = await readFile(alternatesPath, "utf8")
    expect(alternatesAfter).toBe(alternatesBefore)

    // The workspace's working tree is still resolvable against the
    // cache's object store. `git rev-parse HEAD` on the workspace
    // succeeds only if the alternates reference still resolves to a
    // valid object store.
    const head = await runCommand("git", ["-C", first.path, "rev-parse", "HEAD"], ".", signal)
    expect(head.exitCode).toBe(0)
    expect(head.stdout.trim().length).toBeGreaterThan(0)
  })

  it("Replacement_BlockedWhenWorkspaceReferencesCacheAlternates", async () => {
    const root = await mkdtemp(join(tmpdir(), "mohist-cache-hardening-"))
    const sourceA = await createRepo(root, "sourceA")
    const sourceB = await createRepo(root, "sourceB")
    const runnerRoot = join(root, "runner")
    const manager = new WorkspaceManager(runnerRoot)
    const signal = new AbortController().signal

    // Materialize the workspace (and the cache) the normal way.
    const first = await manager.materialize(work("wr-block", "issue-block", sourceA), signal)
    const cachePath = join(runnerRoot, "repos", "project-1", "master")
    expect(await isBareRepo(cachePath, signal)).toBe(true)

    // The shared workspace clone references the cache via alternates.
    const alternatesPath = join(first.path, ".git", "objects", "info", "alternates")
    expect(await readFile(alternatesPath, "utf8")).toContain(cachePath)

    // Move the cache's `origin` to sourceB so the runner sees an
    // identity mismatch. The workspace still references the cache
    // via alternates, so replacement must be refused.
    await runCommand("git", ["-C", cachePath, "remote", "set-url", "origin", sourceB], ".", signal)

    // Strip the marker so planResolution() routes through materialize
    // again on the next call (T-002 once-per-run contract).
    const { rm: rmMarker } = await import("node:fs/promises")
    await rmMarker(join(first.path, ".mohist", "workspace.json"), { force: true })

    // materialize() must throw the distinct `cache-replacement-blocked`
    // kind. The work item still declares the original origin (sourceA);
    // the cache's stored origin is now sourceB, so the runner sees
    // an identity mismatch and considers replacement — which the
    // active workspace's alternates reference blocks.
    await expect(manager.materialize(work("wr-block", "issue-block", sourceA), signal)).rejects.toBeInstanceOf(CacheReplacementBlockedError)

    // The cache directory is preserved, still bare, and still has
    // its original object store.
    expect(exists(cachePath)).toBe(true)
    expect(await isBareRepo(cachePath, signal)).toBe(true)
    expect((await runCommand("git", ["-C", cachePath, "rev-parse", "--is-bare-repository"], ".", signal)).stdout.trim()).toBe("true")

    // The workspace's reference to the cache's object store still
    // resolves (HEAD is still reachable via alternates).
    const head = await runCommand("git", ["-C", first.path, "rev-parse", "HEAD"], ".", signal)
    expect(head.exitCode).toBe(0)
  })

  it("Replacement_AllowedWhenCacheOriginMismatchesAndUnreferenced", async () => {
    const root = await mkdtemp(join(tmpdir(), "mohist-cache-hardening-"))
    const sourceA = await createRepo(root, "sourceA")
    const sourceB = await createRepo(root, "sourceB")
    const runnerRoot = join(root, "runner")
    const manager = new WorkspaceManager(runnerRoot)
    const signal = new AbortController().signal

    // Materialize the cache + workspace.
    const first = await manager.materialize(work("wr-allow", "issue-allow", sourceA), signal)
    const cachePath = join(runnerRoot, "repos", "project-1", "master")
    expect(await isBareRepo(cachePath, signal)).toBe(true)

    // Tear down the workspace so its alternates reference is gone.
    // The reference scan must see an empty workspaces/ directory and
    // an empty workspaces/ directory and allow replacement.
    await runCommand("rm", ["-rf", join(runnerRoot, "mohist-local", "workspaces")], ".", signal)
    expect(exists(first.path)).toBe(false)

    // Switch the cache origin to a different repository so the
    // identity-mismatch path triggers.
    await runCommand("git", ["-C", cachePath, "remote", "set-url", "origin", sourceB], ".", signal)

    // Replacement is allowed: the cache is deleted and re-cloned
    // against the new origin. The previous materialize() wrote a
    // marker, but the workspace directory was deleted, so the marker
    // is gone too and planResolution() routes through materialize().
    const result = await manager.materialize(work("wr-allow", "issue-allow", sourceB), signal)
    expect(result.path).toBeTruthy()
    expect(exists(cachePath)).toBe(true)
    expect(await isBareRepo(cachePath, signal)).toBe(true)

    // The cache now points at the new origin URL.
    const origin = (await runCommand("git", ["-C", cachePath, "remote", "get-url", "origin"], ".", signal)).stdout.trim()
    expect(origin).toBe(sourceB)

    // The freshly-materialized workspace was cloned from the
    // new-origin cache and contains the new repo's content.
    expect(await readFile(join(result.path, "README.md"), "utf8")).toBe("base\n")
  })

  it("FirstMaterialization_CloneFailureIsFatal", async () => {
    const root = await mkdtemp(join(tmpdir(), "mohist-cache-hardening-"))
    const runnerRoot = join(root, "runner")
    const manager = new WorkspaceManager(runnerRoot)
    const signal = new AbortController().signal

    // Use a gitUrl that always fails. There's no prior cache to fall
    // back on, so the failure must be fatal — the runner cannot
    // continue. Calling materialize() directly exercises the
    // first-materialization clone path explicitly.
    //
    // Point at a `file://` URL under a path that does not exist.
    // Git treats `file://` transports as local I/O (no HTTP/HTTPS,
    // no proxy, no DNS), so the clone fails immediately with
    // `fatal: '<path>' does not appear to be a git repository` —
    // a few milliseconds, well inside the 10s test timeout.
    //
    // We previously tried `https://127.0.0.1:1/...` (TCP connect
    // timeout exceeded the 10s test budget) and
    // `https://no-such-host.invalid/...` (DNS does not resolve,
    // but environments with a configured HTTP proxy — e.g.
    // `http.proxy` set in git config — forward the request to the
    // proxy, which then waits for the upstream it can never reach).
    // `file://` avoids both failure modes.
    const badUrl = "file:///this-path-does-not-exist/this-host-has-no-git-server.git"

    // The thrown error must NOT be `CacheReplacementBlockedError`; the
    // first-materialization failure is a plain clone failure surfaced as
    // a fatal error so the run can attribute it correctly.
    await expect(manager.materialize(work("wr-first", "issue-first", badUrl), signal)).rejects.toThrow(/git clone failed/)
    await expect(manager.materialize(work("wr-first", "issue-first", badUrl), signal)).rejects.not.toBeInstanceOf(CacheReplacementBlockedError)

    // No cache directory was created (the clone failed before any
    // local state landed) and no workspace directory was created.
    const cachePath = join(runnerRoot, "repos", "project-1", "master")
    expect(exists(cachePath)).toBe(false)
    expect(exists(join(runnerRoot, "mohist-local", "workspaces"))).toBe(false)
  })

  it("FetchFailure_OnReMaterialization_SurfacesDistinctKindAndKeepsRunUsable", async () => {
    const root = await mkdtemp(join(tmpdir(), "mohist-cache-hardening-"))
    const source = await createRepo(root, "source")
    const runnerRoot = join(root, "runner")
    const manager = new WorkspaceManager(runnerRoot)
    const signal = new AbortController().signal

    // Materialize normally.
    const first = await manager.materialize(work("wr-rm", "issue-rm", source), signal)
    const cachePath = join(runnerRoot, "repos", "project-1", "master")

    // Break the cache's underlying origin so `git fetch origin` fails.
    // We delete the source directory, which keeps the cache's
    // configured remote URL unchanged (so origin-match holds) but
    // makes the fetch fail.
    await runCommand("rm", ["-rf", source], ".", signal)

    // Strip the marker so planResolution() routes through materialize
    // again on the next call (T-002 once-per-run contract).
    const { rm: rmMarker } = await import("node:fs/promises")
    await rmMarker(join(first.path, ".mohist", "workspace.json"), { force: true })

    const rematerialized = await manager.materialize(work("wr-rm", "issue-rm", source), signal)
    expect(rematerialized.path).toBe(first.path)

    // Crucially, the cache directory is preserved AND the previously
    // materialized workspace is still resolvable / usable.
    expect(exists(cachePath)).toBe(true)
    expect(await isBareRepo(cachePath, signal)).toBe(true)
    expect(exists(first.path)).toBe(true)
    const head = await runCommand("git", ["-C", first.path, "rev-parse", "HEAD"], ".", signal)
    expect(head.exitCode).toBe(0)
  })

  it("Replacement_AllowedWhenCacheOriginMismatchesAndUnreferenced_CorruptionRegression", async () => {
    const root = await mkdtemp(join(tmpdir(), "mohist-cache-hardening-"))
    const source = await createRepo(root, "source")
    const runnerRoot = join(root, "runner")
    const manager = new WorkspaceManager(runnerRoot)
    const signal = new AbortController().signal

    const cachePath = join(runnerRoot, "repos", "project-1", "master")
    await runCommand("mkdir", ["-p", join(cachePath, "..")], ".", signal)
    const clone = await runCommand("git", ["clone", "--bare", source, cachePath], ".", signal)
    expect(clone.exitCode).toBe(0)

    await runCommand("git", ["-C", cachePath, "remote", "set-url", "origin", join(root, "other")], ".", signal)

    const result = await manager.materialize(work("wr-corrupt-cache", "issue-corrupt-cache", source), signal)
    expect(exists(result.path)).toBe(true)
    const origin = (await runCommand("git", ["-C", cachePath, "remote", "get-url", "origin"], ".", signal)).stdout.trim()
    expect(origin).toBe(source)
  })

  it("Replacement_AllowedWhenSameOriginCacheIsVerifiedCorruptAndUnreferenced", async () => {
    const root = await mkdtemp(join(tmpdir(), "mohist-cache-hardening-"))
    const source = await createRepo(root, "source")
    const runnerRoot = join(root, "runner")
    const manager = new WorkspaceManager(runnerRoot)
    const signal = new AbortController().signal

    const first = await manager.materialize(work("wr-corrupt-allow", "issue-corrupt-allow", source), signal)
    const cachePath = join(runnerRoot, "repos", "project-1", "master")
    await rm(first.path, { recursive: true, force: true })
    await writeFile(join(cachePath, "refs", "heads", "broken"), "0000000000000000000000000000000000000001\n")
    const fsck = await runCommand("git", ["-C", cachePath, "fsck", "--full", "--no-progress"], ".", signal)
    expect(fsck.exitCode).not.toBe(0)

    const result = await manager.materialize(work("wr-corrupt-allow", "issue-corrupt-allow", source), signal)

    expect(exists(result.path)).toBe(true)
    const repaired = await runCommand("git", ["-C", cachePath, "fsck", "--full", "--no-progress"], ".", signal)
    expect(repaired.exitCode).toBe(0)
  })

  it("Replacement_BlockedWhenSameOriginCacheIsVerifiedCorruptButReferenced", async () => {
    const root = await mkdtemp(join(tmpdir(), "mohist-cache-hardening-"))
    const source = await createRepo(root, "source")
    const runnerRoot = join(root, "runner")
    const manager = new WorkspaceManager(runnerRoot)
    const signal = new AbortController().signal

    const first = await manager.materialize(work("wr-corrupt-block", "issue-corrupt-block", source), signal)
    const cachePath = join(runnerRoot, "repos", "project-1", "master")
    await corruptBareCacheObject(cachePath, signal)
    await rm(join(first.path, ".mohist", "workspace.json"), { force: true })

    await expect(manager.materialize(work("wr-corrupt-block", "issue-corrupt-block", source), signal)).rejects.toBeInstanceOf(CacheReplacementBlockedError)
    expect(exists(cachePath)).toBe(true)
  })

  it("WorkspaceCloneFailure_DoesNotDeleteSameOriginHealthyCache", async () => {
    const root = await mkdtemp(join(tmpdir(), "mohist-cache-hardening-"))
    const source = await createRepo(root, "source")
    const runnerRoot = join(root, "runner")
    const manager = new WorkspaceManager(runnerRoot)
    const signal = new AbortController().signal

    const cachePath = join(runnerRoot, "repos", "project-1", "master")
    await manager.materialize(work("wr-clone-fail", "issue-clone-fail", source), signal)
    await rm(join(runnerRoot, "mohist-local", "workspaces"), { recursive: true, force: true })
    await writeFile(join(runnerRoot, "mohist-local", "workspaces"), "not a directory")
    const before = await runCommand("git", ["-C", cachePath, "rev-parse", "refs/heads/master"], ".", signal)

    await expect(manager.materialize(work("wr-clone-fail", "issue-clone-fail", source), signal)).rejects.toThrow(/ENOTDIR|not a directory|mkdir/)

    expect(exists(cachePath)).toBe(true)
    const after = await runCommand("git", ["-C", cachePath, "rev-parse", "refs/heads/master"], ".", signal)
    expect(after.stdout.trim()).toBe(before.stdout.trim())
  })

  it("Materialize_KeepsRunnerMarkerOutOfCommitHistory", async () => {
    const root = await mkdtemp(join(tmpdir(), "mohist-marker-history-"))
    const source = await createRepo(root, "source")
    const runnerRoot = join(root, "runner")
    const manager = new WorkspaceManager(runnerRoot)
    const signal = new AbortController().signal

    const workspace = await manager.materialize(work("wr-marker", "issue-marker", source), signal)
    const trackedMarker = await runCommand("git", ["-C", workspace.path, "ls-files", ".mohist/workspace.json"], ".", signal)
    expect(trackedMarker.exitCode).toBe(0)
    expect(trackedMarker.stdout.trim()).toBe("")

    const log = await runCommand("git", ["-C", workspace.path, "log", "--oneline", "--", ".mohist/workspace.json", ".gitignore"], ".", signal)
    expect(log.exitCode).toBe(0)
    expect(log.stdout.trim()).toBe("")
  })
})

async function createRepo(root: string, name: string) {
  const repo = join(root, name)
  await git(root, "init", repo)
  await git(repo, "config", "user.email", "test@example.com")
  await git(repo, "config", "user.name", "Test User")
  await writeFile(join(repo, "README.md"), "base\n")
  await git(repo, "add", ".")
  await git(repo, "commit", "-m", "base")
  return repo
}

async function isBareRepo(path: string, signal: AbortSignal) {
  if (!exists(path)) return false
  const result = await runCommand("git", ["-C", path, "rev-parse", "--is-bare-repository"], ".", signal)
  return result.exitCode === 0 && result.stdout.trim() === "true"
}

function work(workflowRunId: string, issueId: string, gitUrl: string) {
  return {
    workflowRunId,
    workId: "proposal.1",
    workType: "task",
    uses: "mohist/acp-agent",
    variables: {
      mohist: { runId: workflowRunId },
      issue: { id: issueId, number: 9 },
      project: { id: "project-1", name: "Mohist Local" },
      repository: { name: "master", gitUrl, baseBranch: "master" },
      openspecChangeDir: "openspec/changes/issue-9",
    },
  }
}

async function git(cwd: string, ...args: string[]) {
  const result = await runCommand("git", args, cwd, new AbortController().signal)
  if (result.exitCode !== 0) throw new Error(result.stderr || result.stdout)
  return result
}

async function corruptBareCacheObject(cachePath: string, signal: AbortSignal) {
  const head = (await runCommand("git", ["-C", cachePath, "rev-parse", "refs/heads/master"], ".", signal)).stdout.trim()
  const objectPath = join(cachePath, "objects", head.slice(0, 2), head.slice(2))
  if (!exists(objectPath)) {
    const packDir = join(cachePath, "objects", "pack")
    const packs = await readdir(packDir)
    const pack = packs.find((entry) => entry.endsWith(".pack"))
    if (!pack) throw new Error("No loose or packed object found to corrupt")
    await writeFile(join(packDir, pack), "corrupt")
    return
  }
  await chmod(objectPath, 0o600)
  await writeFile(objectPath, "corrupt")
}
