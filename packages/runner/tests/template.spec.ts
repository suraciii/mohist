import { describe, expect, it } from "vitest"
import { findTemplateReferences, renderTemplate, unresolvedReferences, wholeStringUnresolvedReferences } from "../src/core/template.js"

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

  it("UnresolvedEmbeddedReference_LeavesLiteral", () => {
    // Mirrors the T-001.6 scenario in #49 where the agent task description
    // embeds the literal text "${{ prompts.xxx }}" (a documentation example
    // the agent should read, not a template reference). The embedded form
    // must NOT fail the dispatch — the unresolved reference is preserved
    // verbatim so the agent sees the example.
    const rendered = renderTemplate(
      {
        prompt:
          "Read the proposal and implement it. The runner's ${{ prompts.xxx }} " +
          "resolution should remain byte-identical. Now ${{ openspecChangeDir }} please.",
      },
      { openspecChangeDir: "openspec/changes/issue-49" },
    )

    expect(rendered?.prompt).toBe(
      "Read the proposal and implement it. The runner's ${{ prompts.xxx }} " +
      "resolution should remain byte-identical. Now openspec/changes/issue-49 please.",
    )
  })

  it("UnresolvedEmbeddedReference_MixedWithResolved_ResolvesResolvables", () => {
    const rendered = renderTemplate(
      { prompt: "Start ${{ known }} then ${{ unknown }} end" },
      { known: "X" },
    )
    expect(rendered?.prompt).toBe("Start X then ${{ unknown }} end")
  })

  it("UnresolvedEmbeddedReference_OnlyUnresolvable_NoProgress", () => {
    const rendered = renderTemplate(
      { prompt: "${{ a }} and ${{ b }}" },
      {},
    )
    expect(rendered?.prompt).toBe("${{ a }} and ${{ b }}")
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
    // The T-001.6 case: a description embeds the literal text "${{ prompts.xxx }}"
    // alongside other resolvable variables. The whole-string check should NOT
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
