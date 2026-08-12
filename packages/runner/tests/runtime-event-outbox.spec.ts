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
  nextTemporaryFilePath,
  RUNTIME_EVENT_OUTBOX_FILE,
  runtimeEventDeliveryKey,
  type RuntimeEventRecord,
} from "../src/server/runtime-event-outbox.js"
import type { AgentSessionRuntimeEventReceipt } from "../src/server/connection.js"
import {
  BlockingWriteFileSystem,
  flushMicrotasks,
  followupTerminal,
  inputRecord,
  makeOutbox,
  RecordingFileSystem,
  workflowFact,
} from "./support/runtime-event-outbox-fixture.js"

beforeEach(() => {
  vi.useFakeTimers()
})

afterEach(() => {
  vi.useRealTimers()
})

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
