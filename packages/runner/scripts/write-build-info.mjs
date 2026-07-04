#!/usr/bin/env node
import { spawnSync } from "node:child_process"
import { writeFileSync, mkdirSync, existsSync } from "node:fs"
import { fileURLToPath } from "node:url"
import { dirname, join, resolve } from "node:path"

const here = dirname(fileURLToPath(import.meta.url))
const distDir = resolve(here, "..", "dist")
const manifestPath = join(distDir, "build-info.json")

/**
 * Reads `git rev-parse HEAD` for the given repo root, returning null on any
 * failure (non-git directory, git missing). Exposed so tests can inject a fake.
 */
export function readGitHeadForRepo(repoRoot) {
  const result = spawnSync("git", ["rev-parse", "HEAD"], { cwd: repoRoot, encoding: "utf8" })
  if (result.status !== 0) return null
  const value = result.stdout.trim()
  return value.length > 0 ? value : null
}

/**
 * Pure builder for the build manifest. Exported (alongside the git reader) so
 * tests can verify the manifest shape without spawning a real git process or
 * writing to the real dist directory.
 *
 * @param {() => (string | null)} readGitHead  returns the current HEAD hash or null
 * @param {() => number} now                    returns the build timestamp (epoch ms)
 */
export function buildManifest(readGitHead, now) {
  return {
    gitHash: readGitHead(),
    builtAt: now(),
  }
}

function resolveRepoRoot() {
  const cwd = process.env.MOHIST_REPO_ROOT
  if (typeof cwd === "string" && cwd.length > 0 && existsSync(cwd)) return cwd
  return process.cwd()
}

function main() {
  if (!existsSync(distDir)) {
    mkdirSync(distDir, { recursive: true })
  }
  const repoRoot = resolveRepoRoot()
  const manifest = buildManifest(() => readGitHeadForRepo(repoRoot), Date.now)
  writeFileSync(manifestPath, `${JSON.stringify(manifest, null, 2)}\n`, "utf8")
  process.stdout.write(`wrote ${manifestPath} (gitHash=${manifest.gitHash ?? "null"})\n`)
}

main()
