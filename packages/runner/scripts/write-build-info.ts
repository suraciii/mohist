#!/usr/bin/env node
import { spawnSync } from "node:child_process"
import { writeFileSync, mkdirSync, existsSync } from "node:fs"
import { fileURLToPath } from "node:url"
import { dirname, join, resolve } from "node:path"
import { buildManifest } from "../src/runtime/build-manifest.js"

const here = dirname(fileURLToPath(import.meta.url))
const distDir = resolve(here, "..", "dist")
const manifestPath = join(distDir, "build-info.json")

function readGitHeadForRepo(repoRoot) {
  const result = spawnSync("git", ["rev-parse", "HEAD"], { cwd: repoRoot, encoding: "utf8" })
  if (result.status !== 0) return null
  const value = result.stdout.trim()
  return value.length > 0 ? value : null
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
