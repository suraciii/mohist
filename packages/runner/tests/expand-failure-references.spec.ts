import { describe, expect, it } from "vitest"
import { expandFailureReferences, UnresolvedFailureReferenceError } from "../src/runtime/recovery.js"

describe("expandFailureReferences", () => {
  const failureContext = {
    errorCode: "pr-checks-failed",
    prNumber: 42,
    prUrl: "https://example/pr/42",
    nested: {
      ok: true,
      list: [1, 2, 3],
    },
    nullish: null,
  }

  it("expands a whole-string failure.output.<field> preserving the JSON type", () => {
    expect(expandFailureReferences("${{ failure.output.prNumber }}", failureContext)).toBe(42)
  })

  it("preserves boolean type for whole-string references", () => {
    expect(expandFailureReferences("${{ failure.output.nested.ok }}", failureContext)).toBe(true)
  })

  it("preserves array type for whole-string references", () => {
    expect(expandFailureReferences("${{ failure.output.nested.list }}", failureContext)).toEqual([1, 2, 3])
  })

  it("preserves null values from the failure context", () => {
    expect(expandFailureReferences("${{ failure.output.nullish }}", failureContext)).toBeNull()
  })

  it("substitutes embedded failure references into surrounding strings", () => {
    const expanded = expandFailureReferences(
      "failed for PR #${{ failure.output.prNumber }} (${{ failure.output.prUrl }})",
      failureContext,
    )
    expect(expanded).toBe("failed for PR #42 (https://example/pr/42)")
  })

  it("expands whole-string failure.output to the full structured output", () => {
    const expanded = expandFailureReferences("${{ failure.output }}", failureContext)
    expect(expanded).toEqual(failureContext)
  })

  it("expands an Action error field from the structured failure context", () => {
    expect(expandFailureReferences("${{ failure.error.message }}", {
      output: null,
      error: { code: "timeout", message: "OpenCode turn timed out" },
    })).toBe("OpenCode turn timed out")
  })

  it("walks object values and expands nested failure references", () => {
    const expanded = expandFailureReferences(
      {
        targetPr: "${{ failure.output.prNumber }}",
        nested: {
          url: "PR #${{ failure.output.prNumber }}",
        },
      },
      failureContext,
    )
    expect(expanded).toEqual({
      targetPr: 42,
      nested: { url: "PR #42" },
    })
  })

  it("walks array values and expands failure references inside each element", () => {
    const expanded = expandFailureReferences(
      ["${{ failure.output.prNumber }}", "static", "${{ failure.output.prUrl }}"],
      failureContext,
    )
    expect(expanded).toEqual([42, "static", "https://example/pr/42"])
  })

  it("leaves non-failure namespaces byte-for-byte unchanged", () => {
    const input = {
      agent: "${{ vars.agent }}",
      branch: "${{ workspace.branch }}",
      stage: "${{ stage.name }}",
      repo: "${{ repository.name }}",
      prompt: "${{ prompts.fix-pr-checks }}",
      docs: "${{ docs.somewhere }}",
      escaped: "\\${{ literal }}",
    }
    expect(expandFailureReferences(input, failureContext)).toEqual(input)
  })

  it("expands failure refs while leaving other namespaces unchanged in mixed strings", () => {
    const expanded = expandFailureReferences(
      "agent=${{ vars.agent }}; pr=${{ failure.output.prNumber }}",
      failureContext,
    )
    expect(expanded).toBe("agent=${{ vars.agent }}; pr=42")
  })

  it("throws UnresolvedFailureReferenceError on unresolvable whole-string path", () => {
    expect(() => expandFailureReferences("${{ failure.output.unknown }}", failureContext))
      .toThrowError(UnresolvedFailureReferenceError)
  })

  it("throws UnresolvedFailureReferenceError on unresolvable embedded path", () => {
    expect(() =>
      expandFailureReferences("failed for PR #${{ failure.output.unknown }}", failureContext),
    ).toThrowError(UnresolvedFailureReferenceError)
  })

  it("names the unresolvable path in the diagnostic", () => {
    try {
      expandFailureReferences("${{ failure.output.prNumber }}", {})
      throw new Error("expected throw")
    } catch (error) {
      expect(error).toBeInstanceOf(UnresolvedFailureReferenceError)
      expect((error as UnresolvedFailureReferenceError).path).toBe("failure.output.prNumber")
      expect((error as UnresolvedFailureReferenceError).message).toContain("failure.output.prNumber")
    }
  })

  it("treats an empty failure context as fully unresolvable", () => {
    expect(() => expandFailureReferences("${{ failure.output.prNumber }}", {})).toThrowError(
      UnresolvedFailureReferenceError,
    )
    expect(() =>
      expandFailureReferences("see ${{ failure.output.prNumber }}", {}),
    ).toThrowError(UnresolvedFailureReferenceError)
    expect(expandFailureReferences("no refs here", {})).toBe("no refs here")
  })

  it("does not collapse a whole-string object reference into its serialized form", () => {
    const expanded = expandFailureReferences("${{ failure.output.nested }}", failureContext)
    expect(expanded).toBeTypeOf("object")
    expect(expanded).toEqual({ ok: true, list: [1, 2, 3] })
  })
})
