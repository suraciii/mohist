import { describe, expect, it } from "vitest"
import { extractSetVars } from "../src/runtime/set-vars.js"

describe("extractSetVars", () => {
  it("mapsOutputPathsIntoRuntimeProfileVars", () => {
    const result = extractSetVars({
      "github.pr.number": "output.prNumber",
      "github.pr.url": "prUrl",
    }, JSON.stringify({ prNumber: 249, prUrl: "https://example.test/pr/249" }))

    expect(result).toEqual({
      vars: {
        github: {
          pr: {
            number: 249,
            url: "https://example.test/pr/249",
          },
        },
      },
    })
  })

  it("failsWhenSourcePathIsMissing", () => {
    const result = extractSetVars({
      "github.pr.number": "output.prNumber",
    }, JSON.stringify({ prUrl: "https://example.test/pr/249" }))

    expect(result.vars).toBeNull()
    expect(result.error).toContain("source path 'output.prNumber' not found")
  })
})
