import { afterEach, describe, expect, it, vi } from "vitest"
import {
  createOpencodeModelsCommandAdapter,
  discoverOpencodeModels,
  mergeOpencodeModelCatalogs,
  opencodeModelSetsEqual,
  parseOpencodeModelsVerbose,
  type ModelsProcessExecutor,
  type OpencodeModelCatalog,
} from "./opencode-models.js"

afterEach(() => {
  vi.unstubAllEnvs()
})

describe("parseOpencodeModelsVerbose", () => {
  it("accepts only headers matching ^([^/\\s]+)/(\\S+)$ and supports flat lists", () => {
    const result = parseOpencodeModelsVerbose([
      "warning: provider unavailable",
      "diagnostic-without-slash",
      "/missing-provider",
      "provider/",
      "provider/model with-space",
      "{ broken output",
      "  openai/gpt-5  ",
      "openrouter/vendor/family/model",
    ].join("\n"))

    expect(result).toEqual({
      models: ["openai/gpt-5", "openrouter/vendor/family/model"],
      variants: {},
    })
  })

  it("parses single-line and multiline metadata while respecting braces and escapes in strings", () => {
    const singleLine = JSON.stringify({
      description: "literal braces { } and escaped quote \" plus slash \\",
      variants: { low: {}, "provider-defined/turbo": {} },
    })
    const multiline = JSON.stringify({
      description: "closing } before opening { inside a string",
      variants: { medium: {}, "EXACT Provider Key": {} },
    }, null, 2)
    const result = parseOpencodeModelsVerbose([
      "openai/gpt-5",
      singleLine,
      "",
      "anthropic/claude-sonnet-4",
      multiline,
    ].join("\n"))

    expect(result).toEqual({
      models: ["openai/gpt-5", "anthropic/claude-sonnet-4"],
      variants: {
        "openai/gpt-5": ["low", "provider-defined/turbo"],
        "anthropic/claude-sonnet-4": ["medium", "EXACT Provider Key"],
      },
    })
  })

  it("keeps models with missing, empty, or non-object variants without variant-map entries", () => {
    const result = parseOpencodeModelsVerbose([
      "provider/missing",
      JSON.stringify({ name: "missing" }),
      "provider/empty",
      JSON.stringify({ variants: {} }),
      "provider/string",
      JSON.stringify({ variants: "high" }),
      "provider/array",
      JSON.stringify({ variants: ["high"] }),
      "provider/null",
      JSON.stringify({ variants: null }),
    ].join("\n"))

    expect(result).toEqual({
      models: ["provider/missing", "provider/empty", "provider/string", "provider/array", "provider/null"],
      variants: {},
    })
  })

  it("resumes after balanced invalid metadata", () => {
    const result = parseOpencodeModelsVerbose([
      "openai/gpt-5",
      "{ invalid }",
      "diagnostic: ignored",
      "anthropic/claude-sonnet-4",
      JSON.stringify({ variants: { high: {} } }),
    ].join("\n"))

    expect(result).toEqual({
      models: ["openai/gpt-5", "anthropic/claude-sonnet-4"],
      variants: { "anthropic/claude-sonnet-4": ["high"] },
    })
  })

  it("resumes at the next valid header after unbalanced metadata without consuming it", () => {
    const result = parseOpencodeModelsVerbose([
      "openai/gpt-5",
      "{",
      "  \"variants\": {",
      "    \"low\": {}",
      "anthropic/claude-sonnet-4",
      JSON.stringify({ variants: { high: {} } }),
    ].join("\n"))

    expect(result).toEqual({
      models: ["openai/gpt-5", "anthropic/claude-sonnet-4"],
      variants: { "anthropic/claude-sonnet-4": ["high"] },
    })
  })

  it("returns an empty catalog when output has no valid header", () => {
    expect(parseOpencodeModelsVerbose("warning: unavailable\n{ broken output\n")).toEqual({
      models: [],
      variants: {},
    })
  })

  it("parses a trailing model and variants from payload larger than 49 KiB", () => {
    const payload = largeCatalogPayload()

    expect(Buffer.byteLength(payload)).toBeGreaterThan(49 * 1024)
    expect(parseOpencodeModelsVerbose(payload)).toEqual({
      models: ["openrouter/vendor/family/trailing-model"],
      variants: { "openrouter/vendor/family/trailing-model": ["high", "max"] },
    })
  })
})

describe("discoverOpencodeModels", () => {
  it("passes the complete command contract through the buffered process executor", async () => {
    vi.stubEnv("MOHIST_AGENT_MODELS_COMMAND", "custom-models-opencode")
    vi.stubEnv("MOHIST_AGENT_COMMAND", "general-opencode")
    const payload = largeCatalogPayload()
    const executor = vi.fn<ModelsProcessExecutor>(async () => ({ status: 0, stdout: payload }))
    const adapter = createOpencodeModelsCommandAdapter(executor)
    const signal = new AbortController().signal

    const result = await discoverOpencodeModels(signal, adapter)

    expect(executor).toHaveBeenCalledOnce()
    expect(executor).toHaveBeenCalledWith("custom-models-opencode", ["models", "--verbose"], {
      signal,
      timeout: 3_000,
      encoding: "utf8",
      maxBuffer: 16 * 1024 * 1024,
    })
    expect(Buffer.byteLength(payload)).toBeGreaterThan(49 * 1024)
    expect(result).toEqual({
      models: ["openrouter/vendor/family/trailing-model"],
      variants: { "openrouter/vendor/family/trailing-model": ["high", "max"] },
      complete: true,
    })
  })

  it.each([
    ["models-opencode", "agent-opencode", "models-opencode"],
    [undefined, "agent-opencode", "agent-opencode"],
    [undefined, undefined, "opencode"],
  ])("selects command precedence from %s and %s", async (modelsCommand, agentCommand, expected) => {
    vi.stubEnv("MOHIST_AGENT_MODELS_COMMAND", modelsCommand)
    vi.stubEnv("MOHIST_AGENT_COMMAND", agentCommand)
    const executor = vi.fn<ModelsProcessExecutor>(() => ({ status: 0, stdout: "openai/gpt-5\n" }))

    await discoverOpencodeModels(
      new AbortController().signal,
      createOpencodeModelsCommandAdapter(executor),
    )

    expect(executor.mock.calls[0]?.[0]).toBe(expected)
  })

  it("executes consecutive discoveries independently without caching", async () => {
    vi.stubEnv("MOHIST_AGENT_MODELS_COMMAND", undefined)
    vi.stubEnv("MOHIST_AGENT_COMMAND", undefined)
    const outputs = [
      "openai/gpt-5\n" + JSON.stringify({ variants: { low: {} } }),
      "anthropic/claude-sonnet-4\n" + JSON.stringify({ variants: { max: {} } }),
    ]
    const executor = vi.fn<ModelsProcessExecutor>(() => ({ status: 0, stdout: outputs.shift() ?? "" }))
    const adapter = createOpencodeModelsCommandAdapter(executor)

    await expect(discoverOpencodeModels(new AbortController().signal, adapter)).resolves.toEqual({
      models: ["openai/gpt-5"],
      variants: { "openai/gpt-5": ["low"] },
      complete: true,
    })
    await expect(discoverOpencodeModels(new AbortController().signal, adapter)).resolves.toEqual({
      models: ["anthropic/claude-sonnet-4"],
      variants: { "anthropic/claude-sonnet-4": ["max"] },
      complete: true,
    })
    expect(executor).toHaveBeenCalledTimes(2)
  })

  it.each([
    ["missing executable", { error: new Error("ENOENT"), status: null, stdout: "" }],
    ["abort", { error: Object.assign(new Error("aborted"), { name: "AbortError" }), status: null, stdout: "" }],
    ["non-zero exit", { status: 2, stdout: "openai/gpt-5\n" }],
  ])("logs and normalizes %s", async (_name, processResult) => {
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)
    const executor: ModelsProcessExecutor = () => processResult
    try {
      await expect(discoverOpencodeModels(
        new AbortController().signal,
        createOpencodeModelsCommandAdapter(executor),
      )).resolves.toEqual({ models: [], variants: {}, complete: false })
      expect(errorSpy).toHaveBeenCalledOnce()
      expect(errorSpy).toHaveBeenCalledWith("failed to discover opencode models", expect.any(Error))
    } finally {
      errorSpy.mockRestore()
    }
  })

  it("keeps valid model output when the CLI times out after writing it", async () => {
    const warnSpy = vi.spyOn(console, "warn").mockImplementation(() => undefined)
    const executor: ModelsProcessExecutor = () => ({
      error: Object.assign(new Error("timed out"), { code: "ETIMEDOUT" }),
      status: null,
      stdout: "kimi-for-coding/kimi-for-coding-highspeed\n",
    })

    try {
      await expect(discoverOpencodeModels(
        new AbortController().signal,
        createOpencodeModelsCommandAdapter(executor),
      )).resolves.toEqual({
        models: ["kimi-for-coding/kimi-for-coding-highspeed"],
        variants: {},
        complete: false,
      })
      expect(warnSpy).toHaveBeenCalledWith("opencode model discovery timed out; using an incomplete catalog")
    } finally {
      warnSpy.mockRestore()
    }
  })

  it("logs and normalizes successful output with no valid header", async () => {
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)
    const executor: ModelsProcessExecutor = () => ({ status: 0, stdout: "warning: no providers\n" })
    try {
      await expect(discoverOpencodeModels(
        new AbortController().signal,
        createOpencodeModelsCommandAdapter(executor),
      )).resolves.toEqual({ models: [], variants: {}, complete: false })
      expect(errorSpy).toHaveBeenCalledWith("failed to discover opencode models", expect.any(Error))
    } finally {
      errorSpy.mockRestore()
    }
  })

  it("can succeed on a later invocation after a process failure", async () => {
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)
    const processResults = [
      { error: new Error("ENOENT"), status: null, stdout: "" },
      { status: 0, stdout: "openai/gpt-5\n" + JSON.stringify({ variants: { high: {} } }) },
    ]
    const executor: ModelsProcessExecutor = () => processResults.shift()!
    const adapter = createOpencodeModelsCommandAdapter(executor)
    try {
      await expect(discoverOpencodeModels(new AbortController().signal, adapter)).resolves.toEqual({
        models: [],
        variants: {},
        complete: false,
      })
      await expect(discoverOpencodeModels(new AbortController().signal, adapter)).resolves.toEqual({
        models: ["openai/gpt-5"],
        variants: { "openai/gpt-5": ["high"] },
        complete: true,
      })
      expect(errorSpy).toHaveBeenCalledOnce()
    } finally {
      errorSpy.mockRestore()
    }
  })
})

describe("opencodeModelSetsEqual", () => {
  const base: OpencodeModelCatalog = {
    models: ["openai/gpt-5", "anthropic/claude-sonnet-4"],
    variants: { "openai/gpt-5": ["low", "high"], "anthropic/claude-sonnet-4": ["max"] },
  }

  it("ignores model and per-model variant order", () => {
    expect(opencodeModelSetsEqual(base, {
      models: [...base.models].reverse(),
      variants: { "anthropic/claude-sonnet-4": ["max"], "openai/gpt-5": ["high", "low"] },
    })).toBe(true)
  })

  it.each([
    ["added model", { ...base, models: [...base.models, "google/gemini-3"] }],
    ["removed model", { ...base, models: ["openai/gpt-5"] }],
    ["added variant-map key", { ...base, variants: { ...base.variants, "google/gemini-3": [] } }],
    ["removed variant-map key", { ...base, variants: { "openai/gpt-5": ["low", "high"] } }],
    ["added variant value", { ...base, variants: { ...base.variants, "openai/gpt-5": ["low", "high", "max"] } }],
    ["removed variant value", { ...base, variants: { ...base.variants, "openai/gpt-5": ["low"] } }],
  ])("detects an %s", (_name, changed) => {
    expect(opencodeModelSetsEqual(base, changed)).toBe(false)
  })
})

describe("mergeOpencodeModelCatalogs", () => {
  it("adds discoveries without removing models or variants from the current catalog", () => {
    const current: OpencodeModelCatalog = {
      models: ["openai/gpt-5", "anthropic/claude-sonnet-4"],
      variants: {
        "openai/gpt-5": ["low", "high"],
        "anthropic/claude-sonnet-4": ["max"],
      },
    }

    expect(mergeOpencodeModelCatalogs(current, {
      models: ["openai/gpt-5", "google/gemini-3"],
      variants: {
        "openai/gpt-5": ["high", "max"],
        "google/gemini-3": ["pro"],
      },
    })).toEqual({
      models: ["openai/gpt-5", "anthropic/claude-sonnet-4", "google/gemini-3"],
      variants: {
        "openai/gpt-5": ["low", "high", "max"],
        "anthropic/claude-sonnet-4": ["max"],
        "google/gemini-3": ["pro"],
      },
    })
  })
})

function largeCatalogPayload(): string {
  return [
    `warning: ${"x".repeat(50 * 1024)}`,
    "openrouter/vendor/family/trailing-model",
    JSON.stringify({ variants: { high: {}, max: {} } }),
  ].join("\n")
}
