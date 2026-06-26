import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises"
import { join } from "node:path"
import { tmpdir } from "node:os"
import { describe, expect, it, vi } from "vitest"
import { WorkspaceManager, slugify } from "../src/runtime/workspace.js"
import { exists, runCommand } from "../src/system/process.js"

// The workspace is a clone of the repo checked out on a per-run branch.
// `prepare()` is the two-step contract: (1) have a clone at the workspace
// path, (2) be on the run branch. The run branch is the identity — its
// presence means "this run is set up here", so re-entry is cheap and a
// new run at a reused path is a pristine re-clone.

describe("WorkspaceManager.prepare", () => {
  it("FreshRun_ClonesAndCreatesRunBranchFromBase", async () => {
    const root = await mkdtemp(join(tmpdir(), "mohist-workspace-"))
    const repo = await createRepo(root, "repo")
    const runnerRoot = join(root, "runner")
    const manager = new WorkspaceManager(runnerRoot)
    const signal = new AbortController().signal

    const workspace = await manager.prepare(work("wr-1", "issue-1", repo), signal)

    expect(workspace.path).toBe(join(runnerRoot, "mohist-local", "workspaces", "issue-9"))
    expect(workspace.branch).toBe("mohist/run-wr-1")
    expect(await readFile(join(workspace.path, "README.md"), "utf8")).toBe("base\n")
    const head = await runCommand("git", ["-C", workspace.path, "rev-parse", "--abbrev-ref", "HEAD"], ".", signal)
    expect(head.stdout.trim()).toBe("mohist/run-wr-1")
    // The clone's origin points at the real source (not a cache).
    const remote = await runCommand("git", ["-C", workspace.path, "remote", "get-url", "origin"], ".", signal)
    expect(remote.stdout.trim()).toBe(repo)
  })

  it("SameRunReentry_ReusesWorkspaceWithoutRecloning", async () => {
    const root = await mkdtemp(join(tmpdir(), "mohist-workspace-"))
    const repo = await createRepo(root, "repo")
    const runnerRoot = join(root, "runner")
    const manager = new WorkspaceManager(runnerRoot)
    const signal = new AbortController().signal

    const first = await manager.prepare(work("wr-1", "issue-1", repo), signal)
    await writeFile(join(first.path, "draft.txt"), "draft\n")

    const processMod = await import("../src/system/process.js")
    const realRun = processMod.runCommand
    const gitCalls: string[] = []
    const spy = vi.spyOn(processMod, "runCommand").mockImplementation(async (cmd, args, cwd, sig) => {
      gitCalls.push(`${cmd} ${args.join(" ")}`)
      return await realRun(cmd, args, cwd, sig)
    })
    try {
      const second = await manager.prepare(work("wr-1", "issue-1", repo), signal)
      expect(second.path).toBe(first.path)
      expect(await readFile(join(second.path, "draft.txt"), "utf8")).toBe("draft\n")
    } finally {
      spy.mockRestore()
    }

    // Re-entry must not re-clone.
    expect(gitCalls.filter((c) => c.startsWith("git clone"))).toHaveLength(0)
  })

  it("RestartWithNewRun_ReclonesFreshWorkspaceDroppingStaleFiles", async () => {
    const root = await mkdtemp(join(tmpdir(), "mohist-workspace-"))
    const repo = await createRepo(root, "repo")
    const runnerRoot = join(root, "runner")
    const manager = new WorkspaceManager(runnerRoot)
    const signal = new AbortController().signal

    const first = await manager.prepare(work("wr-old", "issue-same", repo), signal)
    await writeFile(join(first.path, "stale.txt"), "old run data\n")

    const second = await manager.prepare(work("wr-new", "issue-same", repo), signal)

    expect(second.path).toBe(first.path)
    expect(exists(join(second.path, "stale.txt"))).toBe(false)
    expect(second.branch).toBe("mohist/run-wr-new")
  })

  it("MissingBaseBranch_FailsWithoutCreatingWorkspace", async () => {
    const root = await mkdtemp(join(tmpdir(), "mohist-workspace-"))
    const repo = await createRepo(root, "repo")
    const runnerRoot = join(root, "runner")
    const manager = new WorkspaceManager(runnerRoot)
    const signal = new AbortController().signal

    const item = work("wr-1", "issue-1", repo)
    item.variables.repository = { name: "master", gitUrl: repo, baseBranch: "does-not-exist" }

    await expect(manager.prepare(item, signal)).rejects.toThrow(/cannot be resolved/)
    expect(exists(join(runnerRoot, "mohist-local", "workspaces"))).toBe(false)
  })

  it("CloneFailure_IsFatal", async () => {
    const root = await mkdtemp(join(tmpdir(), "mohist-workspace-"))
    const runnerRoot = join(root, "runner")
    const manager = new WorkspaceManager(runnerRoot)
    const signal = new AbortController().signal

    const badUrl = "file:///this-path-does-not-exist/this-host-has-no-git-server.git"
    await expect(manager.prepare(work("wr-first", "issue-first", badUrl), signal)).rejects.toThrow(/git clone failed/)
    expect(exists(join(runnerRoot, "mohist-local", "workspaces", "issue-9"))).toBe(false)
  })

  it("Preparation_DoesNotUseGitWorktreeCommands", async () => {
    const root = await mkdtemp(join(tmpdir(), "mohist-workspace-"))
    const repo = await createRepo(root, "repo")
    const runnerRoot = join(root, "runner")
    const manager = new WorkspaceManager(runnerRoot)
    const signal = new AbortController().signal

    const spy = vi.spyOn(await import("../src/system/process.js"), "runCommand")
    try {
      await manager.prepare(work("wr-1", "issue-1", repo), signal)
    } finally {
      spy.mockRestore()
    }

    const worktreeCalls = spy.mock.calls.filter((call) => call[0] === "git" && call[1].some((a) => a === "worktree"))
    expect(worktreeCalls).toHaveLength(0)
  })

  it("ServerSuppliedPath_IsHonored", async () => {
    const root = await mkdtemp(join(tmpdir(), "mohist-workspace-"))
    const repo = await createRepo(root, "repo")
    const runnerRoot = join(root, "runner")
    const manager = new WorkspaceManager(runnerRoot)
    const signal = new AbortController().signal

    const suppliedWorkspacePath = join(runnerRoot, "supplied", "workspaces", "issue-9")
    const item = work("wr-supplied", "issue-1", repo)
    ;(item.variables as Record<string, unknown>).workspace = { path: suppliedWorkspacePath }

    const result = await manager.prepare(item, signal)
    expect(result.path).toBe(suppliedWorkspacePath)
    expect(result.branch).toBe("mohist/run-wr-supplied")
    expect(await readFile(join(suppliedWorkspacePath, "README.md"), "utf8")).toBe("base\n")
    const head = await runCommand("git", ["-C", suppliedWorkspacePath, "rev-parse", "--abbrev-ref", "HEAD"], ".", signal)
    expect(head.stdout.trim()).toBe("mohist/run-wr-supplied")
  })
})

// Crash recovery: a rebase/merge that crashed mid-flight leaves the work
// tree unusable. `prepare()` re-entry must abort the op and realign the
// tree to the run branch ref — which a failed rebase never moved — so the
// run's committed work survives.
describe("WorkspaceManager.prepare crash recovery", () => {
  it("MidRebaseCrash_ReentryRecoversAndPreservesRunBranch", async () => {
    const root = await mkdtemp(join(tmpdir(), "mohist-recovery-"))
    const repo = await createRepo(root, "repo")
    const runnerRoot = join(root, "runner")
    const manager = new WorkspaceManager(runnerRoot)
    const signal = new AbortController().signal

    const item = work("wr-crash", "issue-crash", repo)
    const workspace = await manager.prepare(item, signal)
    const runBranch = "mohist/run-wr-crash"

    // Agent commits work to the run branch — this must survive.
    await writeFile(join(workspace.path, "agent.txt"), "agent work\n")
    await git(workspace.path, "add", ".")
    await git(workspace.path, "commit", "-m", "agent commit on run branch")
    const runShaBefore = (await runCommand("git", ["-C", workspace.path, "rev-parse", `refs/heads/${runBranch}`], ".", signal)).stdout.trim()

    // Advance master with a conflicting change, then start a rebase that
    // conflicts and leave it mid-flight (as a crashed runner would).
    await git(workspace.path, "checkout", "master")
    await writeFile(join(workspace.path, "agent.txt"), "conflicting master change\n")
    await git(workspace.path, "add", ".")
    await git(workspace.path, "commit", "-m", "conflicting master commit")
    await git(workspace.path, "checkout", runBranch)
    const rebase = await runCommand("git", ["-C", workspace.path, "rebase", "master"], workspace.path, new AbortController().signal)
    expect(rebase.exitCode).not.toBe(0)
    expect(exists(join(workspace.path, ".git", "rebase-merge"))).toBe(true)

    // Re-enter: prepare() must heal the workspace.
    const recovered = await manager.prepare(item, signal)
    expect(recovered.path).toBe(workspace.path)
    expect(recovered.branch).toBe(runBranch)
    expect(exists(join(workspace.path, ".git", "rebase-merge"))).toBe(false)
    expect(await readFile(join(workspace.path, "agent.txt"), "utf8")).toBe("agent work\n")
    const status = (await runCommand("git", ["-C", workspace.path, "status", "--porcelain"], ".", signal)).stdout.trim()
    expect(status).toBe("")

    // The run branch ref is preserved — the recovery realigned the tree
    // without moving the ref.
    const runShaAfter = (await runCommand("git", ["-C", workspace.path, "rev-parse", `refs/heads/${runBranch}`], ".", signal)).stdout.trim()
    expect(runShaAfter).toBe(runShaBefore)
  })

  it("CleanReentry_IsNoOp", async () => {
    const root = await mkdtemp(join(tmpdir(), "mohist-recovery-clean-"))
    const repo = await createRepo(root, "repo")
    const runnerRoot = join(root, "runner")
    const manager = new WorkspaceManager(runnerRoot)
    const signal = new AbortController().signal

    const item = work("wr-clean", "issue-clean", repo)
    const workspace = await manager.prepare(item, signal)
    const runBranch = "mohist/run-wr-clean"
    const runShaBefore = (await runCommand("git", ["-C", workspace.path, "rev-parse", `refs/heads/${runBranch}`], ".", signal)).stdout.trim()

    const second = await manager.prepare(item, signal)
    expect(second.path).toBe(workspace.path)
    expect(second.branch).toBe(runBranch)

    const runShaAfter = (await runCommand("git", ["-C", workspace.path, "rev-parse", `refs/heads/${runBranch}`], ".", signal)).stdout.trim()
    expect(runShaAfter).toBe(runShaBefore)
    const status = (await runCommand("git", ["-C", workspace.path, "status", "--porcelain"], ".", signal)).stdout.trim()
    expect(status).toBe("")
  })
})

describe("WorkspaceManager.slugify", () => {
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
  const result = await runCommand("git", args, cwd, new AbortController().signal, {
    GIT_AUTHOR_NAME: "Mohist Test",
    GIT_AUTHOR_EMAIL: "mohist-test@example.com",
    GIT_COMMITTER_NAME: "Mohist Test",
    GIT_COMMITTER_EMAIL: "mohist-test@example.com",
  })
  if (result.exitCode !== 0) throw new Error(result.stderr || result.stdout)
  return result
}
