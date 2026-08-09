import { existsSync, readFileSync } from "node:fs"
import { fileURLToPath } from "node:url"
import { dirname, resolve } from "node:path"

export interface BuildInfo {
  gitHash: string | null
  artifactDigest: string | null
  builtAt: number | null
}

const EMPTY_BUILD_INFO: BuildInfo = { gitHash: null, artifactDigest: null, builtAt: null }

function candidatesForManifest() {
  const here = dirname(fileURLToPath(import.meta.url))
  return [
    resolve(here, "build-info.json"),
    resolve(here, "..", "build-info.json"),
  ]
}

export function loadBuildInfo(): BuildInfo {
  for (const path of candidatesForManifest()) {
    if (!existsSync(path)) continue
    try {
      const raw = readFileSync(path, "utf8")
      const parsed = JSON.parse(raw) as { gitHash?: unknown; artifactDigest?: unknown; builtAt?: unknown }
      const gitHash = typeof parsed.gitHash === "string" && parsed.gitHash.length > 0 ? parsed.gitHash : null
      const artifactDigest = typeof parsed.artifactDigest === "string" && /^[a-f0-9]{64}$/.test(parsed.artifactDigest)
        ? parsed.artifactDigest
        : null
      const builtAt = typeof parsed.builtAt === "number" && Number.isFinite(parsed.builtAt) ? parsed.builtAt : null
      return { gitHash, artifactDigest, builtAt }
    } catch {
      return { ...EMPTY_BUILD_INFO }
    }
  }
  return { ...EMPTY_BUILD_INFO }
}

export function manifestCandidatesForTesting() {
  return candidatesForManifest()
}
