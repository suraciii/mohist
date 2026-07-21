import { afterEach, beforeEach, describe, expect, it } from "vitest"
import { WorkExecutor } from "../src/runtime/executor.js"
import { setCleanupAgentActionForTest } from "../src/runtime/worktree-enforcement.js"
import { setExecutorGitRunnerForTest } from "../src/runtime/git-probe.js"
import { rebaseAction, setRebaseExistsCheckerForTest, setRebaseGitRunnerForTest } from "../src/actions/rebase.js"
import { pushAction, setPushGitRunnerForTest } from "../src/actions/push.js"
import { verifyOnlyWorkspaceManager } from "./support/workspace-mock.js"
import type { ActionContext, ActionResult, JsonObject, RenderedWorkItem } from "../src/core/types.js"
import type { ServerConnection } from "../src/server/connection.js"
import { defineTestActions, type ActionRegistry, type TestActionDefinition } from "./support/action-registry-test.js"

interface FakeWorktree {
  workDir: string
  branch: string
  staged: string[]
  unstaged: string[]
  untracked: string[]
  cleanupCommits: { files: string[]; sha: string }[]
}

let worktree: FakeWorktree
let connection: Pick<ServerConnection, "uploadArtifact" | "report">

beforeEach(() => {
  worktree = createFakeWorktree()
  installExecutorGit(worktree)
  connection = {
    async report() {
      return {}
    },
    async uploadArtifact() {
      throw new Error("uploadArtifact should not be called in cleanup delivery tests")
    },
  } as unknown as Pick<ServerConnection, "uploadArtifact" | "report">
})

afterEach(() => {
  setCleanupAgentActionForTest(null)
  setExecutorGitRunnerForTest(null)
  setRebaseGitRunnerForTest(null)
  setRebaseExistsCheckerForTest(null)
  setPushGitRunnerForTest(null)
})

function createFakeWorktree(): FakeWorktree {
  return {
    workDir: process.cwd(),
    branch: "mo/worktree-cleanup",
    staged: [],
    unstaged: [],
    untracked: [],
    cleanupCommits: [],
  }
}

function installExecutorGit(state: FakeWorktree) {
  setExecutorGitRunnerForTest(async (workDir, args) => {
    expect(workDir).toBe(state.workDir)
    switch (args.join(" ")) {
      case "rev-parse --abbrev-ref HEAD":
        return gitOk(`${state.branch}\n`)
      case "rev-parse --is-inside-work-tree":
        return gitOk("true\n")
      case "diff --cached --name-only":
        return gitOk(fileList(state.staged))
      case "diff --name-only":
        return gitOk(fileList(state.unstaged))
      case "ls-files --others --exclude-standard":
        return gitOk(fileList(state.untracked))
      case "rev-parse --git-path index.lock":
        return gitOk("/fake/worktree/.git/index.lock\n")
      default:
        throw new Error(`unexpected executor git call: ${args.join(" ")}`)
    }
  })
}

function fileList(files: string[]) {
  return files.length === 0 ? "" : `${files.join("\n")}\n`
}

function commitCleanup(state: FakeWorktree, files: string[], sha: string) {
  expect(state.untracked).toEqual(files)
  state.staged = []
  state.unstaged = []
  state.untracked = []
  state.cleanupCommits.push({ files, sha })
}

function buildRegistry(handlers: Record<string, TestActionDefinition | ((ctx: ActionContext) => Promise<ActionResult>)>): ActionRegistry {
  return defineTestActions(handlers)
}

function buildExecutor(registry: ActionRegistry): WorkExecutor {
  return new WorkExecutor(
    registry,
    verifyOnlyWorkspaceManager({ path: worktree.workDir, branch: worktree.branch, changeDir: null }),
    connection as never,
    worktree.workDir,
  )
}

function buildWork(overrides: Partial<RenderedWorkItem> = {}): RenderedWorkItem {
  return {
    workflowRunId: "wf-worktree-cleanup",
    workId: "build:agent.1",
    workType: "task",
    title: "Agent-backed task",
    uses: "mohist/opencode",
    with: { prompt: "do the work" },
    variables: {
      workspace: { path: worktree.workDir, branch: worktree.branch, changeDir: null },
      project: { path: worktree.workDir },
      issue: { title: "Worktree cleanup delivery", number: 42 },
    },
    ...overrides,
  }
}

function rebaseContext(overrides: JsonObject = {}, variables: JsonObject = {}): ActionContext {
  return {
    workflowRunId: "wf-worktree-cleanup",
    workId: "integrate:rebase.1",
    workType: "task",
    stage: "integrate",
    title: "Rebase and squash branch",
    uses: "mohist/rebase",
    with: {
      baseBranch: "master",
      remote: "origin",
      squash: true,
      message: "Complete worktree cleanup",
      ...overrides,
    },
    variables: {
      project: { path: worktree.workDir },
      workspace: { path: worktree.workDir, branch: worktree.branch, changeDir: null },
      issue: { title: "Worktree cleanup delivery", number: 42 },
      ...variables,
    },
    workDir: worktree.workDir,
    signal: new AbortController().signal,
    writeVars: async () => {},
  }
}

function pushContext(overrides: JsonObject = {}, variables: JsonObject = {}): ActionContext {
  return {
    workflowRunId: "wf-worktree-cleanup",
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
      issue: { title: "Worktree cleanup delivery", number: 42 },
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
        return gitOk("Successfully rebased and updated refs/heads/mo/worktree-cleanup.")
      case "reset --soft base-sha":
        return gitOk("")
      case "commit -m Complete worktree cleanup":
        return gitOk("[mo/worktree-cleanup squashed-sha] Complete worktree cleanup")
      default:
        return gitFail(`unexpected git call: ${cmd}`, 1)
    }
  })
  setRebaseExistsCheckerForTest(() => false)
}

describe("worktree cleanup before delivery", () => {
  it("commits agent leftovers before rebase and push", async () => {
    const cleanupPrompts: string[] = []
    setCleanupAgentActionForTest(async (ctx) => {
      const prompt = String(ctx.with?.prompt ?? "")
      cleanupPrompts.push(prompt)
      expect(ctx.workId).toBe("build:agent.1")
      expect(prompt).toMatch(/do NOT start any new task work/i)
      expect(prompt).toMatch(/do NOT push to any remote/i)
      expect(prompt).toContain("src/agent-output.ts")
      commitCleanup(worktree, ["src/agent-output.ts"], "cleanup-sha")
      return { output: { commitSha: "cleanup-sha" } }
    })

    const registry = buildRegistry({
      "mohist/opencode": {
        run: async () => {
          worktree.untracked = ["src/agent-output.ts"]
          return { output: null }
        },
        inputs: { prompt: { types: ["string", "object"] } },
      },
      "mohist/rebase": rebaseAction,
      "mohist/push": pushAction,
    })
    const executor = buildExecutor(registry)

    const agentResult = await executor.execute(buildWork(), new AbortController().signal)
    expect(agentResult.status).toBe("completed")
    expect(agentResult.cleanupAttempts).toBe(1)
    expect(cleanupPrompts).toHaveLength(1)
    expect(worktree.cleanupCommits).toEqual([{ files: ["src/agent-output.ts"], sha: "cleanup-sha" }])
    expect(worktree.untracked).toEqual([])

    const rebaseCalls: string[] = []
    installRebaseMockGit(rebaseCalls)
    const rebaseResult = await rebaseAction(rebaseContext())
    const rebaseOutput = rebaseResult.output as Record<string, unknown>

    expect(rebaseResult.error).toBeUndefined()
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
      "commit -m Complete worktree cleanup",
      "rev-parse HEAD",
    ])
    expect(rebaseOutput).toMatchObject({
      kind: "rebase",
      status: "completed",
      baseBranch: "master",
      remote: "origin",
      squashed: true,
      squashedHeadSha: "squashed-sha",
    })

    const pushCalls: { workDir: string; command: string }[] = []
    setPushGitRunnerForTest(async (workDir, args) => {
      const command = args.join(" ")
      pushCalls.push({ workDir, command })
      switch (command) {
        case "rev-parse mo/worktree-cleanup":
          return gitOk("squashed-sha\n")
        case "push origin mo/worktree-cleanup:master":
          return gitOk("To origin\n   base-sha..squashed-sha  mo/worktree-cleanup -> master")
        default:
          return gitFail(`unexpected git call: ${command}`, 1)
      }
    })

    const pushResult = await pushAction(pushContext())
    const pushOutput = pushResult.output as Record<string, unknown>
    expect(pushResult.error).toBeUndefined()
    expect(pushOutput).toMatchObject({
      kind: "push",
      status: "completed",
      source: "mo/worktree-cleanup",
      target: "master",
      landedCommit: "squashed-sha",
      pushed: true,
      workDir: worktree.workDir,
    })
    expect(pushCalls).toEqual([
      { workDir: worktree.workDir, command: "rev-parse mo/worktree-cleanup" },
      { workDir: worktree.workDir, command: "push origin mo/worktree-cleanup:master" },
    ])
  })

  it("fails delivery after cleanup attempts leave the workspace dirty", async () => {
    let attempt = 0
    setCleanupAgentActionForTest(async (ctx) => {
      attempt += 1
      const prompt = String(ctx.with?.prompt ?? "")
      expect(prompt).toContain(`attempt ${attempt}`)
      return { output: null }
    })

    const registry = buildRegistry({
      "mohist/opencode": {
        run: async () => {
          worktree.untracked = ["src/never-clean.ts"]
          return { output: null }
        },
        inputs: { prompt: { types: ["string", "object"] } },
      },
      "mohist/rebase": rebaseAction,
      "mohist/push": pushAction,
    })
    const executor = buildExecutor(registry)

    const agentResult = await executor.execute(
      buildWork({
        variables: {
          workspace: { path: worktree.workDir, branch: worktree.branch, changeDir: null },
          project: { path: worktree.workDir },
          issue: { title: "Worktree cleanup delivery", number: 42 },
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
  })
})
