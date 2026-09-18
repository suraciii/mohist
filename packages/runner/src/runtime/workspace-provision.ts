import { createHash, randomUUID } from 'node:crypto'
import { isAbsolute, join, relative, resolve } from 'node:path'
import type { WorkspaceArtifactDirectory, WorkspaceArtifactInfo } from '../server/connection-workspace-artifacts.js'
import { currentRunnerFileSystem, type RunnerFileInfo } from '../system/filesystem.js'

export interface WorkspaceArtifactProvisionClient {
  listWorkspaceArtifacts(workflowRunId: string, workId: string, signal: AbortSignal): Promise<WorkspaceArtifactInfo[]>
  readWorkspaceArtifactDirectory(
    workflowRunId: string,
    workId: string,
    artifactId: string,
    signal: AbortSignal,
  ): Promise<WorkspaceArtifactDirectory>
  downloadWorkspaceArtifact(
    workflowRunId: string,
    workId: string,
    artifactId: string,
    signal: AbortSignal,
    file?: string,
  ): Promise<Uint8Array>
}

export async function provisionWorkspaceArtifacts(
  client: WorkspaceArtifactProvisionClient,
  workflowRunId: string,
  workId: string,
  workspaceRoot: string,
  signal: AbortSignal,
): Promise<void> {
  const rootInfo = await readPathInfo(workspaceRoot)
  if (!rootInfo || rootInfo.isSymbolicLink() || !rootInfo.isDirectory()) {
    throw new Error(`Workspace Home '${workspaceRoot}' is not a safe directory`)
  }
  const artifacts = await client.listWorkspaceArtifacts(workflowRunId, workId, signal)
  const createdFiles: string[] = []
  try {
    for (const artifact of artifacts) {
      signal.throwIfAborted()
      const path = normalizeWorkspaceArtifactPath(artifact.path)
      const destination = resolveWorkspacePath(workspaceRoot, path)
      await ensureSafeParent(workspaceRoot, destination)
      const existing = await readPathInfo(destination)

      if (artifact.kind === 'file') {
        if (existing) {
          if (existing.isSymbolicLink() || !existing.isFile()) {
            throw new Error(`workspace artifact destination '${path}' is not a regular file`)
          }
          const content = await currentRunnerFileSystem().readBinary(destination)
          validateContent(path, content, artifact.size, artifact.contentHash)
          continue
        }
        const content = await client.downloadWorkspaceArtifact(workflowRunId, workId, artifact.artifactId, signal)
        validateContent(path, content, artifact.size, artifact.contentHash)
        await writeFileAtomically(workspaceRoot, destination, content, signal)
        createdFiles.push(destination)
        continue
      }

      if (artifact.kind !== 'directory') {
        throw new Error(`workspace artifact '${artifact.artifactId}' has unknown kind '${artifact.kind}'`)
      }

      if (existing) {
        if (existing.isSymbolicLink() || !existing.isDirectory()) {
          throw new Error(`workspace artifact destination '${path}' is not a directory`)
        }
      } else {
        await ensureSafeDirectory(workspaceRoot, destination)
      }

      const directory = await client.readWorkspaceArtifactDirectory(workflowRunId, workId, artifact.artifactId, signal)
      if (directory.artifactId !== artifact.artifactId || normalizeWorkspaceArtifactPath(directory.path) !== path) {
        throw new Error(`workspace artifact directory metadata does not match '${path}'`)
      }
      createdFiles.push(
        ...(await provisionDirectoryArtifact(
          client,
          workflowRunId,
          workId,
          workspaceRoot,
          destination,
          artifact,
          directory,
          signal,
        )),
      )
    }
  } catch (error) {
    await Promise.all(
      createdFiles.reverse().map((path) =>
        currentRunnerFileSystem()
          .deleteFile(path)
          .catch(() => {}),
      ),
    )
    throw error
  }
}

export function normalizeWorkspaceArtifactPath(rawPath: string): string {
  if (typeof rawPath !== 'string' || rawPath.trim().length === 0) {
    throw new Error('workspace artifact path is required')
  }
  const path = rawPath.trim().replaceAll('\\', '/')
  if (path.startsWith('/') || /^[A-Za-z]:\//.test(path)) {
    throw new Error(`workspace artifact path '${rawPath}' must be Workspace-relative`)
  }
  const parts = path.split('/')
  if (parts.some((part) => part.length === 0 || part === '.' || part === '..' || hasControlCharacter(part))) {
    throw new Error(`workspace artifact path '${rawPath}' is not normalized`)
  }
  if (
    ['REPOS', '.mohist', '.scratch', '.mohist-provision-temp'].some(
      (reserved) => reserved.toLowerCase() === parts[0]!.toLowerCase(),
    )
  ) {
    throw new Error(`workspace artifact path '${rawPath}' is outside the Workspace artifact boundary`)
  }
  return parts.join('/')
}

function resolveWorkspacePath(workspaceRoot: string, path: string): string {
  const root = resolve(workspaceRoot)
  const destination = resolve(root, path)
  const relativePath = relative(root, destination)
  if (!relativePath || relativePath.startsWith('..') || isAbsolute(relativePath)) {
    throw new Error(`workspace artifact path '${path}' escapes the Workspace`)
  }
  return destination
}

async function provisionDirectoryArtifact(
  client: WorkspaceArtifactProvisionClient,
  workflowRunId: string,
  workId: string,
  workspaceRoot: string,
  destinationRoot: string,
  artifact: WorkspaceArtifactInfo,
  directory: WorkspaceArtifactDirectory,
  signal: AbortSignal,
): Promise<string[]> {
  const pending: Array<{ destination: string; relativePath: string; content: Uint8Array }> = []
  const seen = new Set<string>()
  let totalSize = 0

  for (const entry of directory.entries) {
    signal.throwIfAborted()
    const relativePath = normalizeDirectoryEntryPath(entry.relativePath)
    if (!seen.add(relativePath)) {
      throw new Error(`workspace artifact directory contains duplicate entry '${relativePath}'`)
    }
    const destination = resolveWorkspacePath(
      workspaceRoot,
      relative(`${resolve(workspaceRoot)}`, join(destinationRoot, relativePath)).replaceAll('\\', '/'),
    )
    await ensureSafeParent(workspaceRoot, destination)
    const existing = await readPathInfo(destination)
    if (existing) {
      if (existing.isSymbolicLink() || !existing.isFile()) {
        throw new Error(`workspace artifact file destination '${relativePath}' is not a regular file`)
      }
      const content = await currentRunnerFileSystem().readBinary(destination)
      validateContent(relativePath, content, entry.size, entry.contentHash)
      totalSize += content.byteLength
      continue
    }

    const content = await client.downloadWorkspaceArtifact(
      workflowRunId,
      workId,
      artifact.artifactId,
      signal,
      relativePath,
    )
    validateContent(relativePath, content, entry.size, entry.contentHash)
    totalSize += content.byteLength
    pending.push({ destination, relativePath, content })
  }

  if (directory.totalSize !== totalSize) {
    throw new Error(
      `workspace artifact directory '${artifact.path}' total size mismatch: expected ${directory.totalSize}, received ${totalSize}`,
    )
  }

  const fileSystem = currentRunnerFileSystem()
  const tempRoot = resolveWorkspacePath(workspaceRoot, '.mohist-provision-temp')
  await ensureSafeDirectory(workspaceRoot, tempRoot)
  const stage = join(tempRoot, `${randomUUID()}.directory`)
  const backup = join(tempRoot, `${randomUUID()}.backup`)
  let originalMoved = false
  let committed = false
  try {
    await ensureSafeDirectory(workspaceRoot, stage)
    await copySafeDirectory(workspaceRoot, destinationRoot, stage, signal)
    for (const entry of pending) {
      signal.throwIfAborted()
      const stagedDestination = resolveWorkspacePath(
        workspaceRoot,
        relative(resolve(workspaceRoot), join(stage, entry.relativePath)).replaceAll('\\', '/'),
      )
      await writeFileAtomically(workspaceRoot, stagedDestination, entry.content, signal)
    }

    await fileSystem.rename(destinationRoot, backup)
    originalMoved = true
    try {
      await fileSystem.rename(stage, destinationRoot)
      committed = true
    } catch (error) {
      await fileSystem.rename(backup, destinationRoot).catch(() => {})
      originalMoved = false
      throw error
    }
    await fileSystem.deleteDirectory(backup).catch(() => {})
    return pending.map((entry) => entry.destination)
  } catch (error) {
    if (originalMoved && !fileSystem.exists(destinationRoot)) {
      await fileSystem.rename(backup, destinationRoot).catch(() => {})
    }
    throw error
  } finally {
    if (!committed) await fileSystem.deleteDirectory(stage).catch(() => {})
    if (committed || !originalMoved) await fileSystem.deleteDirectory(backup).catch(() => {})
  }
}

async function copySafeDirectory(
  workspaceRoot: string,
  source: string,
  destination: string,
  signal: AbortSignal,
): Promise<void> {
  const fileSystem = currentRunnerFileSystem()
  const sourceInfo = await readPathInfo(source)
  if (!sourceInfo || sourceInfo.isSymbolicLink() || !sourceInfo.isDirectory()) {
    throw new Error(`workspace artifact directory source '${source}' is not a safe directory`)
  }
  await ensureSafeDirectory(workspaceRoot, destination)
  for (const entry of await fileSystem.readdir(source)) {
    signal.throwIfAborted()
    const sourcePath = join(source, entry.name)
    const destinationPath = join(destination, entry.name)
    const info = await readPathInfo(sourcePath)
    if (!info || info.isSymbolicLink()) {
      throw new Error(`workspace artifact directory contains a symlink or missing entry '${entry.name}'`)
    }
    if (info.isDirectory()) {
      await copySafeDirectory(workspaceRoot, sourcePath, destinationPath, signal)
      continue
    }
    if (!info.isFile()) {
      throw new Error(`workspace artifact directory contains a non-file entry '${entry.name}'`)
    }
    await fileSystem.writeBinary(destinationPath, await fileSystem.readBinary(sourcePath))
  }
}

function normalizeDirectoryEntryPath(rawPath: string): string {
  if (typeof rawPath !== 'string' || rawPath.trim().length === 0) {
    throw new Error('workspace artifact directory entry path is required')
  }
  const path = rawPath.trim().replaceAll('\\', '/')
  const parts = path.split('/')
  if (parts.some((part) => part.length === 0 || part === '.' || part === '..' || hasControlCharacter(part))) {
    throw new Error(`workspace artifact directory entry '${rawPath}' is not normalized`)
  }
  return parts.join('/')
}

async function writeFileAtomically(
  workspaceRoot: string,
  destination: string,
  content: Uint8Array,
  signal: AbortSignal,
): Promise<void> {
  const fileSystem = currentRunnerFileSystem()
  await ensureSafeParent(workspaceRoot, destination)
  const tempRoot = resolveWorkspacePath(workspaceRoot, '.mohist-provision-temp')
  await ensureSafeDirectory(workspaceRoot, tempRoot)
  const temporary = join(tempRoot, `${randomUUID()}.tmp`)
  try {
    await fileSystem.writeBinary(temporary, content)
    signal.throwIfAborted()
    await fileSystem.rename(temporary, destination)
  } finally {
    await fileSystem.deleteFile(temporary).catch(() => {})
  }
}

async function ensureSafeDirectory(workspaceRoot: string, path: string): Promise<void> {
  await ensureSafeParent(workspaceRoot, path)
  const fileSystem = currentRunnerFileSystem()
  const existing = await readPathInfo(path)
  if (existing) {
    if (existing.isSymbolicLink() || !existing.isDirectory()) {
      throw new Error(`workspace artifact directory '${path}' is not a directory`)
    }
    return
  }
  await fileSystem.ensureDir(path)
}

async function ensureSafeParent(workspaceRoot: string, destination: string): Promise<void> {
  const fileSystem = currentRunnerFileSystem()
  const root = resolve(workspaceRoot)
  const parent = resolve(destination, '..')
  const relativeParent = relative(root, parent)
  if (relativeParent.startsWith('..') || isAbsolute(relativeParent)) {
    throw new Error(`workspace artifact destination '${destination}' escapes the Workspace`)
  }

  let current = root
  for (const component of relativeParent.split(/[\\/]+/).filter(Boolean)) {
    current = join(current, component)
    const existing = await readPathInfo(current)
    if (existing) {
      if (existing.isSymbolicLink() || !existing.isDirectory()) {
        throw new Error(`workspace artifact parent '${current}' is not a directory`)
      }
    } else {
      await fileSystem.ensureDir(current)
    }
  }
}

async function readPathInfo(path: string): Promise<RunnerFileInfo | null> {
  try {
    return await currentRunnerFileSystem().lstat(path)
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === 'ENOENT') return null
    throw error
  }
}

function validateContent(
  path: string,
  content: Uint8Array,
  expectedSize: number | null,
  expectedHash: string | null,
): void {
  if (expectedSize !== null && content.byteLength !== expectedSize) {
    throw new Error(
      `workspace artifact '${path}' size mismatch: expected ${expectedSize}, received ${content.byteLength}`,
    )
  }
  if (expectedHash) {
    const actual = `sha256:${createHash('sha256').update(content).digest('hex')}`
    if (actual.toLowerCase() !== expectedHash.toLowerCase()) {
      throw new Error(`workspace artifact '${path}' content hash mismatch`)
    }
  }
}

function hasControlCharacter(value: string): boolean {
  return [...value].some((character) => character.charCodeAt(0) < 32 || character.charCodeAt(0) === 127)
}
