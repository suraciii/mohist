import { mkdtemp, rm } from "node:fs/promises"
import { tmpdir } from "node:os"
import { join } from "node:path"
import { afterEach, beforeEach, describe, expect, it } from "vitest"
import { createDefaultRegistry } from "../../src/actions/registry.js"
import type { RenderedWorkItem, WorkItemResult } from "../../src/core/types.js"
import { WorkExecutor } from "../../src/runtime/executor.js"
import { setExecutorGitRunnerForTest, type GitRunner } from "../../src/runtime/git-probe.js"
import { TaskLogCollector } from "../../src/runtime/task-log.js"
import type { WorkspaceManager } from "../../src/runtime/workspace.js"
import { verifyOnlyWorkspaceManager } from "../support/workspace-mock.js"

let workDir: string

const nonGitRunner: GitRunner = async () => ({
  success: false,
  stdout: "",
  stderr: "not a git repository",
  exitCode: 128,
  combinedOutput: "not a git repository",
})

beforeEach(async () => {
  workDir = await mkdtemp(join(tmpdir(), "mohist-task-log-executor-"))
  setExecutorGitRunnerForTest(nonGitRunner)
})

afterEach(async () => {
  await rm(workDir, { recursive: true, force: true })
  setExecutorGitRunnerForTest(null)
})

function buildExecutor(workspaceManager: WorkspaceManager = verifyOnlyWorkspaceManager({ path: workDir, branch: null, changeDir: null })): WorkExecutor {
  return new WorkExecutor(
    createDefaultRegistry(),
    workspaceManager,
    {} as never,
    {} as never,
    null,
    workDir,
    () => new Date("2026-07-01T00:00:00.000Z"),
  )
}

function buildWork(overrides: Partial<RenderedWorkItem> = {}): RenderedWorkItem {
  return {
    workflowRunId: "wf-task-log-integration",
    workId: "task-log-process-output",
    workType: "task",
    title: "Task-log process output",
    uses: "core/process",
    with: {},
    variables: { workspace: { path: workDir, branch: null, changeDir: null } },
    ...overrides,
  }
}

async function runWith(work: RenderedWorkItem): Promise<{ result: WorkItemResult; collector: TaskLogCollector }> {
  const collector = new TaskLogCollector()
  const execution = await buildExecutor().executeWithLog(work, new AbortController().signal, collector)
  return { result: execution.result, collector: execution.collector }
}

describe("WorkExecutor task-log process forwarding", () => {
  it("ForwardsCoreProcessStdoutAndStderrToTaskLogSink", async () => {
    const { result, collector } = await runWith(buildWork({
      uses: "core/process",
      with: {
        command: process.execPath,
        args: ["-e", "process.stdout.write('process-out\\n'); process.stderr.write('process-err\\n')"],
      },
    }))

    expect(result.status).toBe("completed")
    const entries = collector.flush().entries.filter((entry) => entry.source === "action:process")
    expect(entries.map((entry) => entry.text).sort()).toEqual(["process-err", "process-out"])
  })

  it("ForwardsCoreScriptStdoutAndStderrToTaskLogSink", async () => {
    const { result, collector } = await runWith(buildWork({
      uses: "core/script",
      with: {
        shell: process.execPath,
        run: "process.stdout.write('script-out\\n'); process.stderr.write('script-err\\n')",
      },
    }))

    expect(result.status).toBe("completed")
    const entries = collector.flush().entries.filter((entry) => entry.source === "action:script")
    expect(entries.map((entry) => entry.text).sort()).toEqual(["script-err", "script-out"])
  })
})
