import { AsyncLocalStorage } from "node:async_hooks"
import { join } from "node:path"
import { describe, expect, it as vitestIt } from "vitest"
import {
  actionProducedArtifacts,
  captureArtifacts,
  captureOne,
  declaredArtifactPaths,
  DEFAULT_ARTIFACT_CAPTURE_LIMITS,
  type ArtifactCaptureLimits,
  type CapturedArtifact,
  uploadCapturedArtifacts,
} from "../src/runtime/artifact-capture.js"
import type { ServerConnection, ArtifactUploadResponse } from "../src/server/connection.js"
import type { JsonObject, DispatchWorkItem } from "../src/core/types.js"
import { createTestTempDir } from "./support/temp-dir.js"
import { withTestRunnerResources } from "./support/test-resources.js"
import { currentRunnerFileSystem } from "../src/system/filesystem.js"

class FakeServerConnection implements Pick<ServerConnection, "uploadArtifact"> {
  public uploads: CapturedArtifact[] = []
  public responses: ArtifactUploadResponse[] = []
  public failure: Error | null = null
  public calls: number = 0

  async uploadArtifact(
    workflowRunId: string,
    workId: string,
    upload: { path: string; contentType?: string | null; contentHash?: string | null; size: number; content: Uint8Array; filename?: string },
  ): Promise<ArtifactUploadResponse> {
    this.calls += 1
    this.uploads.push({
      path: upload.path,
      kind: upload.contentType === "application/x-mohist-artifact-directory" ? "directory" : "file",
      content: upload.content,
      contentType: upload.contentType ?? "application/octet-stream",
      contentHash: upload.contentHash ?? "",
      size: upload.size,
      source: "declared",
    })
    if (this.failure) throw this.failure
    const response: ArtifactUploadResponse = {
      uploadId: `artup_${this.calls}`,
      workflowRunId,
      workId,
      taskRunId: "task-run-1",
      path: upload.path,
      contentType: upload.contentType ?? null,
      contentHash: upload.contentHash ?? null,
      size: upload.size,
      createdAt: new Date().toISOString(),
      expiresAt: new Date(Date.now() + 60_000).toISOString(),
      idempotent: false,
    }
    this.responses.push(response)
    return response
  }
}

interface ArtifactTestPaths {
  workDir: string
  outsideDir: string
}

const testPathStorage = new AsyncLocalStorage<ArtifactTestPaths>()

function paths(): ArtifactTestPaths {
  const value = testPathStorage.getStore()
  if (!value) throw new Error("artifact test resource context is not active")
  return value
}

const it = Object.assign(
  (name: string, body: () => unknown) => vitestIt(name, () => withTestRunnerResources(async () => {
    const testPaths: ArtifactTestPaths = {
      workDir: await createTestTempDir("mohist-artifact-capture-"),
      outsideDir: await createTestTempDir("mohist-artifact-capture-outside-"),
    }
    await testPathStorage.run(testPaths, async () => await body())
  })),
  { each: vitestIt.each.bind(vitestIt) },
) as typeof vitestIt

function workItem(artifacts: JsonObject | null): DispatchWorkItem {
  return {
    workflowRunId: "wf-1",
    workId: "work-1",
    workType: "task",
    title: "Test task",
    uses: "core/process",
    with: null,
    variables: {},
    artifacts,
  }
}

describe("declaredArtifactPaths", () => {
  it("declaredFiles_AreReturnedWithDeclaredSource", () => {
    const work = workItem({ files: [{ path: "review.md" }, { path: "design.md" }] })
    const paths = declaredArtifactPaths(work)
    expect(paths).toEqual([
      { path: "review.md", source: "declared" },
      { path: "design.md", source: "declared" },
    ])
  })

  it("noArtifacts_EmptyResult", () => {
    expect(declaredArtifactPaths(workItem(null))).toEqual([])
    expect(declaredArtifactPaths(workItem({}))).toEqual([])
  })

  it("nonStringPath_IsIgnored", () => {
    const work = workItem({ files: [{ path: 42 }, { path: "review.md" }] })
    expect(declaredArtifactPaths(work)).toEqual([{ path: "review.md", source: "declared" }])
  })
})

describe("actionProducedArtifacts", () => {
  it("readsProducedArtifactsFromActionOutputObject", () => {
    const result = { output: { producedArtifacts: [{ path: "logs/run.log" }, { path: "data.json" }] } }
    expect(actionProducedArtifacts(result)).toEqual([
      { path: "logs/run.log", source: "dynamic" },
      { path: "data.json", source: "dynamic" },
    ])
  })

  it("missingOrMalformedOutput_YieldsEmptyList", () => {
    expect(actionProducedArtifacts(undefined)).toEqual([])
    expect(actionProducedArtifacts({ output: null })).toEqual([])
    expect(actionProducedArtifacts({ output: {} })).toEqual([])
    expect(actionProducedArtifacts({ output: { producedArtifacts: "nope" } })).toEqual([])
  })
})

describe("captureOne", () => {
  it("capturesFileWithSha256HashAndContentType", async () => {
    const path = join(paths().workDir, "review.md")
    await currentRunnerFileSystem().writeText(path, "this is the review\n")
    const capture = await captureOne(paths().workDir, { path: "review.md", source: "declared" })
    expect(capture.kind).toBe("file")
    expect(capture.source).toBe("declared")
    expect(capture.path).toBe("review.md")
    expect(capture.size).toBe("this is the review\n".length)
    expect(capture.contentType).toBe("text/markdown")
    expect(capture.contentHash).toMatch(/^sha256:[a-f0-9]{64}$/)
    expect(new TextDecoder().decode(capture.content)).toBe("this is the review\n")
  })

  it("capturesDirectoryWithFileManifestAndLimits", async () => {
    const specsDir = join(paths().workDir, "specs")
    await currentRunnerFileSystem().ensureDir(join(specsDir, "sub"))
    await currentRunnerFileSystem().writeText(join(specsDir, "a.md"), "alpha")
    await currentRunnerFileSystem().writeText(join(specsDir, "sub", "b.md"), "beta")
    const capture = await captureOne(paths().workDir, { path: "specs", source: "declared" })
    expect(capture.kind).toBe("directory")
    expect(capture.fileCount).toBe(2)
    const manifest = JSON.parse(new TextDecoder().decode(capture.content))
    expect(manifest.kind).toBe("directory")
    const manifestPaths = manifest.files.map((f: { path: string }) => f.path).sort()
    expect(manifestPaths).toEqual(["a.md", "sub/b.md"])
  })

  it("refusesPathsEscapingWorkspace", async () => {
    await expect(captureOne(paths().workDir, { path: "../escape.md", source: "declared" })).rejects.toThrow(/escapes the workspace/)
    await expect(captureOne(paths().workDir, { path: "/etc/passwd", source: "declared" })).rejects.toThrow(/escapes the workspace/)
  })

  it("refusesSymlinkedTargetAtTopLevel", async () => {
    const target = join(paths().workDir, "real.md")
    await currentRunnerFileSystem().writeText(target, "real content")
    const linkPath = join(paths().workDir, "link.md")
    await currentRunnerFileSystem().symlink(target, linkPath)
    await expect(captureOne(paths().workDir, { path: "link.md", source: "declared" })).rejects.toThrow(/symlink/)
  })

  it("refusesSymlinkedFileInsideDirectoryArtifact", async () => {
    const specsDir = join(paths().workDir, "specs")
    await currentRunnerFileSystem().ensureDir(specsDir)
    const target = join(paths().outsideDir, "outside.md")
    await currentRunnerFileSystem().writeText(target, "outside")
    await currentRunnerFileSystem().symlink(target, join(specsDir, "link.md"))
    await expect(captureOne(paths().workDir, { path: "specs", source: "declared" })).rejects.toThrow(/symlink/)
  })

  it("enforcesFileCountLimitForDirectory", async () => {
    const specsDir = join(paths().workDir, "specs")
    await currentRunnerFileSystem().ensureDir(specsDir)
    for (let i = 0; i < 3; i += 1) {
      await currentRunnerFileSystem().writeText(join(specsDir, `f${i}.md`), `file ${i}`)
    }
    const limits: ArtifactCaptureLimits = { ...DEFAULT_ARTIFACT_CAPTURE_LIMITS, maxDirectoryFileCount: 2 }
    await expect(captureOne(paths().workDir, { path: "specs", source: "declared" }, limits)).rejects.toThrow(/file limit/)
  })

  it("enforcesTotalSizeLimitForDirectory", async () => {
    const specsDir = join(paths().workDir, "specs")
    await currentRunnerFileSystem().ensureDir(specsDir)
    await currentRunnerFileSystem().writeText(join(specsDir, "big.md"), "x".repeat(200))
    const limits: ArtifactCaptureLimits = { ...DEFAULT_ARTIFACT_CAPTURE_LIMITS, maxDirectoryTotalSize: 100 }
    await expect(captureOne(paths().workDir, { path: "specs", source: "declared" }, limits)).rejects.toThrow(/total size limit/)
  })

  it("enforcesSingleFileSizeLimit", async () => {
    const path = join(paths().workDir, "big.bin")
    await currentRunnerFileSystem().writeText(path, "y".repeat(2048))
    const limits: ArtifactCaptureLimits = { ...DEFAULT_ARTIFACT_CAPTURE_LIMITS, maxFileSize: 100 }
    await expect(captureOne(paths().workDir, { path: "big.bin", source: "declared" }, limits)).rejects.toThrow(/single-file limit/)
  })

  it("missingDeclaredFile_IsReportedAsFailure", async () => {
    await expect(captureOne(paths().workDir, { path: "missing.md", source: "declared" })).rejects.toThrow()
  })
})

describe("captureArtifacts", () => {
  it("collectsDeclaredAndDynamicArtifactsWithoutDuplicates", async () => {
    await currentRunnerFileSystem().writeText(join(paths().workDir, "review.md"), "first review")
    await currentRunnerFileSystem().writeText(join(paths().workDir, "diagnostic.log"), "diagnostic")
    const work = workItem({ files: [{ path: "review.md" }] })
    const outcome = await captureArtifacts({
      work,
      workDir: paths().workDir,
      dynamicArtifacts: [{ path: "review.md" }, { path: "diagnostic.log" }],
    })
    expect(outcome.failures).toEqual([])
    expect(outcome.captures.map((c) => c.path).sort()).toEqual(["diagnostic.log", "review.md"])
    expect(outcome.captures.find((c) => c.path === "review.md")?.source).toBe("declared")
    expect(outcome.captures.find((c) => c.path === "diagnostic.log")?.source).toBe("dynamic")
  })

  it("missingDeclaredArtifact_ProducesFailureWithDeclaredSource", async () => {
    const work = workItem({ files: [{ path: "review.md" }] })
    const outcome = await captureArtifacts({ work, workDir: paths().workDir, dynamicArtifacts: [] })
    expect(outcome.captures).toEqual([])
    expect(outcome.failures).toEqual([
      expect.objectContaining({ path: "review.md", source: "declared" }),
    ])
  })
})

describe("uploadCapturedArtifacts", () => {
  it("uploadsEachCaptureAndReturnsUploadIds", async () => {
    await currentRunnerFileSystem().writeText(join(paths().workDir, "a.md"), "alpha")
    await currentRunnerFileSystem().writeText(join(paths().workDir, "b.md"), "beta")
    const outcome = await captureArtifacts({
      work: workItem({ files: [{ path: "a.md" }, { path: "b.md" }] }),
      workDir: paths().workDir,
    })
    const connection = new FakeServerConnection()
    const result = await uploadCapturedArtifacts(connection, "wf-1", "work-1", outcome.captures, new AbortController().signal)
    expect(result.failures).toEqual([])
    expect(result.uploads).toHaveLength(2)
    expect(result.uploads.map((u) => u.uploadId)).toEqual(["artup_1", "artup_2"])
    expect(connection.uploads).toHaveLength(2)
  })

  it("uploadFailureForDeclaredArtifact_PropagatesAsFailure", async () => {
    await currentRunnerFileSystem().writeText(join(paths().workDir, "a.md"), "alpha")
    const outcome = await captureArtifacts({
      work: workItem({ files: [{ path: "a.md" }] }),
      workDir: paths().workDir,
    })
    const connection = new FakeServerConnection()
    connection.failure = new Error("network down")
    const result = await uploadCapturedArtifacts(connection, "wf-1", "work-1", outcome.captures, new AbortController().signal)
    expect(result.uploads).toEqual([])
    expect(result.failures).toEqual([
      expect.objectContaining({ path: "a.md", source: "declared", reason: "network down" }),
    ])
  })

  it("directoryArtifact_IsUploadedAsDirectoryContent", async () => {
    const specsDir = join(paths().workDir, "specs")
    await currentRunnerFileSystem().ensureDir(specsDir)
    await currentRunnerFileSystem().writeText(join(specsDir, "a.md"), "alpha")
    const outcome = await captureArtifacts({
      work: workItem({ files: [{ path: "specs" }] }),
      workDir: paths().workDir,
    })
    const connection = new FakeServerConnection()
    const result = await uploadCapturedArtifacts(connection, "wf-1", "work-1", outcome.captures, new AbortController().signal)
    expect(result.failures).toEqual([])
    expect(result.uploads).toHaveLength(1)
    expect(result.uploads[0].path).toBe("specs")
    expect(result.uploads[0].contentType).toBe("application/x-mohist-artifact-directory")
  })
})
