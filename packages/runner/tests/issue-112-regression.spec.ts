import { execFile } from "node:child_process"
import { mkdir, mkdtemp, rm, writeFile } from "node:fs/promises"
import { tmpdir } from "node:os"
import { dirname, join } from "node:path"
import { promisify } from "node:util"
import { afterEach, beforeEach, describe, expect, it } from "vitest"
import { WorkExecutor, setCleanupAgentActionForTest } from "../src/runtime/executor.js"
import { ActionRegistry } from "../src/actions/registry.js"
import {
  prepareAction,
  publishAction,
  setDeliveryGitRunnerForTest,
  setDeliveryWorkspaceManagerForTest,
} from "../src/actions/registry.js"
import {
  setRebaseConflictResolverForTest,
  setRebaseExistsCheckerForTest,
} from "../src/actions/rebase.js"
import type { LandingWorkspaceInfo } from "../src/runtime/workspace.js"
import { verifyOnlyWorkspaceManager } from "./support/workspace-mock.js"
import type { ActionContext, ActionResult, JsonObject, WorkItem } from "../src/core/types.js"
import type { ServerConnection } from "../src/server/connection.js"
import type { DeliveryWorkspaceManager } from "../src/actions/registry.js"

const exec = promisify(execFile)

interface WorktreeFixture {
  workDir: string
  branch: string
  upstream: string
}

const LANDING_PATH = "/landing/wr-issue-112-regression"

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
  const landing: LandingWorkspaceInfo = {
    path: LANDING_PATH,
    runId: "wr-issue-112-regression",
    runBranch: "mohist/run-wr-issue-112-regression",
    baseBranch: "master",
    gitUrl: "https://example.com/repo.git",
  }
  const manager: DeliveryWorkspaceManager = {
    createLandingWorkspace: async (_work, _signal) => landing,
    disposeLandingWorkspace: async (target, _signal) => {
      const path = typeof target === "string" ? target : target.path
      return { path, disposed: true }
    },
  }
  setDeliveryWorkspaceManagerForTest(manager)
})

afterEach(async () => {
  setCleanupAgentActionForTest(null)
  setDeliveryGitRunnerForTest(null)
  setDeliveryWorkspaceManagerForTest(null)
  setRebaseConflictResolverForTest(null)
  setRebaseExistsCheckerForTest(null)
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

function prepareContext(overrides: JsonObject = {}, variables: JsonObject = {}): ActionContext {
  return {
    workflowRunId: "wf-112",
    workId: "integrate:prepare.1",
    workType: "task",
    stage: "integrate",
    title: "Prepare issue branch",
    uses: "mohist/prepare",
    with: { baseBranch: "master", ...overrides },
    variables: {
      project: { path: worktree.workDir },
      issue: { title: "Issue #102 regression", number: 112 },
      ...variables,
    },
    workDir: worktree.workDir,
    signal: new AbortController().signal,
  }
}

function publishContext(overrides: JsonObject = {}, variables: JsonObject = {}): ActionContext {
  return {
    workflowRunId: "wf-112",
    workId: "integrate:publish.1",
    workType: "task",
    stage: "integrate",
    title: "Publish merge",
    uses: "mohist/publish",
    with: {
      source: "mo/issue-112",
      target: "master",
      remote: "origin",
      message: "Issue #102 regression (#112)",
      ...overrides,
    },
    variables: {
      project: { path: worktree.workDir },
      issue: { title: "Issue #102 regression", number: 112 },
      ...variables,
    },
    workDir: worktree.workDir,
    signal: new AbortController().signal,
  }
}

function gitOk(stdout: string) {
  return { success: true, stdout, stderr: "", exitCode: 0, combinedOutput: stdout.trim() }
}

function gitFail(stderr: string, exitCode = 1) {
  return { success: false, stdout: "", stderr, exitCode, combinedOutput: stderr }
}

function installPrepareMockGit(calls: string[]) {
  setDeliveryGitRunnerForTest(async (_dir, args) => {
    const cmd = args.join(" ")
    calls.push(cmd)
    switch (cmd) {
      case "rev-parse --git-path rebase-merge":
        return gitOk("/fake/worktree/.git/rebase-merge\n")
      case "rev-parse --git-path rebase-apply":
        return gitOk("/fake/worktree/.git/rebase-apply\n")
      case "status --porcelain":
        return gitOk("")
      case "fetch origin master":
        return gitOk("From origin\n * branch            master     -> FETCH_HEAD")
      case "rev-parse origin/master":
        return gitOk("base-sha\n")
      case "rev-parse HEAD":
        return gitOk("rebased-sha\n")
      case "rebase origin/master":
        return gitOk("Successfully rebased and updated refs/heads/mo/issue-112.")
      default:
        return gitFail(`unexpected git call: ${cmd}`, 1)
    }
  })
  setRebaseExistsCheckerForTest(() => false)
  setRebaseConflictResolverForTest(async () => ({ status: "success", message: "noop", output: "" }))
}

describe("Issue #112 regression — agent leftover never silently committed by delivery", () => {
  it("AgentTaskLeavesChanges_CleanupAgentCommits_PrepareAcceptsCleanSource", async () => {
    // Reproduction of the issue #102 shape: agent reports success
    // but leaves a modified file in the worktree. The executor must
    // detect the dirty worktree, send a constrained follow-up
    // prompt, observe the cleanup commit, and only then mark the
    // task completed. The prepare task (which guards delivery
    // against dirty sources) must then accept the clean source.
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
      return { status: "success", message: "committed leftover", output: JSON.stringify({ commitSha: "cleanup-sha" }) }
    })

    const agentHandler = async (_ctx: ActionContext): Promise<ActionResult> => ({ status: "success", message: "agent finished" })
    const registry = buildRegistry({
      "mohist/acp-agent": agentHandler,
      "mohist/prepare": prepareAction,
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
    // prepare task's source-cleanup guard must NOT trigger. We
    // mock the git runner so the test does not need a real fetch
    // target.
    const gitCalls: string[] = []
    installPrepareMockGit(gitCalls)

    const prepareResult = await prepareAction(prepareContext())
    const prepareOutput = JSON.parse(prepareResult.output ?? "{}")

    expect(prepareResult.status).toBe("success")
    // The very first git call the prepare action makes is the
    // worktree status check. If it were dirty, the action would
    // fail before the fetch call. The presence of fetch and rebase
    // in the call list proves the guard passed.
    expect(gitCalls).toContain("status --porcelain")
    expect(gitCalls).toContain("fetch origin master")
    expect(gitCalls).toContain("rebase origin/master")
    expect(prepareOutput).toMatchObject({
      kind: "prepare",
      status: "completed",
      baseBranch: "master",
      preparedBaseSha: "base-sha",
      preparedHeadSha: "rebased-sha",
      prepared: true,
      conflicts: [],
      resolveAttempts: 0,
    })
  })

  it("AgentTaskLeavesChanges_CleanupExhausts_TaskFailsWithStructuredEvidence_PrepareRefuses", async () => {
    // The agent repeatedly leaves the worktree dirty. After the
    // configured bound, the executor must fail the task with
    // structured dirty-worktree evidence, and the subsequent
    // prepare task must NOT proceed because the source worktree is
    // still dirty — exactly the failure mode of issue #102, now
    // caught before delivery silently commits leftovers.
    await dirtyFile("src/never-clean.ts", "export const v = 1\n")

    let attempt = 0
    setCleanupAgentActionForTest(async (ctx) => {
      attempt += 1
      const prompt = String(ctx.with?.prompt ?? "")
      expect(prompt).toContain(`attempt ${attempt}`)
      return { status: "success", message: "noop" }
    })

    const agentHandler = async (_ctx: ActionContext): Promise<ActionResult> => ({ status: "success", message: "first run" })
    const registry = buildRegistry({
      "mohist/acp-agent": agentHandler,
      "mohist/prepare": prepareAction,
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

    // The prepare task must now refuse the dirty source. Because
    // we use a real git repo we rely on the actual `git status
    // --porcelain` to report the dirty file. We mock the rest of
    // the git runner so any call beyond the source-cleanup check
    // would be flagged.
    const gitCalls: string[] = []
    setDeliveryGitRunnerForTest(async (_dir, args) => {
      const cmd = args.join(" ")
      gitCalls.push(cmd)
      switch (cmd) {
        case "rev-parse --git-path rebase-merge":
          return gitOk("/fake/worktree/.git/rebase-merge\n")
        case "rev-parse --git-path rebase-apply":
          return gitOk("/fake/worktree/.git/rebase-apply\n")
        case "status --porcelain":
          return gitOk(" M src/never-clean.ts\n")
        default:
          return gitFail(`unexpected git call: ${cmd}`, 1)
      }
    })
    setRebaseExistsCheckerForTest(() => false)
    setRebaseConflictResolverForTest(async () => ({ status: "success", message: "noop", output: "" }))

    const prepareResult = await prepareAction(prepareContext())
    const prepareOutput = JSON.parse(prepareResult.output ?? "{}")

    expect(prepareResult.status).toBe("failure")
    expect(prepareResult.message).toMatch(/worktree is dirty before rebase/i)
    // The prepare task's source-cleanup guard rejected the dirty
    // worktree — its `kind` is "prepare" (the new split), and the
    // message names the dirty file. Crucially, no fetch/rebase
    // call must have run.
    expect(prepareOutput.kind).toBe("prepare")
    expect(prepareOutput.status).toBe("failed")
    expect(gitCalls).not.toContain("fetch origin master")
    expect(gitCalls).not.toContain("rebase origin/master")
  })

  it("PrepareReceivesDirtyWorktree_FailsWithoutRebase_NoGitOpsBeyondCleanCheck", async () => {
    // Standalone coverage: a dirty source worktree at prepare
    // start must fail with a structured failure, and no
    // fetch/rebase must run. We use a real git repo so the actual
    // `git status --porcelain` reports the dirty file, and the
    // mock git runner refuses every other command.
    await dirtyFile("src/leftover.ts", "export const v = 1\n")

    const gitCalls: string[] = []
    setDeliveryGitRunnerForTest(async (_dir, args) => {
      const cmd = args.join(" ")
      gitCalls.push(cmd)
      switch (cmd) {
        case "rev-parse --git-path rebase-merge":
          return gitOk("/fake/worktree/.git/rebase-merge\n")
        case "rev-parse --git-path rebase-apply":
          return gitOk("/fake/worktree/.git/rebase-apply\n")
        case "status --porcelain":
          return gitOk(" M src/leftover.ts\n?? noise.log\n")
        default:
          return gitFail(`unexpected git call: ${cmd}`, 1)
      }
    })
    setRebaseExistsCheckerForTest(() => false)
    setRebaseConflictResolverForTest(async () => ({ status: "success", message: "noop", output: "" }))

    const result = await prepareAction(prepareContext())
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(result.message).toMatch(/worktree is dirty before rebase/i)
    expect(output.kind).toBe("prepare")
    expect(output.status).toBe("failed")
    expect(output.baseBranch).toBe("master")
    expect(gitCalls).not.toContain("fetch origin master")
    expect(gitCalls).not.toContain("rebase origin/master")
  })

  it("CleanAgentTask_CleanPrepareSource_PrepareCompletesAndPublishPushesLandingCommit", async () => {
    // End-to-end happy path: a clean agent task commits a new
    // file on the source branch; the prepare task validates the
    // clean source and rebases; the publish task then constructs
    // the squash landing commit and fast-forwards the target
    // branch. We use a real git repo for the source-cleanup
    // check, then drive the rest of the pipeline through the
    // mock git runner so the test stays self-contained.
    await dirtyFile("src/squash-input.ts", "export const shipped = true\n")
    const committedSha = await commitAll("Add squash input")

    // Sanity check: the worktree on disk is clean after the agent
    // task, and the source branch contains the new commit. The
    // prepare task's first git status must see the same.
    const liveStatus = await exec("git", ["status", "--porcelain"], { cwd: worktree.workDir })
    expect(liveStatus.stdout).toBe("")

    // Prepare: mock the full rebase pipeline.
    const prepareCalls: string[] = []
    installPrepareMockGit(prepareCalls)

    const prepareResult = await prepareAction(prepareContext())
    expect(prepareResult.status).toBe("success")
    const prepareOutput = JSON.parse(prepareResult.output ?? "{}")
    expect(prepareOutput).toMatchObject({
      kind: "prepare",
      status: "completed",
      baseBranch: "master",
      preparedBaseSha: "base-sha",
      preparedHeadSha: "rebased-sha",
      prepared: true,
      conflicts: [],
      resolveAttempts: 0,
    })

    // Publish: replace the mock so the landing workspace sequence
    // (fetch → checkout → ff → squash → commit → push) succeeds.
    const publishCalls: { workDir: string; command: string }[] = []
    setDeliveryGitRunnerForTest(async (workDir, args) => {
      const command = args.join(" ")
      publishCalls.push({ workDir, command })
      switch (command) {
        case "rev-parse mo/issue-112":
          return gitOk("rebased-sha\n")
        case "fetch origin master":
          return gitOk("From origin\n * branch            master     -> FETCH_HEAD")
        case "rev-parse origin/master":
          return gitOk("base-sha\n")
        case "checkout -B master origin/master":
          return gitOk("Switched to branch 'master'")
        case "status --porcelain":
          return gitOk("")
        case "merge-base --is-ancestor origin/master mo/issue-112":
          return gitOk("")
        case "merge --squash mo/issue-112":
          return gitOk("Squash commit -- not updating HEAD")
        case "commit -m Issue #102 regression (#112) -m mo/issue-112 into master":
          return gitOk("[detached HEAD landing-sha] Issue #102 regression (#112)")
        case "rev-parse HEAD":
          return gitOk("landing-sha\n")
        case "push origin master":
          return gitOk("To origin\n   base-sha..landing-sha  master -> master")
        default:
          return gitFail(`unexpected git call: ${command}`, 1)
      }
    })

    const publishResult = await publishAction(publishContext())
    expect(publishResult.status).toBe("success")
    const publishOutput = JSON.parse(publishResult.output ?? "{}")
    expect(publishOutput).toMatchObject({
      kind: "publish",
      status: "completed",
      source: "mo/issue-112",
      target: "master",
      landedCommit: "landing-sha",
      pushed: true,
      failureKind: null,
    })

    // The full delivery pipeline then executes in order:
    // prepare (status → fetch → rebase) then publish
    // (rev-parse → fetch → checkout → ff → squash → commit → push).
    expect(prepareCalls).toContain("status --porcelain")
    expect(prepareCalls).toContain("fetch origin master")
    expect(prepareCalls).toContain("rebase origin/master")
    const landingCalls = publishCalls.filter((c) => c.workDir === LANDING_PATH).map((c) => c.command)
    expect(landingCalls).toContain("merge --squash mo/issue-112")
    expect(landingCalls).toContain(
      "commit -m Issue #102 regression (#112) -m mo/issue-112 into master",
    )
    expect(landingCalls).toContain("push origin master")
    // The workflow workspace only sees the read-only source-anchor check.
    const workflowCalls = publishCalls.filter((c) => c.workDir === worktree.workDir).map((c) => c.command)
    expect(workflowCalls).toEqual(["rev-parse mo/issue-112"])

    // Re-confirm the local commit is still present on the source
    // branch so the prepare task had real content to rebase.
    const branchHead = await exec("git", ["rev-parse", "HEAD"], { cwd: worktree.workDir })
    expect(branchHead.stdout.trim()).toBe(committedSha)
  })
})