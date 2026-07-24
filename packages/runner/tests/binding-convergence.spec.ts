import { describe, expect, it, vi } from "vitest"
import { BindingConvergence, type BindingConvergenceConnection } from "../src/runtime/binding-convergence.js"
import type { OpenCodeRuntime } from "../src/runtime/opencode/index.js"
import type { AgentSessionReconcileBinding } from "../src/server/connection.js"
import type { AgentSessionRuntimeEventOutbox, RuntimeEventRecord } from "../src/server/runtime-event-outbox.js"

const FIXED_DATE = new Date("2026-07-24T00:00:00.000Z")

function binding(runtimeSessionId = "runtime-current"): AgentSessionReconcileBinding {
  return {
    sessionId: "session-1",
    runtime: "opencode",
    runtimeSessionId,
    workDir: "/work",
  }
}

function runtime(options: {
  resolve: (runtimeSessionId: string) => { ok: true; activeTurn: boolean } | { ok: false; kind: string }
  create?: () => string
}): OpenCodeRuntime {
  return {
    resolveSession: vi.fn(async (request) => {
      const result = options.resolve(request.target.runtimeSessionId ?? "")
      return result.ok
        ? { ok: true, value: { runtimeSessionId: request.target.runtimeSessionId!, workDir: request.target.workDir, activeTurn: result.activeTurn }, diagnostics: [] }
        : { ok: false, error: { kind: result.kind, message: result.kind, diagnostics: [] }, diagnostics: [] }
    }),
    createSession: vi.fn(async (request) => ({
      ok: true,
      value: { runtimeSessionId: options.create?.() ?? "runtime-candidate", workDir: request.target.workDir },
      diagnostics: [],
    })),
  } as unknown as OpenCodeRuntime
}

function outbox(records: RuntimeEventRecord[]): AgentSessionRuntimeEventOutbox {
  return {
    ready: () => true,
    load: async () => undefined,
    recover: async () => undefined,
    enqueueBeforeExecution: async () => undefined,
    enqueueProducedFact: async (record) => { records.push(record) },
    enqueueProducedFactBatch: async (batch) => { records.push(...batch) },
    kick: async () => undefined,
    stop: async () => undefined,
    snapshot: () => records,
  }
}

function connection(
  list: () => AgentSessionReconcileBinding[],
  reconcileMissing = vi.fn(async (_sessionId: string, body: unknown) => ({
    ...binding((body as { replacementRuntimeSessionId: string }).replacementRuntimeSessionId),
  })),
): BindingConvergenceConnection {
  return {
    listAgentSessionsForReconcile: vi.fn(async () => list()),
    reconcileMissingAgentSession: reconcileMissing,
    reconcileAgentSessionRuntimeEvents: vi.fn(async () => []),
  }
}

function convergence(
  runtimeHandle: OpenCodeRuntime | null,
  server: BindingConvergenceConnection,
  records: RuntimeEventRecord[],
): BindingConvergence {
  return new BindingConvergence({
    runnerId: "runner-1",
    connection: server,
    outbox: outbox(records),
    openCodeRuntime: () => runtimeHandle,
    piRuntime: () => null,
    now: () => FIXED_DATE,
    randomId: () => "fixed",
  })
}

describe("Runner reconnect AgentSession binding convergence", () => {
  it("preserves a present idle binding and reports idle without creating a candidate", async () => {
    const records: RuntimeEventRecord[] = []
    const handle = runtime({ resolve: () => ({ ok: true, activeTurn: false }) })
    const server = connection(() => [binding()])

    await convergence(handle, server, records).runOnce(new AbortController().signal)

    expect(handle.createSession).not.toHaveBeenCalled()
    expect(server.reconcileMissingAgentSession).not.toHaveBeenCalled()
    expect(records).toHaveLength(1)
    expect(records[0]).toMatchObject({
      producerFamily: "binding-reconcile",
      target: { kind: "session", sessionId: "session-1" },
      runtimeSessionId: "runtime-current",
      event: { type: "session.activity", payload: { activity: "idle" } },
    })
  })

  it("reports active for a present active turn and never classifies it as missing", async () => {
    const records: RuntimeEventRecord[] = []
    const handle = runtime({ resolve: () => ({ ok: true, activeTurn: true }) })
    const server = connection(() => [binding()])

    await convergence(handle, server, records).runOnce(new AbortController().signal)

    expect(handle.createSession).not.toHaveBeenCalled()
    expect(server.reconcileMissingAgentSession).not.toHaveBeenCalled()
    expect(records[0]?.event.payload.activity).toBe("active")
  })

  it("recovers a confirmed missing binding once and submits no input", async () => {
    let current = binding()
    const handle = runtime({
      resolve: (runtimeSessionId) => runtimeSessionId === "runtime-current"
        ? { ok: false, kind: "missing-session" }
        : { ok: true, activeTurn: false },
      create: () => "runtime-candidate",
    })
    const reconcileMissing = vi.fn(async (_sessionId: string, body: unknown) => {
      current = binding((body as { replacementRuntimeSessionId: string }).replacementRuntimeSessionId)
      return current
    })
    const server = connection(() => [current], reconcileMissing)
    const records: RuntimeEventRecord[] = []
    const pass = convergence(handle, server, records)

    await pass.runOnce(new AbortController().signal)
    await pass.runOnce(new AbortController().signal)

    expect(handle.createSession).toHaveBeenCalledOnce()
    expect(reconcileMissing).toHaveBeenCalledOnce()
    expect(reconcileMissing).toHaveBeenCalledWith("session-1", {
      expectedRunnerId: "runner-1",
      expectedRuntime: "opencode",
      expectedRuntimeSessionId: "runtime-current",
      replacementRuntimeSessionId: "runtime-candidate",
    }, expect.any(AbortSignal))
    expect(records[0]?.runtimeSessionId).toBe("runtime-candidate")
    expect(JSON.stringify(reconcileMissing.mock.calls)).not.toContain("prompt")
    expect(JSON.stringify(reconcileMissing.mock.calls)).not.toContain("input")
  })

  it.each(["deadline-exceeded", "turn-failed", "unavailable-runtime", "incompatible-runtime"])(
    "preserves unknown and does not recover after %s",
    async (kind) => {
      const records: RuntimeEventRecord[] = []
      const handle = runtime({ resolve: () => ({ ok: false, kind }) })
      const server = connection(() => [binding()])

      await convergence(handle, server, records).runOnce(new AbortController().signal)

      expect(handle.createSession).not.toHaveBeenCalled()
      expect(server.reconcileMissingAgentSession).not.toHaveBeenCalled()
      expect(records).toEqual([])
    },
  )

  it("preserves unknown after a transport failure", async () => {
    const records: RuntimeEventRecord[] = []
    const handle = runtime({ resolve: () => { throw new Error("transport failed") } })
    const server = connection(() => [binding()])

    await convergence(handle, server, records).runOnce(new AbortController().signal)

    expect(handle.createSession).not.toHaveBeenCalled()
    expect(server.reconcileMissingAgentSession).not.toHaveBeenCalled()
    expect(records).toEqual([])
  })

  it("preserves unknown when the runtime is unavailable", async () => {
    const records: RuntimeEventRecord[] = []
    const server = connection(() => [binding()])

    await convergence(null, server, records).runOnce(new AbortController().signal)

    expect(server.reconcileMissingAgentSession).not.toHaveBeenCalled()
    expect(records).toEqual([])
  })

  it("does not report activity when a stale missing recovery is rejected", async () => {
    const records: RuntimeEventRecord[] = []
    const handle = runtime({ resolve: () => ({ ok: false, kind: "missing-session" }) })
    const server = connection(
      () => [binding()],
      vi.fn(async () => { throw new Error("409 stale_binding") }),
    )

    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)
    await convergence(handle, server, records).runOnce(new AbortController().signal)
    expect(errorSpy).toHaveBeenCalledWith(
      "agent-session binding reconciliation failed for session-1:",
      expect.objectContaining({ message: "409 stale_binding" }),
    )
    errorSpy.mockRestore()
    expect(records).toEqual([])
  })

  it("does nothing when the reconcile list is corrupt", async () => {
    const records: RuntimeEventRecord[] = []
    const handle = runtime({ resolve: () => ({ ok: true, activeTurn: false }) })
    const server: BindingConvergenceConnection = {
      listAgentSessionsForReconcile: vi.fn(async () => { throw new Error("malformed binding") }),
      reconcileMissingAgentSession: vi.fn(),
      reconcileAgentSessionRuntimeEvents: vi.fn(),
    }

    await expect(convergence(handle, server, records).runOnce(new AbortController().signal))
      .rejects.toThrow("malformed binding")
    expect(handle.resolveSession).not.toHaveBeenCalled()
    expect(records).toEqual([])
  })
})
