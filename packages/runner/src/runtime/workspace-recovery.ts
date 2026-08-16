import { isAbsolute, relative, resolve } from 'node:path'
import type {
  DispatchWorkItem,
  WorkflowTaskCleanupLease,
  WorkflowTaskCleanupOperation,
  WorkflowTaskSourceAdoption,
  WorkflowTaskSourceAdoptionRequest,
  WorkflowTaskExecutionIdentity,
} from '../core/types.js'
import { git, type GitRunner } from './git-probe.js'
import { readMarker, type IssueWorkspaceMarker } from './workspace-identity.js'
import { isUnderRunnerRoot } from './workspace-query.js'
import type { WorkspaceRegistry, WorkspaceRegistryEntry } from './workspace-registry.js'
import { currentRunnerFileSystem } from '../system/filesystem.js'

export interface WorkspaceRecoveryClock {
  now(): Date
}

export interface ScopedCleanupResult {
  operation: WorkflowTaskCleanupOperation
  rejected: boolean
}

export interface SourceAdoptionResult {
  operation: WorkflowTaskSourceAdoption
  rejected: boolean
}

const defaultClock: WorkspaceRecoveryClock = { now: () => new Date() }

/**
 * Performs only the mutations named by a server-issued cleanup lease. The
 * executor deliberately has no git cleanup primitive: generated files are
 * removed as individual paths, and all identity checks run again before each
 * deletion so a rebind or marker replacement cannot redirect the operation.
 */
export class ScopedWorkspaceCleanup {
  private readonly results = new Map<string, ScopedCleanupResult>()

  constructor(
    private readonly runnerRoot: string,
    private readonly registry: Pick<WorkspaceRegistry, 'get'>,
    private readonly clock: WorkspaceRecoveryClock = defaultClock,
  ) {}

  async execute(lease: WorkflowTaskCleanupLease, workspacePath: string): Promise<ScopedCleanupResult> {
    const prior = this.results.get(lease.operationId)
    if (prior) return cloneCleanupResult(prior)

    const removedPaths: string[] = []
    const rejected = await this.validateLease(lease, workspacePath, 0, null)
    if (rejected) {
      const result = this.result(lease, false, false, removedPaths, rejected)
      this.results.set(lease.operationId, result)
      return cloneCleanupResult(result)
    }

    for (const relativePath of lease.cleanupScope) {
      const refusal = await this.validateLease(lease, workspacePath, removedPaths.length, relativePath)
      if (refusal) {
        const result = this.result(lease, removedPaths.length > 0, false, removedPaths, refusal)
        this.results.set(lease.operationId, result)
        return cloneCleanupResult(result)
      }

      const path = resolve(workspacePath, relativePath)
      if (!currentRunnerFileSystem().exists(path)) continue
      const info = await currentRunnerFileSystem().lstat(path)
      // Symlinks are removed as links, never followed. The marker itself is
      // always protected even when a malformed lease names it explicitly.
      if (info.isDirectory() && !info.isSymbolicLink()) {
        await currentRunnerFileSystem().deleteDirectory(path)
      } else {
        await currentRunnerFileSystem().deleteFile(path)
      }
      removedPaths.push(relativePath)
    }

    const result = this.result(lease, removedPaths.length > 0, false, removedPaths, 'verification-required')
    this.results.set(lease.operationId, result)
    return cloneCleanupResult(result)
  }

  private async validateLease(
    lease: WorkflowTaskCleanupLease,
    workspacePath: string,
    mutations: number,
    path: string | null,
  ): Promise<string | null> {
    if (new Date(lease.expiresAt).getTime() <= this.clock.now().getTime()) return 'cleanup-fence-expired'
    if (mutations >= lease.workBudget) return 'cleanup-work-budget-exhausted'
    const root = resolve(this.runnerRoot)
    const workspace = resolve(workspacePath)
    if (!isUnderRunnerRoot(root, workspace)) return 'workspace-outside-managed-root'

    const entry = this.registry.get(lease.identity.workflowRunId)
    if (!entry) return 'workspace-registry-binding-missing'
    if (resolve(entry.workspacePath) !== workspace) return 'workspace-registry-binding-mismatch'
    if (!entry.binding) return 'workspace-registry-binding-missing'
    if (entry.binding.runnerId !== lease.identity.runnerId || resolve(entry.binding.runnerRoot) !== root) {
      return 'workspace-registry-runner-mismatch'
    }
    if (
      entry.workspaceId !== null &&
      entry.workspaceId !== undefined &&
      entry.workspaceId !== lease.identity.workspaceId
    )
      return 'workspace-generation-mismatch'
    if (
      entry.workspaceGeneration !== null &&
      entry.workspaceGeneration !== undefined &&
      entry.workspaceGeneration !== lease.identity.workspaceGeneration
    )
      return 'workspace-generation-mismatch'

    const marker = await readMarker(workspace)
    const markerError = validateMarker(marker, lease.identity, entry)
    if (markerError) return markerError
    if (path === null) return null
    if (!isSafeRelativePath(path)) return 'cleanup-path-unsafe'
    if (path === '.mohist/workspace.json') return 'cleanup-marker-protected'
    const absolute = resolve(workspace, path)
    const rel = relative(workspace, absolute)
    if (!rel || rel.startsWith('..') || isAbsolute(rel)) return 'cleanup-path-outside-workspace'
    return null
  }

  private result(
    lease: WorkflowTaskCleanupLease,
    applied: boolean,
    clean: boolean,
    removedPaths: string[],
    reason: string | null,
  ): ScopedCleanupResult {
    return {
      rejected: !applied && removedPaths.length === 0 && reason !== 'verification-required',
      operation: {
        operationId: lease.operationId,
        fence: lease.fence,
        identity: structuredClone(lease.identity),
        applied,
        clean,
        mutations: removedPaths.length,
        removedPaths: [...removedPaths],
        reason,
        recordedAt: this.clock.now().toISOString(),
      },
    }
  }
}

/**
 * Adopts only an explicitly authorized source allowlist. Preflight probes are
 * complete before `git add`, so malformed, stale, or protected requests do
 * not touch the index or worktree. The only git mutations are path-limited
 * add and commit operations.
 */
export class AdoptTaskSourceChanges {
  private readonly results = new Map<string, SourceAdoptionResult>()

  constructor(
    private readonly runnerRoot: string,
    private readonly registry: Pick<WorkspaceRegistry, 'get'>,
    private readonly gitRunner: GitRunner = git,
    private readonly clock: WorkspaceRecoveryClock = defaultClock,
  ) {}

  async execute(
    authorization: WorkflowTaskSourceAdoptionRequest,
    workspacePath: string,
  ): Promise<SourceAdoptionResult> {
    const prior = this.results.get(authorization.operationId)
    if (prior) return cloneAdoptionResult(prior)

    const paths = normalizePaths(authorization.sourcePaths)
    const refusal =
      paths === null
        ? 'source-path-allowlist-unsafe'
        : await this.validateAuthorization(authorization, workspacePath, paths)
    if (refusal) {
      const result = this.result(authorization, false, false, null, refusal)
      this.results.set(authorization.operationId, result)
      return cloneAdoptionResult(result)
    }

    const signal = new AbortController().signal
    const status = await this.gitRunner(workspacePath, ['status', '--porcelain=v1', '-z'], signal)
    if (!status.success) {
      const result = this.result(authorization, false, false, null, 'adoption-status-probe-failed')
      this.results.set(authorization.operationId, result)
      return cloneAdoptionResult(result)
    }
    const changed = parseStatusPaths(status.stdout)
    if (changed.some((path) => !paths!.includes(path))) {
      // Unrelated changes are preserved, but a selected allowlist must name
      // real changes so adoption cannot manufacture a commit over no source.
      const selectedChanged = changed.some((path) => paths!.includes(path))
      if (!selectedChanged) {
        const result = this.result(authorization, false, false, null, 'source-paths-not-changed')
        this.results.set(authorization.operationId, result)
        return cloneAdoptionResult(result)
      }
    }

    const beforeAdd = await this.validateAuthorization(authorization, workspacePath, paths!)
    if (beforeAdd) {
      const result = this.result(authorization, false, false, null, beforeAdd)
      this.results.set(authorization.operationId, result)
      return cloneAdoptionResult(result)
    }
    const add = await this.gitRunner(workspacePath, ['add', '--', ...paths!], signal)
    if (!add.success) {
      const result = this.result(authorization, false, false, null, 'adoption-add-failed')
      this.results.set(authorization.operationId, result)
      return cloneAdoptionResult(result)
    }

    const beforeCommit = await this.validateAuthorization(authorization, workspacePath, paths!)
    if (beforeCommit) {
      // No reset/restore is attempted. The lease result is failed and the
      // operator can inspect the preserved workspace rather than risk a
      // broad rollback of source, output, or artifact files.
      const result = this.result(authorization, false, false, null, beforeCommit)
      this.results.set(authorization.operationId, result)
      return cloneAdoptionResult(result)
    }
    const commit = await this.gitRunner(
      workspacePath,
      ['commit', '-m', `Adopt task source changes (${authorization.operationId})`, '--', ...paths!],
      signal,
    )
    if (!commit.success) {
      const result = this.result(authorization, false, false, null, 'adoption-commit-failed')
      this.results.set(authorization.operationId, result)
      return cloneAdoptionResult(result)
    }
    const head = await this.gitRunner(workspacePath, ['rev-parse', 'HEAD'], signal)
    const result = head.success
      ? this.result(authorization, true, true, head.stdout.trim(), null)
      : this.result(authorization, true, false, null, 'adoption-head-probe-failed')
    this.results.set(authorization.operationId, result)
    return cloneAdoptionResult(result)
  }

  private async validateAuthorization(
    authorization: WorkflowTaskSourceAdoptionRequest,
    workspacePath: string,
    paths: string[],
  ): Promise<string | null> {
    if (!authorization.authenticated || !authorization.hasWorkflowPermission) return 'recovery-operator-unauthorized'
    const root = resolve(this.runnerRoot)
    const workspace = resolve(workspacePath)
    if (!isUnderRunnerRoot(root, workspace)) return 'workspace-outside-managed-root'
    const entry = this.registry.get(authorization.identity.workflowRunId)
    if (!entry || resolve(entry.workspacePath) !== workspace) return 'workspace-registry-binding-mismatch'
    if (!entry.binding) return 'workspace-registry-binding-missing'
    if (entry.binding.runnerId !== authorization.identity.runnerId || resolve(entry.binding.runnerRoot) !== root) {
      return 'workspace-registry-runner-mismatch'
    }
    if (
      entry.workspaceId !== null &&
      entry.workspaceId !== undefined &&
      entry.workspaceId !== authorization.identity.workspaceId
    )
      return 'workspace-generation-mismatch'
    if (
      entry.workspaceGeneration !== null &&
      entry.workspaceGeneration !== undefined &&
      entry.workspaceGeneration !== authorization.identity.workspaceGeneration
    )
      return 'workspace-generation-mismatch'
    const marker = await readMarker(workspace)
    const markerError = validateMarker(marker, authorization.identity, entry)
    if (markerError) return markerError
    const protectedPaths = normalizePaths(authorization.protectedPaths ?? [])
    if (protectedPaths === null) return 'protected-path-unsafe'
    if (paths.some((path) => protectedPaths.some((protectedPath) => overlaps(path, protectedPath)))) {
      return 'source-path-overlaps-protected-scope'
    }
    return null
  }

  private result(
    request: WorkflowTaskSourceAdoptionRequest,
    accepted: boolean,
    completed: boolean,
    resultingHead: string | null,
    reason: string | null,
  ): SourceAdoptionResult {
    return {
      rejected: !accepted,
      operation: {
        operationId: request.operationId,
        fence: request.fence,
        identity: structuredClone(request.identity),
        operatorId: request.operatorId,
        sourcePaths: [...request.sourcePaths],
        accepted,
        completed,
        resultingHead,
        reason,
        recordedAt: this.clock.now().toISOString(),
      },
    }
  }
}

function validateMarker(
  marker: Partial<IssueWorkspaceMarker> | null,
  identity: WorkflowTaskExecutionIdentity,
  entry: WorkspaceRegistryEntry,
): string | null {
  if (!marker) return 'workspace-marker-missing'
  if (!identity.workspaceId || identity.workspaceGeneration === null || identity.workspaceGeneration === undefined)
    return 'workspace-identity-missing'
  if (marker.workflowRunId !== identity.workflowRunId) return 'workspace-marker-mismatch'
  if (!marker.runBranch) return 'workspace-marker-branch-missing'
  if (identity.workspaceId !== null && marker.workspaceId !== identity.workspaceId)
    return 'workspace-marker-identity-mismatch'
  if (identity.workspaceGeneration !== null && marker.workspaceGeneration !== identity.workspaceGeneration)
    return 'workspace-marker-generation-mismatch'
  if (entry.runBranch && marker.runBranch !== entry.runBranch) return 'workspace-marker-branch-mismatch'
  return null
}

function isSafeRelativePath(path: string): boolean {
  const normalized = path.trim().replaceAll('\\', '/')
  if (!normalized || normalized === '.' || normalized.startsWith('/') || normalized.includes(':')) return false
  return normalized.split('/').every((part) => part.length > 0 && part !== '.' && part !== '..')
}

function normalizePaths(paths: readonly string[]): string[] | null {
  const normalized = paths.map((path) => path.trim().replaceAll('\\', '/'))
  if (normalized.some((path) => !isSafeRelativePath(path))) return null
  return [...new Set(normalized)].sort()
}

function overlaps(left: string, right: string): boolean {
  return left === right || left.startsWith(`${right}/`) || right.startsWith(`${left}/`)
}

function parseStatusPaths(raw: string): string[] {
  const paths: string[] = []
  for (const entry of raw.split('\0')) {
    if (!entry) continue
    const path = entry.slice(3).trim() || entry.slice(2).trim()
    if (path) paths.push(path)
  }
  return [...new Set(paths)]
}

function cloneCleanupResult(value: ScopedCleanupResult): ScopedCleanupResult {
  return { rejected: value.rejected, operation: structuredClone(value.operation) }
}

function cloneAdoptionResult(value: SourceAdoptionResult): SourceAdoptionResult {
  return { rejected: value.rejected, operation: structuredClone(value.operation) }
}
