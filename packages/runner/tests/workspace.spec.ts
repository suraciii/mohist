import { mkdtemp, readFile, writeFile } from "node:fs/promises"
import { join } from "node:path"
import { tmpdir } from "node:os"
import { describe, expect, it } from "vitest"
import { WorkspaceManager } from "../src/runtime/workspace.js"
import { runCommand } from "../src/system/process.js"

describe("WorkspaceManager", () => {
  it("NewIssueReusingNumber_RecreatesStaleWorktreeFromBaseBranch", async () => {
    const root = await mkdtemp(join(tmpdir(), "mohist-workspace-"))
    const repo = join(root, "repo")
    await git(root, "init", repo)
    await git(repo, "config", "user.email", "test@example.com")
    await git(repo, "config", "user.name", "Test User")
    await writeFile(join(repo, "README.md"), "base\n")
    await git(repo, "add", ".")
    await git(repo, "commit", "-m", "base")

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

  it("NewIssueReusingNumber_RemovesStaleBranchWorktreeBeforeCreatingFreshWorktree", async () => {
    const root = await mkdtemp(join(tmpdir(), "mohist-workspace-"))
    const repo = join(root, "repo")
    await git(root, "init", repo)
    await git(repo, "config", "user.email", "test@example.com")
    await git(repo, "config", "user.name", "Test User")
    await writeFile(join(repo, "README.md"), "base\n")
    await git(repo, "add", ".")
    await git(repo, "commit", "-m", "base")

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
})

function work(workflowRunId: string, issueId: string, projectPath: string) {
  return {
    workflowRunId,
    workId: "proposal.1",
    workType: "task",
    uses: "mohist/acp-agent",
    variables: {
      mohist: { runId: workflowRunId },
      issue: { id: issueId, number: 9 },
      project: { id: "project-1", name: "Mohist Local", path: projectPath, baseBranch: "master" },
      openspecChangeDir: "openspec/changes/issue-9",
    },
  }
}

async function git(cwd: string, ...args: string[]) {
  const result = await runCommand("git", args, cwd, new AbortController().signal)
  if (result.exitCode !== 0) throw new Error(result.stderr || result.stdout)
  return result
}
