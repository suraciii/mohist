import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { WorkExecutor } from "../src/runtime/executor.js"
import type { DispatchWorkItem } from "../src/core/types.js"
import { setExecutorGitRunnerForTest } from "../src/runtime/git-probe.js"
import { makeRecordingOutbox } from "./support/outbox-test-helpers.js"
import { defineTestActions } from "./support/action-registry-test.js"
import { verifyOnlyWorkspaceManager } from "./support/workspace-mock.js"

const workDir = "/tmp/mohist-runtime-failure-convergence"

const runtimeFailureErrors = [
  "runtime-unavailable",
  "turn-failed",
  "session-binding-failed",
  "execution-unavailable",
  "session-workspace-mismatch",
  "runtime-session-missing",
].map((code) => ({ code, description: code }))

function work(): DispatchWorkItem {
  return {
    workflowRunId: "wf-runtime-failure",
    workId: "plan.1",
    workType: "task",
    stage: "plan",
    title: "Run agent",
    uses: "test/agent",
    with: {},
    projectId: "project-1",
    variables: { workspace: { path: workDir } },
  }
}

function actionRegistry() {
  return defineTestActions({
    "test/agent": {
      capabilities: ["agent-turn"],
      errors: runtimeFailureErrors,
      run: async (_inputs, host) => host.agent!.turn({ prompt: "invoke the agent", session: "plan" }),
    },
  })
}

function fakeConnection(overrides: Record<string, unknown> = {}) {
  return {
    runnerId: "runner-1",
    async openWorkflowAgentSession() {
      return { runtimeSessionId: "runtime-1", workDir }
    },
    async attachWorkflowAgentSession() {},
    async recoverMissingWorkflowAgentSession() {},
    ...overrides,
  } as never
}

function fakeRuntime(overrides: Record<string, unknown> = {}) {
  return {
    ready: () => true,
    diagnostic: () => null,
    async createSession() {
      return { ok: false, error: { kind: "turn-failed", message: "create failed", diagnostics: [] }, diagnostics: [] }
    },
    async resolveSession() {
      return { ok: true, value: { activeTurn: false }, diagnostics: [] }
    },
    async runTurn() {
      throw new Error("pending apply_patch failed")
    },
    ...overrides,
  } as never
}

function createExecutor(runtime: unknown, connection: unknown, outbox = makeRecordingOutbox()) {
  const executor = new WorkExecutor(
    actionRegistry(),
    verifyOnlyWorkspaceManager({ path: workDir, branch: null }),
    connection as never,
    workDir,
    undefined,
    runtime as never,
    null,
    outbox.outbox,
    (() => {
      let n = 0
      return () => `runtime-failure-${++n}`
    })(),
  )
  return { executor, outbox }
}

beforeEach(() => {
  setExecutorGitRunnerForTest(async () => ({
    success: false,
    exitCode: 128,
    stdout: "",
    stderr: "not a git repository",
    combinedOutput: "not a git repository",
  }))
})

afterEach(() => {
  setExecutorGitRunnerForTest(null)
})

describe("WorkExecutor runtime failure convergence", () => {
  it("turns runtime-unavailable into a failed WorkItemResult without a current session binding", async () => {
    const open = vi.fn()
    open.mockResolvedValue({ runtimeSessionId: null, workDir })
    const { executor, outbox } = createExecutor(null, fakeConnection({ openWorkflowAgentSession: open }))

    const result = await executor.execute(work(), new AbortController().signal)

    expect(result.status).toBe("failed")
    expect(result.error?.code).toBe("runtime-unavailable")
    expect(open).toHaveBeenCalledTimes(1)
    expect(outbox.eventTypeList()).toEqual([])
  })

  it("closes an already-bound AgentSession when runtime becomes unavailable", async () => {
    const { executor, outbox } = createExecutor(null, fakeConnection())

    const result = await executor.execute(work(), new AbortController().signal)

    expect(result.status).toBe("failed")
    expect(result.error?.code).toBe("runtime-unavailable")
    expect(outbox.eventTypeList()).toEqual(["turn.failed", "session.activity"])
    expect(outbox.eventsByType("session.activity")[0]?.event.payload).toMatchObject({
      activity: "idle",
      status: "failed",
      exitCode: 1,
    })
  })

  it("closes the bound AgentSession when a pending tool/runtime turn throws", async () => {
    const { executor, outbox } = createExecutor(fakeRuntime(), fakeConnection())

    const result = await executor.execute(work(), new AbortController().signal)

    expect(result.status).toBe("failed")
    expect(result.error?.message).toContain("pending apply_patch failed")
    expect(outbox.eventTypeList()).toEqual(["session.input", "turn.failed", "session.activity"])
    expect(outbox.eventsByType("session.activity")[0]?.event.payload).toMatchObject({
      activity: "idle",
      status: "failed",
      exitCode: 1,
    })
  })

  it("keeps create/attach failures as deterministic failed results", async () => {
    const create = vi.fn(async () => {
      throw new Error("OpenCode createSession unavailable")
    })
    const attach = vi.fn()
    const { executor, outbox } = createExecutor(
      fakeRuntime({ createSession: create }),
      fakeConnection({
        openWorkflowAgentSession: async () => ({ runtimeSessionId: null, workDir }),
        attachWorkflowAgentSession: attach,
      }),
    )

    const result = await executor.execute(work(), new AbortController().signal)

    expect(result.status).toBe("failed")
    expect(result.error?.message).toContain("createSession unavailable")
    expect(attach).not.toHaveBeenCalled()
    expect(outbox.eventTypeList()).toEqual([])
  })

  it("closes the physical session known before an attach failure", async () => {
    const attach = vi.fn(async () => {
      throw new Error("attach rejected")
    })
    const { executor, outbox } = createExecutor(
      fakeRuntime({
        createSession: async () => ({
          ok: true,
          value: { runtimeSessionId: "runtime-created", workDir },
          diagnostics: [],
        }),
      }),
      fakeConnection({
        openWorkflowAgentSession: async () => ({ runtimeSessionId: null, workDir }),
        attachWorkflowAgentSession: attach,
      }),
    )

    const result = await executor.execute(work(), new AbortController().signal)

    expect(result.status).toBe("failed")
    expect(result.error?.message).toContain("attach rejected")
    expect(outbox.eventTypeList()).toEqual(["turn.failed", "session.activity"])
    expect(outbox.eventsByType("session.activity")[0]?.runtimeSessionId).toBe("runtime-created")
  })
})
