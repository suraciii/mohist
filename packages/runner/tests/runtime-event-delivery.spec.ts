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
    work: { workId: "work-1", workType: "task", stage: "plan" },
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
})
