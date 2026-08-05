import { mkdtemp, rm } from "node:fs/promises"
import { tmpdir } from "node:os"
import { join } from "node:path"
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { ConvergenceBackstop, type ConvergenceRunner } from "../src/runtime/cleanup-convergence.js"
import { WorkspaceRegistry } from "../src/runtime/workspace-registry.js"
import { capturedLogs } from "./support/logger-test.js"

// Unit coverage for the convergence backstop. The backstop
// enumerates only registry entries still in phase `active`, asks the
// server for their status, and transitions server-reported-terminal
// entries to `eligible`. It must:
//   - skip already-eligible entries (no re-stamp, no re-query);
//   - never ask the server about workflowRunIds the runner does not
//     track locally (no full-history scan);
//   - drop registry entries the server has no record of;
//   - tolerate query failures (return error counts, leave registry
//     state consistent).

class StubRunner implements ConvergenceRunner {
  public lastQuery: string[] | null = null
  public responses: Array<Record<string, string> | Error> = []
  public callCount = 0

  constructor(responses: Array<Record<string, string> | Error>) {
    this.responses = [...responses]
  }

  async queryActiveStatuses(workflowRunIds: string[], _signal: AbortSignal): Promise<Record<string, string>> {
    this.lastQuery = [...workflowRunIds]
    this.callCount++
    const next = this.responses.shift()
    if (next instanceof Error) throw next
    return next ?? {}
  }
}

describe("ConvergenceBackstop", () => {
  let root: string

  beforeEach(async () => {
    root = await mkdtemp(join(tmpdir(), "mohist-convergence-"))
  })

  afterEach(async () => {
    await rm(root, { recursive: true, force: true })
  })

  async function makeRegistry() {
    const registry = new WorkspaceRegistry(root)
    await registry.load()
    return registry
  }

  it("RunOnce_WithNoActiveEntries_DoesNotQueryServer", async () => {
    const registry = await makeRegistry()
    const stub = new StubRunner([])
    const backstop = new ConvergenceBackstop(registry, stub)

    const result = await backstop.runOnce(new AbortController().signal)

    expect(result).toEqual({ queried: 0, transitioned: 0, dropped: 0 })
    expect(stub.callCount).toBe(0)
    expect(stub.lastQuery).toBeNull()
  })

  it("RunOnce_QueriesOnlyActiveEntries_IgnoresEligible", async () => {
    const registry = await makeRegistry()
    await registry.register({ issueNumber: 1, workflowRunId: "wr-active", workspacePath: join(root, "w1") })
    await registry.register({ issueNumber: 2, workflowRunId: "wr-eligible", workspacePath: join(root, "w2") })
    await registry.markEligible("wr-eligible")

    const stub = new StubRunner([{ "wr-active": "Running" }])
    const backstop = new ConvergenceBackstop(registry, stub)

    const result = await backstop.runOnce(new AbortController().signal)

    expect(stub.lastQuery).toEqual(["wr-active"])
    expect(result).toEqual({ queried: 1, transitioned: 0, dropped: 0 })
    // Eligible entry stays eligible.
    const eligible = registry.get("wr-eligible")
    expect(eligible?.phase).toBe("eligible")
  })

  it("RunOnce_OnTerminalStatus_TransitionsActiveToEligible", async () => {
    const registry = await makeRegistry()
    await registry.register({ issueNumber: 1, workflowRunId: "wr-1", workspacePath: join(root, "w1") })

    const stub = new StubRunner([{ "wr-1": "Completed" }])
    const backstop = new ConvergenceBackstop(registry, stub)

    const result = await backstop.runOnce(new AbortController().signal)

    expect(result).toEqual({ queried: 1, transitioned: 1, dropped: 0 })
    expect(registry.get("wr-1")?.phase).toBe("eligible")
    expect(registry.get("wr-1")?.terminalAt).toBeTruthy()
  })

  it("RunOnce_OnStoppedStatus_TransitionsActiveToEligible", async () => {
    const registry = await makeRegistry()
    await registry.register({ issueNumber: 1, workflowRunId: "wr-1", workspacePath: join(root, "w1") })

    const stub = new StubRunner([{ "wr-1": "Stopped" }])
    const backstop = new ConvergenceBackstop(registry, stub)

    const result = await backstop.runOnce(new AbortController().signal)

    expect(result.transitioned).toBe(1)
    expect(registry.get("wr-1")?.phase).toBe("eligible")
  })

  it("RunOnce_OnFailedStatus_LeavesEntryActive", async () => {
    // Failed is a recoverable mid-state (Retry/Rerun revive it), NOT
    // terminal: a failed run's workspace must stay active so the next
    // dispatch finds the run branch intact.
    const registry = await makeRegistry()
    await registry.register({ issueNumber: 1, workflowRunId: "wr-1", workspacePath: join(root, "w1") })

    const stub = new StubRunner([{ "wr-1": "Failed" }])
    const backstop = new ConvergenceBackstop(registry, stub)

    const result = await backstop.runOnce(new AbortController().signal)

    expect(result).toEqual({ queried: 1, transitioned: 0, dropped: 0 })
    expect(registry.get("wr-1")?.phase).toBe("active")
    expect(registry.get("wr-1")?.terminalAt).toBeNull()
  })

  it("RunOnce_OnNonTerminalStatus_LeavesEntryActive", async () => {
    const registry = await makeRegistry()
    await registry.register({ issueNumber: 1, workflowRunId: "wr-1", workspacePath: join(root, "w1") })

    const stub = new StubRunner([{ "wr-1": "AwaitingApproval" }])
    const backstop = new ConvergenceBackstop(registry, stub)

    const result = await backstop.runOnce(new AbortController().signal)

    expect(result).toEqual({ queried: 1, transitioned: 0, dropped: 0 })
    expect(registry.get("wr-1")?.phase).toBe("active")
    expect(registry.get("wr-1")?.terminalAt).toBeNull()
  })

  it("RunOnce_OnRunningStatus_LeavesEntryActive", async () => {
    const registry = await makeRegistry()
    await registry.register({ issueNumber: 1, workflowRunId: "wr-1", workspacePath: join(root, "w1") })

    const stub = new StubRunner([{ "wr-1": "Running" }])
    const backstop = new ConvergenceBackstop(registry, stub)

    const result = await backstop.runOnce(new AbortController().signal)

    expect(result.transitioned).toBe(0)
    expect(registry.get("wr-1")?.phase).toBe("active")
  })

  // The cleanup guard recognizes the expanded workflow status vocabulary. The
  // convergence backstop is the runner-side enforcement point: each
  // non-terminal status reported by the server must
  // leave an active entry active and drop nothing from the registry.
  // The state-machine contract requires the runner to specifically
  // tolerate the new `Created` (built not started) and `Ready`
  // (assigned, waiting for pickup) values — a regression in either
  // would mark the workspace eligible and trigger automatic removal on
  // the next retention/budget tick.

  it("RunOnce_OnCreatedStatus_LeavesEntryActive (D1 created — built not started)", async () => {
    const registry = await makeRegistry()
    await registry.register({ issueNumber: 1, workflowRunId: "wr-created", workspacePath: join(root, "w1") })

    const stub = new StubRunner([{ "wr-created": "Created" }])
    const backstop = new ConvergenceBackstop(registry, stub)

    const result = await backstop.runOnce(new AbortController().signal)

    expect(result).toEqual({ queried: 1, transitioned: 0, dropped: 0 })
    expect(registry.get("wr-created")?.phase).toBe("active")
    expect(registry.get("wr-created")?.terminalAt).toBeNull()
  })

  it("RunOnce_OnPendingStatus_LeavesEntryActive (D1 pending — waiting for claim)", async () => {
    const registry = await makeRegistry()
    await registry.register({ issueNumber: 1, workflowRunId: "wr-pending", workspacePath: join(root, "w1") })

    const stub = new StubRunner([{ "wr-pending": "Pending" }])
    const backstop = new ConvergenceBackstop(registry, stub)

    const result = await backstop.runOnce(new AbortController().signal)

    expect(result).toEqual({ queried: 1, transitioned: 0, dropped: 0 })
    expect(registry.get("wr-pending")?.phase).toBe("active")
    expect(registry.get("wr-pending")?.terminalAt).toBeNull()
  })

  it("RunOnce_OnReadyStatus_LeavesEntryActive (D1 ready — assigned, waiting for pickup)", async () => {
    const registry = await makeRegistry()
    await registry.register({ issueNumber: 1, workflowRunId: "wr-ready", workspacePath: join(root, "w1") })

    const stub = new StubRunner([{ "wr-ready": "Ready" }])
    const backstop = new ConvergenceBackstop(registry, stub)

    const result = await backstop.runOnce(new AbortController().signal)

    expect(result).toEqual({ queried: 1, transitioned: 0, dropped: 0 })
    expect(registry.get("wr-ready")?.phase).toBe("active")
    expect(registry.get("wr-ready")?.terminalAt).toBeNull()
  })

  it("RunOnce_OnPausedStatus_LeavesEntryActive", async () => {
    const registry = await makeRegistry()
    await registry.register({ issueNumber: 1, workflowRunId: "wr-1", workspacePath: join(root, "w1") })

    const stub = new StubRunner([{ "wr-1": "Paused" }])
    const backstop = new ConvergenceBackstop(registry, stub)

    const result = await backstop.runOnce(new AbortController().signal)

    expect(result.transitioned).toBe(0)
    expect(registry.get("wr-1")?.phase).toBe("active")
  })

  it("RunOnce_OnServerForgotRunId_DropsRegistryEntry", async () => {
    const registry = await makeRegistry()
    await registry.register({ issueNumber: 1, workflowRunId: "wr-gone", workspacePath: join(root, "w1") })

    // Server response omits wr-gone entirely.
    const stub = new StubRunner([{}])
    const backstop = new ConvergenceBackstop(registry, stub)

    const result = await backstop.runOnce(new AbortController().signal)

    expect(result).toEqual({ queried: 1, transitioned: 0, dropped: 1 })
    expect(registry.get("wr-gone")).toBeNull()
  })

  it("RunOnce_MixedStatuses_TransitionsOnlyTerminal", async () => {
    const registry = await makeRegistry()
    await registry.register({ issueNumber: 1, workflowRunId: "wr-done", workspacePath: join(root, "w1") })
    await registry.register({ issueNumber: 2, workflowRunId: "wr-running", workspacePath: join(root, "w2") })
    await registry.register({ issueNumber: 3, workflowRunId: "wr-stopped", workspacePath: join(root, "w3") })
    await registry.register({ issueNumber: 4, workflowRunId: "wr-forgotten", workspacePath: join(root, "w4") })

    const stub = new StubRunner([{
      "wr-done": "Completed",
      "wr-running": "Running",
      "wr-stopped": "Stopped",
      // wr-forgotten omitted
    }])
    const backstop = new ConvergenceBackstop(registry, stub)

    const result = await backstop.runOnce(new AbortController().signal)

    expect(result).toEqual({ queried: 4, transitioned: 2, dropped: 1 })
    expect(registry.get("wr-done")?.phase).toBe("eligible")
    expect(registry.get("wr-running")?.phase).toBe("active")
    expect(registry.get("wr-stopped")?.phase).toBe("eligible")
    expect(registry.get("wr-forgotten")).toBeNull()
  })

  it("RunOnce_OnAlreadyEligibleEntry_DoesNotQueryThatEntry", async () => {
    // Pre-existing eligible entries from a prior push or tick must NOT
    // be re-queried — that is the no-full-history-scan guarantee for the
    // pre-eligible side of the registry.
    const registry = await makeRegistry()
    await registry.register({ issueNumber: 1, workflowRunId: "wr-eligible-1", workspacePath: join(root, "w1") })
    await registry.register({ issueNumber: 2, workflowRunId: "wr-eligible-2", workspacePath: join(root, "w2") })
    await registry.markEligible("wr-eligible-1")
    await registry.markEligible("wr-eligible-2")

    const stub = new StubRunner([])
    const backstop = new ConvergenceBackstop(registry, stub)

    const result = await backstop.runOnce(new AbortController().signal)

    expect(result).toEqual({ queried: 0, transitioned: 0, dropped: 0 })
    expect(stub.lastQuery).toBeNull()
  })

  it("RunOnce_NeverQueriesWorkflowRunsOutsideTheRegistry", async () => {
    // Pre-condition: no full-history scan. The stub will fail the test
    // if the runner sends a workflowRunId it does not own.
    const registry = await makeRegistry()
    await registry.register({ issueNumber: 1, workflowRunId: "wr-mine", workspacePath: join(root, "w1") })

    const stub = new StubRunner([{ "wr-mine": "Running" }])
    const backstop = new ConvergenceBackstop(registry, stub)

    await backstop.runOnce(new AbortController().signal)

    expect(stub.lastQuery).not.toBeNull()
    for (const id of stub.lastQuery!) {
      expect(registry.get(id)).not.toBeNull()
    }
  })

  it("RunOnce_OnServerError_LeavesRegistryUnchanged", async () => {
    const registry = await makeRegistry()
    await registry.register({ issueNumber: 1, workflowRunId: "wr-1", workspacePath: join(root, "w1") })

    const failure = new Error("network blip")
    const stub = new StubRunner([failure])
    const backstop = new ConvergenceBackstop(registry, stub)
    try {
      const result = await backstop.runOnce(new AbortController().signal)

      expect(result).toEqual({ queried: 1, transitioned: 0, dropped: 0 })
      expect(registry.get("wr-1")?.phase).toBe("active")
      expect(capturedLogs()).toEqual(expect.arrayContaining([
        expect.objectContaining({ level: "ERROR", message: "workspace cleanup convergence query failed", fields: { exception: failure } }),
      ]))
    } finally {
    }
  })

  it("RunOnce_OnTerminalStatus_StampsTerminalAt", async () => {
    const registry = await makeRegistry()
    await registry.register({ issueNumber: 1, workflowRunId: "wr-1", workspacePath: join(root, "w1") })

    const before = new Date("2026-06-25T10:00:00.000Z")
    vi.useFakeTimers()
    vi.setSystemTime(before)

    const stub = new StubRunner([{ "wr-1": "Completed" }])
    const backstop = new ConvergenceBackstop(registry, stub)

    await backstop.runOnce(new AbortController().signal)

    expect(registry.get("wr-1")?.terminalAt).toBe(before.toISOString())
    vi.useRealTimers()
  })

  it("RunOnce_TwoActiveEntriesServerReportsOneTerminal_TransitionsOnlyThatOne", async () => {
    const registry = await makeRegistry()
    await registry.register({ issueNumber: 1, workflowRunId: "wr-1", workspacePath: join(root, "w1") })
    await registry.register({ issueNumber: 2, workflowRunId: "wr-2", workspacePath: join(root, "w2") })

    const stub = new StubRunner([{ "wr-1": "Completed", "wr-2": "Running" }])
    const backstop = new ConvergenceBackstop(registry, stub)

    const result = await backstop.runOnce(new AbortController().signal)

    expect(result).toEqual({ queried: 2, transitioned: 1, dropped: 0 })
    expect(registry.get("wr-1")?.phase).toBe("eligible")
    expect(registry.get("wr-2")?.phase).toBe("active")
  })
})
