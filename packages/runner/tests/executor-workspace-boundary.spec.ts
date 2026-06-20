import { execFile } from "node:child_process"
import { mkdir, mkdtemp, rm, writeFile } from "node:fs/promises"
import { tmpdir } from "node:os"
import { join } from "node:path"
import { promisify } from "node:util"
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { WorkExecutor } from "../src/runtime/executor.js"
import { ActionRegistry } from "../src/actions/registry.js"
import { WorkspaceManager } from "../src/runtime/workspace.js"
import type { ActionContext, ActionResult, WorkItem, WorkItemResult } from "../src/core/types.js"
import type { ServerConnection } from "../src/server/connection.js"

const exec = promisify(execFile)

describe("T-002 once-per-run materialization across stages", () => {
  let root: string
  let repo: string
  let runnerRoot: string
  let connection: Pick<ServerConnection, "uploadArtifact" | "report">

  beforeEach(async () => {
    root = await mkdtemp(join(tmpdir(), "mohist-t002-cross-stage-"))
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

  it("StartupMaterializesBeforePlanThenDispatchesAreVerifyOnly", async () => {
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

    // Use a no-op action registered as `core/script` so the test
    // does not spawn real ACP processes. The precheck +
    // materialize/verify contract is independent of the action.
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
      const startup = await manager.materialize(buildWork(repo, "wr-cross-stage", "issue-cross-stage", "plan", "plan:write"), signal)
      const workspacePath = join(runnerRoot, "mohist-local", "workspaces", "issue-9")
      expect(startup.path).toBe(workspacePath)

      const cloneCallsAtStartup = gitCalls.filter((call) => call.startsWith("git clone ") && call.includes(repo))
      expect(cloneCallsAtStartup).toHaveLength(1)

      gitCalls.length = 0

      const plan = await executor.execute(buildWork(repo, "wr-cross-stage", "issue-cross-stage", "plan", "plan:write"), signal)
      if (plan.status !== "completed") {
        throw new Error(`Plan dispatch did not complete: status=${plan.status} message=${plan.message} output=${plan.output}`)
      }

      const build = await executor.execute(buildWork(repo, "wr-cross-stage", "issue-cross-stage", "build", "build:agent"), signal)
      expect(build.status).toBe("completed")

      const check = await executor.execute(buildWork(repo, "wr-cross-stage", "issue-cross-stage", "check", "check:verdict"), signal)
      expect(check.status).toBe("completed")

      const prepare = await executor.execute(buildWork(repo, "wr-cross-stage", "issue-cross-stage", "integrate", "integrate:prepare"), signal)
      expect(prepare.status).toBe("completed")

      const retry = await executor.execute(buildWork(repo, "wr-cross-stage", "issue-cross-stage", "integrate", "integrate:prepare"), signal)
      expect(retry.status).toBe("completed")

      // The action handler saw all five dispatches.
      expect(handlerCalls).toEqual([
        "task:plan",
        "task:build",
        "task:check",
        "task:integrate",
        "task:integrate",
      ])

      const remoteCloneCalls = gitCalls.filter((call) =>
        call.startsWith("git clone ") && call.includes(repo),
      )
      expect(remoteCloneCalls).toHaveLength(0)
      const cachePath = join(runnerRoot, "repos", "project-1", "master")
      const origin = (await realRunCommand("git", ["-C", cachePath, "remote", "get-url", "origin"], ".", signal)).stdout.trim()
      expect(origin).toBe(repo)
      const { readFile: rf } = await import("node:fs/promises")
      const marker = JSON.parse(await rf(join(workspacePath, ".mohist", "workspace.json"), "utf8"))
      expect(marker).toMatchObject({
        issueId: "issue-cross-stage",
        issueNumber: 9,
        workflowRunId: "wr-cross-stage",
      })
    } finally {
      spy.mockRestore()
    }
  })

  it("IntegrateStageRetry_AfterMarkerDeleted_RunnerReportsWorkspaceCorruptAndDoesNotRecover", async () => {
    // Per the spec: a dispatch against a workspace whose marker is
    // missing or unreadable is reported as a `workspace-corrupt`
    // infrastructure failure; it is NEVER recovered by re-cloning
    // (the start-boundary precheck routes through verify() in this
    // case and refuses to fall back to materialize()). Simulate an
    // integrate:prepare dispatch whose marker has been deleted (e.g.
    // a cleanup process removed `.mohist/workspace.json`) and assert
    // the runner surfaces workspace-corrupt without re-cloning the
    // upstream.
    //
    // The directory-presence signal in `planResolution` ensures the
    // runner takes the verify path here: the workspace directory
    // still exists (only the marker is gone), so the precheck calls
    // verify() which surfaces workspace-corrupt rather than
    // silently re-materializing.
    const manager = new WorkspaceManager(runnerRoot)
    const signal = new AbortController().signal

    await manager.materialize(buildWork(repo, "wr-corrupt", "issue-corrupt", "plan", "plan:write"), signal)

    // Delete the marker to simulate the workspace becoming unbound.
    // The workspace path is `<runnerRoot>/<projectName>/workspaces/issue-<number>`;
    // the test uses issueNumber=9 so the path is `issue-9`.
    const workspacePath = join(runnerRoot, "mohist-local", "workspaces", "issue-9")
    await rm(join(workspacePath, ".mohist", "workspace.json"), { force: true })

    // Spy on git invocations on the next dispatch.
    const processMod = await import("../src/system/process.js")
    const realRunCommand = processMod.runCommand
    const gitCalls: string[] = []
    const spy = vi.spyOn(processMod, "runCommand").mockImplementation(async (cmd, args, cwd, sig) => {
      gitCalls.push(`${cmd} ${args.join(" ")}`)
      return await realRunCommand(cmd, args, cwd, sig)
    })

    try {
      // A second dispatch on the now-missing marker must surface
      // workspace-corrupt and the action MUST NOT run.
      let actionInvoked = false
      const handler: (ctx: ActionContext) => Promise<ActionResult> = async () => {
        actionInvoked = true
        return { status: "success", message: "should not run" }
      }
      const registry = new ActionRegistry()
      registry.register("core/script", async (ctx) => handler(ctx))
      const exec = new WorkExecutor(
        registry,
        manager,
        connection as never,
        {} as never,
        null,
        runnerRoot,
      )
      const second = await exec.execute(
        buildWork(repo, "wr-corrupt", "issue-corrupt", "integrate", "integrate:prepare"),
        signal,
      )

      // The dispatch fails as workspace-corrupt. The action must
      // NOT have run.
      if (actionInvoked) {
        throw new Error(`Action invoked despite marker deletion. status=${second.status} message=${second.message} output=${second.output}`)
      }
      expect(actionInvoked).toBe(false)
      expect(second.status).toBe("failed")
      expect(second.message).toMatch(/workspace materialization failure.*workspace-corrupt/)
      const evidence = JSON.parse(second.output ?? "{}")
      expect(evidence).toMatchObject({
        kind: "workspace-corrupt",
        workspacePath,
      })

      // Crucially, the runner MUST NOT have re-cloned the upstream
      // repository to recover. No `git clone` against the upstream
      // gitUrl appears in the spy log.
      const remoteCloneCalls = gitCalls.filter((call) =>
        call.startsWith("git clone ") && call.includes(repo),
      )
      expect(remoteCloneCalls).toHaveLength(0)
    } finally {
      spy.mockRestore()
    }
  })

  it("AgentJobDispatch_SkipsMaterializeAndVerify_ResolvesWorkspaceFromVariables", async () => {
    // Per the spec, agent-job owner-kind dispatches MUST resolve
    // their workspace from variables without invoking materialize()
    // or verify(). Pin the contract: a recordable WorkspaceManager
    // mock sees zero materialize/verify/ensure calls during an
    // agent-job dispatch.
    const recorded = { materialize: 0, verify: 0, ensure: 0 }
    const recordingManager = {
      async materialize() {
        recorded.materialize += 1
        throw new Error("materialize must not be called for agent-job dispatches")
      },
      async verify() {
        recorded.verify += 1
        throw new Error("verify must not be called for agent-job dispatches")
      },
      async ensure() {
        recorded.ensure += 1
        throw new Error("ensure must not be called for agent-job dispatches")
      },
      async planResolution() {
        throw new Error("planResolution must not be called for agent-job dispatches")
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
    expect(recorded).toEqual({ materialize: 0, verify: 0, ensure: 0 })
  })
})

// Build a WorkItem for the given stage with `mohist/acp-agent` (a
// representative task action). The agent handler is registered for
// every stage; the executor's start-boundary precheck runs before
// the action, so this lets us drive the test through the real
// precheck + executeOne pipeline.
function buildWork(repo: string, workflowRunId: string, issueId: string, stage: string, workId: string): WorkItem {
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

function buildAgentJobWork(suppliedPath: string, workflowRunId: string, agentJobId: string): WorkItem {
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
  // Seed the upstream with an initial commit so the bare repo isn't
  // empty (an empty bare repo would still clone but the workspace
  // HEAD would be unborn; we want a clean real-world shape).
  const seed = join(root, "seed")
  await mkdir(seed, { recursive: true })
  await exec("git", ["init", "-q", "--initial-branch=master"], { cwd: seed })
  await exec("git", ["config", "user.email", "test@example.com"], { cwd: seed })
  await exec("git", ["config", "user.name", "T-002 Test"], { cwd: seed })
  await exec("git", ["config", "commit.gpgsign", "false"], { cwd: seed })
  await writeFile(join(seed, "README.md"), "base\n", "utf8")
  await exec("git", ["add", "README.md"], { cwd: seed })
  await exec("git", ["commit", "-m", "init", "-q"], { cwd: seed })
  await exec("git", ["remote", "add", "origin", upstream], { cwd: seed })
  await exec("git", ["push", "-u", "origin", "master", "-q"], { cwd: seed })
  return upstream
}

async function invokeExecutor(
  manager: WorkspaceManager,
  repo: string,
  workflowRunId: string,
  issueId: string,
  stage: string,
  workId: string,
  connection: Pick<ServerConnection, "uploadArtifact" | "report">,
  signal: AbortSignal,
  runnerRootDir: string,
): Promise<WorkItemResult> {
  const exec = new WorkExecutor(
    buildRegistry(async () => ({ status: "success", message: "ok" })),
    manager,
    connection as never,
    {} as never,
    null,
    runnerRootDir,
  )
  return await exec.execute(buildWork(repo, workflowRunId, issueId, stage, workId), signal)
}
