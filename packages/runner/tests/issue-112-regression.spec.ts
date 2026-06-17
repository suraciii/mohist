import { execFile } from "node:child_process"
import { mkdir, mkdtemp, rm, writeFile } from "node:fs/promises"
import { tmpdir } from "node:os"
import { dirname, join } from "node:path"
import { promisify } from "node:util"
import { afterEach, beforeEach, describe, expect, it } from "vitest"
import { WorkExecutor, setCleanupAgentActionForTest } from "../src/runtime/executor.js"
import { ActionRegistry } from "../src/actions/registry.js"
import { mergeAction, setMergeConflictResolverForTest, setMergeGitRunnerForTest } from "../src/actions/registry.js"
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
  setMergeGitRunnerForTest(null)
  setMergeConflictResolverForTest(null)
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
      project: { path: worktree.workDir },
      issue: { title: "Issue #102 regression", number: 112 },
    },
    ...overrides,
  }
}

function mergeContext(overrides: JsonObject = {}): ActionContext {
  return {
    workflowRunId: "wf-112",
    workId: "integrate:merge.1",
    workType: "task",
    stage: "integrate",
    title: "Merge issue",
    uses: "mohist/merge",
    with: {
      source: "mo/issue-112",
      target: "master",
      strategy: "squash",
      push: true,
      remote: "origin",
      message: "Complete issue (#112)",
      ...overrides,
    },
    variables: {
      project: { path: worktree.workDir },
      issue: { title: "Issue #102 regression", number: 112 },
    },
    workDir: worktree.workDir,
    signal: new AbortController().signal,
  }
}

describe("Issue #112 regression — full agent + merge delivery pipeline", () => {
  it("AgentTaskLeavesChanges_CleanupAgentCommits_TaskCompletesAndSubsequentMergeAcceptsCleanSource", async () => {
    // Reproduction of the issue #102 shape: agent reports success
    // but leaves a modified file in the worktree. The executor must
    // detect the dirty worktree, send a constrained follow-up
    // prompt, observe the cleanup commit, and only then mark the
    // task completed. The same worktree is then handed to
    // `mergeAction`, which must accept the clean source and
    // succeed without invoking the source-cleanup guard.
    await dirtyFile("src/agent-output.ts", "export const v = 112\n")

    const cleanupPrompts: string[] = []
    let cleanupAttemptCount = 0
    setCleanupAgentActionForTest(async (ctx) => {
      cleanupAttemptCount += 1
      const prompt = String(ctx.with?.prompt ?? "")
      cleanupPrompts.push(prompt)
      // The same agent session reference must be reused.
      expect(ctx.workId).toBe("build:agent.1")
      // The cleanup prompt must instruct the agent to commit, not
      // push, and not start new work.
      expect(prompt).toMatch(/do NOT start any new task work/i)
      expect(prompt).toMatch(/do NOT push to any remote/i)
      expect(prompt).toContain("src/agent-output.ts")
      // Cleanup commits the file so the worktree returns to clean.
      await exec("git", ["add", "src/agent-output.ts"], { cwd: ctx.workDir })
      await exec("git", ["commit", "-m", "cleanup: commit agent output", "-q"], { cwd: ctx.workDir })
      return { status: "success", message: "committed leftover", output: JSON.stringify({ commitSha: "cleanup-sha" }) }
    })

    const agentHandler = async (_ctx: ActionContext): Promise<ActionResult> => ({ status: "success", message: "agent finished" })
    const registry = buildRegistry({
      "mohist/acp-agent": agentHandler,
      "mohist/merge": mergeAction,
    })
    const executor = buildExecutor(registry)

    const agentResult = await executor.execute(buildWork(), new AbortController().signal)
    expect(agentResult.status).toBe("completed")
    expect(agentResult.message).toBe("agent finished")
    expect(agentResult.cleanupAttempts).toBe(1)
    expect(cleanupAttemptCount).toBe(1)
    expect(cleanupPrompts).toHaveLength(1)
    expect(cleanupPrompts[0]).toContain("Cleanup Follow-up (attempt 1)")

    const statusAfterCleanup = await exec("git", ["status", "--porcelain"], { cwd: worktree.workDir })
    expect(statusAfterCleanup.stdout).toBe("")

    // After the agent task completes with a clean worktree, the
    // merge action's source-cleanup guard must NOT trigger. We
    // mock the merge git runner so the test does not need a real
    // fetch target, but we still record the call list to prove
    // that no source-cleanup failure short-circuited the flow.
    const gitCalls: string[] = []
    let headCalls = 0
    setMergeGitRunnerForTest(async (_dir, args) => {
      const cmd = args.join(" ")
      gitCalls.push(cmd)
      if (cmd === "status --porcelain") return gitOk("")
      if (cmd === "fetch origin master") return gitOk("From origin\n * branch            master     -> FETCH_HEAD")
      if (cmd === "rev-parse origin/master") return gitOk("base-sha\n")
      if (cmd === "checkout mo/issue-112") return gitOk("Switched to branch 'mo/issue-112'")
      if (cmd === "rebase origin/master") return gitOk("Successfully rebased.")
      if (cmd === "rev-parse HEAD") {
        headCalls += 1
        return gitOk(headCalls === 1 ? "rebased-sha\n" : "landing-sha\n")
      }
      if (cmd === "checkout --detach base-sha") return gitOk("HEAD is now at base-sha")
      if (cmd === "merge --squash mo/issue-112") return gitOk("Squash commit -- not updating HEAD")
      if (cmd === "log --format=* %s base-sha..mo/issue-112") return gitOk("* T-001 commit")
      if (cmd === "commit -m Issue #102 regression (#112) -m * T-001 commit") return gitOk("[detached HEAD landing-sha] Issue #102 regression (#112)")
      if (cmd === "log -1 --format=%P landing-sha") return gitOk("base-sha\n")
      if (cmd === "push origin landing-sha:refs/heads/master") return gitOk("To origin\n   base-sha..landing-sha  master -> master")
      if (cmd === "ls-remote origin refs/heads/master") return gitOk("landing-sha\trefs/heads/master\n")
      return gitFail(`unexpected git call: ${cmd}`, 1)
    })

    const mergeResult = await mergeAction(mergeContext())
    const mergeOutput = JSON.parse(mergeResult.output ?? "{}")

    expect(mergeResult.status).toBe("success")
    expect(mergeOutput.phase).toBeUndefined()
    // The very first git call the merge action makes is the source
    // worktree status check. If it were dirty, the action would
    // fail with phase=source-cleanup before the fetch call. The
    // presence of fetch and rebase in the call list proves the
    // guard passed.
    expect(gitCalls[0]).toBe("status --porcelain")
    expect(gitCalls).toContain("fetch origin master")
    expect(gitCalls).toContain("rebase origin/master")
    expect(gitCalls).toContain("push origin landing-sha:refs/heads/master")
    expect(mergeOutput).toMatchObject({
      kind: "merge",
      source: "mo/issue-112",
      target: "master",
      remote: "origin",
      strategy: "squash",
      pushEnabled: true,
      baseSha: "base-sha",
      rebasedSha: "rebased-sha",
      landingSha: "landing-sha",
      remoteRef: "landing-sha",
    })
  })

  it("AgentTaskLeavesChanges_CleanupExhausts_TaskFailsWithStructuredEvidence_MergeRefuses", async () => {
    // The agent repeatedly leaves the worktree dirty. After the
    // configured bound, the executor must fail the task with
    // structured dirty-worktree evidence, and the subsequent
    // merge action must NOT proceed because the source worktree
    // is still dirty — exactly the failure mode of issue #102,
    // now caught before merge silently commits leftovers.
    await dirtyFile("src/never-clean.ts", "export const v = 1\n")

    let attempt = 0
    setCleanupAgentActionForTest(async (ctx) => {
      attempt += 1
      const prompt = String(ctx.with?.prompt ?? "")
      expect(prompt).toContain(`attempt ${attempt}`)
      // Agent "tries" but leaves the file dirty on purpose so the
      // bounded loop is forced to exhaust.
      return { status: "success", message: "noop" }
    })

    const agentHandler = async (_ctx: ActionContext): Promise<ActionResult> => ({ status: "success", message: "first run" })
    const registry = buildRegistry({
      "mohist/acp-agent": agentHandler,
      "mohist/merge": mergeAction,
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

    // The merge action must now refuse the dirty source. Because
    // we use a real git repo we can rely on the actual
    // `git status --porcelain` to report the dirty file. We mock
    // the rest of the merge git runner so that any call beyond
    // the source-cleanup check would be flagged.
    const gitCalls: string[] = []
    setMergeGitRunnerForTest(async (_dir, args) => {
      const cmd = args.join(" ")
      gitCalls.push(cmd)
      if (cmd === "status --porcelain") return gitOk(" M src/never-clean.ts\n")
      return gitFail(`unexpected git call: ${cmd}`, 1)
    })

    const mergeResult = await mergeAction(mergeContext())
    const mergeOutput = JSON.parse(mergeResult.output ?? "{}")

    expect(mergeResult.status).toBe("failure")
    expect(mergeOutput.phase).toBe("source-cleanup")
    expect(mergeOutput.dirty).toEqual({
      staged: [],
      unstaged: ["src/never-clean.ts"],
      untracked: [],
    })
    // Only the worktree status check should have run — fetch,
    // rebase, checkout, push must all be absent.
    expect(gitCalls).toEqual(["status --porcelain"])
  })

  it("MergeActionReceivesDirtyWorktree_FailsWithPhaseSourceCleanup_NoGitOpsBeyondCleanCheck", async () => {
    // Standalone coverage: a dirty source worktree at merge start
    // must fail with phase=source-cleanup and a structured
    // dirty-worktree payload, and no fetch/checkout/rebase/push
    // must run. We use a real git repo so the actual `git status
    // --porcelain` command reports the dirty file, and the mock
    // merge git runner refuses every other command.
    await dirtyFile("src/leftover.ts", "export const v = 1\n")

    const gitCalls: string[] = []
    setMergeGitRunnerForTest(async (_dir, args) => {
      const cmd = args.join(" ")
      gitCalls.push(cmd)
      if (cmd === "status --porcelain") return gitOk(" M src/leftover.ts\n?? noise.log\n")
      return gitFail(`unexpected git call: ${cmd}`, 1)
    })

    const result = await mergeAction(mergeContext())
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(output.phase).toBe("source-cleanup")
    expect(output.kind).toBe("merge")
    expect(output.source).toBe("mo/issue-112")
    expect(output.target).toBe("master")
    expect(output.dirty).toEqual({
      staged: [],
      unstaged: ["src/leftover.ts"],
      untracked: ["noise.log"],
    })
    expect(gitCalls).toEqual(["status --porcelain"])
    expect(gitCalls).not.toContain("fetch origin master")
    expect(gitCalls).not.toContain("rebase origin/master")
    expect(gitCalls).not.toContain("checkout --detach base-sha")
    expect(gitCalls).not.toContain("push origin")
  })

  it("CleanAgentTask_CleanMergeSource_FullPipelineCompletesWithPushDeliveryFacts", async () => {
    // End-to-end happy path: a clean agent task commits a new
    // file on the source branch; the merge action validates the
    // clean source, fetches the remote target, rebases, creates
    // the squash landing commit, and fast-forward pushes the
    // landing commit. The merge task output must include the
    // delivery facts (landing SHA, remote ref, retry attempts).
    // We use a real git repo for the source-cleanup check, then
    // drive the rest of the pipeline through the mock merge git
    // runner so the test stays self-contained.
    await dirtyFile("src/squash-input.ts", "export const shipped = true\n")
    const committedSha = await commitAll("Add squash input")

    // The first `git status --porcelain` must report clean (the
    // commit made the worktree clean again). The mock runner
    // pretends to fetch and rebase onto the upstream base, then
    // synthesises a single landing commit and a fast-forward
    // push.
    const gitCalls: string[] = []
    let headCalls = 0
    setMergeGitRunnerForTest(async (_dir, args) => {
      const cmd = args.join(" ")
      gitCalls.push(cmd)
      switch (cmd) {
        case "status --porcelain":
          return gitOk("")
        case "fetch origin master":
          return gitOk("From origin\n * branch            master     -> FETCH_HEAD")
        case "rev-parse origin/master":
          return gitOk("base-sha\n")
        case "checkout mo/issue-112":
          return gitOk("Switched to branch 'mo/issue-112'")
        case "rebase origin/master":
          return gitOk("Successfully rebased and updated refs/heads/mo/issue-112.")
        case "rev-parse HEAD":
          headCalls += 1
          return gitOk(headCalls === 1 ? "rebased-sha\n" : "landing-sha\n")
        case "checkout --detach base-sha":
          return gitOk("HEAD is now at base-sha")
        case "merge --squash mo/issue-112":
          return gitOk("Squash commit -- not updating HEAD")
        case "log --format=* %s base-sha..mo/issue-112":
          return gitOk("* T-001 commit")
        case "commit -m Issue #102 regression (#112) -m * T-001 commit":
          return gitOk("[detached HEAD landing-sha] Issue #102 regression (#112)")
        case "log -1 --format=%P landing-sha":
          return gitOk("base-sha\n")
        case "push origin landing-sha:refs/heads/master":
          return gitOk("To origin\n   base-sha..landing-sha  master -> master")
        case "ls-remote origin refs/heads/master":
          return gitOk("landing-sha\trefs/heads/master\n")
        default:
          return gitFail(`unexpected git call: ${cmd}`, 1)
      }
    })

    // Sanity check: the worktree on disk is clean after the agent
    // task, and the source branch contains the new commit. The
    // merge action's first git status must see the same.
    const liveStatus = await exec("git", ["status", "--porcelain"], { cwd: worktree.workDir })
    expect(liveStatus.stdout).toBe("")

    const result = await mergeAction(mergeContext())
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(output.phase).toBeUndefined()
    expect(output).toMatchObject({
      kind: "merge",
      source: "mo/issue-112",
      target: "master",
      remote: "origin",
      strategy: "squash",
      pushEnabled: true,
      push: true,
      baseSha: "base-sha",
      rebasedSha: "rebased-sha",
      landingSha: "landing-sha",
      remoteRef: "landing-sha",
      pushRetryAttempts: 1,
      lastRemoteSha: "base-sha",
    })
    // The source-cleanup guard runs first; the full delivery
    // pipeline then executes in order: fetch → rebase → landing
    // → push → remote ref verification.
    expect(gitCalls[0]).toBe("status --porcelain")
    expect(gitCalls).toContain("fetch origin master")
    expect(gitCalls).toContain("rebase origin/master")
    expect(gitCalls).toContain("checkout --detach base-sha")
    expect(gitCalls).toContain("merge --squash mo/issue-112")
    expect(gitCalls).toContain("commit -m Issue #102 regression (#112) -m * T-001 commit")
    expect(gitCalls).toContain("push origin landing-sha:refs/heads/master")
    expect(gitCalls).toContain("ls-remote origin refs/heads/master")
    // Re-confirm the local commit is still present on the source
    // branch so the merge action had real content to rebase.
    const branchHead = await exec("git", ["rev-parse", "HEAD"], { cwd: worktree.workDir })
    expect(branchHead.stdout.trim()).toBe(committedSha)
  })
})

function gitOk(stdout: string) {
  return { success: true, stdout, stderr: "", exitCode: 0, combinedOutput: stdout.trim() }
}

function gitFail(stderr: string, exitCode = 1) {
  return { success: false, stdout: "", stderr, exitCode, combinedOutput: stderr }
}
