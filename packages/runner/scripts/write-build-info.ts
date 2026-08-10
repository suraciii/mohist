#!/usr/bin/env node
import { spawnSync } from "node:child_process"
import { writeFileSync, mkdirSync, existsSync, readFileSync } from "node:fs"
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

function readManagedSourceIdentity(repoRoot) {
  const markerPath = join(repoRoot, ".mohist-source-marker.json")
  if (!existsSync(markerPath)) return null
  try {
    const marker = JSON.parse(readFileSync(markerPath, "utf8"))
    if (typeof marker.GitCommit !== "string" && typeof marker.gitCommit !== "string") return null
    return {
      component: "runner",
      version: `0.0.0+${marker.GitCommit ?? marker.gitCommit}`,
      sourceRevision: marker.GitCommit ?? marker.gitCommit,
      treeHash: marker.TreeHash ?? marker.treeHash ?? null,
    }
  } catch {
    return null
  }
}

function main() {
  if (!existsSync(distDir)) {
    mkdirSync(distDir, { recursive: true })
  }
  const repoRoot = resolveRepoRoot()
  const managed = process.env.MOHIST_RUNTIME_IDENTITY_FILE
  let identity = null
  if (typeof managed === "string" && managed.length > 0 && existsSync(managed)) {
    try {
      identity = JSON.parse(readFileSync(managed, "utf8"))
    } catch {
      identity = null
    }
  }
  identity ??= readManagedSourceIdentity(repoRoot)
  const manifest = identity
    ? buildManifest(() => identity.sourceRevision ?? identity.gitHash ?? null, Date.now, {
        component: identity.component ?? "runner",
        version: identity.version ?? undefined,
        sourceRevision: identity.sourceRevision ?? identity.gitHash ?? undefined,
        treeHash: identity.treeHash ?? undefined,
        artifactDigest: identity.artifactDigest ?? undefined,
        releaseId: identity.releaseId ?? undefined,
        generation: identity.generation ?? undefined,
        runnerId: identity.runnerId ?? undefined,
      })
    : buildManifest(() => readGitHeadForRepo(repoRoot), Date.now)
  writeFileSync(manifestPath, `${JSON.stringify(manifest, null, 2)}\n`, "utf8")
  process.stdout.write(`wrote ${manifestPath} (gitHash=${manifest.gitHash ?? "null"})\n`)
}

main()
