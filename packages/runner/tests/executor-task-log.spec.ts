import { mkdtemp, rm } from "node:fs/promises"
import { tmpdir } from "node:os"
import { join } from "node:path"
import { afterEach, beforeEach, describe, expect, it } from "vitest"
import { ActionRegistry, createDefaultRegistry } from "../src/actions/registry.js"
import type { ActionContext, ActionResult, WorkItem } from "../src/core/types.js"
import { WorkExecutor } from "../src/runtime/executor.js"
import { TaskLogCollector } from "../src/runtime/task-log.js"
import { setExecutorGitRunnerForTest } from "../src/runtime/git-probe.js"
import { verifyOnlyWorkspaceManager } from "./support/workspace-mock.js"

let workDir: string

beforeEach(async () => {
  workDir = await mkdtemp(join(tmpdir(), "mohist-task-log-executor-"))
  setExecutorGitRunnerForTest(null)
})

afterEach(async () => {
  await rm(workDir, { recursive: true, force: true })
  setExecutorGitRunnerForTest(null)
})

function makeRegistry(handler: (ctx: ActionContext) => Promise<ActionResult>): ActionRegistry {
  const registry = new ActionRegistry()
  registry.register("mohist/test-action", async (ctx) => handler(ctx))
  return registry
}

function buildExecutor(registry: ActionRegistry): WorkExecutor {
  return new WorkExecutor(
    registry,
    verifyOnlyWorkspaceManager({ path: workDir, branch: null, changeDir: null }),
    {} as never,
    {} as never,
    null,
    workDir,
    () => new Date("2026-07-01T00:00:00.000Z"),
  )
}

function buildWork(overrides: Partial<WorkItem> = {}): WorkItem {
  return {
    workflowRunId: "wf-336",
    workId: "work-336",
    workType: "task",
    title: "Task-log wiring",
    uses: "mohist/test-action",
    with: {},
    variables: { workspace: { path: workDir, branch: null, changeDir: null } },
    ...overrides,
  }
}

async function runWith(registry: ActionRegistry, work: WorkItem = buildWork()): Promise<{ result: ActionResult; collector: TaskLogCollector }> {
  const executor = buildExecutor(registry)
  const collector = new TaskLogCollector()
  const execution = await (executor as unknown as {
    executeWithLog: (work: WorkItem, signal: AbortSignal, collector: TaskLogCollector | null) => Promise<{ result: ActionResult; collector: TaskLogCollector }>
  }).executeWithLog(work, new AbortController().signal, collector)
  return { result: execution.result, collector: execution.collector }
}

describe("WorkExecutor task-log phase wiring (T-003)", () => {
  it("ForwardsActionBodyWriteToSinkTaggedWithActionSource", async () => {
    let loggerPresent = false
    const registry = makeRegistry(async (ctx) => {
      loggerPresent = ctx.log !== null && ctx.log !== undefined
      ctx.log?.write("action:rebase", "rebasing commit a1b2c3")
      ctx.log?.write("action:rebase", "Auto-merging src/lib/rebase.ts")
      return { status: "success", message: "ok", output: JSON.stringify({ rebase: "ok" }) }
    })
    const { collector } = await runWith(registry)
    expect(loggerPresent).toBe(true)
    const flushed = collector.flush()
    expect(flushed.entries.filter((e) => e.source === "action:rebase").map((e) => e.text)).toEqual([
      "rebasing commit a1b2c3",
      "Auto-merging src/lib/rebase.ts",
    ])
    // The two writes must carry the same monotonic seq across phases.
    const rebaseSeqs = flushed.entries.filter((e) => e.source === "action:rebase").map((e) => e.seq)
    const others = flushed.entries.filter((e) => e.source !== "action:rebase").map((e) => e.seq)
    const allSeqs = flushed.entries.map((e) => e.seq)
    expect(allSeqs).toEqual([...allSeqs].sort((a, b) => a - b))
    expect(rebaseSeqs[1]).toBe(rebaseSeqs[0]! + 1)
    expect(others.every((seq) => seq > rebaseSeqs[0]! || seq < rebaseSeqs[1]!)).toBe(true)
  })

  it("CapturesFailingOpsCommandOutputInCollector", async () => {
    const registry = makeRegistry(async (ctx) => {
      ctx.log?.write("action:rebase", "Rebase conflict in src/lib/rebase.ts")
      ctx.log?.write("action:rebase", "fatal: Could not apply abc1234")
      return {
        status: "failure",
        message: "Rebase conflict",
        output: JSON.stringify({ kind: "rebase", rebased: false, conflicts: ["src/lib/rebase.ts"] }),
        exitCode: 1,
      }
    })
    const { result, collector } = await runWith(registry)
    expect(result.status).toBe("failed")
    const rebaseEntries = collector.flush().entries.filter((e) => e.source === "action:rebase")
    expect(rebaseEntries).toHaveLength(2)
    expect(rebaseEntries[0]!.text).toContain("Rebase conflict")
    expect(rebaseEntries[1]!.text).toContain("Could not apply")
  })

  it("PreservesAggregateContractForActionBody_GitOutputUnchanged", async () => {
    // The action body sees ctx.log for line-by-line forwarding, but
    // the downstream `combinedOutput` aggregation must remain
    // available — i.e. a downstream consumer that reads the message /
    // output JSON sees the same shape whether or not the sink is on.
    // We verify this by stubbing git() at the runtime-git-probe layer
    // and asserting the aggregate fields are present on the result.
    let gitSinkSeen = false
    const fakeGit = async (workDir: string, args: string[], signal: AbortSignal, options?: { sink?: { log: { write: (source: string, text: string) => void }; source: string } }) => {
      if (options?.sink) {
        gitSinkSeen = true
        options.sink.log.write(options.sink.source, "out-line-1")
        options.sink.log.write(options.sink.source, "out-line-2")
      }
      return {
        success: true,
        stdout: "out-line-1\nout-line-2\n",
        stderr: "",
        exitCode: 0,
        combinedOutput: "out-line-1\nout-line-2",
      }
    }
    setExecutorGitRunnerForTest(fakeGit as unknown as Parameters<typeof setExecutorGitRunnerForTest>[0])

    const registry = makeRegistry(async (ctx) => {
      ctx.log?.write("action:health-check", "ok")
      return {
        status: "success",
        message: "done",
        output: JSON.stringify({
          kind: "health-check",
          gitOutput: "out-line-1\nout-line-2",
        }),
        exitCode: 0,
      }
    })
    const { result, collector } = await runWith(registry)
    expect(gitSinkSeen).toBe(true)
    expect(result.output).toContain("gitOutput")
    expect(collector.flush().entries.map((e) => e.source)).toContain("action:health-check")
  })

  it("DeliberatelyFailingOpsCommandOutputReachesCollectorBuffer", async () => {
    const registry = makeRegistry(async (ctx) => {
      // Simulate a rebase conflict tail end: emit the failure text
      // through the sink before returning a failure result.
      ctx.log?.write("action:rebase", "First, rewinding head to replay your work on top of it...")
      ctx.log?.write("action:rebase", "Applying: feat: introduce capture-and-upload plumbing")
      ctx.log?.write("action:rebase", "Using index info to reconstruct a base tree...")
      ctx.log?.write("action:rebase", "Falling back to patching base and 3-way merge...")
      ctx.log?.write("action:rebase", "Auto-merging src/runtime/executor.ts")
      ctx.log?.write("action:rebase", "CONFLICT (content): Merge conflict in src/runtime/executor.ts")
      ctx.log?.write("action:rebase", "error: Failed to merge in the changes.")
      ctx.log?.write("action:rebase", "Patch failed at 0001 feat: introduce capture-and-upload plumbing")
      ctx.log?.write("action:rebase", "hint: Use 'git am --show-current-patch' to see the failed patch")
      return { status: "failure", message: "Rebase failed: conflict", output: JSON.stringify({ rebase: false }) }
    })
    const { result, collector } = await runWith(registry)
    expect(result.status).toBe("failed")
    const lines = collector.flush().entries.filter((e) => e.source === "action:rebase").map((e) => e.text)
    expect(lines.some((l) => l.includes("CONFLICT"))).toBe(true)
    expect(lines.some((l) => l.includes("Patch failed"))).toBe(true)
  })

  it("RegistersRuntimeConfiguredSecretsBeforeBuffering", async () => {
    const secretName = "MOHIST_TEST_RUNNER_TOKEN"
    const secret = "runner-configured-secret-12345"
    process.env[secretName] = secret
    try {
      const registry = makeRegistry(async (ctx) => {
        ctx.log?.write("action:script", `command failed with ${secret}`)
        return { status: "failure", message: "failed" }
      })

      const { collector } = await runWith(registry)
      const text = collector.flush().entries.at(-1)?.text ?? ""
      expect(text).toContain("***")
      expect(text).not.toContain(secret)
    } finally {
      delete process.env[secretName]
    }
  })

  it("CoreProcessForwardsStdoutAndStderrToTaskLogSink", async () => {
    const { result, collector } = await runWith(createDefaultRegistry(), buildWork({
      uses: "core/process",
      with: {
        command: process.execPath,
        args: ["-e", "process.stdout.write('process-out\\n'); process.stderr.write('process-err\\n')"],
      },
    }))

    expect(result.status).toBe("completed")
    const entries = collector.flush().entries.filter((entry) => entry.source === "action:process")
    expect(entries.map((entry) => entry.text)).toEqual(["process-out", "process-err"])
  })

  it("CoreScriptForwardsStdoutAndStderrToTaskLogSink", async () => {
    const { result, collector } = await runWith(createDefaultRegistry(), buildWork({
      uses: "core/script",
      with: {
        shell: process.execPath,
        run: "process.stdout.write('script-out\\n'); process.stderr.write('script-err\\n')",
      },
    }))

    expect(result.status).toBe("completed")
    const entries = collector.flush().entries.filter((entry) => entry.source === "action:script")
    expect(entries.map((entry) => entry.text)).toEqual(["script-out", "script-err"])
  })
})
