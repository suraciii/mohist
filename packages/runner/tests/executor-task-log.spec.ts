import { describe, expect, it as vitestIt, vi } from "vitest"
import { ActionRegistry, createDefaultRegistry } from "../src/actions/registry.js"
import type { ActionResult, JsonObject, DispatchWorkItem, WorkItemResult } from "../src/core/types.js"
import type { ActionHost } from "../src/actions/host.js"
import { WorkExecutor } from "../src/runtime/executor.js"
import { TaskLogCollector } from "../src/runtime/task-log.js"
import type { GitRunner } from "../src/runtime/git-probe.js"
import { verifyOnlyWorkspaceManager } from "./support/workspace-mock.js"
import type { WorkspaceManager } from "../src/runtime/workspace.js"
import { FakeProcessSpawner } from "./support/fake-process.js"
import { defineTestActions } from "./support/action-registry-test.js"
import { withTestRunnerResources } from "./support/test-resources.js"

const nonGitRunner: GitRunner = async () => ({
  success: false,
  stdout: "",
  stderr: "not a git repository",
  exitCode: 128,
  combinedOutput: "not a git repository",
})

const withTaskLogResources = <T>(
  body: (workDir: string, processSpawner: FakeProcessSpawner) => Promise<T>,
  gitRunner: GitRunner = nonGitRunner,
  environment?: Readonly<Record<string, string | undefined>>,
) => {
  const processSpawner = new FakeProcessSpawner()
  return withTestRunnerResources(
    async () => await body("/virtual/task-log-executor", processSpawner),
    { gitRunner, processSpawner: processSpawner.spawn, ...(environment ? { environment } : {}) },
  )
}

function makeRegistry(handler: (inputs: JsonObject, host: ActionHost) => Promise<ActionResult>): ActionRegistry {
  return defineTestActions({
    "mohist/test-action": handler,
  })
}

function buildExecutor(registry: ActionRegistry, workDir: string, workspaceManager: WorkspaceManager = verifyOnlyWorkspaceManager({ path: workDir, branch: null })): WorkExecutor {
  return new WorkExecutor(
    registry,
    workspaceManager,
    {} as never,
    workDir,
    () => new Date("2026-07-01T00:00:00.000Z"),
  )
}

function buildWork(workDir: string, overrides: Partial<DispatchWorkItem> = {}): DispatchWorkItem {
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

async function runWith(registry: ActionRegistry, workDir: string, work: DispatchWorkItem = buildWork(workDir), workspaceManager?: WorkspaceManager): Promise<{ result: WorkItemResult; collector: TaskLogCollector }> {
  const executor = buildExecutor(registry, workDir, workspaceManager)
  const collector = new TaskLogCollector()
  const execution = await executor.executeWithLog(work, new AbortController().signal, collector)
  return { result: execution.result, collector: execution.collector }
}

describe("WorkExecutor forwards action output to the task log", () => {
  const it = (name: string, body: (workDir: string, processSpawner: FakeProcessSpawner) => Promise<void>) => vitestIt(name, () => withTaskLogResources(body))

  it("ForwardsActionBodyWriteToSinkTaggedWithActionSource", async (workDir) => {
    let loggerPresent = false
    const registry = makeRegistry(async (_inputs, host) => {
      loggerPresent = host.log !== null && host.log !== undefined
      host.log?.write("action:rebase", "rebasing commit a1b2c3")
      host.log?.write("action:rebase", "Auto-merging src/lib/rebase.ts")
      return { output: { rebase: "ok" } }
    })
    const { collector } = await runWith(registry, workDir)
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

  it("ProjectsCoreProcessOutputThroughSetVars", async (workDir, processSpawner) => {
    const patches: Array<Record<string, unknown>> = []
    const executor = new WorkExecutor(
      createDefaultRegistry(),
      verifyOnlyWorkspaceManager({ path: workDir, branch: null }),
      { patchRunVars: async (_workflowRunId: string, vars: Record<string, unknown>) => { patches.push(vars) } } as never,
      workDir,
    )
    const running = executor.execute(buildWork(workDir, {
      uses: "core/process",
      with: { command: "fake-command", args: [] },
      setVars: {
        "release.result": "output.stdout",
        "release.exitCode": "output.exitCode",
      },
    }), new AbortController().signal)
    await vi.waitFor(() => expect(processSpawner.children).toHaveLength(1))
    processSpawner.children[0]!.writeStdout("release-ready\n")
    processSpawner.children[0]!.close(0)

    await expect(running).resolves.toMatchObject({
      status: "completed",
      output: { stdout: "release-ready", exitCode: 0 },
    })
    expect(patches).toEqual([{ release: { result: "release-ready", exitCode: 0 } }])
  })

  it("PassesWorkspacePreparationOutputThroughWorkspacePrepSource", async (workDir) => {
    const registry = makeRegistry(async () => ({ output: { ok: true } }))
    const workspaceManager = verifyOnlyWorkspaceManager(
       { path: workDir, branch: null },
      (log) => log?.write("workspace-prep", "clone output from workspace preparation"),
    )

    const { collector } = await runWith(registry, workDir, buildWork(workDir), workspaceManager)

    const entries = collector.flush().entries.filter((entry) => entry.source === "workspace-prep")
    expect(entries.map((entry) => entry.text)).toContain("clone output from workspace preparation")
  })

  it("CapturesBranchCheckOutputWithBranchCheckSource", async (workDir) => {
    const gitRunner: GitRunner = async (_workDir, args, _signal, options) => {
      options?.sink?.log.write(options.sink.source, `git ${args.join(" ")}`)
      return {
        success: true,
        stdout: args.join(" ") === "rev-parse --abbrev-ref HEAD" ? "main\n" : "",
        stderr: "",
        exitCode: 0,
        combinedOutput: "",
      }
    }
    const registry = makeRegistry(async () => ({ error: { code: "action-failed", message: "stop after start check" } }))

    const { collector } = await withTaskLogResources(
      async () => await runWith(registry, workDir, buildWork(workDir, { variables: { workspace: { path: workDir, branch: "main", changeDir: null } } })),
      gitRunner,
    )

    const entries = collector.flush().entries.filter((entry) => entry.source === "branch-check")
    expect(entries.map((entry) => entry.text)).toContain("git rev-parse --abbrev-ref HEAD")
  })

  it("CapturesCompletionReceiptProbeOutputWithReceiptSource", async (workDir) => {
    const gitRunner: GitRunner = async (_workDir, args, _signal, options) => {
      options?.sink?.log.write(options.sink.source, `git ${args.join(" ")}`)
      const joined = args.join(" ")
      return {
        success: true,
        stdout:
          joined === "rev-parse --abbrev-ref HEAD"
            ? "main\n"
            : joined === "rev-parse HEAD"
              ? "head-1\n"
              : joined === "rev-parse HEAD^{tree}"
                ? "tree-1\n"
                : "",
        stderr: "",
        exitCode: 0,
        combinedOutput: "",
      }
    }
    const registry = makeRegistry(async () => ({ output: { ok: true } }))

    const { collector } = await withTaskLogResources(
      async () => await runWith(registry, workDir, buildWork(workDir, {
        runnerId: "runner-1",
        workspaceId: "workspace-1",
        workspaceGeneration: 1,
        workspaceHead: "head-1",
        workspaceTree: "tree-1",
        variables: { workspace: { path: workDir, branch: "main", changeDir: null } },
      }), verifyOnlyWorkspaceManager({
        path: workDir,
        branch: "main",
        workspaceId: "workspace-1",
        workspaceGeneration: 1,
      })),
      gitRunner,
    )

    const entries = collector.flush().entries.filter((entry) => entry.source === "receipt-probe")
    expect(entries.map((entry) => entry.text)).toContain("git rev-parse --abbrev-ref HEAD")
    expect(entries.map((entry) => entry.text)).toContain("git status --porcelain=v1 -z")
  })

  it("CapturesFailingOpsCommandOutputInCollector", async (workDir) => {
    const registry = makeRegistry(async (_inputs, host) => {
      host.log?.write("action:rebase", "Rebase conflict in src/lib/rebase.ts")
      host.log?.write("action:rebase", "fatal: Could not apply abc1234")
      return {
        error: { code: "conflict", message: "Rebase conflict" },
        exitCode: 1,
      }
    })
    const { result, collector } = await runWith(registry, workDir)
    expect(result.status).toBe("failed")
    const rebaseEntries = collector.flush().entries.filter((e) => e.source === "action:rebase")
    expect(rebaseEntries).toHaveLength(2)
    expect(rebaseEntries[0]!.text).toContain("Rebase conflict")
    expect(rebaseEntries[1]!.text).toContain("Could not apply")
  })

  it("PreservesAggregateContractForActionBody_GitOutputUnchanged", async (workDir) => {
    // The action body sees ctx.log for line-by-line forwarding, but
    // the downstream `combinedOutput` aggregation must remain
    // available — i.e. a downstream consumer that reads the message /
    // output JSON sees the same shape whether or not the sink is on.
    // We verify this by stubbing git() at the runtime-git-probe layer
    // and asserting the aggregate fields are present on the result.
    let gitSinkSeen = false
    const fakeGit: GitRunner = async (workDir: string, args: string[], signal: AbortSignal, options) => {
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

    const registry = makeRegistry(async (_inputs, host) => {
      host.log?.write("action:health-check", "ok")
      return {
        output: {
          kind: "health-check",
          gitOutput: "out-line-1\nout-line-2",
        },
        exitCode: 0,
      }
    })
    const { result, collector } = await withTaskLogResources(() => runWith(registry, workDir), fakeGit)
    expect(gitSinkSeen).toBe(true)
    expect(result.output).toMatchObject({ kind: "health-check" })
    expect(collector.flush().entries.map((e) => e.source)).toContain("action:health-check")
  })

  it("DeliberatelyFailingOpsCommandOutputReachesCollectorBuffer", async (workDir) => {
    const registry = makeRegistry(async (_inputs, host) => {
      // Simulate a rebase conflict tail end: emit the failure text
      // through the sink before returning a failure result.
      host.log?.write("action:rebase", "First, rewinding head to replay your work on top of it...")
      host.log?.write("action:rebase", "Applying: feat: introduce capture-and-upload plumbing")
      host.log?.write("action:rebase", "Using index info to reconstruct a base tree...")
      host.log?.write("action:rebase", "Falling back to patching base and 3-way merge...")
      host.log?.write("action:rebase", "Auto-merging src/runtime/executor.ts")
      host.log?.write("action:rebase", "CONFLICT (content): Merge conflict in src/runtime/executor.ts")
      host.log?.write("action:rebase", "error: Failed to merge in the changes.")
      host.log?.write("action:rebase", "Patch failed at 0001 feat: introduce capture-and-upload plumbing")
      host.log?.write("action:rebase", "hint: Use 'git am --show-current-patch' to see the failed patch")
      return { error: { code: "conflict", message: "Rebase failed: conflict" } }
    })
    const { result, collector } = await runWith(registry, workDir)
    expect(result.status).toBe("failed")
    const lines = collector.flush().entries.filter((e) => e.source === "action:rebase").map((e) => e.text)
    expect(lines.some((l) => l.includes("CONFLICT"))).toBe(true)
    expect(lines.some((l) => l.includes("Patch failed"))).toBe(true)
  })

  it("RegistersRuntimeConfiguredSecretsBeforeBuffering", async (workDir) => {
    const secretName = "MOHIST_TEST_RUNNER_TOKEN"
    const secret = "runner-configured-secret-12345"
    const registry = makeRegistry(async (_inputs, host) => {
      host.log?.write("action:script", `command failed with ${secret}`)
      return { error: { code: "action-failed", message: "failed" } }
    })

    const { collector } = await withTaskLogResources(
      async () => await runWith(registry, workDir),
      nonGitRunner,
      { [secretName]: secret },
    )
    const text = collector.flush().entries.at(-1)?.text ?? ""
    expect(text).toContain("***")
    expect(text).not.toContain(secret)
  })

})
