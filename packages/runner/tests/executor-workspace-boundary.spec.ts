import { execFile } from "node:child_process"
import { mkdir, mkdtemp, rm, writeFile } from "node:fs/promises"
import { tmpdir } from "node:os"
import { join } from "node:path"
import { promisify } from "node:util"
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { WorkExecutor } from "../src/runtime/executor.js"
import { ActionRegistry } from "../src/actions/registry.js"
import { NETWORK_COMMAND_TIMEOUT_MS } from "../src/actions/git.js"
import { WorkspaceManager, WorkspaceNetworkTimeoutError } from "../src/runtime/workspace.js"
import type { ActionContext, ActionResult, RenderedWorkItem } from "../src/core/types.js"
import type { ServerConnection } from "../src/server/connection.js"

const exec = promisify(execFile)

describe("workspace preparation across stages", () => {
  let root: string
  let repo: string
  let runnerRoot: string
  let connection: Pick<ServerConnection, "uploadArtifact" | "report">

  beforeEach(async () => {
    root = await mkdtemp(join(tmpdir(), "mohist-workspace-stages-"))
    repo = await createBareUpstream(root)
    runnerRoot = join(root, "runner")
    connection = {
      async report() {
        return {}
      },
      async uploadArtifact() {
        throw new Error("uploadArtifact should not be called in cross-stage tests")
      },
    } as unknown as Pick<ServerConnection, "uploadArtifact" | "report">
  })

  afterEach(async () => {
    await rm(root, { recursive: true, force: true })
  })

  it("FirstDispatchPrepares_ThenReentriesReuseWithoutRecloning", async () => {
    const manager = new WorkspaceManager(runnerRoot)
    const signal = new AbortController().signal

    const processMod = await import("../src/system/process.js")
    const realRunCommand = processMod.runCommand
    const gitCalls: string[] = []
    const spy = vi.spyOn(processMod, "runCommand").mockImplementation(async (cmd, args, cwd, sig) => {
      gitCalls.push(`${cmd} ${args.join(" ")}`)
      return await realRunCommand(cmd, args, cwd, sig)
    })

    const handlerCalls: string[] = []
    const handler: (ctx: ActionContext) => Promise<ActionResult> = async (ctx) => {
      handlerCalls.push(`${ctx.workType}:${ctx.stage ?? ""}`)
      return { status: "success", message: "ok" }
    }

    // No-op action registered as `core/script` so the test does not spawn
    // real ACP processes. The workspace-preparation contract is
    // independent of the action.
    const registry = new ActionRegistry()
    registry.register("core/script", async (ctx) => handler(ctx))

    const executor = new WorkExecutor(
      registry,
      manager,
      connection as never,
      {} as never,
      null,
      runnerRoot,
    )

    try {
      // The first dispatch prepares the workspace (one clone); every
      // later dispatch for the same run re-enters without re-cloning.
      const plan = await executor.execute(buildWork(repo, "wr-cross-stage", "issue-cross-stage", "plan", "plan:write"), signal)
      expect(plan.status).toBe("completed")

      gitCalls.length = 0

      const build = await executor.execute(buildWork(repo, "wr-cross-stage", "issue-cross-stage", "build", "build:agent"), signal)
      expect(build.status).toBe("completed")
      const check = await executor.execute(buildWork(repo, "wr-cross-stage", "issue-cross-stage", "check", "check:verdict"), signal)
      expect(check.status).toBe("completed")
      const prepare = await executor.execute(buildWork(repo, "wr-cross-stage", "issue-cross-stage", "integrate", "integrate:prepare"), signal)
      expect(prepare.status).toBe("completed")

      // All four dispatches ran their action.
      expect(handlerCalls).toEqual(["task:plan", "task:build", "task:check", "task:integrate"])

      // Re-entries never re-clone the upstream.
      const remoteCloneCalls = gitCalls.filter((call) => call.startsWith("git clone ") && call.includes(repo))
      expect(remoteCloneCalls).toHaveLength(0)

      // The workspace stays on the run branch throughout.
      const workspacePath = join(runnerRoot, "mohist-local", "workspaces", "issue-9")
      const head = await realRunCommand("git", ["-C", workspacePath, "rev-parse", "--abbrev-ref", "HEAD"], ".", signal)
      expect(head.stdout.trim()).toBe("mohist/run-wr-cross-stage")
    } finally {
      spy.mockRestore()
    }
  })

  it("AgentJobDispatch_SkipsWorkspacePreparation_ResolvesWorkspaceFromVariables", async () => {
    // Agent-job dispatches own their workspace and must NOT go through
    // the runner's prepare() path.
    const recorded = { prepare: 0 }
    const recordingManager = {
      async prepare() {
        recorded.prepare += 1
        throw new Error("prepare must not be called for agent-job dispatches")
      },
    } as unknown as WorkspaceManager

    const executor = new WorkExecutor(
      buildRegistry(async () => ({ status: "success", message: "agent ran" })),
      recordingManager,
      connection as never,
      {} as never,
      null,
      runnerRoot,
    )

    const result = await executor.execute(
      buildAgentJobWork("/tmp/agent-job-ws", "wr-agent", "agent-job-1"),
      new AbortController().signal,
    )

    expect(result.status).toBe("completed")
    expect(result.message).toBe("agent ran")
    expect(recorded).toEqual({ prepare: 0 })
  })

  it("WorkspaceNetworkTimeoutFailure_SerializesStructuredRetrySafeStep", async () => {
    const timeout = new WorkspaceNetworkTimeoutError(
      "Workspace preparation network command timed out: git-ls-remote after 120s",
      {
        name: "git-ls-remote",
        command: "ls-remote --heads https://example.com/repo.git master",
        exitCode: 124,
        output: `Command timed out after ${NETWORK_COMMAND_TIMEOUT_MS / 1000}s`,
        status: "timeout",
        timeoutMs: NETWORK_COMMAND_TIMEOUT_MS,
      },
    )
    const failingManager = {
      async prepare() {
        throw timeout
      },
    } as unknown as WorkspaceManager
    const executor = new WorkExecutor(
      buildRegistry(async () => ({ status: "success", message: "should not run" })),
      failingManager,
      connection as never,
      {} as never,
      null,
      runnerRoot,
    )

    const result = await executor.execute(buildWork("https://example.com/repo.git", "wr-timeout", "issue-timeout", "plan", "plan:write"), new AbortController().signal)
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failed")
    expect(result.message).toContain("workspace-setup")
    expect(output).toEqual({
      kind: "workspace-setup",
      failureKind: "retry-safe",
      step: {
        name: "git-ls-remote",
        command: "ls-remote --heads https://example.com/repo.git master",
        exitCode: 124,
        output: `Command timed out after ${NETWORK_COMMAND_TIMEOUT_MS / 1000}s`,
        status: "timeout",
        timeoutMs: NETWORK_COMMAND_TIMEOUT_MS,
      },
    })
  })
})

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

function buildAgentJobWork(suppliedPath: string, workflowRunId: string, agentJobId: string): RenderedWorkItem {
  return {
    workflowRunId,
    workId: "agent:job.1",
    workType: "task",
    stage: "agent-job",
    title: "agent-job dispatch",
    uses: "core/script",
    with: { run: "echo ok" },
    variables: {
      mohist: { runId: workflowRunId },
      workspace: { path: suppliedPath, branch: "agent-branch", changeDir: null },
      project: { id: "project-1", name: "Mohist Local" },
      repository: { name: "master", gitUrl: "https://example.invalid/repo.git", baseBranch: "master" },
    },
    ownerKind: "agent-job",
    agentJobId,
  }
}

function buildRegistry(handler: (ctx: ActionContext) => Promise<ActionResult>): ActionRegistry {
  const registry = new ActionRegistry()
  registry.register("core/script", async (ctx) => handler(ctx))
  registry.register("mohist/rebase", async (ctx) => handler(ctx))
  return registry
}

async function createBareUpstream(root: string): Promise<string> {
  const upstream = join(root, "upstream.git")
  await mkdir(upstream, { recursive: true })
  await exec("git", ["init", "--bare", "--initial-branch=master", upstream])
  // Seed the upstream with an initial commit so the bare repo isn't empty.
  const seed = join(root, "seed")
  await mkdir(seed, { recursive: true })
  await exec("git", ["init", "-q", "--initial-branch=master"], { cwd: seed })
  await exec("git", ["config", "user.email", "test@example.com"], { cwd: seed })
  await exec("git", ["config", "user.name", "Workspace Test"], { cwd: seed })
  await exec("git", ["config", "commit.gpgsign", "false"], { cwd: seed })
  await writeFile(join(seed, "README.md"), "base\n", "utf8")
  await exec("git", ["add", "README.md"], { cwd: seed })
  await exec("git", ["commit", "-m", "init", "-q"], { cwd: seed })
  await exec("git", ["remote", "add", "origin", upstream], { cwd: seed })
  await exec("git", ["push", "-u", "origin", "master", "-q"], { cwd: seed })
  return upstream
}
