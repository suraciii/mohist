import { mkdtemp, rm } from "node:fs/promises"
import { tmpdir } from "node:os"
import { join } from "node:path"
import { afterEach, beforeEach, describe, expect, it } from "vitest"
import { ActionRegistry } from "../src/actions/registry.js"
import { workspacePrepareAction, setWorkspacePrepareExistsCheckerForTest, setWorkspacePrepareGitRunnerForTest } from "../src/actions/workspace-prepare.js"
import { WorkExecutor } from "../src/runtime/executor.js"
import { setExecutorGitRunnerForTest } from "../src/runtime/git-probe.js"
import { verifyOnlyWorkspaceManager } from "./support/workspace-mock.js"
import type { ActionContext, ActionResult, RenderedWorkItem } from "../src/core/types.js"
import type { ServerConnection } from "../src/server/connection.js"

const WORKFLOW_RUN_ID = "wr-workspace-prepare-regression"
const EXPECTED_BRANCH = "mohist/run-wr-workspace-prepare-regression"

let workspacePath: string

beforeEach(async () => {
  workspacePath = await mkdtemp(join(tmpdir(), "mohist-workspace-prepare-regression-"))
})

afterEach(async () => {
  setExecutorGitRunnerForTest(null)
  setWorkspacePrepareGitRunnerForTest(null)
  setWorkspacePrepareExistsCheckerForTest(null)
  await rm(workspacePath, { recursive: true, force: true })
})

function ok(stdout: string) {
  return { success: true, stdout, stderr: "", exitCode: 0, combinedOutput: stdout.trim() }
}

function fail(stderr: string) {
  return { success: false, stdout: "", stderr, exitCode: 1, combinedOutput: stderr }
}

function installExecutorGitProbe() {
  setExecutorGitRunnerForTest(async (_workDir, args) => {
    const command = args.join(" ")
    switch (command) {
      case "rev-parse --abbrev-ref HEAD":
        return ok(`${EXPECTED_BRANCH}\n`)
      case "rev-parse --is-inside-work-tree":
        return ok("true\n")
      case "diff --cached --name-only":
      case "diff --name-only":
      case "ls-files --others --exclude-standard":
        return ok("")
      default:
        return fail(`unexpected executor git call: ${command}`)
    }
  })
}

function installWorkspacePrepareGit(residual: { rebase: boolean }) {
  setWorkspacePrepareExistsCheckerForTest((path) => path.endsWith("rebase-merge") && residual.rebase)
  setWorkspacePrepareGitRunnerForTest(async (_workDir, args) => {
    const command = args.join(" ")
    switch (command) {
      case "rev-parse --git-path rebase-merge":
        return ok(`${workspacePath}/.git/rebase-merge\n`)
      case "rev-parse --git-path rebase-apply":
        return ok(`${workspacePath}/.git/rebase-apply\n`)
      case "rev-parse --git-path MERGE_HEAD":
        return ok(`${workspacePath}/.git/MERGE_HEAD\n`)
      case "rev-parse --git-path CHERRY_PICK_HEAD":
        return ok(`${workspacePath}/.git/CHERRY_PICK_HEAD\n`)
      case "rev-parse HEAD":
        return ok("prepare-head-sha\n")
      case "rev-parse --abbrev-ref HEAD":
        return ok(`${EXPECTED_BRANCH}\n`)
      case "status --porcelain":
        return ok("")
      case "rebase --abort":
        residual.rebase = false
        return ok("Aborted rebase\n")
      default:
        return fail(`unexpected workspace-prepare git call: ${command}`)
    }
  })
}

function buildExecutor(registry: ActionRegistry): WorkExecutor {
  return new WorkExecutor(
    registry,
    verifyOnlyWorkspaceManager({ path: workspacePath, branch: EXPECTED_BRANCH, changeDir: null }),
    { async report() {}, async uploadArtifact() { throw new Error("uploadArtifact should not be called") } } as unknown as ServerConnection,
    {} as never,
    null,
    workspacePath,
  )
}

function work(workId: string, uses: string, overrides: Partial<RenderedWorkItem> = {}): RenderedWorkItem {
  return {
    workflowRunId: WORKFLOW_RUN_ID,
    workId,
    workType: "task",
    stage: "integrate",
    title: workId,
    uses,
    with: {},
    variables: {
      workspace: { path: workspacePath, branch: EXPECTED_BRANCH, changeDir: null },
    },
    ...overrides,
  }
}

function actionContext(item: RenderedWorkItem): ActionContext {
  return {
    ...item,
    variables: item.variables ?? {},
    workDir: workspacePath,
    signal: new AbortController().signal,
    writeVars: async () => {},
  }
}

describe("workspace-prepare stage-boundary dispatch regression", () => {
  it("RerunAfterRebaseFailure_DispatchesWorkspacePrepareFirstAndThenBusinessTask", async () => {
    installExecutorGitProbe()
    const residual = { rebase: false }
    installWorkspacePrepareGit(residual)
    const dispatched: string[] = []

    const registry = new ActionRegistry()
    registry.register("mohist/rebase", async (ctx: ActionContext): Promise<ActionResult> => {
      dispatched.push(ctx.workId)
      residual.rebase = true
      return { error: { code: "conflict", message: "rebase conflict" } }
    })
    registry.register("mohist/workspace-prepare", async (ctx) => {
      dispatched.push(ctx.workId)
      return await workspacePrepareAction(ctx)
    })
    registry.register("core/business", async (ctx) => {
      dispatched.push(ctx.workId)
      expect(residual.rebase).toBe(false)
      return { output: `ran ${ctx.workId}` }
    })

    const executor = buildExecutor(registry)
    const failedRebase = await executor.execute(work("integrate:rebase", "mohist/rebase"), new AbortController().signal)
    expect(failedRebase.status).toBe("failed")
    expect(residual.rebase).toBe(true)

    const prepare = await executor.execute(work("workspace-prepare", "mohist/workspace-prepare"), new AbortController().signal)
    expect(prepare.status).toBe("completed")
    expect(residual.rebase).toBe(false)

    const business = await executor.execute(work("integrate:push", "core/business"), new AbortController().signal)
    expect(business.status).toBe("completed")
    expect(dispatched).toEqual(["integrate:rebase", "workspace-prepare", "integrate:push"])
  })

  it("RecoveryScheduledTasks_AreReturnedWithoutFreshWorkspacePrepare", async () => {
    installExecutorGitProbe()
    const registry = new ActionRegistry()
    registry.register("mohist/rebase", async () => ({ error: { code: "conflict", message: "rebase conflict" } }))
    const executor = buildExecutor(registry)

    const result = await executor.execute(
      work("integrate:rebase", "mohist/rebase", {
        recovery: {
          budget: 1,
          handlers: [
            {
              when: "error.code=conflict",
              retrySelf: true,
              tasks: [{ id: "resolve-rebase-conflicts", title: "Resolve rebase conflicts", uses: "mohist/acp-agent" }],
            },
          ],
        },
        recoveryRemaining: null,
      }),
      new AbortController().signal,
    )
    expect(result.status).toBe("completed")
    expect(result.addTasks?.map((task) => task.id)).toEqual(["resolve-rebase-conflicts", "integrate:rebase"])
    expect(result.addTasks?.some((task) => task.id === "workspace-prepare" || task.uses === "mohist/workspace-prepare")).toBe(false)
  })

  it("WorkspacePrepareProbes_AreLocalOnlyAndCarryNoCommandTimeout", async () => {
    type RecordingGitCall = { command: string; timeoutMs: number | undefined }
    const calls: RecordingGitCall[] = []
    installExecutorGitProbe()
    setWorkspacePrepareExistsCheckerForTest(() => false)
    setWorkspacePrepareGitRunnerForTest(async (_workDir, args, _signal, options) => {
      const command = args.join(" ")
      calls.push({ command, timeoutMs: options?.timeoutMs })
      switch (command) {
        case "rev-parse --git-path rebase-merge":
          return ok(`${workspacePath}/.git/rebase-merge\n`)
        case "rev-parse --git-path rebase-apply":
          return ok(`${workspacePath}/.git/rebase-apply\n`)
        case "rev-parse --git-path MERGE_HEAD":
          return ok(`${workspacePath}/.git/MERGE_HEAD\n`)
        case "rev-parse --git-path CHERRY_PICK_HEAD":
          return ok(`${workspacePath}/.git/CHERRY_PICK_HEAD\n`)
        case "rev-parse HEAD":
          return ok("prepare-head-sha\n")
        case "rev-parse --abbrev-ref HEAD":
          return ok(`${EXPECTED_BRANCH}\n`)
        case "status --porcelain":
          return ok("")
        default:
          return fail(`unexpected workspace-prepare git call: ${command}`)
      }
    })

    await workspacePrepareAction(actionContext(work("workspace-prepare", "mohist/workspace-prepare")))

    // Local-only probes — no network, no per-command timeout.
    for (const call of calls) {
      expect(call.timeoutMs, `git call ${call.command} should have no timeoutMs`).toBeUndefined()
    }
    expect(calls.length).toBeGreaterThan(0)
  })
})
