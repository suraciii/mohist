import { execFile } from "node:child_process"
import { mkdir, mkdtemp, rm, writeFile } from "node:fs/promises"
import { tmpdir } from "node:os"
import { dirname, join } from "node:path"
import { promisify } from "node:util"
import { afterEach, beforeEach, describe, expect, it } from "vitest"
import { WorkExecutor, setCleanupAgentActionForTest } from "../src/runtime/executor.js"
import { ActionRegistry } from "../src/actions/registry.js"
import { rebaseAction, setRebaseExistsCheckerForTest, setRebaseGitRunnerForTest } from "../src/actions/rebase.js"
import { pushAction, setPushGitRunnerForTest } from "../src/actions/push.js"
import { verifyOnlyWorkspaceManager } from "./support/workspace-mock.js"
import type { ActionContext, ActionResult, JsonObject, WorkItem } from "../src/core/types.js"
import type { ServerConnection } from "../src/server/connection.js"

const exec = promisify(execFile)

interface WorktreeFixture {
  workDir: string
  branch: string
  upstream: string
}

let worktree: WorktreeFixture
let connection: Pick<ServerConnection, "uploadArtifact" | "report">

beforeEach(async () => {
  worktree = await initWorktreeFixture()
  connection = {
    async report() {
      return {}
    },
    async uploadArtifact() {
      throw new Error("uploadArtifact should not be called in regression tests")
    },
  } as unknown as Pick<ServerConnection, "uploadArtifact" | "report">
})

afterEach(async () => {
  setCleanupAgentActionForTest(null)
  setRebaseGitRunnerForTest(null)
  setRebaseExistsCheckerForTest(null)
  setPushGitRunnerForTest(null)
  await rm(worktree.workDir, { recursive: true, force: true })
  await rm(worktree.upstream, { recursive: true, force: true })
})

async function initWorktreeFixture(): Promise<WorktreeFixture> {
  const root = await mkdtemp(join(tmpdir(), "mohist-issue112-regression-"))
  const upstream = join(root, "upstream.git")
  const workDir = join(root, "worktree")
  await mkdir(upstream, { recursive: true })
  await exec("git", ["init", "--bare", "--initial-branch=master", upstream])

  await mkdir(workDir, { recursive: true })
  await exec("git", ["init", "-q", "--initial-branch=master"], { cwd: workDir })
  await exec("git", ["config", "user.email", "test@example.com"], { cwd: workDir })
  await exec("git", ["config", "user.name", "Mohist Test"], { cwd: workDir })
  await exec("git", ["config", "commit.gpgsign", "false"], { cwd: workDir })
  await exec("git", ["remote", "add", "origin", upstream], { cwd: workDir })
  await writeFile(join(workDir, "README.md"), "init\n", "utf8")
  await exec("git", ["add", "README.md"], { cwd: workDir })
  await exec("git", ["commit", "-m", "init", "-q"], { cwd: workDir })
  await exec("git", ["push", "-u", "origin", "master", "-q"], { cwd: workDir })
  await exec("git", ["checkout", "-q", "-b", "mo/issue-112"], { cwd: workDir })
  return { workDir, branch: "mo/issue-112", upstream }
}

async function dirtyFile(relativePath: string, content: string): Promise<void> {
  const full = join(worktree.workDir, relativePath)
  await mkdir(dirname(full), { recursive: true })
  await writeFile(full, content, "utf8")
}

function buildRegistry(handlers: Record<string, (ctx: ActionContext) => Promise<ActionResult>>): ActionRegistry {
  const registry = new ActionRegistry()
  for (const [uses, handler] of Object.entries(handlers)) {
    registry.register(uses, async (ctx) => handler(ctx))
  }
  return registry
}

function buildExecutor(registry: ActionRegistry): WorkExecutor {
  return new WorkExecutor(
    registry,
    verifyOnlyWorkspaceManager({ path: worktree.workDir, branch: worktree.branch, changeDir: null }),
    connection as never,
    {} as never,
    null,
    worktree.workDir,
  )
}

function buildWork(overrides: Partial<WorkItem> = {}): WorkItem {
  return {
    workflowRunId: "wf-112",
    workId: "build:agent.1",
    workType: "task",
    title: "Agent-backed task",
    uses: "mohist/acp-agent",
    with: { prompt: "do the work" },
    variables: {
      workspace: { path: worktree.workDir, branch: worktree.branch, changeDir: null },
      project: { path: worktree.workDir },
      issue: { title: "Issue #102 regression", number: 112 },
    },
    ...overrides,
  }
}

function rebaseContext(overrides: JsonObject = {}, variables: JsonObject = {}): ActionContext {
  return {
    workflowRunId: "wf-112",
    workId: "integrate:rebase.1",
    workType: "task",
    stage: "integrate",
    title: "Rebase and squash branch",
    uses: "mohist/rebase",
    with: {
      baseBranch: "master",
      remote: "origin",
      squash: true,
      message: "Complete issue #112",
      ...overrides,
    },
    variables: {
      project: { path: worktree.workDir },
      workspace: { path: worktree.workDir, branch: worktree.branch, changeDir: null },
      issue: { title: "Issue #102 regression", number: 112 },
      ...variables,
    },
    workDir: worktree.workDir,
    signal: new AbortController().signal,
    writeVars: async () => {},
  }
}

function pushContext(overrides: JsonObject = {}, variables: JsonObject = {}): ActionContext {
  return {
    workflowRunId: "wf-112",
    workId: "integrate:push.1",
    workType: "task",
    stage: "integrate",
    title: "Push changes",
    uses: "mohist/push",
    with: { source: worktree.branch, target: "master", remote: "origin", ...overrides },
    variables: {
      project: { path: "/not/the/workspace" },
      repository: { baseBranch: "master" },
      workspace: { path: worktree.workDir, branch: worktree.branch, changeDir: null },
      issue: { title: "Issue #102 regression", number: 112 },
      ...variables,
    },
    workDir: worktree.workDir,
    signal: new AbortController().signal,
    writeVars: async () => {},
  }
}

function gitOk(stdout: string) {
  return { success: true, stdout, stderr: "", exitCode: 0, combinedOutput: stdout.trim() }
}

function gitFail(stderr: string, exitCode = 1) {
  return { success: false, stdout: "", stderr, exitCode, combinedOutput: stderr }
}

function installRebaseMockGit(calls: string[]) {
  setRebaseGitRunnerForTest(async (_dir, args) => {
    const cmd = args.join(" ")
    calls.push(cmd)
    switch (cmd) {
      case "rev-parse --git-path rebase-merge":
        return gitOk("/fake/worktree/.git/rebase-merge\n")
      case "rev-parse --git-path rebase-apply":
        return gitOk("/fake/worktree/.git/rebase-apply\n")
      case "fetch origin master":
        return gitOk("From origin\n * branch            master     -> FETCH_HEAD")
      case "rev-parse origin/master":
        return gitOk("base-sha\n")
      case "status --porcelain":
        return gitOk("")
      case "rev-parse HEAD": {
        const count = calls.filter((call) => call === "rev-parse HEAD").length
        if (count === 1) return gitOk("before-sha\n")
        if (count === 2) return gitOk("rebased-sha\n")
        return gitOk("squashed-sha\n")
      }
      case "rebase origin/master":
        return gitOk("Successfully rebased and updated refs/heads/mo/issue-112.")
      case "reset --soft base-sha":
        return gitOk("")
      case "commit -m Complete issue #112":
        return gitOk("[mo/issue-112 squashed-sha] Complete issue #112")
      default:
        return gitFail(`unexpected git call: ${cmd}`, 1)
    }
  })
  setRebaseExistsCheckerForTest(() => false)
}

describe("Issue #112 regression — agent leftovers are cleaned before rebase+push delivery", () => {
  it("AgentTaskLeavesChanges_CleanupAgentCommits_RebaseAndPushUseCleanWorkspace", async () => {
    await dirtyFile("src/agent-output.ts", "export const v = 112\n")

    const cleanupPrompts: string[] = []
    setCleanupAgentActionForTest(async (ctx) => {
      const prompt = String(ctx.with?.prompt ?? "")
      cleanupPrompts.push(prompt)
      expect(ctx.workId).toBe("build:agent.1")
      expect(prompt).toMatch(/do NOT start any new task work/i)
      expect(prompt).toMatch(/do NOT push to any remote/i)
      expect(prompt).toContain("src/agent-output.ts")
      await exec("git", ["add", "src/agent-output.ts"], { cwd: ctx.workDir })
      await exec("git", ["commit", "-m", "cleanup: commit agent output", "-q"], { cwd: ctx.workDir })
      return { status: "success", message: "committed leftover", output: JSON.stringify({ commitSha: "cleanup-sha" }) }
    })

    const registry = buildRegistry({
      "mohist/acp-agent": async () => ({ status: "success", message: "agent finished" }),
      "mohist/rebase": rebaseAction,
      "mohist/push": pushAction,
    })
    const executor = buildExecutor(registry)

    const agentResult = await executor.execute(buildWork(), new AbortController().signal)
    expect(agentResult.status).toBe("completed")
    expect(agentResult.cleanupAttempts).toBe(1)
    expect(cleanupPrompts).toHaveLength(1)

    const statusAfterCleanup = await exec("git", ["status", "--porcelain"], { cwd: worktree.workDir })
    expect(statusAfterCleanup.stdout).toBe("")

    const rebaseCalls: string[] = []
    installRebaseMockGit(rebaseCalls)
    const rebaseResult = await rebaseAction(rebaseContext())
    const rebaseOutput = JSON.parse(rebaseResult.output ?? "{}")

    expect(rebaseResult.status).toBe("success")
    expect(rebaseCalls).toEqual([
      "rev-parse --git-path rebase-merge",
      "rev-parse --git-path rebase-apply",
      "fetch origin master",
      "rev-parse origin/master",
      "status --porcelain",
      "rev-parse HEAD",
      "rebase origin/master",
      "rev-parse HEAD",
      "reset --soft base-sha",
      "commit -m Complete issue #112",
      "rev-parse HEAD",
    ])
    expect(rebaseOutput).toMatchObject({
      kind: "rebase",
      status: "completed",
      baseBranch: "master",
      remote: "origin",
      squashed: true,
      squashedHeadSha: "squashed-sha",
      errorCode: null,
    })

    const pushCalls: { workDir: string; command: string }[] = []
    setPushGitRunnerForTest(async (workDir, args) => {
      const command = args.join(" ")
      pushCalls.push({ workDir, command })
      switch (command) {
        case "rev-parse mo/issue-112":
          return gitOk("squashed-sha\n")
        case "push origin mo/issue-112:master":
          return gitOk("To origin\n   base-sha..squashed-sha  mo/issue-112 -> master")
        default:
          return gitFail(`unexpected git call: ${command}`, 1)
      }
    })

    const pushResult = await pushAction(pushContext())
    const pushOutput = JSON.parse(pushResult.output ?? "{}")
    expect(pushResult.status).toBe("success")
    expect(pushOutput).toMatchObject({
      kind: "push",
      status: "completed",
      source: "mo/issue-112",
      target: "master",
      landedCommit: "squashed-sha",
      pushed: true,
      failureKind: null,
      workDir: worktree.workDir,
    })
    expect(pushCalls).toEqual([
      { workDir: worktree.workDir, command: "rev-parse mo/issue-112" },
      { workDir: worktree.workDir, command: "push origin mo/issue-112:master" },
    ])
  })

  it("AgentTaskLeavesChanges_CleanupExhausts_TaskFailsBeforeDelivery", async () => {
    await dirtyFile("src/never-clean.ts", "export const v = 1\n")

    let attempt = 0
    setCleanupAgentActionForTest(async (ctx) => {
      attempt += 1
      const prompt = String(ctx.with?.prompt ?? "")
      expect(prompt).toContain(`attempt ${attempt}`)
      return { status: "success", message: "noop" }
    })

    const registry = buildRegistry({
      "mohist/acp-agent": async () => ({ status: "success", message: "first run" }),
      "mohist/rebase": rebaseAction,
      "mohist/push": pushAction,
    })
    const executor = buildExecutor(registry)

    const agentResult = await executor.execute(
      buildWork({
        variables: {
          workspace: { path: worktree.workDir, branch: worktree.branch, changeDir: null },
          project: { path: worktree.workDir },
          issue: { title: "Issue #102 regression", number: 112 },
          runner: { cleanup: { maxAttempts: 3 } },
        },
      }),
      new AbortController().signal,
    )

    expect(agentResult.status).toBe("failed")
    expect(agentResult.cleanupAttempts).toBe(3)
    expect(attempt).toBe(3)
    expect(agentResult.message).toMatch(/worktree dirty after 3 cleanup attempt/i)
    expect(agentResult.message).toMatch(/untracked=\[src\/never-clean\.ts\]/)
    const evidence = JSON.parse(agentResult.output ?? "{}")
    expect(evidence).toMatchObject({
      kind: "dirty-worktree",
      staged: [],
      unstaged: [],
      untracked: ["src/never-clean.ts"],
      cleanupAttempts: 3,
    })
  })
})
