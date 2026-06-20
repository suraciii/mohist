import { mkdtempSync, existsSync, readFileSync, rmSync, writeFileSync, chmodSync } from "node:fs"
import { tmpdir } from "node:os"
import { join } from "node:path"
import { describe, expect, it, afterEach, beforeEach } from "vitest"
import {
  clearOpencodeModelsCacheForTesting,
  discoverOpencodeModels,
  parseOpencodeModelsVerbose,
} from "../src/runtime/opencode-models.js"

const originalModelsCommand = process.env.MOHIST_AGENT_MODELS_COMMAND
const originalAgentCommand = process.env.MOHIST_AGENT_COMMAND

function restoreEnv() {
  setEnv("MOHIST_AGENT_MODELS_COMMAND", originalModelsCommand)
  setEnv("MOHIST_AGENT_COMMAND", originalAgentCommand)
}

function setEnv(key: string, value: string | undefined) {
  if (value === undefined) delete process.env[key]
  else process.env[key] = value
}

function makeCounterScript(tmp: string, payload: string): { scriptPath: string; counterPath: string } {
  const counterPath = join(tmp, "counter")
  writeFileSync(counterPath, "0")
  const scriptPath = join(tmp, "fake-opencode.cjs")
  writeFileSync(
    scriptPath,
    `#!/usr/bin/env node\n` +
    `const fs = require("node:fs");\n` +
    `const counterPath = ${JSON.stringify(counterPath)};\n` +
    `const payload = ${JSON.stringify(payload)};\n` +
    `const n = Number(fs.readFileSync(counterPath, "utf8").trim()) + 1;\n` +
    `fs.writeFileSync(counterPath, String(n));\n` +
    `process.stdout.write(payload);\n`,
  )
  chmodSync(scriptPath, 0o755)
  return { scriptPath, counterPath }
}

function makeFailingScript(tmp: string, message: string): { scriptPath: string } {
  const scriptPath = join(tmp, "fake-fail.cjs")
  writeFileSync(
    scriptPath,
    `#!/usr/bin/env node\n` +
    `process.stderr.write(${JSON.stringify(message)});\n` +
    `process.exit(2);\n`,
  )
  chmodSync(scriptPath, 0o755)
  return { scriptPath }
}

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

describe("discoverOpencodeModels", () => {
  let tmp: string

  beforeEach(() => {
    clearOpencodeModelsCacheForTesting()
    setEnv("MOHIST_AGENT_COMMAND", undefined)
    tmp = mkdtempSync(join(tmpdir(), "mohist-models-"))
  })

  afterEach(() => {
    clearOpencodeModelsCacheForTesting()
    restoreEnv()
    if (existsSync(tmp)) {
      rmSync(tmp, { recursive: true, force: true })
    }
  })

  it("returnsParsedModelsAndVariantsFromCommand", async () => {
    const payload = [
      "openai/gpt-5.5",
      JSON.stringify({ variants: { low: {}, high: {}, max: {} } }),
      "anthropic/claude-sonnet-4",
      JSON.stringify({ variants: {} }),
      "",
    ].join("\n")
    const { scriptPath } = makeCounterScript(tmp, payload)
    setEnv("MOHIST_AGENT_MODELS_COMMAND", scriptPath)

    const result = await discoverOpencodeModels(new AbortController().signal)
    expect(result.models).toEqual(["openai/gpt-5.5", "anthropic/claude-sonnet-4"])
    expect(result.variants["openai/gpt-5.5"]).toEqual(["low", "high", "max"])
    expect(result.variants["anthropic/claude-sonnet-4"]).toBeUndefined()
  })

  it("returnsEmptyResultAndDoesNotCacheWhenCommandFails", async () => {
    const { scriptPath } = makeFailingScript(tmp, "boom")
    setEnv("MOHIST_AGENT_MODELS_COMMAND", scriptPath)

    const first = await discoverOpencodeModels(new AbortController().signal)
    expect(first).toEqual({ models: [], variants: {} })

    const payload = "openai/gpt-5.5\n" + JSON.stringify({ variants: { low: {} } }) + "\n"
    const { scriptPath: okPath, counterPath } = makeCounterScript(tmp, payload)
    setEnv("MOHIST_AGENT_MODELS_COMMAND", okPath)

    const second = await discoverOpencodeModels(new AbortController().signal)
    expect(second.models).toEqual(["openai/gpt-5.5"])
    expect(second.variants["openai/gpt-5.5"]).toEqual(["low"])
    const counter = Number(readFileSync(counterPath, "utf8").trim())
    expect(counter).toBe(1)
  })

  it("cachesSuccessfulResultsForSubsequentCalls", async () => {
    const payload = "openai/gpt-5.5\n" + JSON.stringify({ variants: { high: {} } }) + "\n"
    const { scriptPath, counterPath } = makeCounterScript(tmp, payload)
    setEnv("MOHIST_AGENT_MODELS_COMMAND", scriptPath)

    const first = await discoverOpencodeModels(new AbortController().signal)
    const second = await discoverOpencodeModels(new AbortController().signal)
    const third = await discoverOpencodeModels(new AbortController().signal)

    expect(first).toEqual(second)
    expect(second).toEqual(third)
    expect(first.variants["openai/gpt-5.5"]).toEqual(["high"])
    const counter = Number(readFileSync(counterPath, "utf8").trim())
    expect(counter).toBe(1)
  })

  it("handlesMalformedStdoutWithoutThrowing", async () => {
    const payload = "openai/gpt-5.5\n{ broken json\n"
    const { scriptPath } = makeCounterScript(tmp, payload)
    setEnv("MOHIST_AGENT_MODELS_COMMAND", scriptPath)

    const result = await discoverOpencodeModels(new AbortController().signal)
    expect(result.models).toEqual(["openai/gpt-5.5"])
    expect(result.variants["openai/gpt-5.5"]).toBeUndefined()
  })
})
