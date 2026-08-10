import { existsSync, readFileSync } from "node:fs"
import { fileURLToPath } from "node:url"
import { dirname, resolve } from "node:path"

export interface BuildInfo {
  gitHash: string | null
  builtAt: number | null
  component: string | null
  version: string | null
  sourceRevision: string | null
  treeHash: string | null
  artifactDigest: string | null
  releaseId: string | null
  generation: number | null
  runnerId: string | null
}

const EMPTY_BUILD_INFO: BuildInfo = {
  gitHash: null,
  builtAt: null,
  component: null,
  version: null,
  sourceRevision: null,
  treeHash: null,
  artifactDigest: null,
  releaseId: null,
  generation: null,
  runnerId: null,
}

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
      const parsed = JSON.parse(raw) as {
        gitHash?: unknown; builtAt?: unknown; component?: unknown; version?: unknown
        sourceRevision?: unknown; treeHash?: unknown; artifactDigest?: unknown
        releaseId?: unknown; generation?: unknown; runnerId?: unknown
      }
      const gitHash = typeof parsed.gitHash === "string" && parsed.gitHash.length > 0 ? parsed.gitHash : null
      const builtAt = typeof parsed.builtAt === "number" && Number.isFinite(parsed.builtAt) ? parsed.builtAt : null
      const text = (value: unknown) => typeof value === "string" && value.length > 0 ? value : null
      const generation = typeof parsed.generation === "number" && Number.isInteger(parsed.generation) && parsed.generation > 0
        ? parsed.generation
        : null
      return {
        gitHash,
        builtAt,
        component: text(parsed.component),
        version: text(parsed.version),
        sourceRevision: text(parsed.sourceRevision),
        treeHash: text(parsed.treeHash),
        artifactDigest: text(parsed.artifactDigest),
        releaseId: text(parsed.releaseId),
        generation,
        runnerId: text(parsed.runnerId),
      }
    } catch {
      return { ...EMPTY_BUILD_INFO }
    }
  }
  return { ...EMPTY_BUILD_INFO }
}

export function manifestCandidatesForTesting() {
  return candidatesForManifest()
}
