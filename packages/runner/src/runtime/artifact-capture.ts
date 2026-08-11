import { createHash } from "node:crypto"
import { isAbsolute, normalize, relative, resolve, sep } from "node:path"
import type { ArtifactUploadRequest, ArtifactUploadResponse } from "../server/connection.js"
import type { ActionResult, JsonObject, JsonValue, DispatchWorkItem } from "../core/types.js"
import { isObject } from "../core/json.js"
import { currentRunnerFileSystem } from "../system/filesystem.js"

export interface ArtifactUploader {
  uploadArtifact(
    ownerId: string,
    workId: string,
    upload: ArtifactUploadRequest,
    signal: AbortSignal,
    ownerKind?: string,
  ): Promise<ArtifactUploadResponse>
}

export interface ArtifactCaptureLimits {
  maxFileSize: number
  maxDirectoryFileCount: number
  maxDirectoryTotalSize: number
}

export const DEFAULT_ARTIFACT_CAPTURE_LIMITS: ArtifactCaptureLimits = {
  maxFileSize: 16 * 1024 * 1024,
  maxDirectoryFileCount: 200,
  maxDirectoryTotalSize: 64 * 1024 * 1024,
}

export type ArtifactKind = "file" | "directory"

export interface CapturedArtifact {
  path: string
  kind: ArtifactKind
  content: Uint8Array
  contentType: string
  contentHash: string
  size: number
  fileCount?: number
  totalSize?: number
  source: "declared" | "dynamic"
}

export interface ArtifactCaptureInput {
  work: DispatchWorkItem
  workDir: string
  dynamicArtifacts?: ReadonlyArray<{ path: string }>
  limits?: ArtifactCaptureLimits
  /**
   * Optional pre-rendered `artifacts` object. When provided, the capture
   * uses this in place of <see cref="DispatchWorkItem.artifacts"/> so callers
   * that have already substituted template variables on the workflow
   * definition can feed the resolved paths through. Falls back to
   * <c>work.artifacts</c> when not set.
   */
  renderedArtifacts?: JsonObject | null
}

export interface ArtifactCaptureOutcome {
  captures: CapturedArtifact[]
  failures: ArtifactCaptureFailure[]
}

export interface ArtifactCaptureFailure {
  path: string
  reason: string
  source: "declared" | "dynamic"
}

export class ArtifactCaptureError extends Error {
  readonly failures: ArtifactCaptureFailure[]
  constructor(message: string, failures: ArtifactCaptureFailure[]) {
    super(message)
    this.name = "ArtifactCaptureError"
    this.failures = failures
  }
}

interface DeclaredArtifactDeclaration {
  path: string
  source: "declared" | "dynamic"
}

export function declaredArtifactPaths(work: DispatchWorkItem): DeclaredArtifactDeclaration[] {
  return declaredPathsFromArtifacts(work.artifacts)
}

export function declaredPathsFromArtifacts(artifacts: JsonObject | null | undefined): DeclaredArtifactDeclaration[] {
  const files = artifacts && Array.isArray(artifacts.files) ? artifacts.files : []
  const out: DeclaredArtifactDeclaration[] = []
  for (const entry of files) {
    if (!entry || typeof entry !== "object") continue
    const path = (entry as { path?: unknown }).path
    if (typeof path === "string" && path.length > 0) {
      out.push({ path, source: "declared" })
    }
  }
  return out
}

export function actionProducedArtifacts(result: ActionResult | undefined): DeclaredArtifactDeclaration[] {
  if (!result) return []
  const output = result.output
  if (output === null || output === undefined) return []
  if (!isObject(output)) return []
  const produced = output["producedArtifacts"]
  if (!Array.isArray(produced)) return []
  const out: DeclaredArtifactDeclaration[] = []
  for (const entry of produced) {
    if (!entry || typeof entry !== "object") continue
    const path = (entry as { path?: unknown }).path
    if (typeof path === "string" && path.length > 0) {
      out.push({ path, source: "dynamic" })
    }
  }
  return out
}

export async function captureArtifacts(input: ArtifactCaptureInput): Promise<ArtifactCaptureOutcome> {
  const limits = input.limits ?? DEFAULT_ARTIFACT_CAPTURE_LIMITS
  const declared = declaredPathsFromArtifacts(input.renderedArtifacts ?? input.work.artifacts)
  const dynamic = (input.dynamicArtifacts ?? []).map((entry) => ({ path: entry.path, source: "dynamic" as const }))
  const seen = new Set<string>()
  const ordered: DeclaredArtifactDeclaration[] = []
  for (const decl of [...declared, ...dynamic]) {
    const key = decl.path
    if (seen.has(key)) continue
    seen.add(key)
    ordered.push(decl)
  }

  const captures: CapturedArtifact[] = []
  const failures: ArtifactCaptureFailure[] = []
  for (const decl of ordered) {
    try {
      const capture = await captureOne(input.workDir, decl, limits)
      captures.push(capture)
    } catch (error) {
      failures.push({
        path: decl.path,
        source: decl.source,
        reason: error instanceof Error ? error.message : String(error),
      })
    }
  }
  return { captures, failures }
}

export async function captureOne(
  workDir: string,
  declaration: DeclaredArtifactDeclaration,
  limits: ArtifactCaptureLimits = DEFAULT_ARTIFACT_CAPTURE_LIMITS,
): Promise<CapturedArtifact> {
  const safePath = await resolveArtifactPath(workDir, declaration.path)
  const stat = await lstatSafe(safePath)
  if (stat.isSymbolicLink()) {
    throw new Error(`artifact path '${declaration.path}' is a symlink; refusing to follow it`)
  }
  if (stat.isDirectory()) {
    return await captureDirectory(safePath, declaration, limits)
  }
  if (stat.isFile()) {
    return await captureFile(safePath, declaration, limits)
  }
  throw new Error(`artifact path '${declaration.path}' is not a regular file or directory`)
}

function captureFile(absolutePath: string, declaration: DeclaredArtifactDeclaration, limits: ArtifactCaptureLimits): Promise<CapturedArtifact> {
  return currentRunnerFileSystem().readBinary(absolutePath).then((buffer) => {
    if (buffer.byteLength > limits.maxFileSize) {
      throw new Error(`artifact file '${declaration.path}' exceeds the ${limits.maxFileSize}-byte single-file limit`)
    }
    const content = new Uint8Array(buffer)
    return {
      path: declaration.path,
      kind: "file",
      content,
      contentType: guessContentType(declaration.path),
      contentHash: `sha256:${createHash("sha256").update(content).digest("hex")}`,
      size: content.byteLength,
      source: declaration.source,
    }
  })
}

async function captureDirectory(absolutePath: string, declaration: DeclaredArtifactDeclaration, limits: ArtifactCaptureLimits): Promise<CapturedArtifact> {
  const collected = await collectDirectoryFiles(absolutePath, declaration.path, limits)
  const content = encodeDirectoryArchive(collected)
  if (content.byteLength > limits.maxDirectoryTotalSize) {
    throw new Error(`artifact directory '${declaration.path}' exceeds the ${limits.maxDirectoryTotalSize}-byte total size limit`)
  }
  return {
    path: declaration.path,
    kind: "directory",
    content,
    contentType: "application/x-mohist-artifact-directory",
    contentHash: `sha256:${createHash("sha256").update(content).digest("hex")}`,
    size: content.byteLength,
    fileCount: collected.length,
    totalSize: collected.reduce((sum, entry) => sum + entry.size, 0),
    source: declaration.source,
  }
}

interface DirectoryFileEntry {
  relativePath: string
  size: number
  data: Uint8Array
}

async function collectDirectoryFiles(absoluteRoot: string, sourceLabel: string, limits: ArtifactCaptureLimits): Promise<DirectoryFileEntry[]> {
  const out: DirectoryFileEntry[] = []
  let totalSize = 0
  const stack: string[] = [absoluteRoot]
  while (stack.length > 0) {
    const current = stack.pop()!
    let entries
    try {
      entries = await currentRunnerFileSystem().readdir(current)
    } catch (error) {
      throw new Error(`artifact directory '${sourceLabel}' could not be read: ${(error as Error).message}`)
    }
    for (const entry of entries) {
      const entryAbsolute = resolve(current, entry.name)
      if (entry.isSymbolicLink()) {
        throw new Error(`artifact directory '${sourceLabel}' contains a symlink at '${entry.name}'; refusing to follow it`)
      }
      if (entry.isDirectory()) {
        stack.push(entryAbsolute)
        continue
      }
      if (!entry.isFile()) {
        throw new Error(`artifact directory '${sourceLabel}' contains a non-file entry at '${entry.name}'`)
      }
      const data = await currentRunnerFileSystem().readBinary(entryAbsolute)
      if (data.byteLength > limits.maxFileSize) {
        throw new Error(`artifact directory '${sourceLabel}' contains a file exceeding the ${limits.maxFileSize}-byte single-file limit at '${entry.name}'`)
      }
      totalSize += data.byteLength
      if (totalSize > limits.maxDirectoryTotalSize) {
        throw new Error(`artifact directory '${sourceLabel}' exceeds the ${limits.maxDirectoryTotalSize}-byte total size limit`)
      }
      if (out.length + 1 > limits.maxDirectoryFileCount) {
        throw new Error(`artifact directory '${sourceLabel}' exceeds the ${limits.maxDirectoryFileCount}-file limit`)
      }
      out.push({
        relativePath: relative(absoluteRoot, entryAbsolute).split(sep).join("/"),
        size: data.byteLength,
        data: new Uint8Array(data),
      })
    }
  }
  out.sort((a, b) => a.relativePath.localeCompare(b.relativePath))
  return out
}

function encodeDirectoryArchive(entries: DirectoryFileEntry[]): Uint8Array {
  const json = JSON.stringify({
    kind: "directory",
    files: entries.map((entry) => ({ path: entry.relativePath, size: entry.size, data: Buffer.from(entry.data).toString("base64") })),
  })
  return new TextEncoder().encode(json)
}

async function resolveArtifactPath(workDir: string, rawPath: string): Promise<string> {
  if (!rawPath || typeof rawPath !== "string") {
    throw new Error("artifact path is required")
  }
  const trimmed = rawPath.trim()
  if (trimmed.length === 0) throw new Error("artifact path is required")

  const candidate = isAbsolute(trimmed) ? trimmed : resolve(workDir, trimmed)
  const workDirAbsolute = resolve(workDir)
  const workReal = await safeRealpath(workDirAbsolute) ?? workDirAbsolute
  const candidateReal = await safeRealpath(candidate) ?? candidate
  const relativePath = relative(workReal, candidateReal)
  if (relativePath.startsWith("..") || isAbsolute(relativePath)) {
    throw new Error(`artifact path '${rawPath}' escapes the workspace`)
  }
  // Also refuse back-traversal in the raw input even when the file
  // does not yet exist: the declared path must already resolve inside
  // the workspace after normalization, so we re-check the un-realpathed
  // form as well.
  const normalized = normalize(candidate)
  const relativeToWork = relative(workDirAbsolute, normalized)
  if (relativeToWork.startsWith("..") || isAbsolute(relativeToWork)) {
    throw new Error(`artifact path '${rawPath}' escapes the workspace`)
  }
  return candidate
}

async function safeRealpath(path: string): Promise<string | null> {
  try {
    return await currentRunnerFileSystem().realpath(path)
  } catch {
    return null
  }
}

function lstatSafe(path: string) {
  return currentRunnerFileSystem().lstat(path)
}

function guessContentType(path: string): string {
  const lower = path.toLowerCase()
  if (lower.endsWith(".md") || lower.endsWith(".markdown")) return "text/markdown"
  if (lower.endsWith(".json")) return "application/json"
  if (lower.endsWith(".txt")) return "text/plain"
  if (lower.endsWith(".xml")) return "application/xml"
  if (lower.endsWith(".yaml") || lower.endsWith(".yml")) return "application/yaml"
  if (lower.endsWith(".html") || lower.endsWith(".htm")) return "text/html"
  if (lower.endsWith(".js")) return "text/javascript"
  if (lower.endsWith(".ts")) return "text/typescript"
  if (lower.endsWith(".log")) return "text/plain"
  return "application/octet-stream"
}

export interface UploadCapturedArtifactsResult {
  uploads: ArtifactUploadResponse[]
  failures: ArtifactCaptureFailure[]
}

export async function uploadCapturedArtifacts(
  connection: ArtifactUploader,
  ownerId: string,
  workId: string,
  captures: ReadonlyArray<CapturedArtifact>,
  signal: AbortSignal,
  ownerKind = "workflow",
): Promise<UploadCapturedArtifactsResult> {
  const uploads: ArtifactUploadResponse[] = []
  const failures: ArtifactCaptureFailure[] = []
  for (const capture of captures) {
    const request: ArtifactUploadRequest = {
      path: capture.path,
      contentType: capture.contentType,
      contentHash: capture.contentHash,
      size: capture.size,
      content: capture.content,
    }
    try {
      const response = await connection.uploadArtifact(ownerId, workId, request, signal, ownerKind)
      uploads.push(response)
    } catch (error) {
      failures.push({
        path: capture.path,
        source: capture.source,
        reason: error instanceof Error ? error.message : String(error),
      })
    }
  }
  return { uploads, failures }
}

export function summarizeCaptureFailures(failures: ReadonlyArray<ArtifactCaptureFailure>): string {
  return failures.map((failure) => `${failure.source === "declared" ? "declared" : "dynamic"} artifact '${failure.path}': ${failure.reason}`).join("; ")
}

export async function ensureWorkspaceDirectoryExists(workDir: string) {
  await currentRunnerFileSystem().stat(workDir)
}

export function readRealpath(path: string): Promise<string> {
  return currentRunnerFileSystem().realpath(path)
}
