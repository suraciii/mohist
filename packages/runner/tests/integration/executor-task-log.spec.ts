import { describe, expect, it as vitestIt, vi } from "vitest"
import { createDefaultRegistry } from "../../src/actions/registry.js"
import type { DispatchWorkItem, WorkItemResult } from "../../src/core/types.js"
import { WorkExecutor } from "../../src/runtime/executor.js"
import type { GitRunner } from "../../src/runtime/git-probe.js"
import { TaskLogCollector } from "../../src/runtime/task-log.js"
import type { WorkspaceManager } from "../../src/runtime/workspace.js"
import { verifyOnlyWorkspaceManager } from "../support/workspace-mock.js"
import { FakeProcessSpawner } from "../support/fake-process.js"
import { withTestRunnerResources } from "../support/test-resources.js"

const nonGitRunner: GitRunner = async () => ({
  success: false,
  stdout: "",
  stderr: "not a git repository",
  exitCode: 128,
  combinedOutput: "not a git repository",
})

const withTaskLogResources = <T>(body: (workDir: string, processSpawner: FakeProcessSpawner) => Promise<T>) => {
  const processSpawner = new FakeProcessSpawner()
  return withTestRunnerResources(async () => await body("/virtual/task-log-integration", processSpawner), {
    gitRunner: nonGitRunner,
    processSpawner: processSpawner.spawn,
  })
}

function buildExecutor(workDir: string, workspaceManager: WorkspaceManager = verifyOnlyWorkspaceManager({ path: workDir, branch: null })): WorkExecutor {
  return new WorkExecutor(
    createDefaultRegistry(),
    workspaceManager,
    {} as never,
    workDir,
    () => new Date("2026-07-01T00:00:00.000Z"),
  )
}

function buildWork(workDir: string, overrides: Partial<DispatchWorkItem> = {}): DispatchWorkItem {
  return {
    workflowRunId: "wf-task-log-integration",
    workId: "task-log-process-output",
    workType: "task",
    title: "Task-log process output",
    uses: "core/process",
    with: {},
   variables: { workspace: { path: workDir, branch: null } },
    ...overrides,
  }
}

async function runWith(workDir: string, work: DispatchWorkItem): Promise<{ result: WorkItemResult; collector: TaskLogCollector }> {
  const collector = new TaskLogCollector()
  const execution = await buildExecutor(workDir).executeWithLog(work, new AbortController().signal, collector)
  return { result: execution.result, collector: execution.collector }
}

describe("WorkExecutor task-log process forwarding", () => {
  const it = (name: string, body: (workDir: string, processSpawner: FakeProcessSpawner) => Promise<void>) => vitestIt(name, () => withTaskLogResources(body))

  it("ForwardsCoreProcessStdoutAndStderrToTaskLogSink", async (workDir, processSpawner) => {
    const running = runWith(workDir, buildWork(workDir, {
      uses: "core/process",
      with: {
        command: "fake-command",
        args: [],
      },
    }))
    await vi.waitFor(() => expect(processSpawner.children).toHaveLength(1))
    const child = processSpawner.children[0]!
    child.writeStdout("process-out\n")
    child.writeStderr("process-err\n")
    child.close(0)
    const { result, collector } = await running

    expect(result.status).toBe("completed")
    const entries = collector.flush().entries.filter((entry) => entry.source === "action:process")
    expect(entries.map((entry) => entry.text).sort()).toEqual(["process-err", "process-out"])
  })

  it("ForwardsCoreScriptStdoutAndStderrToTaskLogSink", async (workDir, processSpawner) => {
    const running = runWith(workDir, buildWork(workDir, {
      uses: "core/script",
      with: {
        shell: "fake-shell",
        run: "ignored",
      },
    }))
    await vi.waitFor(() => expect(processSpawner.children).toHaveLength(1))
    const child = processSpawner.children[0]!
    child.writeStdout("script-out\n")
    child.writeStderr("script-err\n")
    child.close(0)
    const { result, collector } = await running

    expect(result.status).toBe("completed")
    const entries = collector.flush().entries.filter((entry) => entry.source === "action:script")
    expect(entries.map((entry) => entry.text).sort()).toEqual(["script-err", "script-out"])
  })
})
