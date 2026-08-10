import { mkdir, mkdtemp, rm, writeFile } from "node:fs/promises"
import { join } from "node:path"
import { tmpdir } from "node:os"
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

function work(workspacePath = workDir): DispatchWorkItem {
  return {
    workflowRunId: "wf-runtime-failure",
    workId: "plan.1",
    workType: "task",
    stage: "plan",
    title: "Run agent",
    uses: "test/agent",
    with: {},
    projectId: "project-1",
    variables: { workspace: { path: workspacePath } },
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

function createExecutor(runtime: unknown, connection: unknown, outbox = makeRecordingOutbox(), executionWorkDir = workDir) {
  const executor = new WorkExecutor(
    actionRegistry(),
    verifyOnlyWorkspaceManager({ path: executionWorkDir, branch: null }),
    connection as never,
    executionWorkDir,
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

  it("creates a fresh physical session before retrying a failed binding and satisfies the task artifact", async () => {
    const retryWorkDir = await mkdtemp(join(tmpdir(), "mohist-retry-session-"))
    try {
      const createSession = vi.fn(async () => ({
        ok: true as const,
        value: { runtimeSessionId: "runtime-new", workDir: retryWorkDir },
        diagnostics: [],
      }))
      const resolveSession = vi.fn(async () => ({ ok: true as const, value: { activeTurn: false }, diagnostics: [] }))
      const resetWorkflowAgentSession = vi.fn(async (_projectId: string, _workflowRunId: string, _sessionName: string, _body: unknown, _signal: AbortSignal) => ({ runtimeSessionId: "runtime-new", workDir: retryWorkDir }))
      const runTurn = vi.fn(async (request: { target: { runtimeSessionId: string | null; workDir: string } }) => {
        if (request.target.runtimeSessionId === "runtime-old") {
          return {
            ok: false as const,
            error: { kind: "turn-failed" as const, message: "old runtime session was aborted", diagnostics: [] },
            diagnostics: [],
          }
        }
        const proposalPath = join(request.target.workDir, "openspec/changes/issue-557/proposal.md")
        await mkdir(join(request.target.workDir, "openspec/changes/issue-557"), { recursive: true })
        await writeFile(proposalPath, "proposal", "utf8")
        return {
          ok: true as const,
          value: {
            facts: { finalAssistantText: "proposal written", runtimeSessionId: "runtime-new", workDir: request.target.workDir },
            diagnostics: [],
          },
          diagnostics: [],
        }
      })
      const { executor, outbox } = createExecutor(
        fakeRuntime({ createSession, resolveSession, runTurn }),
        fakeConnection({
          openWorkflowAgentSession: async () => ({
            runtimeSessionId: "runtime-old",
            workDir: retryWorkDir,
            needsFreshRuntimeSession: true,
          }),
          resetWorkflowAgentSession,
        }),
        makeRecordingOutbox(),
        retryWorkDir,
      )

      const result = await executor.execute({
        ...work(retryWorkDir),
        expect: { files: [{ path: "openspec/changes/issue-557/proposal.md" }] },
      }, new AbortController().signal)

      expect(result.status).toBe("completed")
      expect(result.error).toBeUndefined()
      expect(createSession).toHaveBeenCalledTimes(1)
      expect(resolveSession).not.toHaveBeenCalled()
      expect(resetWorkflowAgentSession).toHaveBeenCalledTimes(1)
      expect((resetWorkflowAgentSession.mock.calls[0]?.[3] as Record<string, unknown>)).toMatchObject({
        expectedRunnerId: "runner-1",
        expectedRuntime: "opencode",
        expectedRuntimeSessionId: "runtime-old",
        replacementRuntimeSessionId: "runtime-new",
      })
      expect(runTurn.mock.calls.map((call) => call[0].target.runtimeSessionId)).toEqual(["runtime-new"])
      expect(outbox.eventsByType("session.input")[0]?.runtimeSessionId).toBe("runtime-new")
    } finally {
      await rm(retryWorkDir, { recursive: true, force: true })
    }
  })
})
