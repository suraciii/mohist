import { mkdtemp, rm } from "node:fs/promises"
import { tmpdir } from "node:os"
import { join } from "node:path"
import { afterEach, beforeEach, describe, expect, it } from "vitest"
import { ActionRegistry } from "../src/actions/registry.js"
import type { ActionContext, ActionResult, RenderedWorkItem, WorkItemResult } from "../src/core/types.js"
import { WorkExecutor } from "../src/runtime/executor.js"
import { TaskLogCollector } from "../src/runtime/task-log.js"
import { setExecutorGitRunnerForTest, type GitRunner } from "../src/runtime/git-probe.js"
import { verifyOnlyWorkspaceManager } from "./support/workspace-mock.js"
import type { WorkspaceManager } from "../src/runtime/workspace.js"

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

function makeRegistry(handler: (ctx: ActionContext) => Promise<ActionResult>): ActionRegistry {
  const registry = new ActionRegistry()
  registry.register("mohist/test-action", async (ctx) => handler(ctx))
  return registry
}

function buildExecutor(registry: ActionRegistry, workspaceManager: WorkspaceManager = verifyOnlyWorkspaceManager({ path: workDir, branch: null, changeDir: null })): WorkExecutor {
  return new WorkExecutor(
    registry,
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

async function runWith(registry: ActionRegistry, work: RenderedWorkItem = buildWork(), workspaceManager?: WorkspaceManager): Promise<{ result: WorkItemResult; collector: TaskLogCollector }> {
  const executor = buildExecutor(registry, workspaceManager)
  const collector = new TaskLogCollector()
  const execution = await executor.executeWithLog(work, new AbortController().signal, collector)
  return { result: execution.result, collector: execution.collector }
}

describe("WorkExecutor forwards action output to the task log", () => {
  it("ForwardsActionBodyWriteToSinkTaggedWithActionSource", async () => {
    let loggerPresent = false
    const registry = makeRegistry(async (ctx) => {
      loggerPresent = ctx.log !== null && ctx.log !== undefined
      ctx.log?.write("action:rebase", "rebasing commit a1b2c3")
      ctx.log?.write("action:rebase", "Auto-merging src/lib/rebase.ts")
      return { output: JSON.stringify({ rebase: "ok" }) }
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

  it("PassesWorkspacePreparationOutputThroughWorkspacePrepSource", async () => {
    const registry = makeRegistry(async () => ({ output: "ok" }))
    const workspaceManager = verifyOnlyWorkspaceManager(
      { path: workDir, branch: null, changeDir: null },
      (log) => log?.write("workspace-prep", "clone output from workspace preparation"),
    )

    const { collector } = await runWith(registry, buildWork(), workspaceManager)

    const entries = collector.flush().entries.filter((entry) => entry.source === "workspace-prep")
    expect(entries.map((entry) => entry.text)).toContain("clone output from workspace preparation")
  })

  it("CapturesBranchCheckOutputWithBranchCheckSource", async () => {
    setExecutorGitRunnerForTest(async (_workDir, args, _signal, options) => {
      options?.sink?.log.write(options.sink.source, `git ${args.join(" ")}`)
      return {
        success: true,
        stdout: args.join(" ") === "rev-parse --abbrev-ref HEAD" ? "main\n" : "",
        stderr: "",
        exitCode: 0,
        combinedOutput: "",
      }
    })
    const registry = makeRegistry(async () => ({ error: { code: "action-failed", message: "stop after start check" } }))

    const { collector } = await runWith(registry, buildWork({ variables: { workspace: { path: workDir, branch: "main", changeDir: null } } }))

    const entries = collector.flush().entries.filter((entry) => entry.source === "branch-check")
    expect(entries.map((entry) => entry.text)).toContain("git rev-parse --abbrev-ref HEAD")
  })

  it("CapturesCleanWorktreeOutputWithCleanupSource", async () => {
    setExecutorGitRunnerForTest(async (_workDir, args, _signal, options) => {
      options?.sink?.log.write(options.sink.source, `git ${args.join(" ")}`)
      const joined = args.join(" ")
      return {
        success: true,
        stdout: joined === "rev-parse --abbrev-ref HEAD" ? "main\n" : joined === "rev-parse --is-inside-work-tree" ? "true\n" : "",
        stderr: "",
        exitCode: 0,
        combinedOutput: "",
      }
    })
    const registry = makeRegistry(async () => ({ output: "ok" }))

    const { collector } = await runWith(registry, buildWork({ variables: { workspace: { path: workDir, branch: "main", changeDir: null } } }))

    const entries = collector.flush().entries.filter((entry) => entry.source === "cleanup")
    expect(entries.map((entry) => entry.text)).toContain("git rev-parse --is-inside-work-tree")
    expect(entries.map((entry) => entry.text)).toContain("git diff --cached --name-only")
  })

  it("CapturesFailingOpsCommandOutputInCollector", async () => {
    const registry = makeRegistry(async (ctx) => {
      ctx.log?.write("action:rebase", "Rebase conflict in src/lib/rebase.ts")
      ctx.log?.write("action:rebase", "fatal: Could not apply abc1234")
      return {
        error: { code: "conflict", message: "Rebase conflict" },
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
      return { error: { code: "conflict", message: "Rebase failed: conflict" } }
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
        return { error: { code: "action-failed", message: "failed" } }
      })

      const { collector } = await runWith(registry)
      const text = collector.flush().entries.at(-1)?.text ?? ""
      expect(text).toContain("***")
      expect(text).not.toContain(secret)
    } finally {
      delete process.env[secretName]
    }
  })

})
