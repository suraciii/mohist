import { describe, expect, it } from "vitest"
import { createRuntimeTurnEventProjector } from "../src/runtime/opencode/event-projection.js"
import type { RuntimeGlobalEvent } from "../src/runtime/opencode/event-subscription.js"

function event(type: string, payload: Record<string, unknown>): RuntimeGlobalEvent {
  return { type, sessionID: "ses_1", directory: "/work", payload }
}

describe("OpenCode runtime event projection", () => {
  it("preserves the provider message from the standard session.error DTO", () => {
    const projector = createRuntimeTurnEventProjector("ses_1", "/work")

    const projected = projector.project(event("session.error", {
      sessionID: "ses_1",
      error: {
        name: "APIError",
        data: {
          message: "Insufficient balance",
          statusCode: 402,
          isRetryable: false,
        },
      },
    }))

    expect(projected).toMatchObject([{
      type: "turn.failed",
      payload: {
        code: "turn-failed",
        failureReason: "Insufficient balance",
        message: "Insufficient balance",
        source: "session.error",
      },
    }])
  })

  it("keeps tolerant fallbacks for legacy and malformed session error payloads", () => {
    const projector = createRuntimeTurnEventProjector("ses_1", "/work")

    const legacy = projector.project(event("session.error", {
      error: { message: "legacy provider failure" },
    }))
    const stringPayload = projector.project(event("session.next.step.failed", {
      error: "string provider failure",
    }))
    const unknown = projector.project(event("session.error", { error: { name: "UnknownError" } }))

    expect(legacy[0]?.payload.failureReason).toBe("legacy provider failure")
    expect(stringPayload[0]?.payload.failureReason).toBe("string provider failure")
    expect(unknown[0]?.payload.failureReason).toBe("OpenCode Session failed")
  })

  it("projects assistant model and incremental usage into Mohist events", () => {
    const projector = createRuntimeTurnEventProjector("ses_1", "/work")

    const first = projector.project(event("message.updated", {
      info: {
        id: "msg_1",
        role: "assistant",
        providerID: "openai",
        modelID: "gpt-5.6",
        cost: 0.2,
        tokens: { input: 10, output: 4, reasoning: 2, cache: { read: 3, write: 0 } },
      },
    }))
    const second = projector.project(event("message.updated", {
      info: {
        id: "msg_1",
        role: "assistant",
        providerID: "openai",
        modelID: "gpt-5.6",
        cost: 0.3,
        tokens: { input: 12, output: 7, reasoning: 3, cache: { read: 5, write: 0 } },
      },
    }))

    expect(first.map((item) => item.type)).toEqual(["model.resolved", "usage.updated"])
    expect(first[1]?.payload).toMatchObject({
      inputTokens: 10,
      outputTokens: 4,
      totalTokens: 16,
      cachedReadTokens: 3,
      thoughtTokens: 2,
      costAmount: 0.2,
    })
    expect(second).toHaveLength(1)
    expect(second[0]?.payload).toMatchObject({
      inputTokens: 2,
      outputTokens: 3,
      totalTokens: 6,
      cachedReadTokens: 2,
      thoughtTokens: 1,
    })
    expect(second[0]?.payload.costAmount).toBeCloseTo(0.1)
  })

  it("keeps cost unknown when an assistant message reports tokens only", () => {
    const projector = createRuntimeTurnEventProjector("ses_1", "/work")

    const projected = projector.project(event("message.updated", {
      info: {
        id: "msg_1",
        role: "assistant",
        tokens: { input: 10, output: 4 },
      },
    }))

    expect(projected).toHaveLength(1)
    expect(projected[0]?.type).toBe("usage.updated")
    expect(projected[0]?.payload).toMatchObject({ inputTokens: 10, outputTokens: 4 })
    expect(projected[0]?.payload).not.toHaveProperty("costAmount")
    expect(projected[0]?.payload).not.toHaveProperty("costCurrency")
  })

  it("records a first explicit zero cost even without token growth", () => {
    const projector = createRuntimeTurnEventProjector("ses_1", "/work")

    const projected = projector.project(event("message.updated", {
      info: {
        id: "msg_1",
        role: "assistant",
        cost: 0,
        tokens: { input: 0, output: 0, total: 0 },
      },
    }))

    expect(projected).toHaveLength(1)
    expect(projected[0]?.payload).toMatchObject({ costAmount: 0, costCurrency: "USD" })
  })

  it("charges only new cumulative cost and keeps the baseline across omitted and lower snapshots", () => {
    const projector = createRuntimeTurnEventProjector("ses_1", "/work")
    const snapshots: { input: number; cost?: number }[] = [
      { input: 10, cost: 1.2 },
      { input: 20 },
      { input: 30, cost: 1.2 },
      { input: 40, cost: 1.5 },
      { input: 50, cost: 1.3 },
      { input: 60, cost: 1.5 },
    ]

    const charges = snapshots.map((snapshot) => {
      const projected = projector.project(event("message.updated", {
        info: {
          id: "msg_1",
          role: "assistant",
          tokens: { input: snapshot.input },
          ...(snapshot.cost !== undefined ? { cost: snapshot.cost } : {}),
        },
      }))
      const usage = projected.find((item) => item.type === "usage.updated")
      expect(usage).toBeDefined()
      const value = usage?.payload.costAmount
      return typeof value === "number" ? value : undefined
    })

    expect(charges[0]).toBeCloseTo(1.2)
    expect(charges[1]).toBeUndefined()
    expect(charges[2]).toBeUndefined()
    expect(charges[3]).toBeCloseTo(0.3)
    expect(charges[4]).toBeUndefined()
    expect(charges[5]).toBeUndefined()
    const recorded = charges.reduce<number>((sum, value) => sum + (value ?? 0), 0)
    expect(recorded).toBeCloseTo(1.5)
  })

  it("deduplicates live text deltas against the final prompt snapshot", () => {
    const projector = createRuntimeTurnEventProjector("ses_1", "/work")

    const initial = projector.project(event("message.part.updated", {
      part: { id: "part_1", messageID: "msg_1", sessionID: "ses_1", type: "text", text: "Hello" },
    }))
    const live = projector.project(event("session.next.text.delta", {
      textID: "part_1",
      assistantMessageID: "msg_1",
      delta: " world",
    }))
    const reconciled = projector.reconcile({
      data: {
        parts: [{ id: "part_1", messageID: "msg_1", sessionID: "ses_1", type: "text", text: "Hello world" }],
      },
    })

    expect(live).toEqual([])
    expect([...initial, ...reconciled].map((item) => item.payload.text)).toEqual(["Hello", " world"])
  })

  it("uses one live source per text part when snapshots arrive before deltas", () => {
    const projector = createRuntimeTurnEventProjector("ses_1", "/work")

    const snapshot = projector.project(event("message.part.updated", {
      part: { id: "part_1", messageID: "msg_1", sessionID: "ses_1", type: "text", text: "Hello" },
    }))
    const mirroredDelta = projector.project(event("session.next.text.delta", {
      textID: "part_1",
      assistantMessageID: "msg_1",
      delta: "Hello",
    }))
    const nextSnapshot = projector.project(event("message.part.updated", {
      part: { id: "part_1", messageID: "msg_1", sessionID: "ses_1", type: "text", text: "Hello world" },
    }))

    expect([...snapshot, ...mirroredDelta, ...nextSnapshot].map((item) => item.payload.text))
      .toEqual(["Hello", " world"])
  })

  it("uses message part deltas after the part type is known", () => {
    const projector = createRuntimeTurnEventProjector("ses_1", "/work")
    projector.project(event("message.part.updated", {
      part: { id: "part_1", messageID: "msg_1", sessionID: "ses_1", type: "reasoning", text: "" },
    }))

    const projected = projector.project(event("message.part.delta", {
      sessionID: "ses_1",
      messageID: "msg_1",
      partID: "part_1",
      field: "text",
      delta: "thinking",
    }))

    expect(projected).toMatchObject([{
      type: "reasoning.delta",
      payload: { text: "thinking", partId: "part_1", messageId: "msg_1" },
    }])
    expect(projector.reconcile({
      data: {
        parts: [{ id: "part_1", messageID: "msg_1", sessionID: "ses_1", type: "reasoning", text: "thinking" }],
      },
    })).toEqual([])
  })

  it("projects OpenCode tool state into stable tool-call events", () => {
    const projector = createRuntimeTurnEventProjector("ses_1", "/work")

    const started = projector.project(event("message.part.updated", {
      part: {
        id: "part_tool",
        messageID: "msg_1",
        sessionID: "ses_1",
        type: "tool",
        callID: "call_1",
        tool: "read",
        state: { status: "running", input: { file: "a.ts" }, raw: "", time: { start: 1 } },
      },
    }))
    const completed = projector.project(event("message.part.updated", {
      part: {
        id: "part_tool",
        messageID: "msg_1",
        sessionID: "ses_1",
        type: "tool",
        callID: "call_1",
        tool: "read",
        state: { status: "completed", input: { file: "a.ts" }, output: "ok", title: "Read a.ts" },
      },
    }))

    expect(started[0]).toMatchObject({ type: "tool_call.started", payload: { toolCallId: "call_1", toolName: "read", status: "running" } })
    expect(completed[0]).toMatchObject({ type: "tool_call.completed", payload: { toolCallId: "call_1", status: "completed", rawOutput: "ok" } })
  })

  it("carries tool identity and input into next-api completion events", () => {
    const projector = createRuntimeTurnEventProjector("ses_1", "/work")

    projector.project(event("session.next.tool.called", {
      callID: "call_1",
      tool: "read",
      input: { file: "a.ts" },
    }))
    const completed = projector.project(event("session.next.tool.success", {
      callID: "call_1",
      result: "ok",
    }))

    expect(completed[0]).toMatchObject({
      type: "tool_call.completed",
      payload: {
        toolCallId: "call_1",
        toolName: "read",
        rawInput: { file: "a.ts" },
        rawOutput: "ok",
      },
    })
  })
})
