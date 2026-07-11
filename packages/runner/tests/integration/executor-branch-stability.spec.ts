import { mkdir, writeFile } from "node:fs/promises"
import { join } from "node:path"
import { afterEach, describe, expect, it, vi } from "vitest"
import { ActionRegistry } from "../../src/actions/registry.js"
import type { RenderedWorkItem } from "../../src/core/types.js"
import { WorkExecutor } from "../../src/runtime/executor.js"
import { setExecutorGitRunnerForTest } from "../../src/runtime/git-probe.js"
import { runCommand } from "../../src/system/process.js"
import { clearedInheritedGitEnvironment } from "../support/git-environment.js"
import { createTestTempDir } from "../support/temp-dir.js"
import { verifyOnlyWorkspaceManager } from "../support/workspace-mock.js"

const RUN_BRANCH = "mohist/run-workflow-branch"

afterEach(() => {
  setExecutorGitRunnerForTest(null)
})

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

describe("WorkExecutor real Git branch boundary", () => {
  it("ReportsEndViolationBeforeCleanWorktreeCheck", async () => {
    const root = await createTestTempDir("mohist-branch-integration-")
    const environment = await createGitEnvironment(root)
    isolateGitEnvironment(environment)
    const workDir = join(root, "workspace")
    await initializeRepository(workDir, root, environment)
    setExecutorGitRunnerForTest(null)

    const registry = new ActionRegistry()
    registry.register("mohist/test-action", async (context) => {
      await writeFile(join(context.workDir, "leftover.txt"), "left behind\n")
      await git(context.workDir, ["checkout", "-b", "feature/after-action"], environment)
      return { status: "success" }
    })
    const executor = new WorkExecutor(
      registry,
      verifyOnlyWorkspaceManager({ path: workDir, branch: RUN_BRANCH, changeDir: null }),
      {} as never,
      {} as never,
      null,
      workDir,
    )

    const result = await executor.execute(work(workDir), new AbortController().signal)

    expect(result.status).toBe("failed")
    const output = JSON.parse(result.output ?? "{}")
    expect(output).toMatchObject({
      kind: "branch-invariant-violation",
      boundary: "end",
      expectedBranch: RUN_BRANCH,
      observedBranch: "feature/after-action",
    })
    expect(output.untracked).toBeUndefined()
  })
})

async function createGitEnvironment(root: string): Promise<GitEnvironment> {
  const home = join(root, "home")
  const xdg = join(root, "xdg")
  const hooks = join(root, "hooks")
  const globalConfig = join(root, "gitconfig")
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

function isolateGitEnvironment(environment: GitEnvironment) {
  for (const [key, value] of Object.entries(environment)) vi.stubEnv(key, value)
}

async function initializeRepository(workDir: string, root: string, environment: GitEnvironment) {
  await git(root, ["init", "--initial-branch=main", workDir], environment)
  await git(workDir, ["config", "user.name", environment.GIT_AUTHOR_NAME], environment)
  await git(workDir, ["config", "user.email", environment.GIT_AUTHOR_EMAIL], environment)
  await git(workDir, ["config", "core.hooksPath", join(root, "hooks")], environment)
  await writeFile(join(workDir, "README.md"), "base\n")
  await git(workDir, ["add", "README.md"], environment)
  await git(workDir, ["commit", "-m", "base"], environment)
  await git(workDir, ["checkout", "-b", RUN_BRANCH], environment)
}

async function git(cwd: string, args: string[], environment: GitEnvironment) {
  const result = await runCommand("git", args, cwd, new AbortController().signal, environment)
  if (result.exitCode !== 0) throw new Error(result.stderr || result.stdout || `git ${args.join(" ")} failed`)
  return result
}

function work(workDir: string): RenderedWorkItem {
  return {
    workflowRunId: "workflow-branch-integration",
    workId: "work-branch-integration",
    workType: "task",
    title: "Real Git branch boundary",
    uses: "mohist/test-action",
    with: {},
    variables: { workspace: { path: workDir, branch: RUN_BRANCH, changeDir: null } },
  }
}
