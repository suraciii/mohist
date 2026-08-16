import { describe, expect, it } from "vitest"
import { createPiProjector } from "../src/runtime/pi/projector.js"

describe("Pi runtime projector model.resolved", () => {
  it("reports the frozen canonical effort beside the resolved model", () => {
    const projector = createPiProjector("/virtual/session", "/workspace", undefined, "high")
    const facts = projector.project({
      type: "model_change",
      id: "model-effort",
      provider: "anthropic",
      modelId: "claude-sonnet-4-20250514",
    })

    expect(facts).toHaveLength(1)
    expect(facts[0]?.payload).toEqual({
      resolvedModel: "anthropic/claude-sonnet-4-20250514",
      providerId: "anthropic",
      modelId: "claude-sonnet-4-20250514",
      appliedReasoningEffort: "high",
    })
  })

  it("emits a model.resolved event with resolvedModel for a provider+modelId change", () => {
    const projector = createPiProjector("/virtual/session", "/workspace")
    const facts = projector.project({
      type: "model_change",
      id: "model-1",
      provider: "anthropic",
      modelId: "claude-sonnet-4-20250514",
    })

    expect(facts).toHaveLength(1)
    expect(facts[0]?.type).toBe("model.resolved")
    expect(facts[0]?.payload).toEqual({
      resolvedModel: "anthropic/claude-sonnet-4-20250514",
      providerId: "anthropic",
      modelId: "claude-sonnet-4-20250514",
    })
    expect(facts[0]?.payload).not.toHaveProperty("model")
  })

  it("emits a model.resolved event with resolvedModel when the model field is a string", () => {
    const projector = createPiProjector("/virtual/session", "/workspace")
    const facts = projector.project({
      type: "model_change",
      id: "model-2",
      model: "openai/gpt-5.6",
    })

    expect(facts).toHaveLength(1)
    expect(facts[0]?.type).toBe("model.resolved")
    expect(facts[0]?.payload).toEqual({
      resolvedModel: "openai/gpt-5.6",
      providerId: "openai",
      modelId: "gpt-5.6",
    })
  })

  it("emits a model.resolved event when the model field is a structured object", () => {
    const projector = createPiProjector("/virtual/session", "/workspace")
    const facts = projector.project({
      type: "model_change",
      id: "model-3",
      model: { provider: "openai", id: "gpt-5.6" },
    })

    expect(facts).toHaveLength(1)
    expect(facts[0]?.type).toBe("model.resolved")
    expect(facts[0]?.payload).toEqual({
      resolvedModel: "openai/gpt-5.6",
      providerId: "openai",
      modelId: "gpt-5.6",
    })
  })

  it("falls back to the raw model string when no provider/id parts are available", () => {
    const projector = createPiProjector("/virtual/session", "/workspace")
    const facts = projector.project({
      type: "model_change",
      id: "model-4",
      model: "raw-model-name",
    })

    expect(facts).toHaveLength(1)
    expect(facts[0]?.type).toBe("model.resolved")
    expect(facts[0]?.payload).toEqual({ resolvedModel: "raw-model-name" })
  })

  it("drops a model.resolved event with no resolvable model", () => {
    const projector = createPiProjector("/virtual/session", "/workspace")
    const facts = projector.project({
      type: "model_change",
      id: "model-5",
    })

    expect(facts).toEqual([])
  })

  it("deduplicates consecutive model_change events for the same id", () => {
    const projector = createPiProjector("/virtual/session", "/workspace")
    const first = projector.project({ type: "model_change", id: "model-6", provider: "openai", modelId: "gpt-5.6" })
    const second = projector.project({ type: "model_change", id: "model-6", provider: "openai", modelId: "gpt-5.6" })

    expect(first).toHaveLength(1)
    expect(second).toEqual([])
  })

  it("ignores Pi lifecycle events that have no Mohist transcript representation", () => {
    const projector = createPiProjector("/virtual/session", "/workspace")
    const thinking = projector.project({ type: "thinking_level_changed", id: "think-1", level: "high" })
    const turnStart = projector.project({ type: "turn_start", id: "turn-start-1" })
    const turnEnd = projector.project({ type: "turn_end", id: "turn-end-1", stopReason: "stop" })
    const agentEnd = projector.project({ type: "agent_end", id: "agent-end-1" })

    for (const facts of [thinking, turnStart, turnEnd, agentEnd]) {
      expect(facts).toEqual([])
    }
  })
})
