import { describe, expect, it } from "vitest"
import { OpenCodeRuntime, type RuntimeTurnEvent } from "../src/runtime/opencode/index.js"
import { buildRuntime } from "./support/opencode-turn-test-support.js"

describe("OpenCodeRuntime turn observer", () => {
  it("records the physical Session binding before submitting the prompt", async () => {
    const { deps, client } = buildRuntime()
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()
    let bindingRecorded = false
    client.sessionPrompt.mockImplementationOnce(async () => {
      expect(bindingRecorded).toBe(true)
      return { data: { info: { id: "msg_1", role: "assistant" }, parts: [] } }
    })

    const result = await runtime.runTurn({
      target: { runtime: "opencode", runtimeSessionId: null, workDir: "/tmp/projA" },
      prompt: "do the work",
    }, new AbortController().signal, {
      onSessionReady: async () => { bindingRecorded = true },
    })

    expect(result.ok).toBe(true)
    expect(client.sessionPrompt).toHaveBeenCalledTimes(1)
  })

  it("does not submit the prompt when the physical Session binding cannot be recorded", async () => {
    const { deps, client } = buildRuntime()
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()

    const result = await runtime.runTurn({
      target: { runtime: "opencode", runtimeSessionId: null, workDir: "/tmp/projA" },
      prompt: "do the work",
    }, new AbortController().signal, {
      onSessionReady: async () => { throw new Error("attach rejected") },
    })

    expect(result.ok).toBe(false)
    if (result.ok) return
    expect(result.error.kind).toBe("turn-failed")
    expect(result.error.diagnostics).toEqual(expect.arrayContaining([
      expect.objectContaining({ code: "turn-failed", message: "attach rejected" }),
    ]))
    expect(client.sessionPrompt).not.toHaveBeenCalled()
  })

  it("projects the final OpenCode response into stable runtime events", async () => {
    const { deps } = buildRuntime({
      promptResult: {
        data: {
          info: {
            id: "msg_1",
            sessionID: "ses_/tmp/projA",
            role: "assistant",
            providerID: "openai",
            modelID: "gpt-5",
            cost: 0.25,
            tokens: { input: 10, output: 5, reasoning: 2, cache: { read: 3, write: 0 } },
          },
          parts: [{ id: "part_1", messageID: "msg_1", sessionID: "ses_/tmp/projA", type: "text", text: "done" }],
        },
      },
    })
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()
    const observed: RuntimeTurnEvent[] = []

    const result = await runtime.runTurn({
      target: { runtime: "opencode", runtimeSessionId: null, workDir: "/tmp/projA" },
      prompt: "do the work",
    }, new AbortController().signal, {
      onEvent: (event) => observed.push(event),
    })

    expect(result.ok).toBe(true)
    expect(observed.map((event) => event.type)).toEqual(["model.resolved", "usage.updated", "message.delta"])
    expect(observed[2]?.payload).toMatchObject({ text: "done", messageId: "msg_1", partId: "part_1" })
  })
})
