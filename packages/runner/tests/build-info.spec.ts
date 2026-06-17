import { mkdtempSync, existsSync, readFileSync, rmSync } from "node:fs"
import { tmpdir } from "node:os"
import { dirname, join, resolve } from "node:path"
import { fileURLToPath } from "node:url"
import { spawnSync } from "node:child_process"
import { describe, expect, it } from "vitest"
import { loadBuildInfo, manifestCandidatesForTesting } from "../src/runtime/build-info.js"

const here = dirname(fileURLToPath(import.meta.url))
const repoRoot = resolve(here, "..", "..", "..")
const scriptPath = join(repoRoot, "packages/runner/scripts/write-build-info.mjs")

function runPostbuild(env: Record<string, string> = {}) {
  return spawnSync("node", [scriptPath], {
    cwd: repoRoot,
    env: { ...process.env, ...env },
    encoding: "utf8",
  })
}

function gitHead() {
  const result = spawnSync("git", ["rev-parse", "HEAD"], { cwd: repoRoot, encoding: "utf8" })
  if (result.status !== 0) return null
  const value = result.stdout.trim()
  return value.length > 0 ? value : null
}

describe("runner build manifest script", () => {
  it("writesManifestMatchingGitRevParseHead", () => {
    const result = runPostbuild({ MOHIST_REPO_ROOT: repoRoot })
    expect(result.status).toBe(0)
    const distDir = join(repoRoot, "packages/runner/dist")
    const manifestPath = join(distDir, "build-info.json")
    expect(existsSync(manifestPath)).toBe(true)
    const parsed = JSON.parse(readFileSync(manifestPath, "utf8")) as { gitHash: string | null; builtAt: number }
    const expected = gitHead()
    expect(parsed.gitHash).toBe(expected)
    expect(typeof parsed.builtAt).toBe("number")
    expect(parsed.builtAt).toBeGreaterThan(0)
  })

  it("writesNullGitHashWhenGitRevParseFails", () => {
    const isolated = mkdtempSync(join(tmpdir(), "mohist-no-git-"))
    try {
      const result = runPostbuild({ MOHIST_REPO_ROOT: isolated })
      expect(result.status).toBe(0)
      const distDir = join(repoRoot, "packages/runner/dist")
      const manifestPath = join(distDir, "build-info.json")
      expect(existsSync(manifestPath)).toBe(true)
      const parsed = JSON.parse(readFileSync(manifestPath, "utf8")) as { gitHash: string | null; builtAt: number }
      expect(parsed.gitHash).toBeNull()
      expect(typeof parsed.builtAt).toBe("number")
    } finally {
      rmSync(isolated, { recursive: true, force: true })
    }
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
