import { afterEach, describe, expect, it, vi } from "vitest"
import { ServerConnection } from "./connection.js"

const options = {
  serverUrl: "http://server",
  runnerId: "runner-1",
  runnerRoot: "/tmp/runner",
  pollIntervalMs: 1,
  heartbeatIntervalMs: 1,
  dispatchLivenessProbeIntervalMs: 1,
}

const signal = new AbortController().signal

describe("ServerConnection workflow runtime events", () => {
  afterEach(() => vi.restoreAllMocks())

  it("returns accepted entries when every submitted fact is accepted", async () => {
    vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response(JSON.stringify([
      { id: "1", type: "session.input" },
      { id: "2", type: "message.delta" },
    ]), { status: 200, headers: { "content-type": "application/json" } }))

    const accepted = await new ServerConnection(options).workflowAgentSessionRuntimeEvents(
      "project", "run", "session", { runtimeSessionId: "runtime", runtimeEvents: [{ type: "session.input" }, { type: "message.delta" }] }, signal)

    expect(accepted).toHaveLength(2)
  })

  it("surfaces malformed and count-mismatched acceptance responses", async () => {
    vi.spyOn(globalThis, "fetch").mockResolvedValueOnce(new Response("not-json", { status: 200 }))
    await expect(new ServerConnection(options).workflowAgentSessionRuntimeEvents(
      "project", "run", "session", { runtimeEvents: [{ type: "session.input" }] }, signal)).rejects.toThrow("malformed JSON")

    vi.spyOn(globalThis, "fetch").mockResolvedValueOnce(new Response("[]", { status: 200 }))
    await expect(new ServerConnection(options).workflowAgentSessionRuntimeEvents(
      "project", "run", "session", { runtimeEvents: [{ type: "session.input" }] }, signal)).rejects.toThrow("acceptance mismatch")
  })
})
