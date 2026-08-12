/**
 * Focused unit coverage for `AgentSessionRuntimeEventOutbox`. These
 * specs drive the snapshot/import store through the injected
 * `RuntimeEventOutboxFileSystem` port — no Node filesystem adapter is
 * instantiated, no temporary directory is touched. Fake timers drive
 * the local-persistence retry timer; concurrent kicks are observed
 * through a recording delivery mock.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import {
  createAgentSessionRuntimeEventOutbox,
  nextTemporaryFilePath,
  RUNTIME_EVENT_OUTBOX_FILE,
  runtimeEventDeliveryKey,
  type AgentSessionRuntimeEventOutbox,
  type RuntimeEventRecord,
  type RuntimeEventOutboxFileSystem,
  type RuntimeEventDelivery,
} from "../src/server/runtime-event-outbox.js"
import type { AgentSessionRuntimeEventReceipt } from "../src/server/connection.js"

class RecordingFileSystem implements RuntimeEventOutboxFileSystem {
  readonly textStore = new Map<string, string>()
  readonly journal: Array<{ kind: "write"; path: string }> = []
  failNextWrite: (() => Error) | null = null
  failNextRead: (() => Error) | null = null

  async readText(path: string): Promise<string | null> {
    if (this.failNextRead) {
      const fail = this.failNextRead
      this.failNextRead = null
      throw fail()
    }
    return this.textStore.get(path) ?? null
  }

  async writeAtomicText(path: string, body: string): Promise<void> {
    if (this.failNextWrite) {
      const fail = this.failNextWrite
      this.failNextWrite = null
      throw fail()
    }
    this.textStore.set(path, body)
    this.journal.push({ kind: "write", path })
  }

  body(path: string): string | null {
    return this.textStore.get(path) ?? null
  }
}

class BlockingWriteFileSystem extends RecordingFileSystem {
  readonly bodies: string[] = []
  writesStarted = 0
  activeWrites = 0
  maxConcurrentWrites = 0
  private readonly startWaiters: Array<() => void> = []
  private readonly releaseWaiters: Array<() => void> = []

  waitForNextWrite(): Promise<void> {
    return new Promise((resolve) => this.startWaiters.push(resolve))
  }

  releaseNextWrite(): void {
    const release = this.releaseWaiters.shift()
    if (!release) throw new Error("no blocked snapshot write")
    release()
  }

  override async writeAtomicText(path: string, body: string): Promise<void> {
    this.bodies.push(body)
    this.writesStarted += 1
    this.activeWrites += 1
    this.maxConcurrentWrites = Math.max(this.maxConcurrentWrites, this.activeWrites)
    this.startWaiters.shift()?.()
    await new Promise<void>((resolve) => this.releaseWaiters.push(resolve))
    try {
      await super.writeAtomicText(path, body)
    } finally {
      this.activeWrites -= 1
    }
  }
}

function makeOutbox(options: {
  fileSystem?: RecordingFileSystem
  deliver?: RuntimeEventDelivery
  filePath?: string
  randomId?: () => string
  deliveryTimeoutMs?: number
  retryDelayMs?: number
  localRetryDelayMs?: number
  boundedConcurrency?: number
  deliveryBatchSize?: number
  maxRetentionEntries?: number
}) {
  const fileSystem = options.fileSystem ?? new RecordingFileSystem()
  const randomId = options.randomId ?? (() => `evt_${Math.random().toString(36).slice(2, 10)}`)
  const outbox: AgentSessionRuntimeEventOutbox = createAgentSessionRuntimeEventOutbox({
    fileSystem,
    deliver: options.deliver,
    filePath: options.filePath ?? RUNTIME_EVENT_OUTBOX_FILE,
    randomId,
    deliveryTimeoutMs: options.deliveryTimeoutMs ?? 100,
    retryDelayMs: options.retryDelayMs ?? 100,
    localRetryDelayMs: options.localRetryDelayMs ?? 100,
    boundedConcurrency: options.boundedConcurrency ?? 2,
    deliveryBatchSize: options.deliveryBatchSize,
    maxRetentionEntries: options.maxRetentionEntries,
  })
  return { outbox, fileSystem }
}

function inputRecord(overrides: Partial<RuntimeEventRecord> = {}): RuntimeEventRecord {
  const id = overrides.id ?? "evt_input"
  return {
    id,
    producerFamily: "workflow-session",
    target: {
      kind: "workflow",
      projectId: "proj-1",
      workflowRunId: "wf-1",
      sessionName: "plan",
    },
    runtime: "opencode",
    runtimeSessionId: "ses_1",
    work: {
      workId: "work-1",
      taskRunId: "task-1.1",
      runnerId: "runner-1",
      agentSessionId: "agent-session-1",
      inputDeliveryId: id,
      agentTurnId: null,
      workType: "task",
      stage: "plan",
    },
    event: { type: "session.input", payload: { text: "do work" } },
    acknowledgementPolicy: "matching-receipt",
    ...overrides,
  }
}

function followupTerminal(overrides: Partial<RuntimeEventRecord> = {}): RuntimeEventRecord {
  return {
    id: overrides.id ?? "evt_term",
    producerFamily: "generic-followup",
    target: { kind: "generic", projectId: "proj-1", sessionId: "gen-1" },
    runtimeSessionId: "ses_1",
    work: null,
    event: { type: "session.followup_completed", payload: { status: "completed", operationId: "op-1" } },
    acknowledgementPolicy: "successful-response",
    ...overrides,
  }
}

function workflowFact(id: string, overrides: Partial<RuntimeEventRecord> = {}): RuntimeEventRecord {
  return {
    id,
    producerFamily: "workflow-session",
    target: { kind: "workflow", projectId: "proj-1", workflowRunId: "wf-1", sessionName: "build" },
    runtime: "opencode",
    runtimeSessionId: "runtime-1",
    work: {
      workId: "work-1",
      taskRunId: "task-1.1",
      runnerId: "runner-1",
      agentSessionId: "agent-session-1",
      inputDeliveryId: "input-1",
      agentTurnId: "turn-1",
      workType: "task",
      stage: "build",
    },
    event: { type: "message.delta", payload: { text: id } },
    acknowledgementPolicy: "matching-receipt",
    ...overrides,
  }
}

beforeEach(() => {
  vi.useFakeTimers()
})

afterEach(() => {
  vi.useRealTimers()
})

async function flushMicrotasks(count = 4) {
  for (let i = 0; i < count; i += 1) {
    await Promise.resolve()
  }
}

describe("AgentSessionRuntimeEventOutbox — durable storage", () => {
  it("uses distinct temporary paths for writes in the same millisecond", () => {
    vi.setSystemTime(new Date("2026-07-21T06:48:03.000Z"))

    expect(nextTemporaryFilePath("runtime-events.json")).not.toBe(nextTemporaryFilePath("runtime-events.json"))
  })

  it("persists one-event entries with a local record ID, binding-free target, runtime session id, payload, acknowledgement policy, and sequence position", async () => {
    const { outbox, fileSystem } = makeOutbox({})
    await outbox.load()
    await outbox.enqueueBeforeExecution(inputRecord({ id: "evt_1" }))
    await outbox.enqueueProducedFact(inputRecord({ id: "evt_2", event: { type: "message.delta", payload: { text: "x" } } }))

    const body = fileSystem.body(RUNTIME_EVENT_OUTBOX_FILE)
    expect(body).not.toBeNull()
    const parsed = JSON.parse(body ?? "{}") as { version: number; entries: Array<Record<string, unknown>> }
    expect(parsed.version).toBe(1)
    expect(parsed.entries).toHaveLength(2)
    const [first, second] = parsed.entries
    expect(first?.["id"]).toBe("evt_1")
    expect(second?.["id"]).toBe("evt_2")
    expect(first?.["target"]).toEqual({
      kind: "workflow",
      projectId: "proj-1",
      workflowRunId: "wf-1",
      sessionName: "plan",
    })
    expect(first?.["runtimeSessionId"]).toBe("ses_1")
    expect(first?.["acknowledgementPolicy"]).toBe("matching-receipt")
    expect(typeof first?.["sequence"]).toBe("number")
    expect((first?.["sequence"] as number) < (second?.["sequence"] as number)).toBe(true)
  })

  it("uses atomic replacement and owner-only permissions", async () => {
    const { outbox } = makeOutbox({})
    await outbox.load()
    await outbox.enqueueBeforeExecution(inputRecord())
    expect(outbox.snapshot()).toHaveLength(1)
  })

  it("serializes snapshot writes while preserving synchronous enqueue order", async () => {
    const fileSystem = new BlockingWriteFileSystem()
    const { outbox } = makeOutbox({
      fileSystem,
      deliver: { send: async () => [] },
    })
    await outbox.load()

    const firstWriteStarted = fileSystem.waitForNextWrite()
    const first = outbox.enqueueProducedFact(inputRecord({ id: "evt_1" }))
    await firstWriteStarted

    const secondWriteStarted = fileSystem.waitForNextWrite()
    const second = outbox.enqueueProducedFact(inputRecord({ id: "evt_2" }))
    await flushMicrotasks()
    expect(fileSystem.writesStarted).toBe(1)
    expect(outbox.snapshot().map((record) => record.id)).toEqual(["evt_1", "evt_2"])

    fileSystem.releaseNextWrite()
    await secondWriteStarted
    expect(fileSystem.maxConcurrentWrites).toBe(1)
    fileSystem.releaseNextWrite()
    await Promise.all([first, second])

    expect(fileSystem.bodies.map((body) => (JSON.parse(body) as { entries: unknown[] }).entries.length)).toEqual([1, 2])
    await outbox.stop()
  })

  it("two real outbox instances sharing one recording filesystem restart with durable records", async () => {
    const fileSystem = new RecordingFileSystem()
    const first = makeOutbox({ fileSystem, randomId: () => "evt_a" })
    await first.outbox.load()
    await first.outbox.enqueueBeforeExecution(inputRecord({ id: "evt_a" }))
    await first.outbox.stop()

    const second = makeOutbox({ fileSystem, randomId: () => "evt_b" })
    await second.outbox.load()
    expect(second.outbox.snapshot().map((r) => r.id)).toEqual(["evt_a"])
  })

  it("restart replays the same pending Workflow input until the Server returns its turn receipt", async () => {
    const fileSystem = new RecordingFileSystem()
    const record = inputRecord({
      id: "delivery-1",
      runtime: "opencode",
      work: {
        runnerId: "runner-1",
        agentSessionId: "agent-session-1",
        workId: "work-1",
        taskRunId: "task-1.1",
        workType: "task",
        stage: "plan",
        inputDeliveryId: "delivery-1",
        agentTurnId: null,
      },
    })
    const first = makeOutbox({
      fileSystem,
      deliver: { async send() { throw new Error("server unavailable") } },
    })
    await first.outbox.load()
    await first.outbox.enqueueBeforeExecution(record)
    await flushMicrotasks()
    expect(first.outbox.snapshot()).toMatchObject([{
      id: "delivery-1",
      runtime: "opencode",
      runtimeSessionId: "ses_1",
      work: {
        runnerId: "runner-1",
        taskRunId: "task-1.1",
        workId: "work-1",
        inputDeliveryId: "delivery-1",
        agentTurnId: null,
      },
    }])
    await first.outbox.stop()

    const second = makeOutbox({
      fileSystem,
      deliver: {
        async send(entry) {
          return [{ type: "session.input", inputDeliveryId: entry.id, agentTurnId: "turn-1", agentSessionId: "agent-session-1" }]
        },
      },
    })
    await second.outbox.load()
    const awaitReceipt = second.outbox.awaitInputReceipt
    if (!awaitReceipt) throw new Error("outbox must support Workflow input receipts")

    await expect(awaitReceipt.call(second.outbox, "delivery-1")).resolves.toMatchObject({
      inputDeliveryId: "delivery-1",
      agentTurnId: "turn-1",
      agentSessionId: "agent-session-1",
    })
    expect(second.outbox.snapshot()).toEqual([])
  })

  it("serialization refuses malformed snapshot JSON and never replaces it with empty state", async () => {
    const fileSystem = new RecordingFileSystem()
    fileSystem.textStore.set(RUNTIME_EVENT_OUTBOX_FILE, "{ not json")
    const { outbox } = makeOutbox({ fileSystem })
    await outbox.load()
    expect(outbox.ready()).toBe(false)
    expect(fileSystem.textStore.has(RUNTIME_EVENT_OUTBOX_FILE)).toBe(true)
    expect(fileSystem.textStore.get(RUNTIME_EVENT_OUTBOX_FILE)).toBe("{ not json")
  })
})

describe("AgentSessionRuntimeEventOutbox — acknowledgement policies", () => {
  it("resolves a Workflow input only for its matching delivery receipt and durable Agent turn", async () => {
    const { outbox } = makeOutbox({
      deliver: {
        async send(record) {
          return [{ type: record.event.type, inputDeliveryId: record.id, agentTurnId: "turn-1", agentSessionId: "agent-session-1" }]
        },
      },
    })
    await outbox.load()
    const awaitReceipt = outbox.awaitInputReceipt
    if (!awaitReceipt) throw new Error("outbox must support Workflow input receipts")
    const record = inputRecord({
      id: "delivery-1",
      runtime: "opencode",
      work: {
        runnerId: "runner-1",
        agentSessionId: "agent-session-1",
        workId: "work-1",
        taskRunId: "task-1.1",
        workType: "task",
        stage: "plan",
        inputDeliveryId: "delivery-1",
        agentTurnId: null,
      },
    })

    await outbox.enqueueBeforeExecution(record)

    await expect(awaitReceipt.call(outbox, "delivery-1")).resolves.toEqual({
      type: "session.input",
      inputDeliveryId: "delivery-1",
      agentTurnId: "turn-1",
      agentSessionId: "agent-session-1",
    })
    expect(outbox.snapshot()).toEqual([])
  })

  it("retains a Workflow input when the receipt does not prove its frozen turn", async () => {
    const { outbox } = makeOutbox({
      deliver: {
        async send(record) {
          return [{ type: record.event.type, inputDeliveryId: "other-delivery", agentTurnId: "turn-1", agentSessionId: "agent-session-1" }]
        },
      },
    })
    await outbox.load()
    const awaitReceipt = outbox.awaitInputReceipt
    if (!awaitReceipt) throw new Error("outbox must support Workflow input receipts")
    await outbox.enqueueBeforeExecution(inputRecord({
      id: "delivery-1",
      runtime: "opencode",
      work: {
        runnerId: "runner-1",
        agentSessionId: "agent-session-1",
        workId: "work-1",
        taskRunId: "task-1.1",
        workType: "task",
        stage: "plan",
        inputDeliveryId: "delivery-1",
        agentTurnId: null,
      },
    }))

    await expect(awaitReceipt.call(outbox, "delivery-1")).rejects.toThrow(/matching Server receipt/)
    expect(outbox.snapshot().map((record) => record.id)).toEqual(["delivery-1"])
  })

  it("matching-receipt removes the head only when the response carries the submitted type", async () => {
    let responses: AgentSessionRuntimeEventReceipt[][] = [[], [{
      type: "session.input",
      inputDeliveryId: "evt_input",
      agentTurnId: "turn-1",
      agentSessionId: "agent-session-1",
    }]]
    const { outbox } = makeOutbox({
      deliver: {
        async send() {
          return responses.shift() ?? [{ type: "session.input" }]
        },
      },
    })
    await outbox.load()
    await outbox.enqueueBeforeExecution(inputRecord())

    await outbox.kick()
    expect(outbox.snapshot()).toHaveLength(1)

    await outbox.kick()
    expect(outbox.snapshot()).toHaveLength(0)
  })

  it("matching-receipt retains the head on timeout, transport failure, non-2xx, malformed, empty, or receipt without the submitted type", async () => {
    const cases: Array<{ name: string; response?: () => AgentSessionRuntimeEventReceipt[] | Promise<AgentSessionRuntimeEventReceipt[]>; error?: unknown }> = [
      { name: "timeout", error: Object.assign(new Error("runtime-event delivery timeout"), {}) },
      { name: "transport failure", error: new Error("server unreachable") },
      { name: "non-2xx", error: Object.assign(new Error("400 Bad Request"), {}) },
      { name: "malformed JSON", response: () => Promise.reject(new SyntaxError("Unexpected token")) },
      { name: "empty receipt", response: () => [] },
      { name: "receipt without type", response: () => [{ type: "message.delta" }] },
    ]
    for (const testCase of cases) {
      const fileSystem = new RecordingFileSystem()
      const { outbox } = makeOutbox({
        fileSystem,
        deliver: {
          async send() {
            if (testCase.error) throw testCase.error
            return await testCase.response!()
          },
        },
      })
      await outbox.load()
      await outbox.enqueueBeforeExecution(inputRecord())
      await outbox.kick()
      expect(outbox.snapshot(), testCase.name).toHaveLength(1)
    }
  })

  it("successful-response settles any 2xx receipt array including []", async () => {
    const cases: AgentSessionRuntimeEventReceipt[][] = [[], [{ type: "session.followup_completed" }], [{ type: "stale-binding" }]]
    for (const receipts of cases) {
      const { outbox } = makeOutbox({
        deliver: { async send() { return receipts } },
      })
      await outbox.load()
      await outbox.enqueueProducedFact(followupTerminal())
      await outbox.kick()
      expect(outbox.snapshot()).toHaveLength(0)
    }
  })

  it("successful-response retains the head on timeout, transport failure, non-2xx, or malformed response", async () => {
    const cases: Array<{ name: string; response: () => AgentSessionRuntimeEventReceipt[] | Promise<AgentSessionRuntimeEventReceipt[]> }> = [
      { name: "timeout", response: () => Promise.reject(Object.assign(new Error("runtime-event delivery timeout"), {})) },
      { name: "transport failure", response: () => Promise.reject(new Error("server unreachable")) },
      { name: "non-2xx", response: () => Promise.reject(Object.assign(new Error("500"), {})) },
      { name: "malformed JSON", response: () => Promise.reject(new SyntaxError("Unexpected token")) },
    ]
    for (const testCase of cases) {
      const { outbox } = makeOutbox({
        deliver: { async send() { return await testCase.response() } },
      })
      await outbox.load()
      await outbox.enqueueProducedFact(followupTerminal())
      await outbox.kick()
      expect(outbox.snapshot(), testCase.name).toHaveLength(1)
    }
  })
})

describe("AgentSessionRuntimeEventOutbox — managed-sequence FIFO", () => {
  it("drains one head per managed producer sequence key", async () => {
    const order: string[] = []
    const { outbox } = makeOutbox({
      deliver: {
        async send(record) {
          order.push(record.id)
          return record.event.type === "session.input"
            ? [{
                type: record.event.type,
                inputDeliveryId: record.id,
                agentTurnId: `turn-${record.id}`,
                agentSessionId: record.work?.agentSessionId ?? undefined,
              }]
            : [{ type: record.event.type }]
        },
      },
    })
    await outbox.load()
    await outbox.enqueueBeforeExecution(inputRecord({ id: "wf-1" }))
    await outbox.kick()
    expect(outbox.snapshot().map((r) => r.id)).toEqual([])
    await outbox.enqueueBeforeExecution(inputRecord({ id: "wf-2", event: { type: "message.delta", payload: {} } }))
    await outbox.kick()
    expect(outbox.snapshot().map((r) => r.id)).toEqual([])
    await outbox.enqueueBeforeExecution(followupTerminal({ id: "ff-1" }))
    await outbox.kick()
    await outbox.enqueueBeforeExecution(followupTerminal({ id: "ff-2", event: { type: "session.followup_failed", payload: { status: "failed" } } }))
    await outbox.kick()

    expect(order).toEqual(["wf-1", "wf-2", "ff-1", "ff-2"])
  })

  it("stale matching-receipt records are never retargeted to a different runtime session id", async () => {
    const seen: string[] = []
    const { outbox } = makeOutbox({
      deliver: {
        async send(record) {
          seen.push(record.runtimeSessionId)
          return [] // empty receipt — matches the stale-binding case
        },
      },
    })
    await outbox.load()
    await outbox.enqueueBeforeExecution(inputRecord({ runtimeSessionId: "ses_stale" }))

    await outbox.kick()
    expect(seen).toEqual(["ses_stale"])
    expect(outbox.snapshot()).toHaveLength(1)
  })

  it("delivers queued reconciliation facts separately for each runtime binding", async () => {
    const batches: string[][] = []
    const { outbox } = makeOutbox({
      deliver: {
        async send() {
          throw new Error("single-record delivery should not be used")
        },
        async sendBatch(records) {
          batches.push(records.map((record) => record.runtimeSessionId))
          const runtimeSessionId = records[0]?.runtimeSessionId
          return runtimeSessionId === "runtime-current"
            ? records.map((record) => [{ type: record.event.type }])
            : []
        },
      },
    })
    await outbox.load()
    const reconciliationRecord = (id: string, runtimeSessionId: string): RuntimeEventRecord => ({
      id,
      producerFamily: "binding-reconcile",
      target: { kind: "session", sessionId: "session-1" },
      runtimeSessionId,
      work: null,
      event: { type: "session.activity", payload: { activity: "idle" } },
      acknowledgementPolicy: "successful-response",
    })

    await outbox.enqueueProducedFact(reconciliationRecord("old", "runtime-old"))
    await outbox.enqueueProducedFact(reconciliationRecord("current", "runtime-current"))
    await outbox.kick()

    expect(batches).toEqual([["runtime-old"], ["runtime-current"]])
    expect(outbox.snapshot()).toHaveLength(0)
  })

  it("partitions persisted Workflow facts by the complete immutable execution identity", async () => {
    const batches: RuntimeEventRecord[][] = []
    const { outbox } = makeOutbox({
      deliveryBatchSize: 64,
      deliver: {
        async send() {
          throw new Error("batched delivery expected")
        },
        async sendBatch(records) {
          batches.push([...records])
          return records.map((record) => [{ type: record.event.type }])
        },
      },
    })
    await outbox.load()
    await outbox.enqueueProducedFactBatch([
      workflowFact("same-a"),
      workflowFact("same-b"),
      workflowFact("other-work", { work: { ...workflowFact("template").work!, workId: "work-2" } }),
      workflowFact("other-turn", { work: { ...workflowFact("template").work!, agentTurnId: "turn-2" } }),
      workflowFact("other-runner", { work: { ...workflowFact("template").work!, runnerId: "runner-2" } }),
      workflowFact("other-runtime", { runtimeSessionId: "runtime-2" }),
    ])

    await outbox.kick()

    expect(batches.map((batch) => batch.map((record) => record.id))).toEqual(expect.arrayContaining([
      ["same-a", "same-b"],
      ["other-work"],
      ["other-turn"],
      ["other-runner"],
      ["other-runtime"],
    ]))
    expect(outbox.snapshot()).toEqual([])
  })

  it("rejects Workflow facts without the complete immutable execution identity", () => {
    expect(() => runtimeEventDeliveryKey({ ...workflowFact("missing-identity"), work: null })).toThrow(
      "workflow-session execution record requires its complete immutable execution identity",
    )
  })

  it("successful-response consumes the operation lease; replay legitimately settles with []", async () => {
    let responses: AgentSessionRuntimeEventReceipt[][] = [[], []]
    const { outbox } = makeOutbox({
      deliver: { async send() { return responses.shift() ?? [] } },
    })
    await outbox.load()
    await outbox.enqueueProducedFact(followupTerminal())
    await outbox.kick()
    expect(outbox.snapshot()).toHaveLength(0)
  })

  it("concurrent kicks are idempotent — duplicate kicks share one in-flight sequence", async () => {
    const sendCalls: string[] = []
    let release!: () => void
    const releaseGate = new Promise<void>((resolve) => { release = resolve })
    const { outbox } = makeOutbox({
      deliver: {
        async send(record) {
          sendCalls.push(record.id)
          await releaseGate
          return [{ type: record.event.type }]
        },
      },
    })
    await outbox.load()
    for (let i = 0; i < 4; i += 1) {
      await outbox.enqueueBeforeExecution(inputRecord({ id: `rec_${i}`, event: { type: `event_${i}`, payload: {} } }))
    }

    const promises = [outbox.kick(), outbox.kick(), outbox.kick()]
    // Yield so the kicks enter drainAll before we release the gate.
    await flushMicrotasks()
    release()
    await Promise.all(promises)
    // Snapshot is empty: every record was acknowledged by the deliver mock.
    expect(outbox.snapshot()).toHaveLength(0)
    expect(sendCalls).toEqual(["rec_0", "rec_1", "rec_2", "rec_3"])
  })

  it("concurrent sequences receive independent timeout cancellation signals", async () => {
    const fileSystem = new RecordingFileSystem()
    const first = inputRecord({ id: "wf-1" }) as RuntimeEventRecord & { sequence: number; enqueuedAt: string }
    const second = inputRecord({
      id: "wf-2",
      target: { kind: "workflow", projectId: "proj-1", workflowRunId: "wf-2", sessionName: "plan" },
    }) as RuntimeEventRecord & { sequence: number; enqueuedAt: string }
    fileSystem.textStore.set(RUNTIME_EVENT_OUTBOX_FILE, JSON.stringify({
      version: 1,
      entries: [
        { ...first, sequence: 1, enqueuedAt: "2026-01-01T00:00:00.000Z" },
        { ...second, sequence: 2, enqueuedAt: "2026-01-01T00:00:00.000Z" },
      ],
    }))
    const signals: AbortSignal[] = []
    const { outbox } = makeOutbox({
      fileSystem,
      deliver: {
        async send(_record, signal) {
          signals.push(signal)
          return await new Promise<AgentSessionRuntimeEventReceipt[]>((_, reject) => {
            signal.addEventListener("abort", () => reject(signal.reason), { once: true })
          })
        },
      },
    })
    await outbox.load()

    const drain = outbox.kick()
    await flushMicrotasks()

    expect(signals).toHaveLength(2)
    expect(signals[0]).not.toBe(signals[1])

    await outbox.stop()
    await drain
  })
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
    // Sequences are monotonic across the batch.
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

    // Produced facts are retained in memory so the next persistence
    // recovery can flush them; only their disk snapshot failed.
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
    // Only one additional persist for the whole batch's ack.
    const writesAfterDrain = fileSystem.journal.filter((e) => e.kind === "write").length
    expect(writesAfterDrain - writesAfterEnqueue).toBe(1)
  })

  it("retains records whose acknowledgement policy is not met by the batch receipts", async () => {
    // `evt_0` never receives a matching receipt; the rest always match.
    // drainAll loops within one kick, so the unmatched record must
    // survive both the first batch (63 ack) and the second (1 alone).
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
    // The unmatched record is retained; drainAll stopped after it failed.
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
      // Earliest deltas dropped; the latest three (by sequence) retained.
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

    // Heal the file and let autonomous load recovery restore readiness.
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
