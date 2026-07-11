import { mkdir, writeFile } from "node:fs/promises"
import { join } from "node:path"
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { ActionRegistry } from "../../src/actions/registry.js"
import type { RenderedWorkItem } from "../../src/core/types.js"
import { WorkExecutor } from "../../src/runtime/executor.js"
import { setExecutorGitRunnerForTest } from "../../src/runtime/git-probe.js"
import { WorkspaceManager } from "../../src/runtime/workspace.js"
import { runCommand } from "../../src/system/process.js"
import type { ServerConnection } from "../../src/server/connection.js"
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

beforeEach(() => setExecutorGitRunnerForTest(null))
afterEach(() => setExecutorGitRunnerForTest(null))

describe("WorkExecutor workspace preparation across stages", () => {
  it("reuses the prepared workspace without recloning", async () => {
    const root = await createTestTempDir("mohist-executor-workspace-boundary-")
    const environment = await createGitEnvironment(root)
    isolateGitEnvironment(environment)
    const upstream = await createBareUpstream(root, environment)
    const runnerRoot = join(root, "runner")
    const manager = new WorkspaceManager(runnerRoot)
    const signal = new AbortController().signal
    const processModule = await import("../../src/system/process.js")
    const realRunCommand = processModule.runCommand
    const gitCalls: string[] = []
    const spy = vi.spyOn(processModule, "runCommand").mockImplementation(async (command, args, cwd, childSignal, env, options) => {
      gitCalls.push(`${command} ${args.join(" ")}`)
      return realRunCommand(command, args, cwd, childSignal, env, options)
    })

    const handlerCalls: string[] = []
    const registry = new ActionRegistry()
    registry.register("core/script", async (context) => {
      handlerCalls.push(`${context.workType}:${context.stage ?? ""}`)
      return { status: "success", message: "ok" }
    })
    const executor = new WorkExecutor(registry, manager, connection() as never, {} as never, null, runnerRoot)

    try {
      const plan = await executor.execute(buildWork(upstream, "workflow-cross-stage", "issue-cross-stage", "plan", "plan:write"), signal)
      expect(plan.status).toBe("completed")
      gitCalls.length = 0

      const build = await executor.execute(buildWork(upstream, "workflow-cross-stage", "issue-cross-stage", "build", "build:agent"), signal)
      const check = await executor.execute(buildWork(upstream, "workflow-cross-stage", "issue-cross-stage", "check", "check:verdict"), signal)
      const prepare = await executor.execute(buildWork(upstream, "workflow-cross-stage", "issue-cross-stage", "integrate", "integrate:prepare"), signal)

      expect(build.status).toBe("completed")
      expect(check.status).toBe("completed")
      expect(prepare.status).toBe("completed")
      expect(handlerCalls).toEqual(["task:plan", "task:build", "task:check", "task:integrate"])
      expect(gitCalls.filter((call) => call.startsWith("git clone "))).toEqual([])

      const workspacePath = join(runnerRoot, "mohist-local", "workspaces", "issue-9")
      const head = await realRunCommand("git", ["-C", workspacePath, "rev-parse", "--abbrev-ref", "HEAD"], ".", signal, environment)
      expect(head.stdout.trim()).toBe("mohist/run-workflow-cross-stage")
    } finally {
      spy.mockRestore()
    }
  })
})

function connection(): Pick<ServerConnection, "uploadArtifact" | "report"> {
  return {
    async report() {
      return {}
    },
    async uploadArtifact() {
      throw new Error("uploadArtifact should not be called in workspace boundary tests")
    },
  } as unknown as Pick<ServerConnection, "uploadArtifact" | "report">
}

function buildWork(repo: string, workflowRunId: string, issueId: string, stage: string, workId: string): RenderedWorkItem {
  return {
    workflowRunId,
    workId,
    workType: "task",
    stage,
    title: `${stage} task`,
    uses: "core/script",
    with: { run: "echo ok" },
    variables: {
      mohist: { runId: workflowRunId },
      issue: { id: issueId, number: 9 },
      project: { id: "project-1", name: "Mohist Local" },
      repository: { name: "master", gitUrl: repo, baseBranch: "master" },
      openspecChangeDir: `openspec/changes/${issueId}`,
    },
  }
}

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

function isolateGitEnvironment(environment: GitEnvironment) {
  for (const [key, value] of Object.entries(environment)) vi.stubEnv(key, value)
}

async function createBareUpstream(root: string, environment: GitEnvironment): Promise<string> {
  const upstream = join(root, "upstream.git")
  await mkdir(upstream, { recursive: true })
  await git(root, ["init", "--bare", "--initial-branch=master", upstream], environment)
  const seed = join(root, "seed")
  const hooks = join(root, "hooks")
  await mkdir(seed, { recursive: true })
  await git(seed, ["init", "--initial-branch=master"], environment)
  await git(seed, ["config", "user.email", environment.GIT_AUTHOR_EMAIL], environment)
  await git(seed, ["config", "user.name", environment.GIT_AUTHOR_NAME], environment)
  await git(seed, ["config", "core.hooksPath", hooks], environment)
  await writeFile(join(seed, "README.md"), "base\n")
  await git(seed, ["add", "README.md"], environment)
  await git(seed, ["commit", "-m", "base"], environment)
  await git(seed, ["remote", "add", "origin", upstream], environment)
  await git(seed, ["push", "-u", "origin", "master"], environment)
  return upstream
}

async function git(cwd: string, args: string[], environment: GitEnvironment) {
  const result = await runCommand("git", args, cwd, new AbortController().signal, environment)
  if (result.exitCode !== 0) throw new Error(result.stderr || result.stdout || `git ${args.join(" ")} failed`)
  return result
}
