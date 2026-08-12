import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import {
  RUNTIME_EVENT_OUTBOX_FILE,
  type RuntimeEventRecord,
} from "../src/server/runtime-event-outbox.js"
import {
  followupTerminal,
  inputRecord,
  makeOutbox,
  RecordingFileSystem,
} from "./support/runtime-event-outbox-fixture.js"

beforeEach(() => {
  vi.useFakeTimers()
})

afterEach(() => {
  vi.useRealTimers()
})

describe("AgentSessionRuntimeEventOutbox — batched streaming deltas", () => {
  function deltaRecord(overrides: Partial<RuntimeEventRecord> = {}): RuntimeEventRecord {
    return {
      id: overrides.id ?? "evt_delta",
      producerFamily: "workflow-session",
      target: { kind: "workflow", projectId: "proj-1", workflowRunId: "wf-1", sessionName: "plan" },
      runtime: "opencode",
      runtimeSessionId: "ses_1",
      work: {
        workId: "work-1",
        taskRunId: "task-1.1",
        runnerId: "runner-1",
        agentSessionId: "agent-session-1",
        inputDeliveryId: "input-1",
        agentTurnId: "turn-1",
        workType: "task",
        stage: "plan",
      },
      event: { type: "reasoning.delta", payload: { text: "x" } },
      acknowledgementPolicy: "matching-receipt",
      ...overrides,
    }
  }

  it("enqueueProducedFactBatch persists the whole batch in one atomic write", async () => {
    const { outbox, fileSystem } = makeOutbox({})
    await outbox.load()
    const writesBefore = fileSystem.journal.filter((e) => e.kind === "write").length

    await outbox.enqueueProducedFactBatch(
      Array.from({ length: 50 }, (_, i) => deltaRecord({ id: `evt_${i}` })),
    )

    const writesAfter = fileSystem.journal.filter((e) => e.kind === "write").length
    expect(writesAfter - writesBefore).toBe(1)
    const parsed = JSON.parse(fileSystem.body(RUNTIME_EVENT_OUTBOX_FILE) ?? "{}") as { entries: Array<{ id: string; sequence: number }> }
    expect(parsed.entries).toHaveLength(50)
    const sequences = parsed.entries.map((e) => e.sequence)
    for (let i = 1; i < sequences.length; i += 1) {
      expect(sequences[i]).toBeGreaterThan(sequences[i - 1])
    }
  })

  it("enqueueProducedFactBatch retains every produced fact in memory and marks the outbox unhealthy when persistence fails", async () => {
    const fileSystem = new RecordingFileSystem()
    fileSystem.failNextWrite = () => new Error("disk full")
    const { outbox } = makeOutbox({ fileSystem })
    await outbox.load()

    await expect(outbox.enqueueProducedFactBatch([
      deltaRecord({ id: "evt_a" }),
      deltaRecord({ id: "evt_b" }),
    ])).rejects.toThrow("disk full")

    expect(outbox.ready()).toBe(false)
    expect(outbox.snapshot()).toHaveLength(2)
  })

  it("drains a batch of same-key deltas in one sendBatch call and one post-ack persist", async () => {
    const sendBatch = vi.fn(async (_records: readonly RuntimeEventRecord[], _signal: AbortSignal) => Array.from({ length: 64 }, () => [{ type: "reasoning.delta" }]))
    const { outbox, fileSystem } = makeOutbox({
      deliver: { send: async () => [{ type: "reasoning.delta" }], sendBatch },
    })
    await outbox.load()
    await outbox.enqueueProducedFactBatch(
      Array.from({ length: 64 }, (_, i) => deltaRecord({ id: `evt_${i}` })),
    )
    const writesAfterEnqueue = fileSystem.journal.filter((e) => e.kind === "write").length

    await outbox.kick()

    expect(sendBatch).toHaveBeenCalledTimes(1)
    expect(sendBatch.mock.calls[0][0]).toHaveLength(64)
    expect(outbox.snapshot()).toHaveLength(0)
    const writesAfterDrain = fileSystem.journal.filter((e) => e.kind === "write").length
    expect(writesAfterDrain - writesAfterEnqueue).toBe(1)
  })

  it("retains records whose acknowledgement policy is not met by the batch receipts", async () => {
    const delta = [{ type: "reasoning.delta" }]
    const sendBatch = vi.fn(async (records: readonly RuntimeEventRecord[], _signal: AbortSignal) =>
      records.map((record) => (record.id === "evt_0" ? [] : delta)),
    )
    const { outbox } = makeOutbox({ deliver: { send: async () => delta, sendBatch } })
    await outbox.load()
    await outbox.enqueueProducedFactBatch(
      Array.from({ length: 64 }, (_, i) => deltaRecord({ id: `evt_${i}` })),
    )

    await outbox.kick()
    const retained = outbox.snapshot()
    expect(retained).toHaveLength(1)
    expect(retained[0].id).toBe("evt_0")
    expect(sendBatch).toHaveBeenCalledTimes(2)
  })

  it("drops the earliest streaming deltas when retention cap is exceeded on load", async () => {
    const fileSystem = new RecordingFileSystem()
    const overcap = Array.from({ length: 6 }, (_, i) => ({
      id: `evt_${i}`,
      producerFamily: "workflow-session",
      target: { kind: "workflow", projectId: "proj-1", workflowRunId: "wf-1", sessionName: "plan" },
      runtime: "opencode",
      runtimeSessionId: "ses_1",
      work: {
        workId: "work-1",
        taskRunId: "task-1.1",
        runnerId: "runner-1",
        agentSessionId: "agent-session-1",
        inputDeliveryId: "input-1",
        agentTurnId: "turn-1",
        workType: "task",
        stage: "plan",
      },
      event: { type: "reasoning.delta", payload: { text: String(i) } },
      acknowledgementPolicy: "matching-receipt",
      sequence: i,
      enqueuedAt: "2026-07-21T00:00:00.000Z",
    }))
    fileSystem.textStore.set(RUNTIME_EVENT_OUTBOX_FILE, JSON.stringify({ version: 1, entries: overcap }))
    const warnSpy = vi.spyOn(console, "warn").mockImplementation(() => undefined)
    try {
      const { outbox } = makeOutbox({ fileSystem, maxRetentionEntries: 3 })
      await outbox.load()
      const remaining = outbox.snapshot()
      expect(remaining).toHaveLength(3)
      expect(remaining.map((r) => r.id)).toEqual(["evt_3", "evt_4", "evt_5"])
    } finally {
      warnSpy.mockRestore()
    }
  })

  it("never drops non-delta facts even when retention cap is exceeded", async () => {
    const fileSystem = new RecordingFileSystem()
    const mixed = [
      {
        id: "input",
        producerFamily: "workflow-session",
        target: { kind: "workflow", projectId: "proj-1", workflowRunId: "wf-1", sessionName: "plan" },
        runtimeSessionId: "ses_1",
        work: null,
        event: { type: "session.input", payload: {} },
        acknowledgementPolicy: "matching-receipt",
        sequence: 0,
        enqueuedAt: "2026-07-21T00:00:00.000Z",
      },
      ...Array.from({ length: 5 }, (_, i) => ({
        id: `delta_${i}`,
        producerFamily: "workflow-session" as const,
        target: { kind: "workflow" as const, projectId: "proj-1", workflowRunId: "wf-1", sessionName: "plan" },
        runtimeSessionId: "ses_1",
        work: null,
        event: { type: "reasoning.delta", payload: { text: String(i) } },
        acknowledgementPolicy: "matching-receipt" as const,
        sequence: i + 1,
        enqueuedAt: "2026-07-21T00:00:00.000Z",
      })),
    ]
    fileSystem.textStore.set(RUNTIME_EVENT_OUTBOX_FILE, JSON.stringify({ version: 1, entries: mixed }))
    const warnSpy = vi.spyOn(console, "warn").mockImplementation(() => undefined)
    try {
      const { outbox } = makeOutbox({ fileSystem, maxRetentionEntries: 3 })
      await outbox.load()
      const types = outbox.snapshot().map((r) => r.event.type)
      expect(types).toContain("session.input")
      expect(types.filter((t) => t === "reasoning.delta")).toHaveLength(2)
    } finally {
      warnSpy.mockRestore()
    }
  })
})

describe("AgentSessionRuntimeEventOutbox — enqueue semantics", () => {
  it("enqueueBeforeExecution rolls back an input that fails local persistence", async () => {
    const fileSystem = new RecordingFileSystem()
    fileSystem.failNextWrite = () => new Error("disk full")
    const { outbox } = makeOutbox({ fileSystem })
    await outbox.load()

    await expect(outbox.enqueueBeforeExecution(inputRecord())).rejects.toThrow(/disk full/)
    expect(outbox.snapshot()).toHaveLength(0)
    expect(outbox.ready()).toBe(false)
  })

  it("enqueueProducedFact retains a produced fact in memory when the snapshot fails and marks the outbox unhealthy", async () => {
    const fileSystem = new RecordingFileSystem()
    const { outbox } = makeOutbox({ fileSystem })
    await outbox.load()
    fileSystem.failNextWrite = () => new Error("disk full")

    await expect(outbox.enqueueProducedFact(followupTerminal())).rejects.toThrow(/disk full/)
    expect(outbox.snapshot()).toHaveLength(1)
    expect(outbox.ready()).toBe(false)
  })
})

describe("AgentSessionRuntimeEventOutbox — autonomous health recovery", () => {
  it("autonomously retries an atomic snapshot under fake time without a new enqueue", async () => {
    const fileSystem = new RecordingFileSystem()
    const { outbox } = makeOutbox({ fileSystem, localRetryDelayMs: 100 })
    await outbox.load()
    await outbox.enqueueBeforeExecution(inputRecord({ id: "evt_recover" }))
    expect(outbox.snapshot()).toHaveLength(1)

    fileSystem.failNextWrite = () => new Error("disk full")
    await expect(outbox.enqueueBeforeExecution(inputRecord({ id: "evt_2" }))).rejects.toThrow(/disk full/)
    expect(outbox.ready()).toBe(false)

    fileSystem.failNextWrite = null
    await vi.advanceTimersByTimeAsync(150)
    expect(outbox.ready()).toBe(true)
    expect(outbox.snapshot()).toHaveLength(1)
  })

  it("unreadable startup snapshot is never replaced with empty state; load retries idempotently", async () => {
    const fileSystem = new RecordingFileSystem()
    fileSystem.textStore.set(RUNTIME_EVENT_OUTBOX_FILE, "{ not json")
    const { outbox } = makeOutbox({ fileSystem, localRetryDelayMs: 100 })
    await outbox.load()
    expect(outbox.ready()).toBe(false)
    expect(fileSystem.textStore.get(RUNTIME_EVENT_OUTBOX_FILE)).toBe("{ not json")

    await vi.advanceTimersByTimeAsync(100)
    expect(outbox.ready()).toBe(false)
    expect(fileSystem.textStore.get(RUNTIME_EVENT_OUTBOX_FILE)).toBe("{ not json")

    fileSystem.textStore.set(RUNTIME_EVENT_OUTBOX_FILE, JSON.stringify({ version: 1, entries: [] }))
    await vi.advanceTimersByTimeAsync(100)
    expect(outbox.ready()).toBe(true)
  })

  it("explicit recovery reloads a healed startup snapshot without waiting for the retry timer", async () => {
    const fileSystem = new RecordingFileSystem()
    fileSystem.textStore.set(RUNTIME_EVENT_OUTBOX_FILE, "{ not json")
    const { outbox } = makeOutbox({ fileSystem, localRetryDelayMs: 100 })
    await outbox.load()
    expect(outbox.ready()).toBe(false)

    fileSystem.textStore.set(RUNTIME_EVENT_OUTBOX_FILE, JSON.stringify({ version: 1, entries: [] }))
    await outbox.recover()

    expect(outbox.ready()).toBe(true)
  })
})

describe("AgentSessionRuntimeEventOutbox — stop", () => {
  it("stop cancels retry timers and HTTP attempts without deleting durable records", async () => {
    const fileSystem = new RecordingFileSystem()
    const { outbox } = makeOutbox({ fileSystem })
    await outbox.load()
    await outbox.enqueueBeforeExecution(inputRecord({ id: "evt_keep" }))
    await outbox.stop()
    expect(fileSystem.body(RUNTIME_EVENT_OUTBOX_FILE)).not.toBeNull()
    expect(outbox.snapshot()).toHaveLength(1)
  })
})
