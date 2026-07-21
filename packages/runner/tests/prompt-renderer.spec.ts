import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import {
  PromptLoaderRegistry,
  defaultPromptLoaderRegistry,
  renderStructuredPrompt,
  resolvePrompt,
  setPromptLoaderRegistryForTest,
  type PromptLoader,
  type PromptLoaderContext,
} from "../src/core/prompt.js"

afterEach(() => setPromptLoaderRegistryForTest(null))

describe("resolvePrompt - string identity", () => {
  it("PlainStringSpec_ResolvesByteForByteUnchanged", async () => {
    expect(await resolvePrompt("plain prompt", baseLoaderContext())).toBe("plain prompt")
  })

  it("EmptyStringSpec_PreservesEmptyString", async () => {
    expect(await resolvePrompt("", baseLoaderContext())).toBe("")
  })

  it("MultilineStringSpec_PreservesNewlinesAndIndentationExactly", async () => {
    const source = "line one\n  indented\nline three\n"
    expect(await resolvePrompt(source, baseLoaderContext())).toBe(source)
  })

  it("StringSpecWithTemplateSyntax_PreservesLiteralTemplateText", async () => {
    const source = "literal ${{ prompts.xxx }} should not be rendered"
    expect(await resolvePrompt(source, baseLoaderContext())).toBe(source)
  })

  it("AbsentSpec_ResolvesToUndefinedSoCallerCanFallBack", async () => {
    expect(await resolvePrompt(undefined, baseLoaderContext())).toBeUndefined()
    expect(await resolvePrompt(null, baseLoaderContext())).toBeUndefined()
  })
})

describe("renderStructuredPrompt - object to XML-like text", () => {
  it("SingleRootWithAttrsAndPrimitiveChildren_RendersInlineChildBlocks", () => {
    const output = renderStructuredPrompt({
      artifact: {
        attrs: { id: "build-task" },
        task: "Complete exactly one implementation task.",
        instruction: "Follow acceptance criteria.",
      },
    })

    expect(output).toBe([
      `<artifact id="build-task">`,
      ``,
      `  <task>Complete exactly one implementation task.</task>`,
      ``,
      `  <instruction>Follow acceptance criteria.</instruction>`,
      ``,
      `</artifact>`,
    ].join("\n"))
  })

  it("MultilineStringChild_RendersAsBlockFormFlushLeftBetweenIndentedTags", () => {
    const output = renderStructuredPrompt({
      artifact: {
        description: "first line\nsecond line",
      },
    })

    expect(output).toBe([
      `<artifact>`,
      ``,
      `  <description>`,
      `first line`,
      `second line`,
      `  </description>`,
      ``,
      `</artifact>`,
    ].join("\n"))
  })

  it("ArrayChild_RendersAsBulletedLinesFlushLeftInsideTag", () => {
    const output = renderStructuredPrompt({
      artifact: {
        acceptance_criteria: ["first criterion", "second criterion"],
      },
    })

    expect(output).toBe([
      `<artifact>`,
      ``,
      `  <acceptance_criteria>`,
      `- first criterion`,
      `- second criterion`,
      `  </acceptance_criteria>`,
      ``,
      `</artifact>`,
    ].join("\n"))
  })

  it("EmptyArrayChild_RendersAsEmptyInlineTag", () => {
    const output = renderStructuredPrompt({
      artifact: {
        depends_on: [],
      },
    })

    expect(output).toBe([
      `<artifact>`,
      ``,
      `  <depends_on></depends_on>`,
      ``,
      `</artifact>`,
    ].join("\n"))
  })

  it("NestedObjectChild_RendersAsIndentedSubBlockWithItsOwnAttrsAndChildren", () => {
    const output = renderStructuredPrompt({
      artifact: {
        attrs: { id: "build-task" },
        selected_task: {
          attrs: { id: "T-001" },
          title: "Add structured prompt renderer",
          description: "first\nsecond",
          acceptance_criteria: ["one", "two"],
        },
      },
    })

    expect(output).toBe([
      `<artifact id="build-task">`,
      ``,
      `  <selected_task id="T-001">`,
      ``,
      `    <title>Add structured prompt renderer</title>`,
      ``,
      `    <description>`,
      `first`,
      `second`,
      `    </description>`,
      ``,
      `    <acceptance_criteria>`,
      `- one`,
      `- two`,
      `    </acceptance_criteria>`,
      ``,
      `  </selected_task>`,
      ``,
      `</artifact>`,
    ].join("\n"))
  })

  it("AttrsAreEmittedInInsertionOrder_AndDeterministicForRepeatedRenders", () => {
    const input = {
      artifact: {
        attrs: { id: "T-001", kind: "build", priority: 2, enabled: true },
        task: "x",
      },
    } as const

    const first = renderStructuredPrompt(JSON.parse(JSON.stringify(input)))
    const second = renderStructuredPrompt(JSON.parse(JSON.stringify(input)))

    expect(first).toBe(second)
    expect(first).toContain(`<artifact id="T-001" kind="build" priority="2" enabled="true">`)
  })

  it("AttrsWithDoubleQuoteOrAmpersand_AreMinimallyEscaped", () => {
    const output = renderStructuredPrompt({
      artifact: {
        attrs: { name: `Quoted "Value" & more` },
        task: "x",
      },
    })

    expect(output).toContain(`<artifact name="Quoted &quot;Value&quot; &amp; more">`)
  })

  it("BlockWithEmptyChildrenAndEmptyAttrs_RendersAsSelfContainedEmptyTagOnOneLine", () => {
    const output = renderStructuredPrompt({
      artifact: {
        attrs: {},
      },
    })

    expect(output).toBe(`<artifact></artifact>`)
  })

  it("PrimitiveNumberAndBooleanChildren_StringifyConsistentlyForInlineTags", () => {
    const output = renderStructuredPrompt({
      artifact: {
        priority: 1,
        enabled: false,
      },
    })

    expect(output).toBe([
      `<artifact>`,
      ``,
      `  <priority>1</priority>`,
      ``,
      `  <enabled>false</enabled>`,
      ``,
      `</artifact>`,
    ].join("\n"))
  })

  it("NullChildValue_RendersAsEmptyTagWithoutCrashing", () => {
    const output = renderStructuredPrompt({
      artifact: {
        notes: null,
      },
    })

    expect(output).toBe([
      `<artifact>`,
      ``,
      `  <notes></notes>`,
      ``,
      `</artifact>`,
    ].join("\n"))
  })
})

describe("resolvePrompt - plain object delegates to renderStructuredPrompt", () => {
  it("PlainObjectSpec_RendersThroughStructuredRenderer", async () => {
    const direct = renderStructuredPrompt({
      artifact: { task: "do work" },
    })

    const resolved = await resolvePrompt({
      artifact: { task: "do work" },
    }, baseLoaderContext())

    expect(resolved).toBe(direct)
  })
})

describe("resolvePrompt - loader-backed prompts", () => {
  it("LoaderReturningString_ResolvesToThatStringDirectly", async () => {
    const registry = new PromptLoaderRegistry()
    registry.register("fake/string-loader", async () => "loader produced text")
    setPromptLoaderRegistryForTest(registry)

    const result = await resolvePrompt({ uses: "fake/string-loader" }, baseLoaderContext())

    expect(result).toBe("loader produced text")
  })

  it("LoaderReturningObject_ResolvesByRenderingThroughStructuredRenderer", async () => {
    const registry = new PromptLoaderRegistry()
    registry.register("fake/object-loader", async () => ({
      artifact: { task: "rendered from loader" },
    }))
    setPromptLoaderRegistryForTest(registry)

    const result = await resolvePrompt({ uses: "fake/object-loader" }, baseLoaderContext())

    expect(result).toBe([
      `<artifact>`,
      ``,
      `  <task>rendered from loader</task>`,
      ``,
      `</artifact>`,
    ].join("\n"))
  })

  it("LoaderInvocation_ReceivesDeclaredInputAndHostContextOnly", async () => {
    const registry = new PromptLoaderRegistry()
    const loader = vi.fn<PromptLoader>(async () => "ok")
    registry.register("fake/echo-loader", loader)
    setPromptLoaderRegistryForTest(registry)

    const ctx: PromptLoaderContext = {
      with: {},
      workDir: "/tmp/run",
      workId: "work-7",
      title: "Build task",
      stage: "build",
    }

    await resolvePrompt({
      uses: "fake/echo-loader",
      with: { file: "tasks.json", taskId: "T-001" },
    }, ctx)

    expect(loader).toHaveBeenCalledTimes(1)
    expect(loader.mock.calls[0][0]).toEqual({
      with: { file: "tasks.json", taskId: "T-001" },
      workDir: "/tmp/run",
      workId: "work-7",
      title: "Build task",
      stage: "build",
    })
  })

  it("LoaderSpecWithoutWith_PassesEmptyObjectToLoaderContext", async () => {
    const registry = new PromptLoaderRegistry()
    const loader = vi.fn<PromptLoader>(async () => "ok")
    registry.register("fake/echo-loader", loader)
    setPromptLoaderRegistryForTest(registry)

    await resolvePrompt({ uses: "fake/echo-loader" }, baseLoaderContext())

    expect(loader.mock.calls[0][0].with).toEqual({})
  })

  it("LoaderNameIsCaseInsensitive_ResolvesIgnoringCase", async () => {
    const registry = new PromptLoaderRegistry()
    registry.register("Fake/Mixed-Case", async () => "matched")
    setPromptLoaderRegistryForTest(registry)

    expect(await resolvePrompt({ uses: "fake/mixed-case" }, baseLoaderContext())).toBe("matched")
    expect(await resolvePrompt({ uses: "FAKE/MIXED-CASE" }, baseLoaderContext())).toBe("matched")
  })
})

describe("resolvePrompt - error handling", () => {
  it("UnknownLoaderName_FailsWithClearError", async () => {
    setPromptLoaderRegistryForTest(new PromptLoaderRegistry())

    await expect(resolvePrompt({ uses: "no/such-loader" }, baseLoaderContext()))
      .rejects.toThrow("Unknown prompt loader: 'no/such-loader'")
  })

  it("UsesValueNotAString_FailsWithClearError", async () => {
    await expect(resolvePrompt({ uses: 42 } as never, baseLoaderContext()))
      .rejects.toThrow("Prompt loader spec 'uses' must be a non-empty string")
  })

  it("UsesValueEmptyString_FailsWithClearError", async () => {
    await expect(resolvePrompt({ uses: "   " }, baseLoaderContext()))
      .rejects.toThrow("Prompt loader spec 'uses' must be a non-empty string")
  })

  it("LoaderSpecWithNonObjectWith_FailsWithClearError", async () => {
    const registry = new PromptLoaderRegistry()
    registry.register("fake/loader", async () => "ok")
    setPromptLoaderRegistryForTest(registry)

    await expect(resolvePrompt({ uses: "fake/loader", with: "not-object" } as never, baseLoaderContext()))
      .rejects.toThrow("Prompt loader 'fake/loader' spec 'with' must be an object")
  })

  it("LoaderReturningArray_FailsWithClearError", async () => {
    const registry = new PromptLoaderRegistry()
    registry.register("fake/array-loader", async () => ["nope"] as never)
    setPromptLoaderRegistryForTest(registry)

    await expect(resolvePrompt({ uses: "fake/array-loader" }, baseLoaderContext()))
      .rejects.toThrow("Prompt loader 'fake/array-loader' returned an invalid value")
  })

  it("LoaderReturningPrimitive_FailsWithClearError", async () => {
    const registry = new PromptLoaderRegistry()
    registry.register("fake/number-loader", async () => 42 as never)
    setPromptLoaderRegistryForTest(registry)

    await expect(resolvePrompt({ uses: "fake/number-loader" }, baseLoaderContext()))
      .rejects.toThrow("Prompt loader 'fake/number-loader' returned an invalid value")
  })

  it("SpecAsArray_FailsWithClearError", async () => {
    await expect(resolvePrompt(["bad"], baseLoaderContext()))
      .rejects.toThrow("Prompt spec must be a string or object, received an array")
  })

  it("SpecAsPrimitive_FailsWithClearError", async () => {
    await expect(resolvePrompt(42, baseLoaderContext()))
      .rejects.toThrow("Prompt spec must be a string or object, received number")
    await expect(resolvePrompt(true, baseLoaderContext()))
      .rejects.toThrow("Prompt spec must be a string or object, received boolean")
  })

  it("ObjectSpecWithMultipleRootKeys_FailsWithClearError", async () => {
    await expect(resolvePrompt({ artifact: {}, other: {} }, baseLoaderContext()))
      .rejects.toThrow("Structured prompt must have exactly one root key, received 2 (artifact, other)")
  })

  it("ObjectSpecWithZeroRootKeys_FailsWithClearError", async () => {
    await expect(resolvePrompt({}, baseLoaderContext()))
      .rejects.toThrow("Structured prompt must have exactly one root key, received 0")
  })

  it("ObjectSpecWithInvalidRootTagName_FailsWithClearError", () => {
    expect(() => renderStructuredPrompt({ "1bad": {} }))
      .toThrow("Structured prompt root key must be a valid tag name: '1bad'")
  })

  it("AttrsAsArray_FailsWithClearError", () => {
    expect(() => renderStructuredPrompt({ artifact: { attrs: ["bad"] } }))
      .toThrow("Structured prompt block 'artifact' 'attrs' must be an object")
  })

  it("AttrsWithObjectValue_FailsWithClearError", () => {
    expect(() => renderStructuredPrompt({ artifact: { attrs: { id: { nested: true } } } }))
      .toThrow("Structured prompt attribute 'id' on 'artifact' must be a string, number, or boolean")
  })

  it("ArrayChildWithObjectItems_FailsWithClearError", () => {
    expect(() => renderStructuredPrompt({ artifact: { items: [{ a: 1 } as never] } }))
      .toThrow("Structured prompt list 'items' supports only primitive items")
  })

  it("ChildKeyInvalidTagName_FailsWithClearError", () => {
    expect(() => renderStructuredPrompt({ artifact: { "bad name": "x" } }))
      .toThrow("Structured prompt child key must be a valid tag name: 'bad name'")
  })
})

describe("PromptLoaderRegistry and test hook", () => {
  it("DefaultRegistry_IsRestoredAfterClearingTestOverride", async () => {
    const original = defaultPromptLoaderRegistry()
    original.register("temp/default", async () => "from-default")
    try {
      const override = new PromptLoaderRegistry()
      override.register("temp/default", async () => "from-override")
      setPromptLoaderRegistryForTest(override)
      expect(await resolvePrompt({ uses: "temp/default" }, baseLoaderContext())).toBe("from-override")

      setPromptLoaderRegistryForTest(null)
      expect(await resolvePrompt({ uses: "temp/default" }, baseLoaderContext())).toBe("from-default")
    } finally {
      original.unregister("temp/default")
    }
  })

  it("RegistryHasAndUnregister_BehaveCaseInsensitively", () => {
    const registry = new PromptLoaderRegistry()
    registry.register("Vendor/Name", async () => "x")
    expect(registry.has("vendor/name")).toBe(true)
    expect(registry.has("VENDOR/NAME")).toBe(true)
    expect(registry.has("other")).toBe(false)
    registry.unregister("VENDOR/NAME")
    expect(registry.has("vendor/name")).toBe(false)
  })

  it("RegisteringEmptyName_ThrowsImmediately", () => {
    const registry = new PromptLoaderRegistry()
    expect(() => registry.register("", async () => "x"))
      .toThrow("Prompt loader name must be a non-empty string")
  })
})

function baseLoaderContext(): PromptLoaderContext {
  return {
    with: {},
    workDir: "/tmp/test",
    workId: "work-1",
  }
}
