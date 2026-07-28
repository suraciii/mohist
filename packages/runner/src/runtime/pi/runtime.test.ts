import { describe, expect, it, vi } from "vitest"
import { PiRuntime } from "./runtime.js"

describe("PiRuntime followup", () => {
  it("applies the requested model and variant before accepting an idle follow-up", async () => {
    const setModel = vi.fn(async () => undefined)
    const setThinkingLevel = vi.fn()
    const prompt = vi.fn(async (_text: string, options?: { preflight?: (accepted: boolean) => void }) => {
      options?.preflight?.(true)
    })
    const session = {
      sessionFile: "/workspace/session.json",
      sessionId: "session-1",
      messages: [],
      isStreaming: false,
      subscribe: () => () => undefined,
      prompt,
      steer: vi.fn(async () => undefined),
      abort: vi.fn(async () => undefined),
      compact: vi.fn(async () => undefined),
      setModel,
      setThinkingLevel,
      getModel: () => undefined,
      getThinkingLevel: () => "off",
      dispose: () => undefined,
    }
    const model = { provider: "provider", id: "configured-model" }
    const runtime = new PiRuntime({
      agentDir: "/agent",
      sdkFactory: {
        create: async () => ({
          catalog: async () => [{ provider: "provider", id: "configured-model" }],
          createSession: async () => session,
          openSession: async () => session,
          model: () => model,
          close: async () => undefined,
        }),
      },
    })
    await runtime.start()

    const result = await runtime.followup({
      target: { runtime: "pi", runtimeSessionId: "/workspace/session.json", workDir: "/workspace" },
      prompt: "continue",
      options: { model: "provider/configured-model", variant: "high" },
    })

    expect(result.ok).toBe(true)
    expect(setModel).toHaveBeenCalledWith(model)
    expect(setThinkingLevel).toHaveBeenCalledWith("high")
    expect(prompt).toHaveBeenCalledWith("continue", expect.any(Object))
  })
})
