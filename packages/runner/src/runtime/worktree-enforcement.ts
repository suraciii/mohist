import { isAbsolute, resolve } from 'node:path'
import type { ActionResult, JsonObject, DispatchWorkItem, WorkItemResult } from '../core/types.js'
import type { ActionHost } from '../actions/host.js'
import type { ActionCapabilitySet } from '../actions/manifest.js'
import { isActionFailure } from '../actions/action-result.js'
import { numberInput, objectInput } from '../core/json.js'
import { errorMessage, isNotFoundError } from '../core/errors.js'
import { runCommand } from '../system/process.js'
import { currentRunnerFileSystem, currentRunnerResources } from '../system/filesystem.js'
import {
  buildCleanupWith,
  isAgentBackedTask,
  resolveMaxCleanupAttempts,
  type WorktreeSnapshot,
} from './worktree-cleanup.js'
import { git } from './git-probe.js'
import type { TaskLogger } from './task-log.js'

export const CLEANUP_SOURCE = 'cleanup'

function cleanupSink(log: TaskLogger | null | undefined) {
  return log ? { log, source: CLEANUP_SOURCE } : undefined
}

export const DEFAULT_STALE_INDEX_LOCK_MS = 60_000

export interface DirtyWorktreeEvidence {
  kind: 'dirty-worktree'
  staged: string[]
  unstaged: string[]
  untracked: string[]
  cleanupAttempts: number
}

export type GitIndexLockRecovery =
  | { status: 'ok'; cleared?: boolean; lockPath?: string; ageMs?: number }
  | { status: 'blocked'; reason: string; lockPath?: string; ageMs?: number }

export class WorktreeProbeError extends Error {
  constructor(
    message: string,
    public readonly exitCode: number | null,
  ) {
    super(message)
    this.name = 'WorktreeProbeError'
  }
}

export type CleanupAgentAction = (host: ActionHost, withInput: JsonObject) => Promise<ActionResult>

export type LockHolderProbe = (
  workDir: string,
  lockPath: string,
  signal: AbortSignal,
) => Promise<{ held: boolean; detail?: string }>

export type ContextParts = {
  buildHost: (work: DispatchWorkItem, signal: AbortSignal, workDir: string, cleanupAttempt?: number) => ActionHost
}

const defaultNow = () => Date.now()

export function resolveCleanupAgentAction(originalAction: CleanupAgentAction): CleanupAgentAction {
  return currentRunnerResources()?.cleanupAgentAction ?? originalAction
}

export function resolveStaleIndexLockMs(variables: JsonObject): number {
  const cleanup = objectInput(objectInput(variables, 'runner'), 'cleanup')
  const staleMs = numberInput(cleanup, 'staleIndexLockMs')
  return staleMs !== undefined && staleMs >= 0 ? Math.floor(staleMs) : DEFAULT_STALE_INDEX_LOCK_MS
}

export async function recoverStaleIndexLock(
  workDir: string,
  variables: JsonObject,
  signal: AbortSignal,
  log: TaskLogger | null = null,
): Promise<GitIndexLockRecovery> {
  const sink = cleanupSink(log)
  const lockPathResult = await git(
    workDir,
    ['rev-parse', '--git-path', 'index.lock'],
    signal,
    sink ? { sink } : undefined,
  )
  if (!lockPathResult.success) {
    return {
      status: 'blocked',
      reason: `git index lock path probe failed: ${lockPathResult.combinedOutput || `exit ${lockPathResult.exitCode}`}`,
    }
  }

  const rawLockPath = lockPathResult.stdout.trim()
  if (!rawLockPath) {
    return { status: 'blocked', reason: 'git index lock path probe returned an empty path' }
  }

  const lockPath = isAbsolute(rawLockPath) ? rawLockPath : resolve(workDir, rawLockPath)
  let info: Awaited<ReturnType<ReturnType<typeof currentRunnerFileSystem>['stat']>>
  try {
    info = await currentRunnerFileSystem().stat(lockPath)
  } catch (error) {
    if (isNotFoundError(error)) return { status: 'ok', lockPath }
    return { status: 'blocked', reason: `git index lock stat failed: ${errorMessage(error)}`, lockPath }
  }

  const ageMs = Math.max(0, (currentRunnerResources()?.worktreeClock ?? defaultNow)() - info.mtimeMs)
  const staleMs = resolveStaleIndexLockMs(variables)
  if (ageMs < staleMs) {
    return {
      status: 'blocked',
      reason: `git index lock is fresh (${Math.floor(ageMs)}ms old, stale threshold ${staleMs}ms)`,
      lockPath,
      ageMs,
    }
  }

  const holder = await (currentRunnerResources()?.executorLockHolderProbe ?? defaultLockHolderProbe)(
    workDir,
    lockPath,
    signal,
  )
  if (holder.held) {
    return {
      status: 'blocked',
      reason: `git index lock is still held${holder.detail ? `: ${holder.detail}` : ''}`,
      lockPath,
      ageMs,
    }
  }

  try {
    await currentRunnerFileSystem().deleteFile(lockPath)
  } catch (error) {
    return {
      status: 'blocked',
      reason: `failed to remove stale git index lock: ${errorMessage(error)}`,
      lockPath,
      ageMs,
    }
  }

  return { status: 'ok', cleared: true, lockPath, ageMs }
}

export async function defaultLockHolderProbe(
  workDir: string,
  lockPath: string,
  signal: AbortSignal,
): Promise<{ held: boolean; detail?: string }> {
  try {
    const result = await runCommand('lsof', [lockPath], workDir, signal)
    if (result.exitCode === 0) {
      return { held: true, detail: result.stdout.trim().split(/\r?\n/).slice(0, 3).join('; ') }
    }
  } catch (error) {
    // lsof is best-effort: if it is not installed or cannot run in the
    // host environment, the age threshold still prevents deleting fresh
    // locks while allowing stale lock recovery to make progress.
    return { held: false, detail: errorMessage(error) }
  }
  return { held: false }
}

export async function readWorktreeSnapshot(
  workDir: string,
  signal: AbortSignal,
  log: TaskLogger | null = null,
): Promise<WorktreeSnapshot> {
  const sink = cleanupSink(log)
  const inside = await git(workDir, ['rev-parse', '--is-inside-work-tree'], signal, sink ? { sink } : undefined)
  if (!inside.success) {
    // Only treat the worktree as "not a git repo" (and therefore as
    // clean-by-default) when the failure is Git's standard "not a git
    // repository" message. Other failures (missing git binary, permission
    // errors, corrupted worktree) must surface to the caller so the task
    // fails with structured evidence rather than silently succeeding on
    // a never-evaluated invariant.
    const lowerStderr = (inside.stderr ?? '').toLowerCase()
    const isPlainDir = lowerStderr.includes('not a git repository')
    if (!isPlainDir) {
      throw new WorktreeProbeError(
        `git worktree probe failed: ${inside.combinedOutput || `exit ${inside.exitCode}`}`,
        inside.exitCode,
      )
    }
    return { staged: [], unstaged: [], untracked: [], isClean: true }
  }
  const checks = await Promise.all([
    git(workDir, ['diff', '--cached', '--name-only'], signal, sink ? { sink } : undefined).then((result) => ({
      name: 'staged',
      result,
    })),
    git(workDir, ['diff', '--name-only'], signal, sink ? { sink } : undefined).then((result) => ({
      name: 'unstaged',
      result,
    })),
    git(workDir, ['ls-files', '--others', '--exclude-standard'], signal, sink ? { sink } : undefined).then(
      (result) => ({ name: 'untracked', result }),
    ),
  ])

  if (checks.some(({ result }) => !result.success)) {
    throw new WorktreeProbeError(
      `git worktree status check failed: ${checks.map(({ name, result }) => `${name}(exit=${result.exitCode})`).join(', ')}`,
      null,
    )
  }

  const stagedList = parseFileList(checks[0].result.stdout)
  const unstagedList = parseFileList(checks[1].result.stdout)
  const untrackedList = parseFileList(checks[2].result.stdout)
  return {
    staged: stagedList,
    unstaged: unstagedList,
    untracked: untrackedList,
    isClean: stagedList.length === 0 && unstagedList.length === 0 && untrackedList.length === 0,
  }
}

export function parseFileList(stdout: string): string[] {
  return [
    ...new Set(
      stdout
        .split(/\r?\n/)
        .map((line) => line.trim())
        .filter(Boolean),
    ),
  ]
}

export function dirtyWorktreeFailure(
  result: WorkItemResult,
  snapshot: WorktreeSnapshot,
  cleanupAttempts: number,
  detail?: string,
): WorkItemResult {
  const evidence: DirtyWorktreeEvidence = {
    kind: 'dirty-worktree',
    staged: [...snapshot.staged],
    unstaged: [...snapshot.unstaged],
    untracked: [...snapshot.untracked],
    cleanupAttempts,
  }
  const baseMessage = result.message?.trim() || 'Task completed by action but worktree remained dirty'
  const summary = formatDirtyWorktreeSummary(evidence)
  const message = detail
    ? `${baseMessage}; ${detail}; ${summary}`.slice(0, 4000)
    : `${baseMessage}; ${summary}`.slice(0, 4000)
  return {
    ...result,
    status: 'failed',
    message,
    error: { code: 'worktree-dirty', message },
    cleanupAttempts,
  }
}

export function gitIndexLockFailure(
  result: WorkItemResult,
  snapshot: WorktreeSnapshot,
  cleanupAttempts: number,
  recovery: Extract<GitIndexLockRecovery, { status: 'blocked' }>,
): WorkItemResult {
  const message =
    `${result.message?.trim() || 'Task completed by action but Git index is locked'}; git index lock blocked cleanup: ${recovery.reason}`.slice(
      0,
      4000,
    )
  return {
    ...result,
    status: 'failed',
    message,
    error: { code: 'git-index-locked', message },
    cleanupAttempts,
  }
}

export function isWorkflowTask(work: Pick<DispatchWorkItem, 'workType' | 'ownerKind'>): boolean {
  return work.workType === 'task' && (work.ownerKind ?? 'workflow').trim().toLowerCase() !== 'agent-job'
}

function scopedCleanupRequired(result: WorkItemResult, snapshot: WorktreeSnapshot): WorkItemResult {
  return {
    ...result,
    workspaceOutcome: 'dirty',
    workspaceReason: 'scoped-recovery-required',
    cleanupAttempts: 0,
    message: [result.message?.trim(), `scoped cleanup required; staged=[${snapshot.staged.join(', ')}]`, `unstaged=[${snapshot.unstaged.join(', ')}]`, `untracked=[${snapshot.untracked.join(', ')}]`]
      .filter(Boolean)
      .join('; ')
      .slice(0, 4000),
  }
}

function formatDirtyWorktreeSummary(evidence: DirtyWorktreeEvidence): string {
  const parts: string[] = []
  parts.push(`worktree dirty after ${evidence.cleanupAttempts} cleanup attempt(s)`)
  parts.push(`staged=[${evidence.staged.join(', ')}]`)
  parts.push(`unstaged=[${evidence.unstaged.join(', ')}]`)
  parts.push(`untracked=[${evidence.untracked.join(', ')}]`)
  return parts.join('; ')
}

export function worktreeProbeFailure(
  work: DispatchWorkItem,
  error: WorktreeProbeError,
  result: WorkItemResult = { status: 'failed' },
): WorkItemResult {
  const label = work.title?.trim() || work.uses || work.workId
  const detail = `git worktree probe failed for ${label}: ${error.message}`
  const message = [result.message?.trim(), detail].filter(Boolean).join('; ').slice(0, 4000)
  return {
    ...result,
    status: 'failed',
    message,
    error: { code: 'worktree-probe-failed', message },
    cleanupAttempts: result.cleanupAttempts ?? 0,
  }
}

export function mergeCleanupCount(result: WorkItemResult, attempts: number): WorkItemResult {
  return { ...result, cleanupAttempts: attempts }
}

/**
 * Legacy bounded cleanup helper retained for explicit recovery callers.
 * Workflow task execution records its immutable completion receipt first and
 * does not use this helper as the Action business-success gate. Any caller
 * that uses it must treat its result as separate recovery evidence.
 */
export async function enforceCleanWorktree(
  work: DispatchWorkItem,
  workDir: string,
  result: WorkItemResult,
  renderedWith: JsonObject | null,
  variables: JsonObject,
  signal: AbortSignal,
  cleanupAction: CleanupAgentAction,
  contextParts: ContextParts,
  log: TaskLogger | null = null,
): Promise<WorkItemResult> {
  // Workflow task cleanup is a recovery operation authorized by the server.
  // This legacy helper must never turn a valid Action into a failure or ask
  // an agent to discard source/output files.
  if (isWorkflowTask(work)) {
    try {
      const snapshot = await readWorktreeSnapshot(workDir, signal, log)
      return snapshot.isClean
        ? result
        : scopedCleanupRequired(result, snapshot)
    } catch (error) {
      return {
        ...result,
        workspaceOutcome: 'unconfirmed',
        workspaceReason: `cleanup-probe-failed:${errorMessage(error)}`,
      }
    }
  }

  let attempts = 0
  try {
    const agentBacked = isAgentBackedTask(work)
    const maxCleanupAttempts = resolveMaxCleanupAttempts(variables)

    let snapshot = await readWorktreeSnapshot(workDir, signal, log)
    while (!snapshot.isClean) {
      if (!agentBacked) {
        return dirtyWorktreeFailure(result, snapshot, attempts)
      }
      if (attempts >= maxCleanupAttempts) {
        return dirtyWorktreeFailure(result, snapshot, attempts)
      }
      const lockRecovery = await recoverStaleIndexLock(workDir, variables, signal, log)
      if (lockRecovery.status === 'blocked') {
        return gitIndexLockFailure(result, snapshot, attempts, lockRecovery)
      }
      attempts += 1
      const cleanupResult = await runAgentCleanupAttempt(
        work,
        workDir,
        renderedWith,
        variables,
        snapshot,
        attempts,
        signal,
        cleanupAction,
        contextParts,
        result,
      )
      if (cleanupResult !== 'ok') {
        return cleanupResult
      }
      snapshot = await readWorktreeSnapshot(workDir, signal, log)
    }

    if (attempts === 0) return result
    return mergeCleanupCount(result, attempts)
  } catch (error) {
    if (error instanceof WorktreeProbeError) {
      return worktreeProbeFailure(work, error, mergeCleanupCount(result, attempts))
    }
    const detail = `worktree cleanup failed: ${errorMessage(error)}`
    const message = [result.message?.trim(), detail].filter(Boolean).join('; ').slice(0, 4000)
    return {
      ...result,
      status: 'failed',
      message,
      error: { code: 'worktree-cleanup-failed', message },
      cleanupAttempts: attempts,
    }
  }
}

export async function runAgentCleanupAttempt(
  work: DispatchWorkItem,
  workDir: string,
  renderedWith: JsonObject | null,
  variables: JsonObject,
  snapshot: WorktreeSnapshot,
  attempt: number,
  signal: AbortSignal,
  cleanupAction: CleanupAgentAction,
  contextParts: ContextParts,
  baseResult: WorkItemResult = { status: 'completed' },
): Promise<WorkItemResult | 'ok'> {
  if (isWorkflowTask(work)) return scopedCleanupRequired(baseResult, snapshot)
  const cleanupWith = buildCleanupWith(work, renderedWith, snapshot, attempt)
  const host = contextParts.buildHost(work, signal, workDir, attempt)

  let cleanupResult: ActionResult
  try {
    cleanupResult = await cleanupAction(host, cleanupWith)
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error)
    return dirtyWorktreeFailure(
      mergeCleanupCount(baseResult, attempt - 1),
      snapshot,
      attempt,
      `Cleanup attempt ${attempt} threw: ${message}`,
    )
  }
  if (isActionFailure(cleanupResult)) {
    return dirtyWorktreeFailure(
      mergeCleanupCount(baseResult, attempt - 1),
      snapshot,
      attempt,
      `Cleanup attempt ${attempt} failed: ${cleanupResult.error.message}`,
    )
  }
  return 'ok'
}
