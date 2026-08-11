import { describe, expect, it } from "vitest"
import { loadBuildInfo, manifestCandidatesForTesting } from "../src/runtime/build-info.js"
import { buildManifest } from "../src/runtime/build-manifest.js"

describe("runner build manifest builder", () => {
  it("buildsManifestMatchingInjectedGitHead", () => {
    const readGitHead = () => "deadbeefcafebabe0000000000000000deadbeef"
    const fixedNow = 1_700_000_000_000
    const manifest = buildManifest(readGitHead, () => fixedNow)

    expect(manifest.gitHash).toBe("deadbeefcafebabe0000000000000000deadbeef")
    expect(manifest.builtAt).toBe(fixedNow)
  })

  it("buildsNullGitHashWhenGitRevParseFails", () => {
    // Mirrors the production path: readGitHeadForRepo returns null when git is
    // absent or the directory is not a repo. The builder must propagate null
    // rather than throw, so the postbuild step stays non-fatal.
    const readGitHead = () => null
    const manifest = buildManifest(readGitHead, () => 1_700_000_000_000)

    expect(manifest.gitHash).toBeNull()
    expect(typeof manifest.builtAt).toBe("number")
  })
})

describe("runner build-info loader", () => {
  it("exposesCandidatePathsRelativeToModule", () => {
    const candidates = manifestCandidatesForTesting()
    expect(candidates.length).toBeGreaterThanOrEqual(1)
    for (const path of candidates) {
      expect(path.endsWith("build-info.json")).toBe(true)
    }
  })

  it("returnsManifestFromInjectedFileSystem", () => {
    const result = loadBuildInfo({
      exists: () => true,
      readText: () => JSON.stringify({ gitHash: "deadbeef", builtAt: 1_700_000_000_000 }),
    })

    expect(result.gitHash).toBe("deadbeef")
    expect(result.builtAt).toBe(1_700_000_000_000)
  })
})
