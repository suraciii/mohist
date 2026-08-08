import { describe, expect, it as vitestIt } from "vitest"
import {
  evaluateCompletion,
  parseLastMarker,
  promiseValue,
} from "../src/actions/expectations.js"
import { join } from "node:path"
import { createTestTempDirSync } from "./support/temp-dir.js"
import { currentRunnerFileSystem } from "../src/system/filesystem.js"
import { withTestRunnerResources } from "./support/test-resources.js"

const it = Object.assign(
  (name: string, body: () => unknown) => vitestIt(name, () => withTestRunnerResources(async () => await body())),
  { each: vitestIt.each.bind(vitestIt) },
) as typeof vitestIt

describe("evaluateCompletion", () => {
  it("AllArtifactsPresent_ReturnsSatisfied", async () => {
    const dir = mkTestDir()
    await currentRunnerFileSystem().writeText(join(dir, "file.txt"), "hello world")

    const result = await evaluateCompletion({
      files: [{ path: "file.txt" }],
      markers: [{ path: "file.txt", contains: "hello" }],
    }, dir)

    expect(result.satisfied).toBe(true)
    expect(result.missingFiles).toHaveLength(0)
    expect(result.missingMarkers).toHaveLength(0)
    expect(result.message).toContain("satisfied")
  })

  it("MissingFile_ReturnsFileDiagnostic", async () => {
    const dir = mkTestDir()

    const result = await evaluateCompletion({
      files: [{ path: "missing.txt" }],
    }, dir)

    expect(result.satisfied).toBe(false)
    expect(result.missingFiles).toHaveLength(1)
    expect(result.missingFiles[0].path).toContain("missing.txt")
    expect(result.message).toMatch(/missing required file/)
  })

  it("MissingMarker_ReturnsMarkerDiagnostic", async () => {
    const dir = mkTestDir()
    await currentRunnerFileSystem().writeText(join(dir, "file.txt"), "hello world")

    const result = await evaluateCompletion({
      markers: [{ path: "file.txt", contains: "## Section" }],
    }, dir)

    expect(result.satisfied).toBe(false)
    expect(result.missingMarkers).toHaveLength(1)
    expect(result.missingMarkers[0].path).toContain("file.txt")
    expect(result.missingMarkers[0].contains).toBe("## Section")
    expect(result.message).toMatch(/missing marker/)
  })

  it("VerdictMarkerNotInExpectation_DoesNotFailHere", async () => {
    const dir = mkTestDir()
    await currentRunnerFileSystem().writeText(join(dir, "review.md"), "<promise>FAIL</promise>")

    const result = await evaluateCompletion({
      files: [{ path: "review.md" }],
    }, dir)

    expect(result.satisfied).toBe(true)
  })

  it("OneOfMarkers_PASSValue_SatisfiesExpectation", async () => {
    const dir = mkTestDir()
    await currentRunnerFileSystem().writeText(join(dir, "review.md"), "Looks good.\n<promise>PASS</promise>\n")

    const result = await evaluateCompletion({
      markers: [{
        path: "review.md",
        oneOf: ["<promise>PASS</promise>", "<promise>FAIL</promise>"],
      }],
    }, dir)

    expect(result.satisfied).toBe(true)
    expect(result.missingMarkers).toHaveLength(0)
    expect(result.message).toContain("satisfied")
  })

  it("OneOfMarkers_FAILValue_SatisfiesExpectation", async () => {
    const dir = mkTestDir()
    await currentRunnerFileSystem().writeText(join(dir, "review.md"), "Issues found.\n<promise>FAIL</promise>\n")

    const result = await evaluateCompletion({
      markers: [{
        path: "review.md",
        oneOf: ["<promise>PASS</promise>", "<promise>FAIL</promise>"],
      }],
    }, dir)

    expect(result.satisfied).toBe(true)
    expect(result.missingMarkers).toHaveLength(0)
    expect(result.message).toContain("satisfied")
  })

  it("OneOfMarkers_NeitherValuePresent_KeepsAskingForRequiredFormat", async () => {
    const dir = mkTestDir()
    await currentRunnerFileSystem().writeText(join(dir, "review.md"), "Still drafting the review.")

    const result = await evaluateCompletion({
      markers: [{
        path: "review.md",
        oneOf: ["<promise>PASS</promise>", "<promise>FAIL</promise>"],
      }],
    }, dir)

    expect(result.satisfied).toBe(false)
    expect(result.missingMarkers).toHaveLength(1)
    expect(result.missingMarkers[0].path).toContain("review.md")
    expect(result.missingMarkers[0].contains).toContain("oneOf")
    expect(result.missingMarkers[0].contains).toContain("<promise>PASS</promise>")
    expect(result.missingMarkers[0].contains).toContain("<promise>FAIL</promise>")
    expect(result.message).toMatch(/missing marker/)
  })

  it("OneOfMarkers_TargetFileMissing_ReportsMissingFileMarker", async () => {
    const dir = mkTestDir()

    const result = await evaluateCompletion({
      markers: [{
        path: "review.md",
        oneOf: ["<promise>PASS</promise>", "<promise>FAIL</promise>"],
      }],
    }, dir)

    expect(result.satisfied).toBe(false)
    expect(result.missingMarkers).toHaveLength(1)
    expect(result.missingMarkers[0].contains).toContain("oneOf")
  })

  it("OneOfMarkers_BeatsContainsFallback_AcceptsListedValue", async () => {
    const dir = mkTestDir()
    await currentRunnerFileSystem().writeText(join(dir, "review.md"), "<promise>FAIL</promise>")

    const result = await evaluateCompletion({
      markers: [{
        path: "review.md",
        contains: "<promise>PASS</promise>",
        oneOf: ["<promise>PASS</promise>", "<promise>FAIL</promise>"],
      }],
    }, dir)

    expect(result.satisfied).toBe(true)
  })

  it("OneOfMarkers_DeclarationOrderPASSBeforeFAIL_ChoosesPASSWhenBothPresent", async () => {
    // File-backed marker precedence: declaration order. The file
    // contains both PASS and FAIL (in that order); declaration lists
    // PASS before FAIL, so PASS wins (and a FAIL failIf MUST NOT fire
    // for that match). Design D3 + spec "first present in
    // declaration order".
    const dir = mkTestDir()
    await currentRunnerFileSystem().writeText(join(dir, "review.md"), "<promise>PASS</promise>\n<promise>FAIL</promise>\n")

    const result = await evaluateCompletion({
      markers: [{
        path: "review.md",
        oneOf: ["<promise>PASS</promise>", "<promise>FAIL</promise>"],
        failIf: "<promise>FAIL</promise>",
      }],
    }, dir)

    expect(result.satisfied).toBe(true)
    expect(result.matched).toBe("<promise>PASS</promise>")
    expect(result.failIfMatches).toHaveLength(0)
  })

  it("OutputMarkers_DoneMarker_SatisfiesAndReturnsMatched", async () => {
    const dir = mkTestDir()
    const agentText = "Fixed all test failures. All suites pass.\n\n<promise>done</promise>"

    const result = await evaluateCompletion({
      markers: [{
        path: "_output",
        oneOf: ["<promise>done</promise>", "<promise>unfinished</promise>"],
      }],
    }, dir, agentText)

    expect(result.satisfied).toBe(true)
    expect(result.matched).toBe("<promise>done</promise>")
    expect(result.missingMarkers).toHaveLength(0)
  })

  it("OutputMarkers_UnfinishedMarker_SatisfiesAndReturnsMatched", async () => {
    const dir = mkTestDir()
    const agentText = "Could not finish fixing all tests.\n\n<promise>unfinished</promise>"

    const result = await evaluateCompletion({
      markers: [{
        path: "_output",
        oneOf: ["<promise>done</promise>", "<promise>unfinished</promise>"],
      }],
    }, dir, agentText)

    expect(result.satisfied).toBe(true)
    expect(result.matched).toBe("<promise>unfinished</promise>")
    expect(result.missingMarkers).toHaveLength(0)
  })

  it("OutputMarkers_LastMatchWins_IgnoresEarlierReferences", async () => {
    const dir = mkTestDir()
    const agentText = "I haven't finished yet (this is <promise>unfinished</promise>).\n\nNow all work is done.\n\n<promise>done</promise>"

    const result = await evaluateCompletion({
      markers: [{
        path: "_output",
        oneOf: ["<promise>done</promise>", "<promise>unfinished</promise>"],
      }],
    }, dir, agentText)

    expect(result.satisfied).toBe(true)
    expect(result.matched).toBe("<promise>done</promise>")
  })

  it("OutputMarkers_ArbitraryPromiseVerdict_PASSIsRecognized", async () => {
    // The generalized parser MUST accept arbitrary <promise>VALUE</promise>
    // markers (not just lowercase done/unfinished). The legacy parser
    // hardcoded two literals; this case locks the new contract.
    const dir = mkTestDir()
    const agentText = "All checks passed.\n<promise>PASS</promise>"

    const result = await evaluateCompletion({
      markers: [{
        path: "_output",
        oneOf: ["<promise>PASS</promise>", "<promise>FAIL</promise>"],
      }],
    }, dir, agentText)

    expect(result.satisfied).toBe(true)
    expect(result.matched).toBe("<promise>PASS</promise>")
  })

  it("OutputMarkers_NoMarker_ReturnsUnsatisfied", async () => {
    const dir = mkTestDir()
    const agentText = "Fixed all test failures. All suites pass."

    const result = await evaluateCompletion({
      markers: [{
        path: "_output",
        oneOf: ["<promise>done</promise>", "<promise>unfinished</promise>"],
      }],
    }, dir, agentText)

    expect(result.satisfied).toBe(false)
    expect(result.matched).toBeUndefined()
    expect(result.missingMarkers).toHaveLength(1)
    expect(result.missingMarkers[0].path).toBe("_output")
  })

  it("OutputMarkers_NoTurnFact_ReturnsUnsatisfiedWithoutFallingBackToOutput", async () => {
    const dir = mkTestDir()

    const result = await evaluateCompletion({
      markers: [{
        path: "_output",
        oneOf: ["<promise>done</promise>", "<promise>unfinished</promise>"],
      }],
    }, dir)

    expect(result.satisfied).toBe(false)
    expect(result.matched).toBeUndefined()
    expect(result.missingMarkers).toHaveLength(1)
    expect(result.missingMarkers[0].path).toBe("_output")
    // Specifically: the marker MUST remain unsatisfied even if the
    // Action produced some output. The executor never falls back to
    // Action Output for _output (design D4 / spec scenario).
  })

  it("OutputMarkers_FallBackToActionOutput_ReturnsUnsatisfied", async () => {
    const dir = mkTestDir()
    // If a turn fact carries a string that does not contain any
    // accepted promise marker, completion still fails — the text is
    // treated as opaque. (We can't supply Action Output here; we just
    // verify the empty-text branch from above.)
    const result = await evaluateCompletion({
      markers: [{
        path: "_output",
        oneOf: ["<promise>done</promise>"],
      }],
    }, dir, "")

    expect(result.satisfied).toBe(false)
  })

  it("OutputMarkers_MixedWithFileMarkers_BothSatisfied", async () => {
    const dir = mkTestDir()
    await currentRunnerFileSystem().writeText(join(dir, "review.md"), "<promise>PASS</promise>")
    const agentText = "All work done.\n\n<promise>done</promise>"

    const result = await evaluateCompletion({
      markers: [
        { path: "review.md", oneOf: ["<promise>PASS</promise>", "<promise>FAIL</promise>"] },
        { path: "_output", oneOf: ["<promise>done</promise>", "<promise>unfinished</promise>"] },
      ],
    }, dir, agentText)

    expect(result.satisfied).toBe(true)
    expect(result.matched).toBe("<promise>done</promise>")
    expect(result.missingMarkers).toHaveLength(0)
  })

  it("FailIf_PASSMarker_DoesNotFailTask", async () => {
    const dir = mkTestDir()
    await currentRunnerFileSystem().writeText(join(dir, "review.md"), "<promise>PASS</promise>")

    const result = await evaluateCompletion({
      markers: [{
        path: "review.md",
        oneOf: ["<promise>PASS</promise>", "<promise>FAIL</promise>"],
        failIf: "<promise>FAIL</promise>",
      }],
    }, dir)

    expect(result.satisfied).toBe(true)
    expect(result.failIfMatches).toHaveLength(0)
    expect(result.matched).toBe("<promise>PASS</promise>")
    expect(result.missingMarkers).toHaveLength(0)
  })

  it("FailIf_FAILMarker_FailsTask", async () => {
    const dir = mkTestDir()
    await currentRunnerFileSystem().writeText(
      join(dir, "review.md"),
      "Reviewing found issues.\n<promise>FAIL</promise>\n",
    )

    const result = await evaluateCompletion({
      markers: [{
        path: "review.md",
        oneOf: ["<promise>PASS</promise>", "<promise>FAIL</promise>"],
        failIf: "<promise>FAIL</promise>",
      }],
    }, dir)

    expect(result.satisfied).toBe(false)
    expect(result.failIfMatches).toHaveLength(1)
    expect(result.failIfMatches[0].marker).toBe("<promise>FAIL</promise>")
    expect(result.failIfMatches[0].failIf).toBe("<promise>FAIL</promise>")
    expect(result.failIfMatches[0].path).toContain("review.md")
    expect(result.missingMarkers).toHaveLength(0)
    expect(result.matched).toBe("<promise>FAIL</promise>")
    expect(result.message).toContain("failIf marker matched")
  })

  it("FailIf_NoMarkerMatched_DoesNotFailTask", async () => {
    const dir = mkTestDir()
    await currentRunnerFileSystem().writeText(join(dir, "review.md"), "Still drafting the review.")

    const result = await evaluateCompletion({
      markers: [{
        path: "review.md",
        oneOf: ["<promise>PASS</promise>", "<promise>FAIL</promise>"],
        failIf: "<promise>FAIL</promise>",
      }],
    }, dir)

    expect(result.satisfied).toBe(false)
    expect(result.failIfMatches).toHaveLength(0)
    expect(result.missingMarkers).toHaveLength(1)
  })

  it("FailIf_OutputMarker_UnfinishedMarker_FailsTask", async () => {
    const dir = mkTestDir()
    const agentText = "Could not finish.\n<promise>unfinished</promise>"

    const result = await evaluateCompletion({
      markers: [{
        path: "_output",
        oneOf: ["<promise>done</promise>", "<promise>unfinished</promise>"],
        failIf: "<promise>unfinished</promise>",
      }],
    }, dir, agentText)

    expect(result.satisfied).toBe(false)
    expect(result.failIfMatches).toHaveLength(1)
    expect(result.failIfMatches[0].marker).toBe("<promise>unfinished</promise>")
    expect(result.matched).toBe("<promise>unfinished</promise>")
  })

  it("FailIf_OutputMarker_DoneMarker_DoesNotFailTask", async () => {
    const dir = mkTestDir()
    const agentText = "All done.\n<promise>done</promise>"

    const result = await evaluateCompletion({
      markers: [{
        path: "_output",
        oneOf: ["<promise>done</promise>", "<promise>unfinished</promise>"],
        failIf: "<promise>unfinished</promise>",
      }],
    }, dir, agentText)

    expect(result.satisfied).toBe(true)
    expect(result.failIfMatches).toHaveLength(0)
    expect(result.matched).toBe("<promise>done</promise>")
  })

  it("FailIf_MultipleMarkers_AllFailIfHitsRecorded", async () => {
    const dir = mkTestDir()
    await currentRunnerFileSystem().writeText(
      join(dir, "review.md"),
      "errorCode: review-failed\n<promise>FAIL</promise>\n",
    )

    const result = await evaluateCompletion({
      markers: [
        {
          path: "review.md",
          oneOf: ["<promise>PASS</promise>", "<promise>FAIL</promise>"],
          failIf: "<promise>FAIL</promise>",
        },
        {
          path: "review.md",
          contains: "<promise>FAIL</promise>",
          failIf: "<promise>FAIL</promise>",
        },
      ],
    }, dir)

    expect(result.satisfied).toBe(false)
    expect(result.failIfMatches).toHaveLength(2)
    expect(result.failIfMatches.every((m) => m.marker === "<promise>FAIL</promise>")).toBe(true)
  })
})

describe("parseLastMarker", () => {
  it("ReturnsLastAcceptedMarker", () => {
    expect(parseLastMarker("first <promise>PASS</promise> then <promise>FAIL</promise>", ["<promise>PASS</promise>", "<promise>FAIL</promise>"])).toBe("<promise>FAIL</promise>")
  })

  it("ReturnsLastAcceptedMarkerEvenWhenOthersTrailingIt", () => {
    expect(parseLastMarker("<promise>PASS</promise> mid <promise>FAIL</promise>", ["<promise>PASS</promise>", "<promise>FAIL</promise>"])).toBe("<promise>FAIL</promise>")
  })

  it("IgnoresNonAcceptedValues", () => {
    // Spec scenario: <promise>PASS</promise> and <promise>FAIL</promise> with
    // PASS followed by FAIL — the matched marker SHALL be FAIL even though
    // a stray value appears after it.
    expect(parseLastMarker("<promise>PASS</promise> then <promise>FAIL</promise> then <promise>other</promise>", ["<promise>PASS</promise>", "<promise>FAIL</promise>"])).toBe("<promise>FAIL</promise>")
  })

  it("ReturnsNullForAbsentMarker", () => {
    expect(parseLastMarker("no marker here", ["<promise>PASS</promise>"])).toBeNull()
  })

  it("ReturnsNullForEmptyAcceptedList", () => {
    expect(parseLastMarker("<promise>PASS</promise>", [])).toBeNull()
  })

  it("ReturnsNullForEmptyText", () => {
    expect(parseLastMarker("", ["<promise>PASS</promise>"])).toBeNull()
  })
})

describe("promiseValue", () => {
  it("ExtractsBareVerdictFromPromiseMarker", () => {
    expect(promiseValue("<promise>PASS</promise>")).toBe("PASS")
    expect(promiseValue("<promise>FAIL</promise>")).toBe("FAIL")
    expect(promiseValue("<promise>done</promise>")).toBe("done")
    expect(promiseValue("<promise>unfinished</promise>")).toBe("unfinished")
  })

  it("ReturnsNullForAbsentOrNonPromiseMarker", () => {
    expect(promiseValue(undefined)).toBeNull()
    expect(promiseValue(null)).toBeNull()
    expect(promiseValue("not a marker")).toBeNull()
    expect(promiseValue("<other>FAIL</other>")).toBeNull()
  })
})

function mkTestDir(): string {
  return createTestTempDirSync("mohist-test-")
}
