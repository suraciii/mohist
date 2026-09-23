import { existsSync, readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { dirname, resolve } from 'node:path'
import { currentRunnerResources } from '../system/filesystem.js'

/**
 * Canonical RuntimeIdentity v1 carried by every managed release manifest.
 *
 * The nine identity fields are always present as keys. Readers treat a
 * managed payload whose `schemaVersion` is present and not exactly `1`, or
 * that carries a wrong field type, as malformed and reject it rather than
 * falling back to any legacy alias. `gitHash` is never part of this type; it
 * is accepted only as a read input for legacy v0 payloads.
 */
export interface RuntimeIdentity {
  schemaVersion: number | null
  component: string | null
  sourceRevision: string | null
  buildGitHash: string | null
  treeHash: string | null
  artifactDigest: string | null
  releaseId: string | null
  generation: number | null
  runnerId: string | null
}

/**
 * Loaded build metadata: the canonical identity plus documented display
 * metadata (`version`, `builtAt`) that never participates in identity
 * comparison.
 */
export interface BuildInfo extends RuntimeIdentity {
  version: string | null
  builtAt: number | null
}

const EMPTY_IDENTITY: RuntimeIdentity = {
  schemaVersion: null,
  component: null,
  sourceRevision: null,
  buildGitHash: null,
  treeHash: null,
  artifactDigest: null,
  releaseId: null,
  generation: null,
  runnerId: null,
}

const EMPTY_BUILD_INFO: BuildInfo = {
  ...EMPTY_IDENTITY,
  version: null,
  builtAt: null,
}

export interface BuildInfoFileSystem {
  exists(path: string): boolean
  readText(path: string): string
}

const nodeBuildInfoFileSystem: BuildInfoFileSystem = {
  exists: existsSync,
  readText: (path) => readFileSync(path, 'utf8'),
}

function candidatesForManifest() {
  const here = dirname(fileURLToPath(import.meta.url))
  const managed = process.env.MOHIST_RUNTIME_IDENTITY_PATH
  return [
    ...(managed ? [resolve(managed)] : []),
    resolve(here, 'build-info.json'),
    resolve(here, '..', 'build-info.json'),
  ]
}

function readText(value: unknown): string | null {
  return typeof value === 'string' && value.length > 0 ? value : null
}

function readGeneration(value: unknown): number | null {
  return typeof value === 'number' && Number.isInteger(value) && value > 0 ? value : null
}

function readBuiltAt(value: unknown): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? value : null
}

function isObjectRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

/**
 * Parse a managed (canonical) payload. Returns null when any required field
 * is missing, has the wrong JSON type, or when `schemaVersion` is not exactly
 * `1`. A malformed managed payload must never fall back to a legacy alias.
 */
function parseManagedIdentity(parsed: Record<string, unknown>): RuntimeIdentity | null {
  if (parsed.schemaVersion !== 1) return null
  const component = readText(parsed.component)
  const sourceRevision = readText(parsed.sourceRevision)
  const buildGitHash = readText(parsed.buildGitHash)
  const treeHash = readText(parsed.treeHash)
  const artifactDigest = readText(parsed.artifactDigest)
  const releaseId = readText(parsed.releaseId)
  const generation = readGeneration(parsed.generation)
  if (
    (component !== 'server' && component !== 'runner') ||
    sourceRevision === null ||
    buildGitHash === null ||
    treeHash === null ||
    artifactDigest === null ||
    releaseId === null ||
    generation === null ||
    typeof parsed.runnerId !== 'string'
  ) {
    return null
  }
  if (component === 'runner' && parsed.runnerId.length === 0) return null
  if (component === 'server' && parsed.runnerId.length !== 0) return null
  return {
    schemaVersion: 1,
    component,
    sourceRevision,
    buildGitHash,
    treeHash,
    artifactDigest,
    releaseId,
    generation,
    runnerId: parsed.runnerId,
  }
}

/**
 * Legacy v0 read path: a payload without `schemaVersion` maps
 * `buildGitHash = buildGitHash ?? gitHash` and
 * `sourceRevision = sourceRevision ?? buildGitHash ?? gitHash`.
 */
function parseLegacyIdentity(parsed: Record<string, unknown>): RuntimeIdentity {
  const gitHash = readText(parsed.gitHash)
  const buildGitHash = readText(parsed.buildGitHash) ?? gitHash
  return {
    schemaVersion: null,
    component: readText(parsed.component),
    sourceRevision: readText(parsed.sourceRevision) ?? buildGitHash ?? gitHash,
    buildGitHash,
    treeHash: readText(parsed.treeHash),
    artifactDigest: readText(parsed.artifactDigest),
    releaseId: readText(parsed.releaseId),
    generation: readGeneration(parsed.generation),
    runnerId: readText(parsed.runnerId),
  }
}

function parseBuildInfo(raw: string): BuildInfo {
  let parsed: unknown
  try {
    parsed = JSON.parse(raw)
  } catch {
    return { ...EMPTY_BUILD_INFO }
  }
  if (!isObjectRecord(parsed)) return { ...EMPTY_BUILD_INFO }

  const version = readText(parsed.version)
  const builtAt = readBuiltAt(parsed.builtAt)
  const managed = parsed.schemaVersion !== undefined && parsed.schemaVersion !== null
  const identity = managed ? parseManagedIdentity(parsed) : parseLegacyIdentity(parsed)
  if (identity === null) return { ...EMPTY_BUILD_INFO }
  return { ...identity, version, builtAt }
}

export function loadBuildInfo(
  fileSystem: BuildInfoFileSystem = currentRunnerResources()?.buildInfoFileSystem ?? nodeBuildInfoFileSystem,
): BuildInfo {
  for (const path of candidatesForManifest()) {
    if (!fileSystem.exists(path)) continue
    try {
      return parseBuildInfo(fileSystem.readText(path))
    } catch {
      return { ...EMPTY_BUILD_INFO }
    }
  }
  return { ...EMPTY_BUILD_INFO }
}

export function getRunnerBuildGitHash(): string | null {
  return loadBuildInfo().buildGitHash
}

export function manifestCandidatesForTesting() {
  return candidatesForManifest()
}
