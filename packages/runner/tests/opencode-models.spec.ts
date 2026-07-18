import { describe, expect, it, afterEach, vi } from "vitest"
import {
  discoverOpencodeModels,
  opencodeModelSetsEqual,
  parseOpencodeModelsVerbose,
  setModelsCommandRunnerForTest,
} from "../src/runtime/opencode-models.js"

describe("parseOpencodeModelsVerbose", () => {
  it("returnsModelIdsAndEmptyVariantsOnNonVerboseFlatList", () => {
    const stdout = "openai/gpt-5.5\nanthropic/claude-sonnet-4\n"
    expect(parseOpencodeModelsVerbose(stdout)).toEqual({
      models: ["openai/gpt-5.5", "anthropic/claude-sonnet-4"],
      variants: {},
    })
  })

  it("extractsPopulatedVariantsMapAlongsideModelIds", () => {
    const stdout = [
      "openai/gpt-5.5",
      JSON.stringify({ variants: { low: { reasoningEffort: "low" }, high: { reasoningEffort: "high" } } }),
      "anthropic/claude-sonnet-4",
      JSON.stringify({ variants: { max: { reasoningEffort: "max" } } }),
      "",
    ].join("\n")
    const result = parseOpencodeModelsVerbose(stdout)
    expect(result.models).toEqual(["openai/gpt-5.5", "anthropic/claude-sonnet-4"])
    expect(result.variants["openai/gpt-5.5"]).toEqual(["low", "high"])
    expect(result.variants["anthropic/claude-sonnet-4"]).toEqual(["max"])
  })

  it("treatsEmptyVariantsMapAsEmptyVariantSet", () => {
    const stdout = [
      "openai/gpt-5.5",
      JSON.stringify({ variants: {} }),
      "",
    ].join("\n")
    const result = parseOpencodeModelsVerbose(stdout)
    expect(result.models).toEqual(["openai/gpt-5.5"])
    expect(result.variants["openai/gpt-5.5"]).toBeUndefined()
  })

  it("treatsMissingVariantsKeyAsEmptyVariantSet", () => {
    const stdout = [
      "openai/gpt-5.5",
      JSON.stringify({ name: "gpt-5.5" }),
      "",
    ].join("\n")
    const result = parseOpencodeModelsVerbose(stdout)
    expect(result.models).toEqual(["openai/gpt-5.5"])
    expect(result.variants["openai/gpt-5.5"]).toBeUndefined()
  })

  it("fallsBackToEmptyVariantsOnMalformedJson", () => {
    const stdout = [
      "openai/gpt-5.5",
      "{ this is not json",
      "anthropic/claude-sonnet-4",
      JSON.stringify({ variants: { high: { reasoningEffort: "high" } } }),
      "",
    ].join("\n")
    const result = parseOpencodeModelsVerbose(stdout)
    expect(result.models).toEqual(["openai/gpt-5.5", "anthropic/claude-sonnet-4"])
    expect(result.variants["openai/gpt-5.5"]).toBeUndefined()
    expect(result.variants["anthropic/claude-sonnet-4"]).toEqual(["high"])
  })

  it("keepsMalformedModelEntryWithoutVariants", () => {
    const stdout = "openai/gpt-5.5\n{ broken json\n"
    const result = parseOpencodeModelsVerbose(stdout)
    expect(result.models).toEqual(["openai/gpt-5.5"])
    expect(result.variants["openai/gpt-5.5"]).toBeUndefined()
  })

  it("ignoresNonObjectVariantsField", () => {
    const stdout = [
      "openai/gpt-5.5",
      JSON.stringify({ variants: "not-an-object" }),
      "",
    ].join("\n")
    const result = parseOpencodeModelsVerbose(stdout)
    expect(result.models).toEqual(["openai/gpt-5.5"])
    expect(result.variants["openai/gpt-5.5"]).toBeUndefined()
  })
})

describe("opencodeModelSetsEqual", () => {
  const base = {
    models: ["openai/gpt-5.5", "anthropic/claude-sonnet-4"],
    variants: {
      "openai/gpt-5.5": ["low", "high"],
      "anthropic/claude-sonnet-4": ["max"],
    },
  }

  it("returnsTrueForSameOrder", () => {
    expect(opencodeModelSetsEqual(base, base)).toBe(true)
  })

  it("returnsTrueForDifferentModelOrder", () => {
    expect(opencodeModelSetsEqual(base, { ...base, models: [...base.models].reverse() })).toBe(true)
  })

  it("returnsFalseForNewModelId", () => {
    expect(opencodeModelSetsEqual(base, { ...base, models: [...base.models, "google/gemini-3"] })).toBe(false)
  })

  it("returnsFalseForMissingModelId", () => {
    expect(opencodeModelSetsEqual(base, { ...base, models: [base.models[0]!] })).toBe(false)
  })

  it("returnsTrueForDifferentVariantOrder", () => {
    expect(
      opencodeModelSetsEqual(base, {
        ...base,
        variants: { ...base.variants, "openai/gpt-5.5": ["high", "low"] },
      }),
    ).toBe(true)
  })

  it("returnsFalseForAddedVariant", () => {
    expect(
      opencodeModelSetsEqual(base, {
        ...base,
        variants: { ...base.variants, "openai/gpt-5.5": ["low", "high", "max"] },
      }),
    ).toBe(false)
  })

  it("returnsFalseForRemovedVariant", () => {
    expect(
      opencodeModelSetsEqual(base, {
        ...base,
        variants: { ...base.variants, "openai/gpt-5.5": ["low"] },
      }),
    ).toBe(false)
  })
})

describe("discoverOpencodeModels", () => {
  afterEach(() => {
    setModelsCommandRunnerForTest(null)
  })

  it("returnsParsedModelsAndVariantsFromCommand", async () => {
    const payload = [
      "openai/gpt-5.5",
      JSON.stringify({ variants: { low: {}, high: {}, max: {} } }),
      "anthropic/claude-sonnet-4",
      JSON.stringify({ variants: {} }),
      "",
    ].join("\n")
    setModelsCommandRunnerForTest(async () => payload)

    const result = await discoverOpencodeModels(new AbortController().signal)
    expect(result.models).toEqual(["openai/gpt-5.5", "anthropic/claude-sonnet-4"])
    expect(result.variants["openai/gpt-5.5"]).toEqual(["low", "high", "max"])
    expect(result.variants["anthropic/claude-sonnet-4"]).toBeUndefined()
  })

  it("returnsEmptyResultAndDoesNotCacheWhenCommandFails", async () => {
    let callCount = 0
    const failure = new Error("boom")
    setModelsCommandRunnerForTest(async () => {
      callCount += 1
      if (callCount === 1) throw failure
      return "openai/gpt-5.5\n" + JSON.stringify({ variants: { low: {} } }) + "\n"
    })
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)

    try {
      const first = await discoverOpencodeModels(new AbortController().signal)
      expect(first).toEqual({ models: [], variants: {} })

      const second = await discoverOpencodeModels(new AbortController().signal)
      expect(second.models).toEqual(["openai/gpt-5.5"])
      expect(second.variants["openai/gpt-5.5"]).toEqual(["low"])
      expect(errorSpy).toHaveBeenCalledOnce()
      expect(errorSpy).toHaveBeenCalledWith("failed to discover opencode models", failure)
    } finally {
      errorSpy.mockRestore()
    }
  })

  it("executesCommandForEveryCall", async () => {
    const runner = vi
      .fn()
      .mockResolvedValueOnce("openai/gpt-5.5\n" + JSON.stringify({ variants: { high: {} } }) + "\n")
      .mockResolvedValueOnce("anthropic/claude-sonnet-4\n" + JSON.stringify({ variants: { max: {} } }) + "\n")
    setModelsCommandRunnerForTest(runner)

    const first = await discoverOpencodeModels(new AbortController().signal)
    const second = await discoverOpencodeModels(new AbortController().signal)

    expect(first).toEqual({
      models: ["openai/gpt-5.5"],
      variants: { "openai/gpt-5.5": ["high"] },
    })
    expect(second).toEqual({
      models: ["anthropic/claude-sonnet-4"],
      variants: { "anthropic/claude-sonnet-4": ["max"] },
    })
    expect(runner).toHaveBeenCalledTimes(2)
  })

  it("handlesMalformedStdoutWithoutThrowing", async () => {
    const payload = "openai/gpt-5.5\n{ broken json\n"
    setModelsCommandRunnerForTest(async () => payload)

    const result = await discoverOpencodeModels(new AbortController().signal)
    expect(result.models).toEqual(["openai/gpt-5.5"])
    expect(result.variants["openai/gpt-5.5"]).toBeUndefined()
  })
})
