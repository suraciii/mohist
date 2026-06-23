import { describe, expect, it } from "vitest"
import { defaultCompactionConfig, resolveCompactionConfig } from "../../src/actions/acp-agent.js"

describe("mohist/acp-agent compaction config helpers", () => {
  it("defaultCompactionConfig_ReturnsThresholdZeroPointEightAndSummaryStrategy", () => {
    expect(defaultCompactionConfig()).toEqual({ threshold: 0.8, strategy: "summary" })
  })

  it("resolveCompactionConfig_WithNoAgentConfig_AppliesDefaults", () => {
    expect(resolveCompactionConfig(undefined)).toEqual({ threshold: 0.8, strategy: "summary" })
  })

  it("resolveCompactionConfig_WithEmptyAgentConfig_AppliesDefaults", () => {
    expect(resolveCompactionConfig({})).toEqual({ threshold: 0.8, strategy: "summary" })
  })

  it("resolveCompactionConfig_WithExplicitConfig_PassesThroughValues", () => {
    expect(resolveCompactionConfig({ compaction: { threshold: 0.5, strategy: "summary" } }))
      .toEqual({ threshold: 0.5, strategy: "summary" })
  })
})