import { describe, expect, it } from "vitest"
import { validatePiSessionContents } from "../src/runtime/pi/sdk.js"

function validSession(entries: readonly object[] = []): string {
  return [
    { type: "session", version: 3, id: "session-1", timestamp: "2026-07-22T00:00:00.000Z", cwd: "/workspace" },
    ...entries,
  ].map((entry) => JSON.stringify(entry)).join("\n")
}

describe("Pi session file validation", () => {
  it("accepts a complete session tree", () => {
    const result = validatePiSessionContents(validSession([
      { type: "message", id: "message-1", parentId: null, timestamp: "2026-07-22T00:00:01.000Z", message: { role: "user", content: "hello" } },
      { type: "model_change", id: "model-1", parentId: "message-1", timestamp: "2026-07-22T00:00:02.000Z", provider: "fake", modelId: "model" },
    ]))

    expect(result).toEqual({ entryCount: 2, sessionId: "session-1" })
  })

  it("rejects empty, malformed, and incomplete persisted sessions", () => {
    expect(() => validatePiSessionContents("")).toThrow("session header")
    expect(() => validatePiSessionContents('{"type":"session"}\n{"type":"message"}')).toThrow("invalid id")
    expect(() => validatePiSessionContents(validSession([
      { type: "message", id: "message-1", parentId: "missing", timestamp: "2026-07-22T00:00:01.000Z", message: { role: "user" } },
    ]))).toThrow("unknown parentId")
    expect(() => validatePiSessionContents(validSession(), "different-session")).toThrow("unexpected session id")
  })
})
