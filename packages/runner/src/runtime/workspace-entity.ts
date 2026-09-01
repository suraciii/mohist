import { createHash } from 'node:crypto'
import { join, resolve } from 'node:path'
import { deleteDirectory, ensureDir, exists, readText, runCommand } from '../system/process.js'
import { currentRunnerFileSystem } from '../system/filesystem.js'
import { NETWORK_COMMAND_TIMEOUT_MS } from '../actions/git.js'
import type { ServerConnection } from '../server/connection.js'
import type { TaskLogger } from './task-log.js'
import {
  sanitizeWorkspaceDiagnostic,
  workspaceNetworkTimeout,
  WorkspaceIdentityMismatchError,
} from './workspace-errors.js'
import { repositoryWorkspacePath } from './workspace-identity.js'
import { validateWorkspaceOrigin, workspacePrepSink } from './workspace-managed.js'
import type { NamedWorkspaceRegistry } from './workspace-registry.js'
import { slugify, withManagedWorkspaceHandle } from './workspace.js'

// Named workspace materialization keeps the Workspace root as the shared
// boundary for plans and repository checkouts. Issue-bound workflow work
// uses the fixed REPOS/<repository> layout so AgentJobs and mechanical tasks
// observe the same checkout and branch.

export interface NamedWorkspaceRepository {
  name: string
  gitUrl: string
}

export interface NamedWorkspaceMarker {
  projectId: string
  workspaceName: string
  repositories: NamedWorkspaceRepository[]
}

export interface NamedWorkspaceMaterializeResult {
  path: string
  // True when this call created the directory (it did not exist before).
  // The caller uses this to decide whether yielding to a claimed home
  // runner may delete the directory without destroying pre-existing
  // work.
  created: boolean
}

const MARKER_RELATIVE_PATH = '.mohist/workspace.json'

// Deterministic per-(projectId, workspaceName) directory under the
// managed workspace parent. The slug disambiguates the common case and
// a short content hash keeps rare slug collisions (e.g. "a b" vs "a-b")
// from mapping two distinct workspaces onto one directory.
export function namedWorkspacePath(runnerRoot: string, projectId: string, workspaceName: string): string {
  const digest = createHash('sha256').update(`${projectId}/${workspaceName}`).digest('hex').slice(0, 8)
  const component = `${slugify(projectId)}-${slugify(workspaceName)}-${digest}`
  return resolve(join(runnerRoot, 'workspaces', component))
}

export function namedWorkspaceMarkerPath(workspacePath: string): string {
  return join(workspacePath, MARKER_RELATIVE_PATH)
}

export async function readNamedWorkspaceMarker(workspacePath: string): Promise<NamedWorkspaceMarker | null> {
  const path = namedWorkspaceMarkerPath(workspacePath)
  if (!exists(path)) return null
  try {
    const raw = await readText(path)
    const parsed = JSON.parse(raw) as Partial<NamedWorkspaceMarker>
    if (typeof parsed.projectId !== 'string' || typeof parsed.workspaceName !== 'string') return null
    const repositories = Array.isArray(parsed.repositories)
      ? parsed.repositories.filter(
          (r): r is NamedWorkspaceRepository =>
            typeof r === 'object' &&
            r !== null &&
            typeof (r as NamedWorkspaceRepository).name === 'string' &&
            typeof (r as NamedWorkspaceRepository).gitUrl === 'string',
        )
      : []
    return { projectId: parsed.projectId, workspaceName: parsed.workspaceName, repositories }
  } catch {
    return null
  }
}

export interface NamedWorkspaceMaterializeOptions {
  runnerRoot: string
  projectId: string
  workspaceName: string
  repositories?: readonly NamedWorkspaceRepository[]
  registry: NamedWorkspaceRegistry
  now?: () => Date
}

// Ensure the named workspace's persistent directory exists and record
// the identity marker + registry entry. Re-materialization (directory
// recycled by cleanup) is just ensureDir again: the platform does not
// promise continuity, so there is nothing to restore — the next agent
// starts from the empty directory.
export async function materializeNamedWorkspace(
  options: NamedWorkspaceMaterializeOptions,
): Promise<NamedWorkspaceMaterializeResult> {
  const workspacePath = namedWorkspacePath(options.runnerRoot, options.projectId, options.workspaceName)
  let created = false
  await withManagedWorkspaceHandle(options.runnerRoot, workspacePath, false, async (managedWorkspacePath) => {
    if (!exists(managedWorkspacePath)) {
      await currentRunnerFileSystem().ensureDir(managedWorkspacePath)
      created = true
    }
    const markerDir = join(managedWorkspacePath, '.mohist')
    await currentRunnerFileSystem().ensureDir(markerDir)
    const marker: NamedWorkspaceMarker = {
      projectId: options.projectId,
      workspaceName: options.workspaceName,
      repositories: options.repositories ? [...options.repositories] : [],
    }
    await currentRunnerFileSystem().writeText(
      join(managedWorkspacePath, MARKER_RELATIVE_PATH),
      JSON.stringify(marker, null, 2),
    )
  })
  await options.registry.register({
    projectId: options.projectId,
    workspaceName: options.workspaceName,
    workspacePath,
  })
  return { path: workspacePath, created }
}

interface IssueWorkspaceRepositoryOptions {
  workspacePath: string
  displayPath: string
  repositoryName: string
  gitUrl: string
  baseBranch: string
  runBranch: string
  signal: AbortSignal
  log?: TaskLogger | null
}

async function ensureIssueWorkspaceRepository(options: IssueWorkspaceRepositoryOptions): Promise<void> {
  const repositoryPath = repositoryWorkspacePath(options.workspacePath, options.repositoryName)
  const displayRepositoryPath = repositoryWorkspacePath(options.displayPath, options.repositoryName)
  const preparationPath = `${repositoryPath}.preparing`
  const sink = workspacePrepSink(options.log)
  const commandOptions = sink
    ? { onLine: (line: string) => sink.log.write(sink.source, line), timeoutMs: NETWORK_COMMAND_TIMEOUT_MS }
    : { timeoutMs: NETWORK_COMMAND_TIMEOUT_MS }

  await ensureDir(join(repositoryPath, '..'))
  if (!exists(repositoryPath)) {
    if (exists(preparationPath)) await deleteDirectory(preparationPath)
    const cloneResult = await runCommand(
      'git',
      ['clone', '--filter=blob:none', '--no-checkout', '--no-tags', options.gitUrl, preparationPath],
      '.',
      options.signal,
      undefined,
      commandOptions,
    )
    if (cloneResult.status === 'timeout') {
      throw workspaceNetworkTimeout(
        'git-clone',
        `clone ${options.gitUrl}`,
        cloneResult,
        preparationPath,
        displayRepositoryPath,
      )
    }
    if (cloneResult.exitCode !== 0) {
      await deleteDirectory(preparationPath).catch(() => {})
      throw new Error(`git clone failed for ${options.gitUrl}: ${cloneResult.stderr || cloneResult.stdout}`)
    }
    try {
      await validateWorkspaceOrigin(preparationPath, options.gitUrl, options.signal, options.log, displayRepositoryPath)
      await restoreIssueWorkspaceBranch(
        preparationPath,
        displayRepositoryPath,
        options.baseBranch,
        options.runBranch,
        options.signal,
        options.log,
      )
      await currentRunnerFileSystem().rename(preparationPath, repositoryPath)
    } catch (error) {
      await deleteDirectory(preparationPath).catch(() => {})
      throw error
    }
    return
  }

  if (!exists(join(repositoryPath, '.git'))) {
    throw new WorkspaceIdentityMismatchError(
      `Repository path ${displayRepositoryPath} exists but is not a Git checkout`,
      displayRepositoryPath,
    )
  }
  await validateWorkspaceOrigin(repositoryPath, options.gitUrl, options.signal, options.log, displayRepositoryPath)
  await reenterIssueWorkspaceBranch(options, displayRepositoryPath)
}

async function restoreIssueWorkspaceBranch(
  repositoryPath: string,
  displayRepositoryPath: string,
  baseBranch: string,
  runBranch: string,
  signal: AbortSignal,
  log?: TaskLogger | null,
): Promise<void> {
  const sink = workspacePrepSink(log)
  const commandOptions = sink ? { onLine: (line: string) => sink.log.write(sink.source, line) } : undefined
  const remoteBranch = `refs/remotes/origin/${runBranch}`
  const hasRunBranch = await runCommand(
    'git',
    ['-C', repositoryPath, 'rev-parse', '--verify', remoteBranch],
    '.',
    signal,
    undefined,
    commandOptions,
  )
  const source = hasRunBranch.exitCode === 0 ? `origin/${runBranch}` : `origin/${baseBranch}`
  const checkout = await runCommand(
    'git',
    ['-C', repositoryPath, 'checkout', '-B', runBranch, source],
    '.',
    signal,
    undefined,
    commandOptions,
  )
  if (checkout.exitCode !== 0) {
    const diagnostic = sanitizeWorkspaceDiagnostic(
      checkout.stderr || checkout.stdout || `exit ${checkout.exitCode}`,
      repositoryPath,
      displayRepositoryPath,
    )
    throw new Error(`Failed to create run branch ${runBranch} from ${source}: ${diagnostic}`)
  }
}

async function reenterIssueWorkspaceBranch(
  options: IssueWorkspaceRepositoryOptions,
  displayRepositoryPath: string,
): Promise<void> {
  const residualPaths = ['.git/rebase-merge', '.git/rebase-apply', '.git/MERGE_HEAD', '.git/CHERRY_PICK_HEAD']
  const residual = residualPaths.find((path) =>
    exists(join(options.workspacePath, repositoryWorkspacePathSuffix(options.repositoryName), path)),
  )
  if (residual) {
    throw new WorkspaceIdentityMismatchError(
      `Repository ${options.repositoryName} has residual Git operation state (${residual})`,
      displayRepositoryPath,
    )
  }

  const sink = workspacePrepSink(options.log)
  const commandOptions = sink ? { onLine: (line: string) => sink.log.write(sink.source, line) } : undefined
  const repositoryPath = repositoryWorkspacePath(options.workspacePath, options.repositoryName)
  const branch = await runCommand(
    'git',
    ['-C', repositoryPath, 'rev-parse', '--abbrev-ref', 'HEAD'],
    '.',
    options.signal,
    undefined,
    commandOptions,
  )
  if (branch.exitCode !== 0) {
    throw new WorkspaceIdentityMismatchError(
      `Repository ${displayRepositoryPath} branch probe failed: ${sanitizeWorkspaceDiagnostic(branch.stderr || branch.stdout || `exit ${branch.exitCode}`, repositoryPath, displayRepositoryPath)}`,
      displayRepositoryPath,
    )
  }

  const status = await runCommand(
    'git',
    ['-C', repositoryPath, 'status', '--porcelain'],
    '.',
    options.signal,
    undefined,
    commandOptions,
  )
  if (status.exitCode !== 0) {
    throw new WorkspaceIdentityMismatchError(
      `Repository ${displayRepositoryPath} status probe failed: ${sanitizeWorkspaceDiagnostic(status.stderr || status.stdout || `exit ${status.exitCode}`, repositoryPath, displayRepositoryPath)}`,
      displayRepositoryPath,
    )
  }
  if (status.stdout.trim() !== '') {
    const reset = await runCommand(
      'git',
      ['-C', repositoryPath, 'reset', '--hard', options.runBranch],
      '.',
      options.signal,
      undefined,
      commandOptions,
    )
    if (reset.exitCode !== 0) {
      throw new WorkspaceIdentityMismatchError(
        `Repository ${displayRepositoryPath} reset failed: ${sanitizeWorkspaceDiagnostic(reset.stderr || reset.stdout || `exit ${reset.exitCode}`, repositoryPath, displayRepositoryPath)}`,
        displayRepositoryPath,
      )
    }
    const clean = await runCommand(
      'git',
      ['-C', repositoryPath, 'clean', '-fd'],
      '.',
      options.signal,
      undefined,
      commandOptions,
    )
    if (clean.exitCode !== 0) {
      throw new WorkspaceIdentityMismatchError(
        `Repository ${displayRepositoryPath} clean failed: ${sanitizeWorkspaceDiagnostic(clean.stderr || clean.stdout || `exit ${clean.exitCode}`, repositoryPath, displayRepositoryPath)}`,
        displayRepositoryPath,
      )
    }
  }
  if (branch.stdout.trim() !== options.runBranch) {
    const checkout = await runCommand(
      'git',
      ['-C', repositoryPath, 'checkout', options.runBranch],
      '.',
      options.signal,
      undefined,
      commandOptions,
    )
    if (checkout.exitCode !== 0) {
      throw new WorkspaceIdentityMismatchError(
        `Repository ${displayRepositoryPath} checkout of ${options.runBranch} failed: ${sanitizeWorkspaceDiagnostic(checkout.stderr || checkout.stdout || `exit ${checkout.exitCode}`, repositoryPath, displayRepositoryPath)}`,
        displayRepositoryPath,
      )
    }
  }

  const finalBranch = await runCommand(
    'git',
    ['-C', repositoryPath, 'rev-parse', '--abbrev-ref', 'HEAD'],
    '.',
    options.signal,
    undefined,
    commandOptions,
  )
  const finalStatus = await runCommand(
    'git',
    ['-C', repositoryPath, 'status', '--porcelain'],
    '.',
    options.signal,
    undefined,
    commandOptions,
  )
  if (
    finalBranch.exitCode !== 0 ||
    finalStatus.exitCode !== 0 ||
    finalBranch.stdout.trim() !== options.runBranch ||
    finalStatus.stdout.trim() !== ''
  ) {
    throw new WorkspaceIdentityMismatchError(
      `Repository ${displayRepositoryPath} failed run-branch health check for ${options.runBranch}`,
      displayRepositoryPath,
    )
  }
}

function repositoryWorkspacePathSuffix(repositoryName: string): string {
  return join('REPOS', repositoryName)
}

export interface IssueWorkspaceCloneOptions {
  runnerRoot: string
  projectId: string
  workspaceName: string
  repositoryName: string
  gitUrl: string
  baseBranch: string
  runBranch: string
  registry: NamedWorkspaceRegistry
  signal: AbortSignal
  now?: () => Date
  log?: TaskLogger | null
}

export async function materializeIssueWorkspace(
  options: IssueWorkspaceCloneOptions,
): Promise<NamedWorkspaceMaterializeResult> {
  const workspacePath = namedWorkspacePath(options.runnerRoot, options.projectId, options.workspaceName)
  let created = false
  await withManagedWorkspaceHandle(options.runnerRoot, workspacePath, false, async (managedWorkspacePath) => {
    const existed = exists(managedWorkspacePath)
    if (!existed) created = true
    const existingMarker = await readNamedWorkspaceMarker(managedWorkspacePath)
    if (
      (existed && !existingMarker) ||
      (existingMarker &&
        (existingMarker.projectId !== options.projectId || existingMarker.workspaceName !== options.workspaceName))
    ) {
      throw new WorkspaceIdentityMismatchError(`Named workspace marker mismatch at ${workspacePath}`, workspacePath)
    }
    if (existed && exists(join(managedWorkspacePath, '.git'))) {
      throw new WorkspaceIdentityMismatchError(
        `Named workspace ${workspacePath} contains an obsolete root-level Git checkout; expected REPOS/${options.repositoryName}`,
        workspacePath,
      )
    }
    const conflictingRepository = existingMarker?.repositories.find(
      (repository) => repository.name === options.repositoryName && repository.gitUrl.trim() !== options.gitUrl.trim(),
    )
    if (conflictingRepository) {
      throw new WorkspaceIdentityMismatchError(
        `Named workspace repository '${options.repositoryName}' origin does not match the requested repository`,
        workspacePath,
      )
    }

    try {
      await Promise.all([
        ensureDir(join(managedWorkspacePath, '.mohist')),
        ensureDir(join(managedWorkspacePath, 'REPOS')),
        ensureDir(join(managedWorkspacePath, 'PLANS')),
        ensureDir(join(managedWorkspacePath, 'RESEARCH')),
        ensureDir(join(managedWorkspacePath, '.scratch')),
      ])
      await ensureIssueWorkspaceRepository({
        workspacePath: managedWorkspacePath,
        displayPath: workspacePath,
        repositoryName: options.repositoryName,
        gitUrl: options.gitUrl,
        baseBranch: options.baseBranch,
        runBranch: options.runBranch,
        signal: options.signal,
        log: options.log,
      })

      const repositories = [
        ...(existingMarker?.repositories.filter((repository) => repository.name !== options.repositoryName) ?? []),
        { name: options.repositoryName, gitUrl: options.gitUrl },
      ]
      const marker: NamedWorkspaceMarker = {
        projectId: options.projectId,
        workspaceName: options.workspaceName,
        repositories,
      }
      await currentRunnerFileSystem().writeText(
        join(managedWorkspacePath, MARKER_RELATIVE_PATH),
        JSON.stringify(marker, null, 2),
      )
    } catch (error) {
      if (created) await deleteDirectory(workspacePath).catch(() => {})
      throw error
    }
  })
  await options.registry.register({
    projectId: options.projectId,
    workspaceName: options.workspaceName,
    workspacePath,
  })
  return { path: workspacePath, created }
}

// Thrown when the server refused the materialization report because
// another runner already owns the workspace home (first writer wins).
export class WorkspaceHomeClaimedError extends Error {
  readonly kind = 'workspace-home-claimed'
  constructor(message: string) {
    super(message)
    this.name = 'WorkspaceHomeClaimedError'
  }
}

// Owns the materialize -> report -> registry lifecycle for a named
// workspace. The server report is the home claim: 409
// `workspace_home_claimed` means another runner won the race, this
// runner yields (deleting only a directory it created this attempt) and
// the dispatch fails; job retry then routes to the home runner via
// admission affinity.
export class NamedWorkspaceManager {
  constructor(
    private readonly runnerRoot: string,
    private readonly registry: NamedWorkspaceRegistry,
    private readonly connection: ServerConnection,
    private readonly now: () => Date = () => new Date(),
  ) {}

  async materializeForIssue(
    projectId: string,
    workspaceName: string,
    repositoryName: string,
    gitUrl: string,
    baseBranch: string,
    signal: AbortSignal,
  ): Promise<NamedWorkspaceMaterializeResult> {
    const runBranch = `mohist/ws-${workspaceName}`
    const result = await materializeIssueWorkspace({
      runnerRoot: this.runnerRoot,
      projectId,
      workspaceName,
      repositoryName,
      gitUrl,
      baseBranch,
      runBranch,
      registry: this.registry,
      signal,
      now: this.now,
    })
    try {
      await this.connection.reportWorkspaceMaterialized(projectId, workspaceName, result.path, signal)
    } catch (error) {
      if (error instanceof WorkspaceHomeClaimedError) {
        if (result.created) {
          await deleteDirectory(result.path).catch(() => {})
          await this.registry.remove(`ws:${projectId}:${workspaceName}`).catch(() => {})
        }
        throw error
      }
      throw error
    }
    return result
  }

  async materialize(
    projectId: string,
    workspaceName: string,
    repositories: readonly NamedWorkspaceRepository[],
    signal: AbortSignal,
  ): Promise<NamedWorkspaceMaterializeResult> {
    const result = await materializeNamedWorkspace({
      runnerRoot: this.runnerRoot,
      projectId,
      workspaceName,
      repositories,
      registry: this.registry,
      now: this.now,
    })
    try {
      await this.connection.reportWorkspaceMaterialized(projectId, workspaceName, result.path, signal)
    } catch (error) {
      if (error instanceof WorkspaceHomeClaimedError) {
        if (result.created) {
          await deleteDirectory(result.path).catch(() => {})
          await this.registry.remove(`ws:${projectId}:${workspaceName}`).catch(() => {})
        }
        throw error
      }
      throw error
    }
    return result
  }
}
