import { describe, expect, it as vitestIt } from "vitest"
import { workspacePrepareAction } from "../src/actions/workspace-prepare.js"
import { WorkExecutor } from "../src/runtime/executor.js"
import type { GitRunner } from "../src/runtime/git-probe.js"
import type { RunnerResourceContext } from "../src/system/filesystem.js"
import { verifyOnlyWorkspaceManager } from "./support/workspace-mock.js"
import type { ActionResult, JsonObject, DispatchWorkItem } from "../src/core/types.js"
import type { ActionTestContext as ActionContext } from "./support/action-test-context.js"
import type { ActionHost } from "../src/actions/host.js"
import type { ServerConnection } from "../src/server/connection.js"
import { defineTestActions, type ActionRegistry } from "./support/action-registry-test.js"
import { callAction } from "./support/call-action.js"
import { withTestRunnerResources } from "./support/test-resources.js"

const WORKFLOW_RUN_ID = "wr-workspace-prepare-regression"
const EXPECTED_BRANCH = "mohist/run-wr-workspace-prepare-regression"

const workspacePath = "/virtual/workspace-prepare-regression"

function withResources<T>(resources: Omit<RunnerResourceContext, "fileSystem">, body: () => Promise<T>): Promise<T> {
  return withTestRunnerResources(async () => await body(), resources)
}

function ok(stdout: string) {
  return { success: true, stdout, stderr: "", exitCode: 0, combinedOutput: stdout.trim() }
}

function fail(stderr: string) {
  return { success: false, stdout: "", stderr, exitCode: 1, combinedOutput: stderr }
}

function installExecutorGitProbe(): GitRunner {
  return async (_workDir, args) => {
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
  }
}

function installWorkspacePrepareGit(residual: { rebase: boolean }): GitRunner {
  return async (_workDir, args) => {
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
  }
}

function buildExecutor(registry: ActionRegistry): WorkExecutor {
  return new WorkExecutor(
    registry,
     verifyOnlyWorkspaceManager({ path: workspacePath, branch: EXPECTED_BRANCH }),
    { async report() {}, async uploadArtifact() { throw new Error("uploadArtifact should not be called") } } as unknown as ServerConnection,
    workspacePath,
  )
}

function work(workId: string, uses: string, overrides: Partial<DispatchWorkItem> = {}): DispatchWorkItem {
  return {
    workflowRunId: WORKFLOW_RUN_ID,
    workId,
    workType: "task",
    stage: "integrate",
    title: workId,
    uses,
    with: uses === "mohist/workspace-prepare" ? { expectedBranch: EXPECTED_BRANCH } : {},
    variables: {
       workspace: { path: workspacePath, branch: EXPECTED_BRANCH },
    },
    ...overrides,
  }
}

function actionContext(item: DispatchWorkItem): ActionContext {
  return {
    ...item,
    variables: item.variables ?? {},
    workDir: workspacePath,
    signal: new AbortController().signal,
    writeVars: async () => {},
  }
}

describe("workspace-prepare stage-boundary dispatch regression", () => {
  vitestIt("RerunAfterRebaseFailure_DispatchesWorkspacePrepareFirstAndThenBusinessTask", async () => {
    const residual = { rebase: false }
    const executorGitRunner = installExecutorGitProbe()
    const workspacePrepareGitRunner = installWorkspacePrepareGit(residual)

    await withResources({
      gitRunner: executorGitRunner,
      workspacePrepareGitRunner,
      workspacePrepareExistsChecker: (path) => path.endsWith("rebase-merge") && residual.rebase,
    }, async () => {
      const dispatched: string[] = []
      const registry = defineTestActions({
        "mohist/rebase": {
          run: async (_inputs: JsonObject, _host: ActionHost): Promise<ActionResult> => {
            dispatched.push("integrate:rebase")
            residual.rebase = true
            return { error: { code: "conflict", message: "rebase conflict" } }
          },
          errors: [{ code: "conflict" }],
        },
        "mohist/workspace-prepare": {
          inputs: { expectedBranch: { types: ["string"], required: true } },
          run: async (inputs: JsonObject, host: ActionHost) => {
            dispatched.push("workspace-prepare")
            return await workspacePrepareAction(inputs, host)
          },
        },
        "core/business": async (_inputs: JsonObject, _host: ActionHost) => {
          dispatched.push("integrate:push")
          expect(residual.rebase).toBe(false)
          return { output: { ran: "integrate:push" } }
        },
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
  })

  vitestIt("RecoveryScheduledTasks_AreReturnedWithoutFreshWorkspacePrepare", async () => {
    await withResources({ gitRunner: installExecutorGitProbe() }, async () => {
      const registry = defineTestActions({
        "mohist/rebase": {
          run: async (_inputs: JsonObject, _host: ActionHost) => ({ error: { code: "conflict", message: "rebase conflict" } }),
          errors: [{ code: "conflict" }],
        },
      })
      const executor = buildExecutor(registry)

      const result = await executor.execute(
        work("integrate:rebase", "mohist/rebase", {
          recovery: {
            budget: 1,
            handlers: [
              {
                when: "error.code=conflict",
                retrySelf: true,
                tasks: [{ id: "resolve-rebase-conflicts", title: "Resolve rebase conflicts", uses: "mohist/opencode" }],
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
  })

  vitestIt("WorkspacePrepareProbes_AreLocalOnlyAndCarryNoCommandTimeout", async () => {
    type RecordingGitCall = { command: string; timeoutMs: number | undefined }
    const calls: RecordingGitCall[] = []
    const executorGitRunner = installExecutorGitProbe()
    const workspacePrepareGitRunner: GitRunner = async (_workDir, args, _signal, options) => {
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
    }

    await withResources({
      gitRunner: executorGitRunner,
      workspacePrepareGitRunner,
      workspacePrepareExistsChecker: () => false,
    }, async () => {
      await callAction(workspacePrepareAction, actionContext(work("workspace-prepare", "mohist/workspace-prepare")))

      for (const call of calls) {
        expect(call.timeoutMs, `git call ${call.command} should have no timeoutMs`).toBeUndefined()
      }
      expect(calls.length).toBeGreaterThan(0)
    })
  })
})
