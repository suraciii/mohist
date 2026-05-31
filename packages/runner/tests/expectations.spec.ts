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
})

function mkTestDir(): string {
  const dir = join(tmpdir(), `mohist-test-${Date.now()}-${Math.random().toString(36).slice(2)}`)
  mkdirSync(dir, { recursive: true })
  return dir
}
