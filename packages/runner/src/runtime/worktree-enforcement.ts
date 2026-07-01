import { rm, stat } from "node:fs/promises"
import { isAbsolute, resolve } from "node:path"
import type { ActionContext, ActionResult, JsonObject, RenderedWorkItem, WorkItemResult } from "../core/types.js"
import { runCommand } from "../system/process.js"
import type { AcpSessionManager, SharedAcpConnection } from "./acp-connection.js"
import type { ServerConnection } from "../server/connection.js"
import { acpAgentAction } from "../actions/acp-agent.js"
import {
  buildCleanupWith,
  isAgentBackedTask,
  resolveMaxCleanupAttempts,
  type WorktreeSnapshot,
} from "./worktree-cleanup.js"
import { git } from "./git-probe.js"

export const DEFAULT_STALE_INDEX_LOCK_MS = 60_000

export interface DirtyWorktreeEvidence {
  kind: "dirty-worktree"
  staged: string[]
  unstaged: string[]
  untracked: string[]
  cleanupAttempts: number
}

export type GitIndexLockRecovery =
  | { status: "ok"; cleared?: boolean; lockPath?: string; ageMs?: number }
  | { status: "blocked"; reason: string; lockPath?: string; ageMs?: number }

export class WorktreeProbeError extends Error {
  constructor(message: string, public readonly exitCode: number | null) {
    super(message)
    this.name = "WorktreeProbeError"
  }
}

export type CleanupAgentAction = (context: ActionContext) => Promise<ActionResult>

type LockHolderProbe = (workDir: string, lockPath: string, signal: AbortSignal) => Promise<{ held: boolean; detail?: string }>

export type ContextParts = {
  sessionManager: AcpSessionManager
  acpConnection: SharedAcpConnection | null
  connection: ServerConnection
}

let cleanupAgentAction: CleanupAgentAction = acpAgentAction
let lockHolderProbe: LockHolderProbe = defaultLockHolderProbe

export { cleanupAgentAction }

export function setCleanupAgentActionForTest(handler: CleanupAgentAction | null) {
  cleanupAgentAction = handler ?? acpAgentAction
}

export function setExecutorLockHolderProbeForTest(probe: LockHolderProbe | null) {
  lockHolderProbe = probe ?? defaultLockHolderProbe
}

export function resolveStaleIndexLockMs(variables: JsonObject): number {
  const candidate = variables["runner"]
  if (candidate && typeof candidate === "object" && !Array.isArray(candidate)) {
    const cleanup = (candidate as JsonObject)["cleanup"]
    if (cleanup && typeof cleanup === "object" && !Array.isArray(cleanup)) {
      const value = (cleanup as JsonObject)["staleIndexLockMs"]
      if (typeof value === "number" && Number.isFinite(value) && value >= 0) return Math.floor(value)
      if (typeof value === "string") {
        const parsed = Number(value)
        if (Number.isFinite(parsed) && parsed >= 0) return Math.floor(parsed)
      }
    }
  }
  return DEFAULT_STALE_INDEX_LOCK_MS
}

export async function recoverStaleIndexLock(workDir: string, variables: JsonObject, signal: AbortSignal): Promise<GitIndexLockRecovery> {
  const lockPathResult = await git(workDir, ["rev-parse", "--git-path", "index.lock"], signal)
  if (!lockPathResult.success) {
    return {
      status: "blocked",
      reason: `git index lock path probe failed: ${lockPathResult.combinedOutput || `exit ${lockPathResult.exitCode}`}`,
    }
  }

  const rawLockPath = lockPathResult.stdout.trim()
  if (!rawLockPath) {
    return { status: "blocked", reason: "git index lock path probe returned an empty path" }
  }

  const lockPath = isAbsolute(rawLockPath) ? rawLockPath : resolve(workDir, rawLockPath)
  let info: Awaited<ReturnType<typeof stat>>
  try {
    info = await stat(lockPath)
  } catch (error) {
    if (isNotFoundError(error)) return { status: "ok", lockPath }
    return { status: "blocked", reason: `git index lock stat failed: ${errorMessage(error)}`, lockPath }
  }

  const ageMs = Math.max(0, Date.now() - info.mtimeMs)
  const staleMs = resolveStaleIndexLockMs(variables)
  if (ageMs < staleMs) {
    return {
      status: "blocked",
      reason: `git index lock is fresh (${Math.floor(ageMs)}ms old, stale threshold ${staleMs}ms)`,
      lockPath,
      ageMs,
    }
  }

  const holder = await lockHolderProbe(workDir, lockPath, signal)
  if (holder.held) {
    return {
      status: "blocked",
      reason: `git index lock is still held${holder.detail ? `: ${holder.detail}` : ""}`,
      lockPath,
      ageMs,
    }
  }

  try {
    await rm(lockPath, { force: true })
  } catch (error) {
    return { status: "blocked", reason: `failed to remove stale git index lock: ${errorMessage(error)}`, lockPath, ageMs }
  }

  return { status: "ok", cleared: true, lockPath, ageMs }
}

export async function defaultLockHolderProbe(workDir: string, lockPath: string, signal: AbortSignal): Promise<{ held: boolean; detail?: string }> {
  try {
    const result = await runCommand("lsof", [lockPath], workDir, signal)
    if (result.exitCode === 0) {
      return { held: true, detail: result.stdout.trim().split(/\r?\n/).slice(0, 3).join("; ") }
    }
  } catch (error) {
    // lsof is best-effort: if it is not installed or cannot run in the
    // host environment, the age threshold still prevents deleting fresh
    // locks while allowing stale lock recovery to make progress.
    return { held: false, detail: errorMessage(error) }
  }
  return { held: false }
}

export async function readWorktreeSnapshot(workDir: string, signal: AbortSignal): Promise<WorktreeSnapshot> {
  const inside = await git(workDir, ["rev-parse", "--is-inside-work-tree"], signal)
  if (!inside.success) {
    // Only treat the worktree as "not a git repo" (and therefore as
    // clean-by-default) when the failure is Git's standard "not a git
    // repository" message. Other failures (missing git binary, permission
    // errors, corrupted worktree) must surface to the caller so the task
    // fails with structured evidence rather than silently succeeding on
    // a never-evaluated invariant.
    const lowerStderr = (inside.stderr ?? "").toLowerCase()
    const isPlainDir = lowerStderr.includes("not a git repository")
    if (!isPlainDir) {
      throw new WorktreeProbeError(
        `git worktree probe failed: ${inside.combinedOutput || `exit ${inside.exitCode}`}`,
        inside.exitCode,
      )
    }
    return { staged: [], unstaged: [], untracked: [], isClean: true }
  }
  const staged = await git(workDir, ["diff", "--cached", "--name-only"], signal)
  const unstaged = await git(workDir, ["diff", "--name-only"], signal)
  const untracked = await git(workDir, ["ls-files", "--others", "--exclude-standard"], signal)

  if (!staged.success || !unstaged.success || !untracked.success) {
    throw new WorktreeProbeError(
      `git worktree status check failed: ` +
      `staged(exit=${staged.exitCode}), ` +
      `unstaged(exit=${unstaged.exitCode}), ` +
      `untracked(exit=${untracked.exitCode})`,
      null,
    )
  }

  const stagedList = parseFileList(staged.stdout)
  const unstagedList = parseFileList(unstaged.stdout)
  const untrackedList = parseFileList(untracked.stdout)
  return {
    staged: stagedList,
    unstaged: unstagedList,
    untracked: untrackedList,
    isClean: stagedList.length === 0 && unstagedList.length === 0 && untrackedList.length === 0,
  }
}

export function parseFileList(stdout: string): string[] {
  return [...new Set(stdout.split(/\r?\n/).map((line) => line.trim()).filter(Boolean))]
}

export function dirtyWorktreeFailure(
  result: WorkItemResult,
  snapshot: WorktreeSnapshot,
  cleanupAttempts: number,
  detail?: string,
): WorkItemResult {
  const evidence: DirtyWorktreeEvidence = {
    kind: "dirty-worktree",
    staged: [...snapshot.staged],
    unstaged: [...snapshot.unstaged],
    untracked: [...snapshot.untracked],
    cleanupAttempts,
  }
  const baseMessage = result.message?.trim() || "Task completed by action but worktree remained dirty"
  const summary = formatDirtyWorktreeSummary(evidence)
  const message = detail
    ? `${baseMessage}; ${detail}; ${summary}`.slice(0, 4000)
    : `${baseMessage}; ${summary}`.slice(0, 4000)
  const existingOutput = result.output ? safeParseJson(result.output) : null
  const output = JSON.stringify({
    ...(existingOutput ?? {}),
    kind: "dirty-worktree",
    staged: evidence.staged,
    unstaged: evidence.unstaged,
    untracked: evidence.untracked,
    cleanupAttempts: evidence.cleanupAttempts,
  })
  return {
    ...result,
    status: "failed",
    message,
    output,
    cleanupAttempts,
  }
}

export function gitIndexLockFailure(
  result: WorkItemResult,
  snapshot: WorktreeSnapshot,
  cleanupAttempts: number,
  recovery: Extract<GitIndexLockRecovery, { status: "blocked" }>,
): WorkItemResult {
  const existingOutput = result.output ? safeParseJson(result.output) : null
  const message = `${result.message?.trim() || "Task completed by action but Git index is locked"}; git index lock blocked cleanup: ${recovery.reason}`.slice(0, 4000)
  return {
    ...result,
    status: "failed",
    message,
    output: JSON.stringify({
      ...(existingOutput ?? {}),
      kind: "git-index-lock",
      lockPath: recovery.lockPath,
      lockAgeMs: recovery.ageMs,
      reason: recovery.reason,
      staged: snapshot.staged,
      unstaged: snapshot.unstaged,
      untracked: snapshot.untracked,
      cleanupAttempts,
    }),
    cleanupAttempts,
  }
}

export function formatDirtyWorktreeSummary(evidence: DirtyWorktreeEvidence): string {
  const parts: string[] = []
  parts.push(`worktree dirty after ${evidence.cleanupAttempts} cleanup attempt(s)`)
  parts.push(`staged=[${evidence.staged.join(", ")}]`)
  parts.push(`unstaged=[${evidence.unstaged.join(", ")}]`)
  parts.push(`untracked=[${evidence.untracked.join(", ")}]`)
  return parts.join("; ")
}

export function worktreeProbeFailure(work: RenderedWorkItem, error: WorktreeProbeError): WorkItemResult {
  const evidence: DirtyWorktreeEvidence = {
    kind: "dirty-worktree",
    staged: [],
    unstaged: [],
    untracked: [],
    cleanupAttempts: 0,
    ...({
      probeError: error.message,
      probeExitCode: error.exitCode,
    } as unknown as Pick<DirtyWorktreeEvidence, never>),
  } as DirtyWorktreeEvidence
  const label = work.title?.trim() || work.uses || work.workId
  const message = `git worktree probe failed for ${label}: ${error.message}`.slice(0, 4000)
  return {
    status: "failed",
    message,
    output: JSON.stringify(evidence),
    cleanupAttempts: 0,
  }
}

export function mergeCleanupCount(result: WorkItemResult, attempts: number): WorkItemResult {
  return { ...result, cleanupAttempts: attempts }
}

/**
 * Task completion invariant: the worktree must be clean before
 * `executeOne` reports completion. For agent-backed tasks the
 * executor runs a bounded cleanup loop that sends a constrained
 * follow-up prompt to the same agent session, instructing the
 * agent to commit task-related changes or revert unrelated ones.
 * Deterministic actions that leave a dirty worktree fail
 * immediately with structured evidence.
 */
export async function enforceCleanWorktree(
  work: RenderedWorkItem,
  workDir: string,
  result: WorkItemResult,
  renderedWith: JsonObject | null,
  variables: JsonObject,
  signal: AbortSignal,
  cleanupAction: CleanupAgentAction,
  contextParts: ContextParts,
): Promise<WorkItemResult> {
  try {
    const agentBacked = isAgentBackedTask(work)
    const maxCleanupAttempts = resolveMaxCleanupAttempts(variables)

    let attempts = 0
    let snapshot = await readWorktreeSnapshot(workDir, signal)
    while (!snapshot.isClean) {
      if (!agentBacked) {
        return dirtyWorktreeFailure(result, snapshot, attempts)
      }
      if (attempts >= maxCleanupAttempts) {
        return dirtyWorktreeFailure(result, snapshot, attempts)
      }
      const lockRecovery = await recoverStaleIndexLock(workDir, variables, signal)
      if (lockRecovery.status === "blocked") {
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
      )
      if (cleanupResult !== "ok") {
        return cleanupResult
      }
      snapshot = await readWorktreeSnapshot(workDir, signal)
    }

    if (attempts === 0) return result
    return mergeCleanupCount(result, attempts)
  } catch (error) {
    if (error instanceof WorktreeProbeError) {
      return worktreeProbeFailure(work, error)
    }
    throw error
  }
}

export async function runAgentCleanupAttempt(
  work: RenderedWorkItem,
  workDir: string,
  renderedWith: JsonObject | null,
  variables: JsonObject,
  snapshot: WorktreeSnapshot,
  attempt: number,
  signal: AbortSignal,
  cleanupAction: CleanupAgentAction,
  contextParts: ContextParts,
): Promise<WorkItemResult | "ok"> {
  const cleanupContext: ActionContext = {
    ...baseContext(work, variables, signal, contextParts),
    workDir,
    workType: "task",
    with: buildCleanupWith(work, renderedWith, snapshot, attempt),
  }

  let result: ActionResult
  try {
    result = await cleanupAction(cleanupContext)
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error)
    return dirtyWorktreeFailure(mergeCleanupCount({ status: "completed" }, attempt - 1), snapshot, attempt, `Cleanup attempt ${attempt} threw: ${message}`)
  }
  if (result.status !== "success" && result.status !== "completed") {
    return dirtyWorktreeFailure(mergeCleanupCount({ status: "completed" }, attempt - 1), snapshot, attempt, `Cleanup attempt ${attempt} failed: ${result.message ?? result.status}`)
  }
  return "ok"
}

function baseContext(
  work: RenderedWorkItem,
  variables: JsonObject,
  signal: AbortSignal,
  contextParts: ContextParts,
): Omit<ActionContext, "with" | "workDir"> {
  return {
    workflowRunId: work.workflowRunId,
    workId: work.workId,
    workType: work.workType,
    stage: work.stage,
    title: work.title,
    uses: work.uses,
    variables,
    signal,
    recovery: work.recovery,
    projectId: work.projectId,
    issueNumber: work.issueNumber,
    ownerKind: work.ownerKind,
    agentSessionId: work.agentSessionId,
    acpSessionManager: contextParts.sessionManager,
    acpConnection: contextParts.acpConnection,
    serverConnection: contextParts.connection,
    writeVars: async (vars) => contextParts.connection.patchRunVars(work.workflowRunId, vars, signal),
  }
}

function safeParseJson(value: string): JsonObject | null {
  try {
    const parsed = JSON.parse(value) as unknown
    return parsed && typeof parsed === "object" && !Array.isArray(parsed) ? (parsed as JsonObject) : null
  } catch {
    return null
  }
}

function errorMessage(error: unknown): string {
  if (error instanceof Error) return error.message
  if (error && typeof error === "object" && "name" in error && "message" in error) {
    return String((error as { message: unknown }).message)
  }
  return String(error)
}

function isNotFoundError(error: unknown): boolean {
  return Boolean(error && typeof error === "object" && "code" in error && (error as { code?: unknown }).code === "ENOENT")
}