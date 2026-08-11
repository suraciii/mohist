import { describe, expect, it as vitestIt } from "vitest"
import type { JsonObject, DispatchWorkItem } from "../src/core/types.js"
import { WorkExecutor } from "../src/runtime/executor.js"
import type { GitRunner } from "../src/runtime/git-probe.js"
import type { ServerConnection } from "../src/server/connection.js"
import { verifyOnlyWorkspaceManager } from "./support/workspace-mock.js"
import { defineTestActions, type ActionRegistry } from "./support/action-registry-test.js"
import { withTestRunnerResources } from "./support/test-resources.js"

const nonGitRunner: GitRunner = async () => ({
  success: false,
  stdout: "",
  stderr: "not a git repository",
  exitCode: 128,
  combinedOutput: "not a git repository",
})

const withExecutorResources = <T>(body: (workDir: string) => Promise<T>) =>
  withTestRunnerResources(async () => await body("/virtual/executor-write-vars"), { gitRunner: nonGitRunner })

describe("WorkExecutor result variable effects", () => {
  const it = (name: string, body: (workDir: string) => Promise<void>) => vitestIt(name, () => withExecutorResources(body))

  it("merges result effects and setVars into one patch with setVars precedence", async (workDir) => {
    const signal = new AbortController().signal
    const events: string[] = []
    const patchCalls: Array<{ workflowRunId: string; vars: JsonObject; signal: AbortSignal }> = []
    const connection = {
      async patchRunVars(workflowRunId: string, vars: JsonObject, patchSignal: AbortSignal) {
        events.push("patchRunVars")
        patchCalls.push({ workflowRunId, vars, signal: patchSignal })
      },
    } as Partial<ServerConnection>

    const registry = defineTestActions({
      "test/write-vars": {
        capabilities: ["write-vars"],
        run: async () => {
        events.push("action-start")
        events.push("action-after-write")
        return { output: { checkpoint: "from-output" }, effects: { writeVars: { checkpoint: "from-effect", extra: true } } }
        },
      },
    })

    const executor = new WorkExecutor(
      registry,
       verifyOnlyWorkspaceManager({ path: workDir, branch: null }),
      connection as ServerConnection,
      workDir,
    )

    const result = await executor.execute(buildWork(workDir), signal)

    expect(result.status).toBe("completed")
    expect(events).toEqual(["action-start", "action-after-write", "patchRunVars"])
    expect(patchCalls).toEqual([{ workflowRunId: "wf-write-vars", vars: { checkpoint: "from-output", extra: true }, signal }])
  })
})

function buildWork(workDir: string): DispatchWorkItem {
  return {
    workflowRunId: "wf-write-vars",
    workId: "work-write-vars",
    workType: "task",
    stage: "check",
    title: "Write runtime vars",
    uses: "test/write-vars",
    with: {},
    setVars: { checkpoint: "output.checkpoint" },
     variables: { workspace: { path: workDir, branch: null } },
  }
}
