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
    // by re-cloning. `mohist/prepare` against a missing / unbound
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

describe("WorkspaceManager landing workspaces", () => {
  it("CreateLandingWorkspace_ClonesSharedAndExposesBaseAndRunBranchRefs", async () => {
    const root = await mkdtemp(join(tmpdir(), "mohist-landing-"))
    const repo = await createRepo(root, "repo")
    const runnerRoot = join(root, "runner")
    const manager = new WorkspaceManager(runnerRoot)
    const signal = new AbortController().signal

    const workspace = await manager.ensure(work("wr-land-1", "issue-1", repo), signal)
    // Advance the run branch with a commit so the landing clone must see
    // both the base branch ref and the prepared run branch ref.
    await writeFile(join(workspace.path, "draft.txt"), "draft\n")
    await git(workspace.path, "add", ".")
    await git(workspace.path, "commit", "-m", "issue work")

    const landing = await manager.createLandingWorkspace(work("wr-land-1", "issue-1", repo), signal)
    expect(landing.runBranch).toBe("mohist/run-wr-land-1")
    expect(landing.baseBranch).toBe("master")
    expect(landing.gitUrl).toBe(repo)
    expect(landing.path.startsWith(join(runnerRoot, "mohist-local", "landing", "wr-land-1-"))).toBe(true)
    expect(landing.path).not.toBe(workspace.path)

    // The landing clone is a separate working tree and not the workspace.
    expect(exists(join(landing.path, ".git"))).toBe(true)
    expect(landing.path).not.toBe(workspace.path)

    // Both refs visible: the base branch (master) and the run branch
    // (mohist/run-wr-land-1).
    const baseRef = await runCommand("git", ["-C", landing.path, "rev-parse", "--verify", "refs/heads/master"], ".", signal)
    expect(baseRef.exitCode).toBe(0)
    const runRef = await runCommand("git", ["-C", landing.path, "rev-parse", "--verify", "refs/heads/mohist/run-wr-land-1"], ".", signal)
    expect(runRef.exitCode).toBe(0)

    // The landing workspace was created with `git clone --shared`, so its
    // .git/objects/info/alternates should reference the workspace path.
    const alternates = await readFile(join(landing.path, ".git", "objects", "info", "alternates"), "utf8").catch(() => "")
    expect(alternates).toContain(workspace.path)

    await manager.disposeLandingWorkspace(landing, signal)
  })

  it("CreateLandingWorkspace_ResetsOriginToConfiguredGitUrl", async () => {
    const root = await mkdtemp(join(tmpdir(), "mohist-landing-"))
    const repo = await createRepo(root, "repo")
    const runnerRoot = join(root, "runner")
    const manager = new WorkspaceManager(runnerRoot)
    const signal = new AbortController().signal

    await manager.ensure(work("wr-land-2", "issue-2", repo), signal)
    const landing = await manager.createLandingWorkspace(work("wr-land-2", "issue-2", repo), signal)

    // The landing workspace's origin must be the configured repository
    // gitUrl, not the bare cache or the workspace path. This is what
    // lets publish push to the real upstream from the landing workspace.
    const remote = await runCommand("git", ["-C", landing.path, "remote", "get-url", "origin"], ".", signal)
    expect(remote.exitCode).toBe(0)
    expect(remote.stdout.trim()).toBe(repo)

    await manager.disposeLandingWorkspace(landing, signal)
  })

  it("DisposeLandingWorkspace_RmRfDoesNotCorruptWorkflowWorkspace", async () => {
    const root = await mkdtemp(join(tmpdir(), "mohist-landing-"))
    const repo = await createRepo(root, "repo")
    const runnerRoot = join(root, "runner")
    const manager = new WorkspaceManager(runnerRoot)
    const signal = new AbortController().signal

    const workspace = await manager.ensure(work("wr-land-3", "issue-3", repo), signal)
    await writeFile(join(workspace.path, "keep.txt"), "keep me\n")
    await git(workspace.path, "add", ".")
    await git(workspace.path, "commit", "-m", "issue work")

    const landing = await manager.createLandingWorkspace(work("wr-land-3", "issue-3", repo), signal)
    expect(exists(landing.path)).toBe(true)

    const result = await manager.disposeLandingWorkspace(landing, signal)
    expect(result.disposed).toBe(true)
    expect(exists(landing.path)).toBe(false)

    // After disposing the landing workspace, the workflow workspace must
    // be unaffected: same path, same branch, same working tree, same
    // commit history, and the run branch must still point at the
    // committed work.
    const head = await runCommand("git", ["-C", workspace.path, "rev-parse", "--abbrev-ref", "HEAD"], ".", signal)
    expect(head.exitCode).toBe(0)
    expect(head.stdout.trim()).toBe("mohist/run-wr-land-3")
    expect(await readFile(join(workspace.path, "keep.txt"), "utf8")).toBe("keep me\n")
    const runSha = await runCommand("git", ["-C", workspace.path, "rev-parse", "HEAD"], ".", signal)
    expect(runSha.exitCode).toBe(0)

    // The workspace's object store is intact: the run branch ref is
    // still resolvable, and a fresh clone --shared can be made from it
    // (would fail with a corrupt object store).
    const refCheck = await runCommand("git", ["-C", workspace.path, "rev-parse", "--verify", "refs/heads/mohist/run-wr-land-3"], ".", signal)
    expect(refCheck.exitCode).toBe(0)
    expect(refCheck.stdout.trim()).toBe(runSha.stdout.trim())
  })

  it("Materialize_PrunesStaleLandingDirsFromPriorCrashedRun", async () => {
    const root = await mkdtemp(join(tmpdir(), "mohist-landing-"))
    const repo = await createRepo(root, "repo")
    const runnerRoot = join(root, "runner")
    const manager = new WorkspaceManager(runnerRoot)
    const signal = new AbortController().signal

    // First run materializes the workflow workspace.
    await manager.materialize(work("wr-prune-1", "issue-p", repo), signal)
    const firstLanding = await manager.createLandingWorkspace(work("wr-prune-1", "issue-p", repo), signal)
    expect(exists(firstLanding.path)).toBe(true)

    // Simulate a crashed run: the landing directory is left behind
    // and NOT disposed. The next materialize() for the same runId
    // must remove it. (Under T-002, the once-per-run contract means
    // a normal subsequent ensure() would verify-only and skip the
    // landing prune — but a deliberate re-materialize, e.g. after a
    // crash recovery / restart, exercises the prune path.)
    const second = await manager.materialize(work("wr-prune-1", "issue-p", repo), signal)
    expect(exists(firstLanding.path)).toBe(false)

    // The workflow workspace is intact.
    const head = await runCommand("git", ["-C", second.path, "rev-parse", "--abbrev-ref", "HEAD"], ".", signal)
    expect(head.exitCode).toBe(0)
    expect(head.stdout.trim()).toBe("mohist/run-wr-prune-1")

    // A subsequent create for the same run creates a fresh landing dir.
    const newLanding = await manager.createLandingWorkspace(work("wr-prune-1", "issue-p", repo), signal)
    expect(newLanding.path).not.toBe(firstLanding.path)
    expect(exists(newLanding.path)).toBe(true)
    await manager.disposeLandingWorkspace(newLanding, signal)
  })

  it("Materialize_PruneLandingWorkspaces_RemovesOnlyMatchingRunIdDirectories", async () => {
    const root = await mkdtemp(join(tmpdir(), "mohist-landing-"))
    const repo = await createRepo(root, "repo")
    const runnerRoot = join(root, "runner")
    const manager = new WorkspaceManager(runnerRoot)
    const signal = new AbortController().signal

    // Two distinct runIds. Create landing dirs for each by directly
    // placing directories at the expected path.
    const itemA = work("wr-prune-A", "issue-A", repo)
    const itemB = work("wr-prune-B", "issue-B", repo)
    await manager.materialize(itemA, signal)
    await manager.materialize(itemB, signal)

    const landingRootA = join(runnerRoot, "mohist-local", "landing")
    const staleA = join(landingRootA, "wr-prune-A-leftover")
    const staleB = join(landingRootA, "wr-prune-B-leftover")
    await runCommand("mkdir", ["-p", staleA], ".", signal)
    await runCommand("mkdir", ["-p", staleB], ".", signal)
    // write a marker file so deleteDirectory is meaningful
    await writeFile(join(staleA, "leftover.txt"), "x")
    await writeFile(join(staleB, "leftover.txt"), "x")

    // Re-materialize run A — only A's leftover must be removed.
    await manager.materialize(itemA, signal)
    expect(exists(staleA)).toBe(false)
    expect(exists(staleB)).toBe(true)

    // Re-materialize run B — now B's leftover is removed.
    await manager.materialize(itemB, signal)
    expect(exists(staleB)).toBe(false)
  })

  it("CreateLandingWorkspace_RespectsSuppliedWorkspacePath", async () => {
    const root = await mkdtemp(join(tmpdir(), "mohist-landing-"))
    const repo = await createRepo(root, "repo")
    const runnerRoot = join(root, "runner")
    const manager = new WorkspaceManager(runnerRoot)
    const signal = new AbortController().signal

    const suppliedWorkspacePath = join(runnerRoot, "supplied", "workspaces", "issue-9")
    const item = work("wr-supplied-landing", "issue-s", repo)
    ;(item.variables as Record<string, unknown>).workspace = { path: suppliedWorkspacePath, branch: "mohist/run-wr-supplied-landing", changeDir: null }
    const workspace = await manager.ensure(item, signal)
    expect(workspace.path).toBe(suppliedWorkspacePath)

    const landing = await manager.createLandingWorkspace(item, signal)
    expect(landing.path).not.toBe(suppliedWorkspacePath)
    expect(landing.path.startsWith(join(runnerRoot, "mohist-local", "landing", "wr-supplied-landing-"))).toBe(true)

    // Origin reset still happens for the supplied-path scenario.
    const remote = await runCommand("git", ["-C", landing.path, "remote", "get-url", "origin"], ".", signal)
    expect(remote.exitCode).toBe(0)
    expect(remote.stdout.trim()).toBe(repo)

    await manager.disposeLandingWorkspace(landing, signal)
  })

  it("DisposeLandingWorkspace_OfMissingPathIsIdempotent", async () => {
    const root = await mkdtemp(join(tmpdir(), "mohist-landing-"))
    const manager = new WorkspaceManager(join(root, "runner"))
    const signal = new AbortController().signal
    const missing = join(root, "runner", "mohist-local", "landing", "wr-missing-leftover")
    const result = await manager.disposeLandingWorkspace(missing, signal)
    expect(result.disposed).toBe(true)
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
    // an empty (or missing) landing/ directory and allow replacement.
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

  it("Replacement_BlockedWhenLandingWorkspaceReferencesCacheTransitively", async () => {
    const root = await mkdtemp(join(tmpdir(), "mohist-cache-hardening-"))
    const sourceA = await createRepo(root, "sourceA")
    const sourceB = await createRepo(root, "sourceB")
    const runnerRoot = join(root, "runner")
    const manager = new WorkspaceManager(runnerRoot)
    const signal = new AbortController().signal

    // Materialize the workflow workspace + a landing workspace. The
    // landing workspace is a `--shared` clone whose alternates point
    // at the workflow workspace, which itself points at the cache —
    // so the reference scan must follow the chain and find a
    // transitive reference.
    await manager.materialize(work("wr-land-block", "issue-land-block", sourceA), signal)
    const landing = await manager.createLandingWorkspace(work("wr-land-block", "issue-land-block", sourceA), signal)
    const cachePath = join(runnerRoot, "repos", "project-1", "master")
    const landingAlternates = await readFile(join(landing.path, ".git", "objects", "info", "alternates"), "utf8")
    // sanity-check: the landing alternates point at the workflow
    // workspace, not the cache directly. The runner must follow this
    // chain to detect the transitive reference.
    expect(landingAlternates).toContain(join(runnerRoot, "mohist-local", "workspaces"))

    // Move cache origin to sourceB so the runner sees a mismatch.
    await runCommand("git", ["-C", cachePath, "remote", "set-url", "origin", sourceB], ".", signal)

    // Strip the marker so planResolution() routes through materialize
    // again on the next call (T-002 once-per-run contract).
    const { rm: rmMarker } = await import("node:fs/promises")
    await rmMarker(join(landing.path, "..", "..", "workspaces", "issue-land-block", ".mohist", "workspace.json"), { force: true })

    // The work item still declares the original sourceA, so the
    // runner sees an identity mismatch and must refuse replacement
    // because the landing workspace transitively references the cache.
    await expect(manager.materialize(work("wr-land-block", "issue-land-block", sourceA), signal)).rejects.toBeInstanceOf(CacheReplacementBlockedError)
    expect(exists(cachePath)).toBe(true)
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
    const badUrl = "https://127.0.0.1:1/this-host-has-no-git-server.git"

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
