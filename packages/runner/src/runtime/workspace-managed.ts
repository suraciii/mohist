import { constants } from 'node:fs'
import { homedir, tmpdir } from 'node:os'
import { isAbsolute, join, relative, resolve } from 'node:path'
import type { JsonObject } from '../core/types.js'
import { getSegments } from '../core/json-path.js'
import { exists, readText, runCommand, writeText } from '../system/process.js'
import { currentRunnerFileSystem, type RunnerDirectoryHandle } from '../system/filesystem.js'
import type { TaskLogger } from './task-log.js'
import {
  sanitizeWorkspaceDiagnostic,
  WorkspaceCorruptError,
  WorkspaceIdentityMismatchError,
  WorkspaceMissingError,
} from './workspace-errors.js'
import { readMarker, type IssueWorkspaceMarker } from './workspace-identity.js'

/**
 * `source` tag recorded against every captured workspace-preparation
 * line. Distinct from the action body's `action:*` tag so the web
 * viewer can phase-distinguish the clone / branch / worktree setup
 * from the action itself.
 */
export const WORKSPACE_PREP_SOURCE = 'workspace-prep'

export function workspacePrepSink(log: TaskLogger | null | undefined) {
  return log ? { log, source: WORKSPACE_PREP_SOURCE } : undefined
}

export function defaultRunnerRoot() {
  return process.env.MOHIST_RUNNER_ROOT ?? process.env.MOHIST_WORKSPACE_ROOT ?? join(homedir(), '.mohist', 'projects')
}

export function runnerVariables() {
  return {
    os: process.platform,
    hostname: process.env.COMPUTERNAME ?? process.env.HOSTNAME ?? 'unknown',
    temp: tmpdir(),
  }
}

export async function validateWorkspaceIdentity(
  workspacePath: string,
  expected: IssueWorkspaceMarker,
  gitUrl: string,
  signal: AbortSignal,
  log: TaskLogger | null = null,
  runnerRoot?: string,
  displayPath = workspacePath,
): Promise<void> {
  if (runnerRoot) await assertManagedWorkspacePath(runnerRoot, workspacePath, true)
  const marker = await readMarker(workspacePath)
  if (!marker) {
    throw new WorkspaceCorruptError(`Workflow workspace ${displayPath} has no readable identity marker`, displayPath)
  }
  const fields: (keyof IssueWorkspaceMarker)[] = ['workflowRunId', 'runBranch']
  if (fields.some((field) => marker[field] !== expected[field])) {
    throw new WorkspaceIdentityMismatchError(
      `Workflow workspace ${displayPath} marker identity does not match the requested run`,
      displayPath,
      expected,
      marker,
    )
  }
  await validateWorkspaceOrigin(workspacePath, gitUrl, signal, log, displayPath)
}

export async function validateWorkspaceOrigin(
  workspacePath: string,
  gitUrl: string,
  signal: AbortSignal,
  log: TaskLogger | null = null,
  displayPath = workspacePath,
): Promise<void> {
  const sink = workspacePrepSink(log)
  const options = sink ? { onLine: (line: string) => sink.log.write(sink.source, line) } : undefined
  const result = await runCommand(
    'git',
    ['-C', workspacePath, 'remote', 'get-url', 'origin'],
    '.',
    signal,
    undefined,
    options,
  )
  const diagnostic = sanitizeWorkspaceDiagnostic(
    [result.stderr.trim(), result.stdout.trim()].filter(Boolean).join('\n'),
    workspacePath,
    displayPath,
  )
  if (result.exitCode !== 0) {
    throw new WorkspaceIdentityMismatchError(
      `Workflow workspace ${displayPath} origin probe failed (exit ${result.exitCode}): ${diagnostic || 'no diagnostic'}`,
      displayPath,
      undefined,
      undefined,
      undefined,
      { kind: 'probe-failed', exitCode: result.exitCode, diagnostic: diagnostic || `exit ${result.exitCode}` },
    )
  }
  if (result.stdout.trim() !== gitUrl.trim()) {
    const observedOrigin = sanitizeWorkspaceDiagnostic(result.stdout.trim() || '<empty>', workspacePath, displayPath)
    const expectedOrigin = sanitizeWorkspaceDiagnostic(gitUrl.trim(), workspacePath, displayPath)
    const mismatch = `observed=${observedOrigin} expected=${expectedOrigin}`
    throw new WorkspaceIdentityMismatchError(
      `Workflow workspace ${displayPath} origin value does not match the requested repository: ${mismatch}`,
      displayPath,
      undefined,
      undefined,
      undefined,
      { kind: 'value-mismatch', exitCode: result.exitCode, diagnostic: mismatch },
    )
  }
}

export async function assertManagedWorkspacePath(
  runnerRoot: string,
  candidate: string,
  requireFinal: boolean,
): Promise<void> {
  const root = resolve(runnerRoot)
  const target = resolve(candidate)
  const rel = relative(root, target)
  if (!rel || rel.startsWith('..') || isAbsolute(rel)) {
    throw new WorkspaceIdentityMismatchError(`Workspace path ${target} is outside runner root ${root}`, target)
  }
  try {
    if ((await currentRunnerFileSystem().lstat(root)).isSymbolicLink())
      throw new WorkspaceIdentityMismatchError(`Runner root ${root} is symlinked`, target)
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw error
  }
  const components = rel.split(/[\\/]+/).filter(Boolean)
  let current = root
  for (let i = 0; i < components.length; i++) {
    current = join(current, components[i]!)
    try {
      const stat = await currentRunnerFileSystem().lstat(current)
      if (stat.isSymbolicLink())
        throw new WorkspaceIdentityMismatchError(`Workspace path ${current} is symlinked`, target)
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code === 'ENOENT') {
        if (i === components.length - 1 && !requireFinal) return
        continue
      }
      throw error
    }
  }
  if (requireFinal && !pathExists(target)) {
    throw new WorkspaceMissingError(`Workflow workspace ${target} is missing`, target)
  }
}

export async function withManagedWorkspacePath<T>(
  runnerRoot: string,
  workspacePath: string,
  requireFinal: boolean,
  operation: (workspacePath: string) => Promise<T>,
): Promise<T> {
  const stablePath = resolve(workspacePath)
  return await withManagedWorkspaceHandle(runnerRoot, stablePath, requireFinal, async () => operation(stablePath))
}

// Internal filesystem operations receive a process-owned directory handle
// path. It is valid only for the duration of this callback and must never
// escape into a registry, server binding, runtime session, or recovery task.
export async function withManagedWorkspaceHandle<T>(
  runnerRoot: string,
  workspacePath: string,
  requireFinal: boolean,
  operation: (managedWorkspacePath: string) => Promise<T>,
): Promise<T> {
  const root = resolve(runnerRoot)
  const workspaceParent = join(root, 'workspaces')
  const target = resolve(workspacePath)
  const name = relative(workspaceParent, target)
  if (!name || name.includes('/') || name.includes('\\') || isAbsolute(name)) {
    throw new WorkspaceIdentityMismatchError(
      `Workspace path ${target} is outside managed workspace parent ${workspaceParent}`,
      target,
    )
  }

  const fileSystem = currentRunnerFileSystem()
  if (process.platform !== 'linux' || !fileSystem.supportsDirectoryHandles || !fileSystem.openDirectory) {
    await assertManagedWorkspacePath(root, target, requireFinal)
    return await operation(target)
  }

  await currentRunnerFileSystem().ensureDir(root)
  let rootHandle: RunnerDirectoryHandle | undefined
  let workspaceHandle: RunnerDirectoryHandle | undefined
  let managedWorkspacePath: string
  try {
    rootHandle = await fileSystem.openDirectory(root, constants.O_RDONLY | constants.O_DIRECTORY | constants.O_NOFOLLOW)
    const stableRoot = rootHandle.path
    await fileSystem.ensureDir(join(stableRoot, 'workspaces'))
    workspaceHandle = await fileSystem.openDirectory(
      join(stableRoot, 'workspaces'),
      constants.O_RDONLY | constants.O_DIRECTORY | constants.O_NOFOLLOW,
    )
    managedWorkspacePath = join(workspaceHandle.path, name)
    await assertManagedWorkspaceEntry(managedWorkspacePath, target, requireFinal)
  } catch (error) {
    await workspaceHandle?.close()
    await rootHandle?.close()
    if (error instanceof WorkspaceMissingError || error instanceof WorkspaceIdentityMismatchError) throw error
    throw new WorkspaceIdentityMismatchError(
      `Managed workspace parent ${workspaceParent} is unavailable or symlinked`,
      target,
      undefined,
      undefined,
      error,
    )
  }

  try {
    return await operation(managedWorkspacePath!)
  } catch (error) {
    throw sanitizeManagedWorkspaceError(error, managedWorkspacePath!, target)
  } finally {
    await workspaceHandle?.close()
    await rootHandle?.close()
  }
}

async function assertManagedWorkspaceEntry(
  managedWorkspacePath: string,
  workspacePath: string,
  requireFinal: boolean,
): Promise<void> {
  try {
    if ((await currentRunnerFileSystem().lstat(managedWorkspacePath)).isSymbolicLink()) {
      throw new WorkspaceIdentityMismatchError(`Workspace path ${workspacePath} is symlinked`, workspacePath)
    }
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw error
    if (requireFinal) throw new WorkspaceMissingError(`Workflow workspace ${workspacePath} is missing`, workspacePath)
  }
}

export async function assertNotSymlink(path: string, displayPath = path): Promise<void> {
  try {
    if ((await currentRunnerFileSystem().lstat(path)).isSymbolicLink()) {
      throw new WorkspaceIdentityMismatchError(`Preparation path ${displayPath} is symlinked`, displayPath)
    }
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw error
  }
}

export function pathExists(path: string): boolean {
  return exists(path)
}

function sanitizeManagedWorkspaceError(error: unknown, managedPath: string, displayPath: string): unknown {
  if (!(error instanceof Error)) return error
  const message = sanitizeWorkspaceDiagnostic(error.message, managedPath, displayPath)
  if (message !== error.message) {
    Object.defineProperty(error, 'message', { configurable: true, value: message, writable: true })
  }
  const withWorkspacePath = error as Error & { workspacePath?: unknown }
  if (typeof withWorkspacePath.workspacePath === 'string') {
    Object.defineProperty(error, 'workspacePath', {
      configurable: true,
      value: sanitizeWorkspaceDiagnostic(withWorkspacePath.workspacePath, managedPath, displayPath),
      writable: true,
    })
  }
  return error
}

export async function ensureMarkerExcluded(workspacePath: string) {
  const excludePath = join(workspacePath, '.git', 'info', 'exclude')
  const markerRule = '.mohist/'
  let raw = ''
  try {
    raw = await readText(excludePath)
  } catch {
    // ignore
  }
  if (raw.split(/\r?\n/).some((line) => line.trim() === markerRule || line.trim() === '.mohist')) return
  const suffix = raw.endsWith('\n') || raw.length === 0 ? '' : '\n'
  await writeText(excludePath, `${raw}${suffix}${markerRule}\n`)
}

// Read the configured `origin` URL of a bare repository cache. Returns
// `undefined` if the cache is unreadable / unconfigured rather than
// throwing, so the caller can decide how to surface an unreadable cache
// (treat as identity mismatch → replacement candidate).
async function readCacheOrigin(cachePath: string, signal: AbortSignal) {
  const result = await runCommand('git', ['-C', cachePath, 'remote', 'get-url', 'origin'], '.', signal)
  if (result.exitCode !== 0) return undefined
  return result.stdout.trim() || undefined
}

// Decide whether the cache's object store is still referenced by an
// active workflow workspace clone under `<projectRoot>/workspaces/`.
// The scan follows transitive alternates so deleting the cache cannot
// corrupt active workspace object stores.
async function isCacheReferencedByActiveWorkspace(cachePath: string, projectRoot: string, signal: AbortSignal) {
  const target = resolve(join(cachePath, 'objects'))
  const cloneRoots = [join(projectRoot, 'workspaces')]

  async function readAlternates(objectsDir: string): Promise<string[]> {
    const gitDir = objectsDir.replace(/[\\/]objects$/, '')
    const alternatesPath = join(gitDir, 'objects', 'info', 'alternates')
    if (!exists(alternatesPath)) return []
    let raw: string
    try {
      raw = await readText(alternatesPath)
    } catch {
      return []
    }
    const out: string[] = []
    for (const line of raw.split(/\r?\n/)) {
      const trimmed = line.trim()
      if (!trimmed || trimmed.startsWith('#')) continue
      try {
        out.push(resolve(trimmed))
      } catch {
        // skip
      }
    }
    return out
  }

  for (const dir of cloneRoots) {
    if (!exists(dir)) continue
    const entries = await currentRunnerFileSystem().readdir(dir)
    for (const entry of entries) {
      if (!entry.isDirectory()) continue
      const gitDir = join(dir, entry.name, '.git')
      if (!exists(gitDir)) continue
      // BFS the alternates chain rooted at this clone. An alternates
      // entry is a `<git_dir>/objects` path; if it equals the target,
      // this clone references the cache. If it does not, but it is
      // itself a `.git/objects` path belonging to another clone, we
      // enqueue that clone's alternates to follow the chain further.
      const visited = new Set<string>()
      const queue: string[] = await readAlternates(join(gitDir, 'objects'))
      while (queue.length > 0) {
        const current = queue.shift()!
        if (visited.has(current)) continue
        visited.add(current)
        if (current === target) return true
        // Only follow when the current entry looks like another clone's
        // `.git/objects` (i.e., ends with `.git/objects`). Other paths
        // (e.g., environment-provided object dirs) are leaf nodes.
        if (/(^|[\\/])\.git[\\/]objects$/.test(current)) {
          const next = await readAlternates(current)
          for (const n of next) if (!visited.has(n)) queue.push(n)
        }
      }
    }
  }
  return false
}

// `git fsck` based corruption detector. Runs an unconnected fsck
// against the bare cache; returns true when fsck reports any corrupt /
// missing object. Used as an alternate justification for cache
// replacement (per the spec's "origin URL mismatch OR verified
// corruption" rule).
async function isCacheCorrupt(cachePath: string, baseBranch: string, signal: AbortSignal) {
  const result = await runCommand('git', ['-C', cachePath, 'fsck', '--full', '--no-progress'], '.', signal)
  if (result.exitCode !== 0) return true
  const base = await runCommand(
    'git',
    ['-C', cachePath, 'rev-parse', '--verify', `refs/heads/${baseBranch}^{commit}`],
    '.',
    signal,
  )
  if (base.exitCode !== 0) return true
  const baseType = await runCommand('git', ['-C', cachePath, 'cat-file', '-t', base.stdout.trim()], '.', signal)
  if (baseType.exitCode !== 0) return true
  const refs = await runCommand('git', ['-C', cachePath, 'show-ref', '--heads', '--dereference'], '.', signal)
  if (refs.exitCode !== 0) return true
  for (const line of refs.stdout.split(/\r?\n/)) {
    const oid = line.trim().split(/\s+/)[0]
    if (!oid) continue
    const object = await runCommand('git', ['-C', cachePath, 'cat-file', '-e', `${oid}^{object}`], '.', signal)
    if (object.exitCode !== 0) return true
    const tree = await runCommand('git', ['-C', cachePath, 'ls-tree', '-r', oid], '.', signal)
    if (tree.exitCode !== 0) return true
  }
  return false
}

function slug(value: string): string {
  return (
    value
      .toLowerCase()
      .replace(/[^a-z0-9]+/g, '-')
      .replace(/^-+|-+$/g, '') || 'project'
  )
}

export { slug as slugify }

export function numberAt(value: JsonObject | undefined, path: string[]): number | undefined {
  const found = getSegments(value, path)
  return typeof found === 'number' ? found : undefined
}
