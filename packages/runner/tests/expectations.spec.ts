import { describe, expect, it } from "vitest"
import { verifyExpectations, type TaskArtifactExpectation } from "../src/actions/expectations.js"
import type { ActionContext } from "../src/core/types.js"
import { mkdirSync, writeFileSync, rmSync } from "node:fs"
import { join } from "node:path"
import { tmpdir } from "node:os"

describe("verifyExpectations", () => {
  const makeContext = (withInput: Record<string, unknown>, workDir: string): ActionContext => ({
    workflowRunId: "wf-1",
    workId: "work-1",
    workType: "task",
    stage: "build",
    title: "Test task",
    uses: "mohist/acp-agent",
    with: withInput as never,
    variables: {},
    workDir,
    signal: new AbortController().signal,
    projectId: "project-1",
    issueNumber: 1,
  })

  it("AllArtifactsPresent_ReturnsSatisfied", async () => {
    const dir = mkTestDir()
    writeFileSync(join(dir, "file.txt"), "hello world")

    const result = await verifyExpectations(makeContext({
      expect: {
        files: [{ path: "file.txt" }],
        markers: [{ path: "file.txt", contains: "hello" }],
      },
    }, dir))

    expect(result.satisfied).toBe(true)
    expect(result.missingFiles).toHaveLength(0)
    expect(result.missingArtifactMarkers).toHaveLength(0)
    expect(result.message).toContain("satisfied")
  })

  it("MissingFile_ReturnsArtifactFileDiagnostic", async () => {
    const dir = mkTestDir()

    const result = await verifyExpectations(makeContext({
      expect: {
        files: [{ path: "missing.txt" }],
      },
    }, dir))

    expect(result.satisfied).toBe(false)
    expect(result.missingFiles).toHaveLength(1)
    expect(result.missingFiles[0].path).toContain("missing.txt")
    expect(result.message).toContain("missing artifact file")
  })

  it("MissingArtifactMarker_ReturnsArtifactMarkerDiagnostic", async () => {
    const dir = mkTestDir()
    writeFileSync(join(dir, "file.txt"), "hello world")

    const result = await verifyExpectations(makeContext({
      expect: {
        markers: [{ path: "file.txt", contains: "## Section" }],
      },
    }, dir))

    expect(result.satisfied).toBe(false)
    expect(result.missingArtifactMarkers).toHaveLength(1)
    expect(result.missingArtifactMarkers[0].path).toContain("file.txt")
    expect(result.missingArtifactMarkers[0].contains).toBe("## Section")
    expect(result.message).toContain("missing artifact marker")
    expect(result.message).not.toContain("verdict")
    expect(result.message).not.toContain("PASS")
    expect(result.message).not.toContain("FAIL")
  })

  it("VerdictMarkerNotInTaskExpectation_DoesNotFailHere", async () => {
    const dir = mkTestDir()
    writeFileSync(join(dir, "review.md"), "<promise>FAIL</promise>")

    const result = await verifyExpectations(makeContext({
      expect: {
        files: [{ path: "review.md" }],
      },
    }, dir))

    expect(result.satisfied).toBe(true)
  })

  it("OneOfMarkers_PASSValue_SatisfiesExpectation", async () => {
    const dir = mkTestDir()
    writeFileSync(join(dir, "review.md"), "Looks good.\n<promise>PASS</promise>\n")

    const result = await verifyExpectations(makeContext({
      expect: {
        markers: [{
          path: "review.md",
          oneOf: ["<promise>PASS</promise>", "<promise>FAIL</promise>"],
        }],
      },
    }, dir))

    expect(result.satisfied).toBe(true)
    expect(result.missingArtifactMarkers).toHaveLength(0)
    expect(result.message).toContain("satisfied")
  })

  it("OneOfMarkers_FAILValue_SatisfiesExpectation", async () => {
    const dir = mkTestDir()
    writeFileSync(join(dir, "review.md"), "Issues found.\n<promise>FAIL</promise>\n")

    const result = await verifyExpectations(makeContext({
      expect: {
        markers: [{
          path: "review.md",
          oneOf: ["<promise>PASS</promise>", "<promise>FAIL</promise>"],
        }],
      },
    }, dir))

    expect(result.satisfied).toBe(true)
    expect(result.missingArtifactMarkers).toHaveLength(0)
    expect(result.message).toContain("satisfied")
  })

  it("OneOfMarkers_NeitherValuePresent_KeepsAskingForRequiredFormat", async () => {
    const dir = mkTestDir()
    writeFileSync(join(dir, "review.md"), "Still drafting the review.")

    const result = await verifyExpectations(makeContext({
      expect: {
        markers: [{
          path: "review.md",
          oneOf: ["<promise>PASS</promise>", "<promise>FAIL</promise>"],
        }],
      },
    }, dir))

    expect(result.satisfied).toBe(false)
    expect(result.missingArtifactMarkers).toHaveLength(1)
    expect(result.missingArtifactMarkers[0].path).toContain("review.md")
    expect(result.missingArtifactMarkers[0].contains).toContain("oneOf")
    expect(result.missingArtifactMarkers[0].contains).toContain("<promise>PASS</promise>")
    expect(result.missingArtifactMarkers[0].contains).toContain("<promise>FAIL</promise>")
    expect(result.message).toContain("missing artifact marker")
    expect(result.message).not.toContain("verdict")
  })

  it("OneOfMarkers_TargetFileMissing_ReportsMissingFileMarker", async () => {
    const dir = mkTestDir()

    const result = await verifyExpectations(makeContext({
      expect: {
        markers: [{
          path: "review.md",
          oneOf: ["<promise>PASS</promise>", "<promise>FAIL</promise>"],
        }],
      },
    }, dir))

    expect(result.satisfied).toBe(false)
    expect(result.missingArtifactMarkers).toHaveLength(1)
    expect(result.missingArtifactMarkers[0].contains).toContain("oneOf")
  })

  it("OneOfMarkers_BeatsContainsFallback_AcceptsListedValue", async () => {
    const dir = mkTestDir()
    writeFileSync(join(dir, "review.md"), "<promise>FAIL</promise>")

    const result = await verifyExpectations(makeContext({
      expect: {
        markers: [{
          path: "review.md",
          contains: "<promise>PASS</promise>",
          oneOf: ["<promise>PASS</promise>", "<promise>FAIL</promise>"],
        }],
      },
    }, dir))

    expect(result.satisfied).toBe(true)
  })

  it("OutputMarkers_DoneMarker_SatisfiesAndReturnsMatched", async () => {
    const dir = mkTestDir()
    const agentText = "Fixed all test failures. All suites pass.\n\n<promise>done</promise>"

    const result = await verifyExpectations(makeContext({
      expect: {
        markers: [{
          path: "_output",
          oneOf: ["<promise>done</promise>", "<promise>unfinished</promise>"],
        }],
      },
    }, dir), agentText)

    expect(result.satisfied).toBe(true)
    expect(result.matched).toBe("<promise>done</promise>")
    expect(result.missingArtifactMarkers).toHaveLength(0)
  })

  it("OutputMarkers_UnfinishedMarker_SatisfiesAndReturnsMatched", async () => {
    const dir = mkTestDir()
    const agentText = "Could not finish fixing all tests.\n\n<promise>unfinished</promise>"

    const result = await verifyExpectations(makeContext({
      expect: {
        markers: [{
          path: "_output",
          oneOf: ["<promise>done</promise>", "<promise>unfinished</promise>"],
        }],
      },
    }, dir), agentText)

    expect(result.satisfied).toBe(true)
    expect(result.matched).toBe("<promise>unfinished</promise>")
    expect(result.missingArtifactMarkers).toHaveLength(0)
  })

  it("OutputMarkers_LastMatchWins_IgnoresEarlierReferences", async () => {
    const dir = mkTestDir()
    const agentText = "I haven't finished yet (this is <promise>unfinished</promise>).\n\nNow all work is done.\n\n<promise>done</promise>"

    const result = await verifyExpectations(makeContext({
      expect: {
        markers: [{
          path: "_output",
          oneOf: ["<promise>done</promise>", "<promise>unfinished</promise>"],
        }],
      },
    }, dir), agentText)

    expect(result.satisfied).toBe(true)
    expect(result.matched).toBe("<promise>done</promise>")
  })

  it("OutputMarkers_NoMarker_ReturnsUnsatisfied", async () => {
    const dir = mkTestDir()
    const agentText = "Fixed all test failures. All suites pass."

    const result = await verifyExpectations(makeContext({
      expect: {
        markers: [{
          path: "_output",
          oneOf: ["<promise>done</promise>", "<promise>unfinished</promise>"],
        }],
      },
    }, dir), agentText)

    expect(result.satisfied).toBe(false)
    expect(result.matched).toBeUndefined()
    expect(result.missingArtifactMarkers).toHaveLength(1)
    expect(result.missingArtifactMarkers[0].path).toBe("_output")
  })

  it("OutputMarkers_NoAgentText_ReturnsUnsatisfied", async () => {
    const dir = mkTestDir()

    const result = await verifyExpectations(makeContext({
      expect: {
        markers: [{
          path: "_output",
          oneOf: ["<promise>done</promise>", "<promise>unfinished</promise>"],
        }],
      },
    }, dir))

    expect(result.satisfied).toBe(false)
    expect(result.matched).toBeUndefined()
    expect(result.missingArtifactMarkers).toHaveLength(1)
    expect(result.missingArtifactMarkers[0].path).toBe("_output")
  })

  it("OutputMarkers_MixedWithFileMarkers_BothSatisfied", async () => {
    const dir = mkTestDir()
    writeFileSync(join(dir, "review.md"), "<promise>PASS</promise>")
    const agentText = "All work done.\n\n<promise>done</promise>"

    const result = await verifyExpectations(makeContext({
      expect: {
        markers: [
          { path: "review.md", oneOf: ["<promise>PASS</promise>", "<promise>FAIL</promise>"] },
          { path: "_output", oneOf: ["<promise>done</promise>", "<promise>unfinished</promise>"] },
        ],
      },
    }, dir), agentText)

    expect(result.satisfied).toBe(true)
    expect(result.matched).toBe("<promise>done</promise>")
    expect(result.missingArtifactMarkers).toHaveLength(0)
  })
})

function mkTestDir(): string {
  const dir = join(tmpdir(), `mohist-test-${Date.now()}-${Math.random().toString(36).slice(2)}`)
  mkdirSync(dir, { recursive: true })
  return dir
}
