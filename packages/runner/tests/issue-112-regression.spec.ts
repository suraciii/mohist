import { execFile } from "node:child_process"
import { mkdir, mkdtemp, rm, writeFile } from "node:fs/promises"
import { tmpdir } from "node:os"
import { dirname, join } from "node:path"
import { promisify } from "node:util"
import { afterEach, beforeEach, describe, expect, it } from "vitest"
import { WorkExecutor, setCleanupAgentActionForTest } from "../src/runtime/executor.js"
import { ActionRegistry } from "../src/actions/registry.js"
import type { ActionContext, ActionResult, JsonObject, WorkItem } from "../src/core/types.js"
import type { ServerConnection } from "../src/server/connection.js"

const exec = promisify(execFile)

interface WorktreeFixture {
  root: string
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
  await rm(worktree.root, { recursive: true, force: true })
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
  return { root, workDir, branch: "mo/issue-112", upstream }
}

async function dirtyFile(relativePath: string, content: string): Promise<void> {
  const full = join(worktree.workDir, relativePath)
  await mkdir(dirname(full), { recursive: true })
  await writeFile(full, content, "utf8")
}

async function commitAll(message: string): Promise<string> {
  await exec("git", ["add", "-A"], { cwd: worktree.workDir })
  await exec("git", ["commit", "-m", message, "-q"], { cwd: worktree.workDir })
  const result = await exec("git", ["rev-parse", "HEAD"], { cwd: worktree.workDir })
  return result.stdout.trim()
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
    { ensure: async () => ({ path: worktree.workDir, branch: worktree.branch, changeDir: null }) } as never,
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
      project: { path: worktree.workDir, baseBranch: "master" },
      repository: { name: "main", gitUrl: worktree.upstream, baseBranch: "master" },
      issue: { title: "Issue #112 regression", number: 112 },
    },
    ...overrides,
  }
}

describe("Issue #112 regression — agent cleanup delivery", () => {
  it("AgentTaskLeavesChanges_CleanupAgentCommits_TaskCompletesWithCleanWorktree", async () => {
    await dirtyFile("src/agent-output.ts", "export const v = 112\n")

    const cleanupPrompts: string[] = []
    let cleanupAttemptCount = 0
    setCleanupAgentActionForTest(async (ctx) => {
      cleanupAttemptCount += 1
      const prompt = String(ctx.with?.prompt ?? "")
      cleanupPrompts.push(prompt)
      expect(ctx.workId).toBe("build:agent.1")
      expect(prompt).toMatch(/do NOT start any new task work/i)
      expect(prompt).toMatch(/do NOT push to any remote/i)
      expect(prompt).toContain("src/agent-output.ts")
      await exec("git", ["add", "src/agent-output.ts"], { cwd: ctx.workDir })
      await exec("git", ["commit", "-m", "cleanup: commit agent output", "-q"], { cwd: ctx.workDir })
      return { status: "success", message: "committed leftover" }
    })

    const registry = buildRegistry({
      "mohist/acp-agent": async () => ({ status: "success", message: "agent finished" }),
    })
    const executor = buildExecutor(registry)

    const agentResult = await executor.execute(buildWork(), new AbortController().signal)

    expect(agentResult.status).toBe("completed")
    expect(agentResult.message).toBe("agent finished")
    expect(agentResult.cleanupAttempts).toBe(1)
    expect(cleanupAttemptCount).toBe(1)
    expect(cleanupPrompts[0]).toContain("Cleanup Follow-up (attempt 1)")

    const statusAfterCleanup = await exec("git", ["status", "--porcelain"], { cwd: worktree.workDir })
    expect(statusAfterCleanup.stdout).toBe("")
  })

  it("AgentTaskLeavesChanges_CleanupExhausts_TaskFailsWithStructuredEvidence", async () => {
    await dirtyFile("src/never-clean.ts", "export const v = 1\n")

    let attempt = 0
    setCleanupAgentActionForTest(async (ctx) => {
      attempt += 1
      expect(String(ctx.with?.prompt ?? "")).toContain(`attempt ${attempt}`)
      return { status: "success", message: "noop" }
    })

    const registry = buildRegistry({
      "mohist/acp-agent": async () => ({ status: "success", message: "first run" }),
    })
    const executor = buildExecutor(registry)

    const agentResult = await executor.execute(
      buildWork({
        variables: {
          workspace: { path: worktree.workDir, branch: worktree.branch, changeDir: null },
          project: { path: worktree.workDir, baseBranch: "master" },
          repository: { name: "main", gitUrl: worktree.upstream, baseBranch: "master" },
          issue: { title: "Issue #112 regression", number: 112 },
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

  it("CleanAgentTask_CleanupCommitPreservesSourceBranchForLaterPublish", async () => {
    await dirtyFile("src/squash-input.ts", "export const shipped = true\n")
    const committedSha = await commitAll("Add squash input")

    const statusAfterCommit = await exec("git", ["status", "--porcelain"], { cwd: worktree.workDir })
    expect(statusAfterCommit.stdout).toBe("")
    const branchHead = await exec("git", ["rev-parse", "HEAD"], { cwd: worktree.workDir })
    expect(branchHead.stdout.trim()).toBe(committedSha)
  })
})
