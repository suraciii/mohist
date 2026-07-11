import { mkdir, readFile, writeFile } from "node:fs/promises"
import { join } from "node:path"
import { describe, expect, it, vi } from "vitest"
import { WorkspaceManager } from "../../src/runtime/workspace.js"
import { exists, runCommand } from "../../src/system/process.js"
import { clearedInheritedGitEnvironment } from "../support/git-environment.js"
import { createTestTempDir } from "../support/temp-dir.js"

interface GitEnvironment extends NodeJS.ProcessEnv {
  HOME: string
  XDG_CONFIG_HOME: string
  GIT_CONFIG_GLOBAL: string
  GIT_CONFIG_COUNT: "0"
  GIT_CONFIG_NOSYSTEM: "1"
  GIT_TERMINAL_PROMPT: "0"
  GIT_AUTHOR_NAME: string
  GIT_AUTHOR_EMAIL: string
  GIT_COMMITTER_NAME: string
  GIT_COMMITTER_EMAIL: string
}

describe("WorkspaceManager real Git smoke", () => {
  it("CloneAndBranch_CreatesRunBranchFromLocalBase", async () => {
    const root = await createTestTempDir("mohist-workspace-integration-")
    const environment = await createGitEnvironment(root)
    isolateGitEnvironment(environment)
    const source = await createSourceRepository(root, environment)
    const manager = new WorkspaceManager(join(root, "runner"))

    const workspace = await manager.prepare(work("wr-clone", "issue-clone", source), new AbortController().signal)

    expect(await readFile(join(workspace.path, "README.md"), "utf8")).toBe("base\n")
    expect(workspace.branch).toBe("mohist/run-wr-clone")
    expect((await git(workspace.path, ["rev-parse", "--abbrev-ref", "HEAD"], environment)).stdout.trim()).toBe("mohist/run-wr-clone")
    expect((await git(workspace.path, ["remote", "get-url", "origin"], environment)).stdout.trim()).toBe(source)
  })

  it("RebaseRecovery_RestoresRunBranchAfterConflict", async () => {
    const root = await createTestTempDir("mohist-workspace-integration-")
    const environment = await createGitEnvironment(root)
    isolateGitEnvironment(environment)
    const source = await createSourceRepository(root, environment)
    const manager = new WorkspaceManager(join(root, "runner"))
    const item = work("wr-rebase", "issue-rebase", source)
    const workspace = await manager.prepare(item, new AbortController().signal)
    const runBranch = "mohist/run-wr-rebase"
    await git(workspace.path, ["config", "core.hooksPath", join(root, "hooks")], environment)

    await writeFile(join(workspace.path, "agent.txt"), "agent work\n")
    await git(workspace.path, ["add", "."], environment)
    await git(workspace.path, ["commit", "-m", "agent work"], environment)
    const runRef = (await git(workspace.path, ["rev-parse", `refs/heads/${runBranch}`], environment)).stdout.trim()

    await git(workspace.path, ["checkout", "main"], environment)
    await writeFile(join(workspace.path, "agent.txt"), "base conflict\n")
    await git(workspace.path, ["add", "."], environment)
    await git(workspace.path, ["commit", "-m", "base conflict"], environment)
    await git(workspace.path, ["checkout", runBranch], environment)
    expect((await git(workspace.path, ["rebase", "main"], environment, true)).exitCode).not.toBe(0)
    expect(hasRebaseState(workspace.path)).toBe(true)

    const recovered = await manager.prepare(item, new AbortController().signal)

    expect(recovered).toMatchObject({ path: workspace.path, branch: runBranch })
    expect(hasRebaseState(workspace.path)).toBe(false)
    expect(await readFile(join(workspace.path, "agent.txt"), "utf8")).toBe("agent work\n")
    expect((await git(workspace.path, ["status", "--porcelain"], environment)).stdout).toBe("")
    expect((await git(workspace.path, ["rev-parse", `refs/heads/${runBranch}`], environment)).stdout.trim()).toBe(runRef)
  })
})

async function createGitEnvironment(root: string): Promise<GitEnvironment> {
  const home = join(root, "home")
  const xdg = join(root, "xdg")
  const globalConfig = join(root, "gitconfig")
  const hooks = join(root, "hooks")
  await Promise.all([mkdir(home, { recursive: true }), mkdir(xdg, { recursive: true }), mkdir(hooks, { recursive: true })])
  await writeFile(globalConfig, "")
  return {
    ...clearedInheritedGitEnvironment,
    HOME: home,
    XDG_CONFIG_HOME: xdg,
    GIT_CONFIG_GLOBAL: globalConfig,
    GIT_CONFIG_COUNT: "0",
    GIT_CONFIG_NOSYSTEM: "1",
    GIT_TERMINAL_PROMPT: "0",
    GIT_AUTHOR_NAME: "Mohist Integration Test",
    GIT_AUTHOR_EMAIL: "mohist-integration@example.test",
    GIT_COMMITTER_NAME: "Mohist Integration Test",
    GIT_COMMITTER_EMAIL: "mohist-integration@example.test",
  }
}

async function createSourceRepository(root: string, environment: GitEnvironment) {
  const source = join(root, "source")
  const hooks = join(root, "hooks")
  await git(root, ["init", "--initial-branch=main", source], environment)
  await git(source, ["config", "user.name", environment.GIT_AUTHOR_NAME], environment)
  await git(source, ["config", "user.email", environment.GIT_AUTHOR_EMAIL], environment)
  await git(source, ["config", "core.hooksPath", hooks], environment)
  await writeFile(join(source, "README.md"), "base\n")
  await git(source, ["add", "."], environment)
  await git(source, ["commit", "-m", "base"], environment)
  return source
}

async function git(cwd: string, args: string[], environment: GitEnvironment, allowFailure = false) {
  const result = await runCommand("git", args, cwd, new AbortController().signal, environment)
  if (result.exitCode !== 0 && !allowFailure) throw new Error(result.stderr || result.stdout || `git ${args.join(" ")} failed`)
  return result
}

function isolateGitEnvironment(environment: GitEnvironment) {
  for (const [key, value] of Object.entries(environment)) vi.stubEnv(key, value)
}

function hasRebaseState(workspacePath: string) {
  return exists(join(workspacePath, ".git", "rebase-merge")) || exists(join(workspacePath, ".git", "rebase-apply"))
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
      repository: { name: "main", gitUrl, baseBranch: "main" },
      openspecChangeDir: "openspec/changes/issue-9",
    },
  }
}
