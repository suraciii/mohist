import { describe, expect, it, vi } from "vitest"
import type { AgentSessionRuntimeEventReceipt, ServerConnection } from "../src/server/connection.js"
import { createServerRuntimeEventDelivery } from "../src/server/runtime-event-delivery.js"
import type { RuntimeEventRecord } from "../src/server/runtime-event-outbox.js"

function workflowRecord(id: string, type = "reasoning.delta"): RuntimeEventRecord {
  return {
    id,
    producerFamily: "workflow-session",
    target: { kind: "workflow", projectId: "proj-1", workflowRunId: "wf-1", sessionName: "plan" },
    runtimeSessionId: "ses_1",
    runtime: "opencode",
    work: {
      workId: "work-1",
      taskRunId: "task-1.1",
      runnerId: "runner-1",
      inputDeliveryId: "input-1",
      agentTurnId: "turn-1",
      workType: "task",
      stage: "plan",
    },
    event: { type, payload: { text: id } },
    acknowledgementPolicy: "matching-receipt",
  }
}

describe("createServerRuntimeEventDelivery — sendBatch", () => {
  it("posts every workflow record in one batch call", async () => {
    const calls: Array<{ runtimeEvents: Array<{ type: string }> }> = []
    const connection = {
      async workflowAgentSessionRuntimeEvents(
        _projectId: string,
        _workflowRunId: string,
        _sessionName: string,
        body: unknown,
      ): Promise<AgentSessionRuntimeEventReceipt[]> {
        const envelope = body as { runtimeEvents: Array<{ type: string }> }
        calls.push({ runtimeEvents: envelope.runtimeEvents })
        return envelope.runtimeEvents.map((event) => ({ type: event.type }))
      },
    } as unknown as ServerConnection
    const delivery = createServerRuntimeEventDelivery({ connection })
    const records = [workflowRecord("a"), workflowRecord("b"), workflowRecord("c")]

    const result = await delivery.sendBatch!(records, new AbortController().signal)

    expect(calls).toHaveLength(1)
    expect(calls[0].runtimeEvents).toHaveLength(3)
    expect(calls[0].runtimeEvents.map((e) => e.type)).toEqual(["reasoning.delta", "reasoning.delta", "reasoning.delta"])
    // One receipt-set per record, in order.
    expect(result).toHaveLength(3)
    expect(result[0]).toEqual([{ type: "reasoning.delta" }])
  })

  it("send (single) still works unchanged", async () => {
    const sendSpy = vi.fn(async () => [{ type: "reasoning.delta" }])
    const connection = {
      async workflowAgentSessionRuntimeEvents() {
        return await sendSpy()
      },
    } as unknown as ServerConnection
    const delivery = createServerRuntimeEventDelivery({ connection })

    const result = await delivery.send(workflowRecord("a"), new AbortController().signal)

    expect(sendSpy).toHaveBeenCalledTimes(1)
    expect(result).toEqual([{ type: "reasoning.delta" }])
  })

  it("delivers binding reconciliation facts through the runner-scoped session endpoint", async () => {
    const sendSpy = vi.fn(async (_sessionId: string, _body: unknown) => [{ type: "session.activity" }])
    const connection = {
      async reconcileAgentSessionRuntimeEvents(sessionId: string, body: unknown) {
        return await sendSpy(sessionId, body)
      },
    } as unknown as ServerConnection
    const delivery = createServerRuntimeEventDelivery({ connection })
    const record: RuntimeEventRecord = {
      id: "reconcile-1",
      producerFamily: "binding-reconcile",
      target: { kind: "session", sessionId: "session-1" },
      runtimeSessionId: "runtime-1",
      work: null,
      event: { type: "session.activity", payload: { activity: "idle" } },
      acknowledgementPolicy: "successful-response",
    }

    const result = await delivery.send(record, new AbortController().signal)

    expect(sendSpy).toHaveBeenCalledWith("session-1", {
      workId: null,
      workType: null,
      stage: null,
      taskRunId: null,
      inputDeliveryId: null,
      agentTurnId: null,
      runtime: null,
      runtimeSessionId: "runtime-1",
      runtimeEvents: [{ type: "session.activity", payload: { activity: "idle" } }],
    })
    expect(result).toEqual([{ type: "session.activity" }])
  })

  it("returns an empty array for an empty batch without calling the server", async () => {
    const sendSpy = vi.fn(async () => [{ type: "reasoning.delta" }])
    const connection = {
      async workflowAgentSessionRuntimeEvents() {
        return await sendSpy()
      },
    } as unknown as ServerConnection
    const delivery = createServerRuntimeEventDelivery({ connection })

    const result = await delivery.sendBatch!([], new AbortController().signal)

    expect(sendSpy).not.toHaveBeenCalled()
    expect(result).toEqual([])
  })

  it.each([
    ["task attempt", (record: RuntimeEventRecord) => ({ ...record, work: { ...record.work!, taskRunId: "task-2.1" } })],
    ["work", (record: RuntimeEventRecord) => ({ ...record, work: { ...record.work!, workId: "work-2" } })],
    ["Runner", (record: RuntimeEventRecord) => ({ ...record, work: { ...record.work!, runnerId: "runner-2" } })],
    ["input", (record: RuntimeEventRecord) => ({ ...record, work: { ...record.work!, inputDeliveryId: "input-2" } })],
    ["Agent turn", (record: RuntimeEventRecord) => ({ ...record, work: { ...record.work!, agentTurnId: "turn-2" } })],
    ["runtime Session", (record: RuntimeEventRecord) => ({ ...record, runtimeSessionId: "ses_2" })],
  ])("rejects a mixed %s batch before using the batch head envelope", async (_name, change) => {
    const sendSpy = vi.fn(async () => [{ type: "reasoning.delta" }])
    const connection = {
      async workflowAgentSessionRuntimeEvents() {
        return await sendSpy()
      },
    } as unknown as ServerConnection
    const delivery = createServerRuntimeEventDelivery({ connection })
    const first = workflowRecord("a")

    await expect(delivery.sendBatch!([first, change(workflowRecord("b"))], new AbortController().signal))
      .rejects.toThrow("mixed execution identity batch")

    expect(sendSpy).not.toHaveBeenCalled()
  })
})
