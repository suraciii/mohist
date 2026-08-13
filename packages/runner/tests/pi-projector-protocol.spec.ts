import { describe, expect, it } from "vitest"
import { createPiProjector } from "../src/runtime/pi/projector.js"

describe("Pi runtime projector protocol", () => {
  it("maps Pi text and thinking deltas to Mohist transcript events", () => {
    const projector = createPiProjector("/virtual/session", "/workspace")
    projector.project({ type: "message_start", message: { role: "assistant" } })

    const text = projector.project({
      type: "message_update",
      message: { role: "assistant" },
      assistantMessageEvent: { type: "text_delta", contentIndex: 0, delta: "hello" },
    })
    const thinking = projector.project({
      type: "message_update",
      message: { role: "assistant" },
      assistantMessageEvent: { type: "thinking_delta", contentIndex: 1, delta: "reason" },
    })

    expect(text[0]).toMatchObject({ type: "message.delta", payload: { text: "hello" } })
    expect(thinking[0]).toMatchObject({ type: "reasoning.delta", payload: { text: "reason" } })
    expect([...text, ...thinking].every((event) => !["message", "status", "tool"].includes(event.type))).toBe(true)
  })

  it("maps the Pi tool execution lifecycle to started, updated, and completed events", () => {
    const projector = createPiProjector("/virtual/session", "/workspace")
    const started = projector.project({ type: "tool_execution_start", toolCallId: "call-1", toolName: "read", args: { path: "README.md" } })
    const updated = projector.project({ type: "tool_execution_update", toolCallId: "call-1", toolName: "read", args: { path: "README.md" }, partialResult: "partial" })
    const completed = projector.project({ type: "tool_execution_end", toolCallId: "call-1", toolName: "read", result: "contents", isError: false })

    expect(started[0]).toMatchObject({ type: "tool_call.started", payload: { toolCallId: "call-1", status: "running", rawInput: { path: "README.md" } } })
    expect(updated[0]).toMatchObject({ type: "tool_call.updated", payload: { toolCallId: "call-1", status: "running", rawOutput: "partial" } })
    expect(completed[0]).toMatchObject({ type: "tool_call.completed", payload: { toolCallId: "call-1", status: "completed", rawOutput: "contents" } })
    expect([...started, ...updated, ...completed].every((event) => event.type.startsWith("tool_call."))).toBe(true)
  })

  it("reconciles a final assistant snapshot without duplicating streamed text", () => {
    const projector = createPiProjector("/virtual/session", "/workspace")
    projector.project({ type: "message_start", message: { role: "assistant" } })
    projector.project({
      type: "message_update",
      message: { role: "assistant" },
      assistantMessageEvent: { type: "text_delta", contentIndex: 0, delta: "hello" },
    })

    const facts = projector.reconcile([{ role: "assistant", content: [{ type: "text", text: "hello world" }], usage: { input: 1 } }])

    expect(facts).toHaveLength(2)
    expect(facts).toContainEqual(expect.objectContaining({ type: "message.delta", payload: { text: " world", partId: "assistant-1:message.delta:0", messageId: "assistant-1" } }))
    expect(facts).toContainEqual(expect.objectContaining({ type: "usage.updated", payload: { inputTokens: 1 } }))
  })
})
