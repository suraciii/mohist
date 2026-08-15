import { mkdtempSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { isAbsolute, join, relative, resolve, sep } from 'node:path'

export interface ArtifactDirectoryOps {
  readonly tempDirectory: () => string
  readonly makeDirectory: (prefix: string) => string
}

const nativeArtifactDirectoryOps: ArtifactDirectoryOps = {
  tempDirectory: tmpdir,
  makeDirectory: mkdtempSync,
}

export interface CanonicalRunMetadata {
  readonly runId: string
  readonly startedAt: number
  readonly suiteDeadlineMs: number
  readonly sourceRevision?: string
}

export interface BuildStamp {
  readonly runId: string
  readonly builtAt: number
  readonly sourceRevision?: string
}

export function isInsideDirectory(candidate: string, directory: string): boolean {
  const resolvedCandidate = resolve(candidate)
  const resolvedDirectory = resolve(directory)
  const pathFromDirectory = relative(resolvedDirectory, resolvedCandidate)
  return (
    pathFromDirectory === '' ||
    (pathFromDirectory !== '..' && !pathFromDirectory.startsWith(`..${sep}`) && !isAbsolute(pathFromDirectory))
  )
}

export function createArtifactRoot(
  runId: string,
  repositoryRoot: string,
  artifactParent: string | undefined,
  ops: ArtifactDirectoryOps = nativeArtifactDirectoryOps,
): string {
  const parent = resolve(artifactParent ?? ops.tempDirectory())
  if (isInsideDirectory(parent, repositoryRoot)) {
    throw new Error(`artifact parent must be outside the repository: ${parent}`)
  }
  return ops.makeDirectory(join(parent, `mohist-canonical-gate-${runId}-`))
}

function parseObject(content: string): Record<string, unknown> | undefined {
  try {
    const parsed = JSON.parse(content)
    return parsed !== null && typeof parsed === 'object' && !Array.isArray(parsed)
      ? (parsed as Record<string, unknown>)
      : undefined
  } catch {
    return undefined
  }
}

export function parseCanonicalRunMetadata(content: string): CanonicalRunMetadata | undefined {
  const parsed = parseObject(content)
  if (
    !parsed ||
    typeof parsed.runId !== 'string' ||
    !parsed.runId ||
    typeof parsed.startedAt !== 'number' ||
    !Number.isFinite(parsed.startedAt) ||
    typeof parsed.suiteDeadlineMs !== 'number' ||
    !Number.isFinite(parsed.suiteDeadlineMs) ||
    (parsed.sourceRevision !== undefined && typeof parsed.sourceRevision !== 'string')
  )
    return undefined
  const sourceRevision =
    typeof parsed.sourceRevision === 'string' && parsed.sourceRevision ? parsed.sourceRevision : undefined
  return {
    runId: parsed.runId,
    startedAt: parsed.startedAt,
    suiteDeadlineMs: parsed.suiteDeadlineMs,
    ...(sourceRevision ? { sourceRevision } : {}),
  }
}

export function parseBuildStamp(content: string): BuildStamp | undefined {
  const parsed = parseObject(content)
  if (
    !parsed ||
    typeof parsed.runId !== 'string' ||
    !parsed.runId ||
    typeof parsed.builtAt !== 'number' ||
    !Number.isFinite(parsed.builtAt) ||
    (parsed.sourceRevision !== undefined && typeof parsed.sourceRevision !== 'string')
  )
    return undefined
  const sourceRevision =
    typeof parsed.sourceRevision === 'string' && parsed.sourceRevision ? parsed.sourceRevision : undefined
  return {
    runId: parsed.runId,
    builtAt: parsed.builtAt,
    ...(sourceRevision ? { sourceRevision } : {}),
  }
}

export function buildStampMatchesRun(runJson: string, stampJson: string): boolean {
  const run = parseCanonicalRunMetadata(runJson)
  const stamp = parseBuildStamp(stampJson)
  return (
    run !== undefined &&
    stamp !== undefined &&
    run.runId === stamp.runId &&
    (run.sourceRevision === undefined || run.sourceRevision === stamp.sourceRevision)
  )
}
