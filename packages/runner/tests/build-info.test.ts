import { describe, expect, it } from "vitest"
import { loadBuildInfo, manifestCandidatesForTesting } from "../src/runtime/build-info.js"
// Import the pure builder + git reader from the postbuild script. Importing a
// .mjs via a dynamic import keeps vitest's transform happy while letting the
// test exercise the real manifest-construction logic with injected fakes.
const { buildManifest } = await import("../scripts/write-build-info.mjs")

describe("runner build manifest builder", () => {
  it("writesManifestMatchingInjectedGitHead", () => {
    const readGitHead = () => "deadbeefcafebabe0000000000000000deadbeef"
    const fixedNow = 1_700_000_000_000
    const manifest = buildManifest(readGitHead, () => fixedNow)

    expect(manifest.gitHash).toBe("deadbeefcafebabe0000000000000000deadbeef")
    expect(manifest.builtAt).toBe(fixedNow)
  })

  it("writesNullGitHashWhenGitRevParseFails", () => {
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

  it("returnsWellShapedResultWhenManifestPresent", () => {
    // After `npm run build`, dist/build-info.json should exist and the
    // loader should expose either the real git hash (if available) or
    // null (treated as unknown-identity, non-fatal).
    const result = loadBuildInfo()
    expect(result).toHaveProperty("gitHash")
    expect(result).toHaveProperty("builtAt")
    if (result.gitHash !== null) {
      expect(result.gitHash.length).toBeGreaterThan(0)
    }
    if (result.builtAt !== null) {
      expect(typeof result.builtAt).toBe("number")
    }
  })
})
