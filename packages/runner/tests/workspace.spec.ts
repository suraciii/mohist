import { mkdtemp, readFile, writeFile } from "node:fs/promises"
import { join } from "node:path"
import { tmpdir } from "node:os"
import { describe, expect, it, vi } from "vitest"
import { WorkspaceManager, slugify } from "../src/runtime/workspace.js"
import { exists, runCommand } from "../src/system/process.js"

// Item-5: the workspace path slug must stay in sync with the C#
// MohistWorkspaceLayout.Slug helper. The server-side table is pinned in
// PathContractRegressionSpecs.Slug_MatchesRunnerAlgorithm; the matching
// JS table is asserted here so a future change to either side is caught
// by the appropriate runtime's tests.

describe("WorkspaceManager", () => {
  it("NewIssueReusingNumber_RecreatesStaleWorkspaceFromBaseBranch", async () => {
    const root = await mkdtemp(join(tmpdir(), "mohist-workspace-"))
    const repo = await createRepo(root, "repo")

    const runnerRoot = join(root, "runner")
    const manager = new WorkspaceManager(runnerRoot)
    const signal = new AbortController().signal

    const first = await manager.ensure(work("wr-old", "issue-old", repo), signal)
    await writeFile(join(first.path, "stale.txt"), "old issue data\n")

    const second = await manager.ensure(work("wr-new", "issue-new", repo), signal)

    expect(second.path).toBe(first.path)
    await expect(readFile(join(second.path, "stale.txt"), "utf8")).rejects.toThrow()
    const marker = JSON.parse(await readFile(join(second.path, ".mohist", "workspace.json"), "utf8"))
    expect(marker).toMatchObject({ issueId: "issue-new", issueNumber: 9, workflowRunId: "wr-new" })
  })

  it("NewIssueReusingNumber_RemovesStaleWorkspaceBeforeCreatingFreshWorkspace", async () => {
    const root = await mkdtemp(join(tmpdir(), "mohist-workspace-"))
    const repo = await createRepo(root, "repo")

    const runnerRoot = join(root, "runner")
    const manager = new WorkspaceManager(runnerRoot)
    const signal = new AbortController().signal

    const first = await manager.ensure(work("wr-old", "issue-old", repo), signal)
    await writeFile(join(first.path, ".mohist", "workspace.json"), JSON.stringify({
      issueId: "other-issue",
      issueNumber: 9,
      workflowRunId: "other-run",
    }, null, 2))

    const second = await manager.ensure(work("wr-new", "issue-new", repo), signal)

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

  it("WorkspacePreparation_ClonesCacheAndCreatesWorkspaceWhenServerSuppliesPath", async () => {
    // Item-4: when the server pre-computes workspace.path and includes it
    // in dispatch variables, the runner must still (a) clone the cache
    // from repository.gitUrl, (b) materialize a working clone on the
    // configured base branch, and (c) check out the per-run branch.
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

    // Cache is materialized from gitUrl, not from the supplied path.
    const cachePath = join(runnerRoot, "repos", "project-1", "master")
    expect(await isBareRepo(cachePath, signal)).toBe(true)

    // The supplied path becomes a real git working tree on the base branch,
    // not an empty directory.
    expect(await readFile(join(suppliedWorkspacePath, "README.md"), "utf8")).toBe("base\n")

    const head = await runCommand("git", ["-C", suppliedWorkspacePath, "rev-parse", "--abbrev-ref", "HEAD"], ".", signal)
    expect(head.exitCode).toBe(0)
    expect(head.stdout.trim()).toBe("mohist/run-wr-supplied")

    // origin points back at the supplied gitUrl, not the supplied path.
    const remote = await runCommand("git", ["-C", suppliedWorkspacePath, "remote", "get-url", "origin"], ".", signal)
    expect(remote.exitCode).toBe(0)
    expect(remote.stdout.trim()).toBe(repo)
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
