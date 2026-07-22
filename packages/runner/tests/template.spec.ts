import { describe, expect, it } from "vitest"
import { findTemplateReferences, renderTemplate, renderWithSkippedFields, unresolvedReferences, wholeStringUnresolvedReferences } from "../src/core/template.js"

describe("renderTemplate", () => {
  it("VariableValueContainsTemplate_RendersNestedVariables", () => {
    const rendered = renderTemplate({
      prompt: "${{ prompts.proposal }}",
    }, {
      prompts: {
        proposal: "Write to ${{ openspecChangeDir }}/proposal.md for ${{ issue.title }}",
      },
      openspecChangeDir: "openspec/changes/issue-2",
      issue: {
        title: "Document update smoke validation note after update",
      },
    })

    expect(rendered?.prompt).toBe("Write to openspec/changes/issue-2/proposal.md for Document update smoke validation note after update")
  })

  it("NestedExpansionDoesNotCoerceFullObjectVariables", () => {
    const rendered = renderTemplate({ agent: "${{ vars.agent }}" }, { vars: { agent: { type: "opencode" } } })
    expect(rendered?.agent).toEqual({ type: "opencode" })
  })

  it("EscapedDollarBrace_RendersLiteralText", () => {
    const rendered = renderTemplate({
      description: "The runner's \\${{ prompts.xxx }} resolution is unaffected.",
    }, { prompts: { xxx: "should-not-appear" } })

    expect(rendered?.description).toBe("The runner's ${{ prompts.xxx }} resolution is unaffected.")
  })

  it("EscapedDollarBrace_AdjacentToRealReference_BothWork", () => {
    const rendered = renderTemplate({
      prompt: "literal: \\${{ fake }}; real: ${{ real }}",
    }, { real: "expanded" })

    expect(rendered?.prompt).toBe("literal: ${{ fake }}; real: expanded")
  })

  it("EscapedDollarBrace_DoubleEscapeProducesLiteralBackslash", () => {
    // \\${{ → \ + ${{ (literal). The first \ escapes the second \.
    const rendered = renderTemplate({ prompt: "\\\\${{ x }}" }, { x: "should-not-appear" })
    expect(rendered?.prompt).toBe("\\${{ x }}")
  })

  it("UnresolvedReference_Throws", () => {
    expect(() => renderTemplate({ prompt: "${{ unknown.var }}" }, {})).toThrow("Template variable 'unknown.var' was not found")
  })

  it("UnresolvedEmbeddedReference_Throws", () => {
    expect(() => renderTemplate({ prompt: "see ${{ prompts.xxx }} for details" }, {})).toThrow("prompts.xxx")
  })

  it("UnresolvedEmbeddedReference_MixedWithResolved_Throws", () => {
    expect(() => renderTemplate({ prompt: "Start ${{ known }} then ${{ unknown }} end" }, { known: "X" })).toThrow("unknown")
  })

  it("UnresolvedEmbeddedReference_OnlyUnresolvable_Throws", () => {
    expect(() => renderTemplate({ prompt: "${{ a }} and ${{ b }}" }, {})).toThrow("a")
  })

  it("LiteralFieldPath_SkipsRendering", () => {
    // expect.markers[*].contains is a literal search string, not a file path.
    const rendered = renderTemplate({
      expect: {
        markers: [
          { path: "${{ file }}/x.md", contains: "see ${{ docs }} for details" },
        ],
      },
    }, {
      file: "/tmp",
      docs: "should-not-be-rendered-here",
    }) as { expect: { markers: Array<{ path: string; contains: string }> } }

    expect(rendered.expect.markers[0].path).toBe("/tmp/x.md")
    expect(rendered.expect.markers[0].contains).toBe("see ${{ docs }} for details")
  })

  it("LiteralFieldPath_MatchesAnyArrayIndex", () => {
    const rendered = renderTemplate({
      expect: {
        markers: [
          { path: "${{ a }}/0", contains: "literal-0: ${{ a }}" },
          { path: "${{ a }}/1", contains: "literal-1: ${{ a }}" },
        ],
      },
    }, { a: "ok" }) as { expect: { markers: Array<{ path: string; contains: string }> } }

    expect(rendered.expect.markers[0].contains).toBe("literal-0: ${{ a }}")
    expect(rendered.expect.markers[1].contains).toBe("literal-1: ${{ a }}")
  })

  it("LiteralFieldPath_StandaloneExpectRoot_ProtectsMarkersContainsAndOneOfEntries", () => {
    // T-003 acceptance: "template.ts LITERAL_FIELD_PATHS protects
    // markers.*.contains and markers.*.oneOf.* when rendering expect
    // as a standalone object" (i.e. when the renderer is called on
    // `expect` directly, not on `with`).
    const rendered = renderTemplate({
      markers: [
        {
          path: "${{ openspecChangeDir }}/review.md",
          contains: "see ${{ docs }} for details",
          oneOf: ["<promise>${{ verdict }}</promise>", "<promise>FAIL</promise>"],
        },
      ],
    }, {
      openspecChangeDir: "openspec/changes/issue-408",
      docs: "should-not-be-rendered-here",
      verdict: "PASS",
    }) as { markers: Array<{ path: string; contains: string; oneOf: string[] }> }

    // `path` is a regular (non-literal) field; the embedded template
    // expression resolves to the dispatch value.
    expect(rendered.markers[0].path).toBe("openspec/changes/issue-408/review.md")
    // `contains` is a literal-field path: the embedded `${{ docs }}`
    // reference survives rendering byte-identically so the marker
    // text can be searched for verbatim.
    expect(rendered.markers[0].contains).toBe("see ${{ docs }} for details")
    // `oneOf` entries are literal-field paths: the array entries
    // remain untouched. The `verdict` variable would otherwise expand
    // to `PASS` and turn `<promise>${{ verdict }}</promise>` into
    // `<promise>PASS</promise>`; we lock the byte-identical
    // rendering for this acceptance criterion.
    expect(rendered.markers[0].oneOf).toEqual([
      "<promise>${{ verdict }}</promise>",
      "<promise>FAIL</promise>",
    ])
  })
})

describe("renderWithSkippedFields", () => {
  it("renders string values as strings (not character-indexed objects)", () => {
    // Regression: previously each value was passed through renderTemplate,
    // which iterates Object.entries — a string "abc" became {"0":"a","1":"b","2":"c"}.
    const rendered = renderWithSkippedFields(
      {
        path: "${{ openspecChangeDir }}/tasks.json",
        buildPrompt: "issue=${{ issue.number }}",
      },
      { openspecChangeDir: "openspec/changes/issue-7", issue: { number: 7 } },
      new Set(),
    )

    expect(rendered?.path).toBe("openspec/changes/issue-7/tasks.json")
    expect(rendered?.buildPrompt).toBe("issue=7")
    expect(typeof rendered?.path).toBe("string")
    expect(typeof rendered?.buildPrompt).toBe("string")
  })

  it("passes through skipped fields without rendering", () => {
    const task = { uses: "mohist/opencode", with: { base: "${{ prompts.build }}" } }
    const rendered = renderWithSkippedFields(
      { path: "${{ openspecChangeDir }}/tasks.json", task },
      { openspecChangeDir: "openspec/changes/issue-7" },
      new Set(["task"]),
    )

    expect(rendered?.path).toBe("openspec/changes/issue-7/tasks.json")
    // Skipped field is byte-identical (still contains the unrendered reference)
    expect(rendered?.task).toEqual(task)
  })

  it("whole-string reference resolving to an object returns the object, not a stringified form", () => {
    const rendered = renderWithSkippedFields(
      { options: "${{ vars.agent }}" },
      { vars: { agent: { model: "opencode" } } },
      new Set(),
    )

    expect(rendered?.options).toEqual({ model: "opencode" })
  })

  it("rejects object and array values in string interpolation", () => {
    expect(() => renderWithSkippedFields({ text: "model=${{ vars.agent }}" }, { vars: { agent: { model: "opencode" } } }, new Set())).toThrow(/object or array/)
    expect(() => renderWithSkippedFields({ text: "items=${{ vars.items }}" }, { vars: { items: ["a"] } }, new Set())).toThrow(/object or array/)
  })

  it("expands nested references and rejects cycles and excessive depth", () => {
    expect(renderTemplate({ value: "${{ vars.alias }}" }, { vars: { alias: "${{ vars.real }}", real: "done" } })?.value).toBe("done")
    expect(() => renderTemplate({ value: "prefix ${{ vars.a }}" }, { vars: { a: "${{ vars.b }}", b: "${{ vars.a }}" } })).toThrow(/cycle/)

    const vars: Record<string, string> = { value: "done" }
    for (let index = 0; index < 6; index += 1) vars[`v${index}`] = `\${{ vars.${index === 5 ? "value" : `v${index + 1}`} }}`
    expect(() => renderTemplate({ value: "${{ vars.v0 }}" }, { vars })).toThrow(/maximum depth/)
  })

  it("returns null for null/undefined input", () => {
    expect(renderWithSkippedFields(null, {}, new Set())).toBeNull()
    expect(renderWithSkippedFields(undefined, {}, new Set())).toBeNull()
  })
})

describe("renderTemplate input guards", () => {
  it("rejects a non-object input instead of silently iterating its entries", () => {
    // Passing a string to renderTemplate would previously walk its character
    // indices and return {"0":"a","1":"b",…}. Guard so future misuse throws.
    expect(() => renderTemplate("literal string" as unknown as Record<string, never>, {})).toThrow(/JSON object/)
    expect(() => renderTemplate(["a", "b"] as unknown as Record<string, never>, {})).toThrow(/array/)
  })
})

describe("findTemplateReferences", () => {
  it("ReturnsAllUniqueReferencesInStringValues", () => {
    const refs = findTemplateReferences({
      prompt: "${{ a }} and ${{ b }}",
      path: "${{ c }}/x",
      expect: { markers: [{ contains: "literal ${{ d }}" }] },
    })

    // 'd' is in a literal-field path, so it should not be reported.
    expect(refs.sort()).toEqual(["a", "b", "c"])
  })

  it("SkipsEscape", () => {
    const refs = findTemplateReferences({
      prompt: "literal: \\${{ a }}; real: ${{ b }}",
    })
    expect(refs).toEqual(["b"])
  })

  it("ReturnsEmptyForNullOrUndefined", () => {
    expect(findTemplateReferences(null)).toEqual([])
    expect(findTemplateReferences(undefined)).toEqual([])
  })

  it("HandlesNestedObjectsAndArrays", () => {
    const refs = findTemplateReferences({
      level1: {
        level2: [
          { value: "${{ a }}" },
          { value: "${{ b }} and ${{ a }}" },
        ],
      },
    })
    expect(refs.sort()).toEqual(["a", "b"])
  })
})

describe("unresolvedReferences", () => {
  it("ReturnsOnlyPathsNotInVariables", () => {
    const unresolved = unresolvedReferences(
      { prompt: "${{ known }} and ${{ unknown.a }} and ${{ known }}" },
      { known: "value" }
    )
    expect(unresolved).toEqual(["unknown.a"])
  })

  it("IgnoresLiteralFieldPaths", () => {
    const unresolved = unresolvedReferences(
      { expect: { markers: [{ contains: "${{ would.fail }}" }] } },
      {}
    )
    expect(unresolved).toEqual([])
  })

  it("ReturnsEmptyWhenAllResolve", () => {
    const unresolved = unresolvedReferences(
      { prompt: "${{ a.b }}" },
      { a: { b: "value" } }
    )
    expect(unresolved).toEqual([])
  })
})

describe("wholeStringUnresolvedReferences", () => {
  it("ReturnsWholeStringReferences", () => {
    const unresolved = wholeStringUnresolvedReferences(
      { prompt: "${{ known }} and ${{ unknown.a }} and ${{ known }}" },
      { known: "value" }
    )
    expect(unresolved).toEqual([])
  })

  it("ReturnsOnlyEmbeddedUnresolved_AsEmpty", () => {
    // A description can embed the literal text "${{ prompts.xxx }}" alongside
    // other resolvable variables. The whole-string check should NOT
    // flag it because the unresolved reference is not the entire value of any
    // string field.
    const unresolved = wholeStringUnresolvedReferences(
      {
        description:
          "The runner's ${{ prompts.xxx }} resolution should be unaffected. " +
          "Write to ${{ openspecChangeDir }}.",
      },
      { openspecChangeDir: "openspec/changes/issue-49" },
    )
    expect(unresolved).toEqual([])
  })

  it("FlagsWholeStringUnresolved_AndIgnoresEmbedded", () => {
    const unresolved = wholeStringUnresolvedReferences(
      {
        title: "${{ typo }}",
        description: "Embedded ${{ typo }} does not fail this field.",
      },
      {},
    )
    expect(unresolved).toEqual(["typo"])
  })

  it("ReturnsEmptyForNullOrUndefined", () => {
    expect(wholeStringUnresolvedReferences(null, {})).toEqual([])
    expect(wholeStringUnresolvedReferences(undefined, {})).toEqual([])
  })
})

describe("task output variables", () => {
  it("ResolvesTaskOutputReferenceInWithValue", () => {
    const rendered = renderTemplate(
      { path: "${{ tasks.proposal.outputs.openspecName }}/specs" },
      { tasks: { proposal: { outputs: { openspecName: "issue-97" } } } },
    ) as { path: string }

    expect(rendered.path).toBe("issue-97/specs")
  })

  it("ResolvesTaskOutputReferenceInArtifactPath", () => {
    const rendered = renderTemplate(
      { files: [{ path: "${{ tasks.proposal.outputs.changeDir }}/review.md" }] },
      { tasks: { proposal: { outputs: { changeDir: "openspec/changes/issue-97" } } } },
    ) as { files: Array<{ path: string }> }

    expect(rendered.files[0].path).toBe("openspec/changes/issue-97/review.md")
  })

  it("MissingTaskOutputReferenceFailsWhenEmbedded", () => {
    expect(() => renderTemplate({ path: "${{ tasks.proposal.outputs.missing }}/specs" }, { tasks: { proposal: { outputs: {} } } })).toThrow("tasks.proposal.outputs.missing")
  })

  it("MissingTaskOutputReferenceFailsAsWholeString", () => {
    expect(() => renderTemplate({ path: "${{ tasks.proposal.outputs.missing }}" }, {})).toThrow("tasks.proposal.outputs.missing")
  })

  it("MissingTaskOutputReferenceIsReportedAsUnresolved", () => {
    expect(unresolvedReferences({ path: "${{ tasks.proposal.outputs.missing }}/specs" }, {})).toEqual(["tasks.proposal.outputs.missing"])
  })

  it("NonTaskUnresolvedReferenceStillFailsAsBefore", () => {
    expect(() => renderTemplate({ path: "${{ unknown.var }}" }, {})).toThrow("Template variable 'unknown.var' was not found")
  })

  it("ExistingTemplateBehaviorWithTaskOutputsUnchanged", () => {
    const rendered = renderTemplate(
      { prompt: "Write to ${{ openspecChangeDir }}/proposal.md for ${{ issue.title }}" },
      {
        openspecChangeDir: "openspec/changes/issue-97",
        issue: { title: "Document update" },
        tasks: { proposal: { outputs: { openspecName: "issue-97" } } },
      },
    ) as { prompt: string }

    expect(rendered.prompt).toBe("Write to openspec/changes/issue-97/proposal.md for Document update")
  })
})
