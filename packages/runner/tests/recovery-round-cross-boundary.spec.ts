import { mkdtemp, rm } from "node:fs/promises"
import { join } from "node:path"
import { tmpdir } from "node:os"
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { setExecutorGitRunnerForTest } from "../src/runtime/git-probe.js"
import { WorkExecutor } from "../src/runtime/executor.js"
import { ServerConnection } from "../src/server/connection.js"
import { verifyOnlyWorkspaceManager } from "./support/workspace-mock.js"
import { defineTestActions } from "./support/action-registry-test.js"

// Cross-boundary recovery round. The executor/connection tests cover each
// boundary in isolation; this spec composes the real production components
// (ServerConnection poll/report mapping + WorkExecutor recovery evaluation)
// against a fake server to execute one recovery round end to end:
//   fresh dispatch (explicit null recoveryRemaining)
//   -> executor produces matching failure output
//   -> reported follow-ups carry numeric continuation state
//   -> redispatched self-retry preserves that state into the next decrement.

const originalFetch = globalThis.fetch
let fetchMock: ReturnType<typeof vi.fn>
let workDir: string

beforeEach(async () => {
  workDir = await mkdtemp(join(tmpdir(), "mohist-recovery-round-"))
  fetchMock = vi.fn()
  globalThis.fetch = fetchMock as unknown as typeof fetch
  // The executor never reaches git in this spec (no branch stability path is
  // exercised); stub it as a non-repo so any incidental probe is deterministic.
  setExecutorGitRunnerForTest(async () => ({ success: false, exitCode: 128, stdout: "", stderr: "not a git repository", combinedOutput: "not a git repository" }))
})

afterEach(async () => {
  globalThis.fetch = originalFetch
  vi.restoreAllMocks()
  setExecutorGitRunnerForTest(null)
  await rm(workDir, { recursive: true, force: true })
})

function executor(): WorkExecutor {
  const registry = defineTestActions({
    "test/matching": {
      run: async () => ({ error: { code: "conflict", message: "conflict" } }),
      errors: [{ code: "conflict" }],
    },
  })
  return new WorkExecutor(
    registry,
    verifyOnlyWorkspaceManager({ path: workDir, branch: null, changeDir: null }),
    {} as never,
    workDir,
  )
}

const recovery = {
  budget: 2,
  handlers: [
    {
      when: "error.code=conflict",
      tasks: [{ id: "recover:fix", title: "Fix", uses: "test/matching" }],
      retrySelf: true,
    },
  ],
}

function dispatch(workId: string, recoveryRemaining: number | null) {
  // Wire shape: the server serializes object-typed fields (`with`, `variables`,
  // `recovery`) as JSON strings and keeps `recoveryRemaining` explicit (null or
  // numeric) so the runner can tell fresh from continuation and absent.
  return {
    workflowRunId: "wf-recovery-round",
    workId,
    workType: "task",
    stage: "check",
    title: "Review",
    uses: "test/matching",
    with: JSON.stringify({}),
    variables: JSON.stringify({ workspace: { path: workDir, branch: null, changeDir: null } }),
    recovery: JSON.stringify(recovery),
    recoveryRemaining,
  }
}

describe("recovery round across poll -> execute -> report", () => {
  it("authors numeric continuation state and preserves it on redispatch", async () => {
    const connection = new ServerConnection({
      serverUrl: "http://localhost:3456",
      runnerId: "runner-1",
      runnerRoot: "/tmp",
      pollIntervalMs: 100,
      heartbeatIntervalMs: 60_000,
      dispatchLivenessProbeIntervalMs: 60_000,
    })

    // Fake server: first poll hands the fresh dispatch (explicit null); the
    // report endpoint records the reported result and acks.
    let firstDispatch: ReturnType<typeof dispatch> | null = null

    fetchMock.mockImplementation(async (url: string) => {
      if (url.endsWith("/poll") && !firstDispatch) {
        firstDispatch = dispatch("review.1", null)
        return new Response(JSON.stringify({ dispatches: [firstDispatch] }), { status: 200, headers: { "content-type": "application/json" } })
      }
      if (url.endsWith("/report")) {
        // The real ServerConnection.report POSTs the result body.
        return new Response("{}", { status: 200, headers: { "content-type": "application/json" } })
      }
      return new Response("{}", { status: 200 })
    })

    // 1. Poll receives the fresh dispatch and preserves explicit null state.
    const polled = await connection.poll(new AbortController().signal)
    const fresh = polled[0]!
    expect(Object.prototype.hasOwnProperty.call(fresh, "recoveryRemaining")).toBe(true)
    expect(fresh.recoveryRemaining).toBeNull()

    // 2. Execute: the matching failure schedules a recovery round.
    const result = await executor().execute(fresh, new AbortController().signal)
    expect(result.addTasks).toBeDefined()
    const selfRetry = result.addTasks!.find((t) => t.id === "review")!
    // The declaration is immutable (budget 2); the consumed allowance is a
    // separate numeric continuation state (1), authored by the executor.
    expect(selfRetry.recovery?.budget).toBe(2)
    expect(selfRetry.recoveryRemaining).toBe(1)

    // 3. Report carries the numeric continuation state to the server.
    const reportUrls: string[] = []
    const reportBodies: unknown[] = []
    fetchMock.mockImplementation(async (url: string, init?: RequestInit) => {
      reportUrls.push(url)
      if (url.endsWith("/report")) {
        reportBodies.push(JSON.parse(init?.body as string))
      }
      return new Response("{}", { status: 200, headers: { "content-type": "application/json" } })
    })
    await connection.report(fresh, result, new AbortController().signal)
    const reportedBody = reportBodies[0] as { addTasks?: Array<{ recoveryRemaining?: number }> }
    expect(reportedBody.addTasks?.find((t) => t.recoveryRemaining === 1)).toBeDefined()

    // 4. The server redispatches the self-retry with the numeric state; the
    // next execution decrements again (0) while the declaration stays at 2.
    fetchMock.mockResolvedValueOnce(new Response(
      JSON.stringify({ dispatches: [dispatch("review.2", 1)] }),
      { status: 200, headers: { "content-type": "application/json" } },
    ))
    const redispatched = (await connection.poll(new AbortController().signal))[0]!
    expect(redispatched.recoveryRemaining).toBe(1)

    const next = await executor().execute(redispatched, new AbortController().signal)
    const nextRetry = next.addTasks!.find((t) => t.id === "review")!
    expect(nextRetry.recovery?.budget).toBe(2)
    expect(nextRetry.recoveryRemaining).toBe(0)
  })
})
