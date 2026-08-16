import { createHash } from 'node:crypto'
import type {
  ActionCompletion,
  ActionResult,
  CommitReceipt,
  DispatchWorkItem,
  JsonObject,
  JsonValue,
  WorkItemResult,
  WorkflowTaskCompletionBoundary,
  WorkflowTaskExecutionIdentity,
  WorkspaceOutcome,
} from '../core/types.js'
import { isActionFailure } from '../actions/action-result.js'
import { git } from './git-probe.js'
import type { TaskLogger } from './task-log.js'

export const RECEIPT_PROBE_SOURCE = 'receipt-probe'

export interface CompletionWorkspace {
  path: string
  branch: string | null
  workspaceId?: string | null
  workspaceGeneration?: string | number | null
}

export interface ActionExecutionCapture {
  actionStarted: boolean
  actionResult: ActionResult | null
  phase: string
}

export interface CommitReceiptProbeOptions {
  work: DispatchWorkItem
  identity: WorkflowTaskExecutionIdentity
  workspace: CompletionWorkspace | null
  expectedBranch: string | null
  expectedHead: string | null
  expectedTree: string | null
  signal: AbortSignal
  now?: () => Date
  log?: TaskLogger | null
}

export function isWorkflowTask(work: DispatchWorkItem): boolean {
  return work.workType === 'task' && (work.ownerKind ?? 'workflow').trim().toLowerCase() !== 'agent-job'
}

export function buildExecutionIdentity(
  work: DispatchWorkItem,
  workspace: CompletionWorkspace | null,
  runnerId = work.runnerId ?? 'unknown',
): WorkflowTaskExecutionIdentity {
  const ownerKind = (work.ownerKind ?? 'workflow').trim().toLowerCase() || 'workflow'
  const ownerId = ownerKind === 'agent-job' ? work.agentJobId ?? '' : work.workflowRunId
  const workspaceVariables = workspaceVariablesOf(work)
  return {
    workflowRunId: work.workflowRunId,
    stage: work.stage ?? null,
    taskAttemptId: work.taskRunId ?? work.workId,
    workId: work.workId,
    ownerKind,
    ownerId,
    runnerId: work.runnerId ?? runnerId,
    workspaceId:
      work.workspaceId !== undefined
        ? work.workspaceId
        : workspace?.workspaceId ?? stringField(workspaceVariables, 'id') ?? stringField(workspaceVariables, 'identity'),
    workspaceGeneration:
      work.workspaceGeneration !== undefined
        ? work.workspaceGeneration
        : workspace?.workspaceGeneration ?? scalarField(workspaceVariables, 'generation'),
  }
}

export function buildActionCompletion(
  capture: ActionExecutionCapture,
  result: WorkItemResult,
  now = () => new Date(),
): ActionCompletion {
  const actionResult = capture.actionResult
  const actionFailure = actionResult && isActionFailure(actionResult) ? actionResult.error : null
  const actionOutcome: ActionCompletion['outcome'] =
    actionResult?.outcome === 'unknown'
      ? 'unknown'
      : actionFailure
        ? 'failed'
        : capture.actionStarted
          ? 'succeeded'
          : 'failed'
  const output: JsonValue | null = actionResult && !isActionFailure(actionResult) ? actionResult.output : null
  const error = actionFailure ?? (!capture.actionStarted ? result.error ?? null : null)
  return {
    version: 1,
    actionStarted: capture.actionStarted,
    outcome: actionOutcome,
    phase: capture.phase,
    output,
    error,
    artifactUploadIds: [...(result.artifactUploadIds ?? [])],
    capturedOutputs: result.capturedOutputs ? structuredClone(result.capturedOutputs) : null,
    completedAt: now().toISOString(),
  }
}

export async function probeCommitReceipt(options: CommitReceiptProbeOptions): Promise<CommitReceipt> {
  const now = options.now ?? (() => new Date())
  const base = {
    version: 1 as const,
    identity: structuredClone(options.identity),
    expectedBranch: options.expectedBranch,
    expectedHead: options.expectedHead,
    expectedTree: options.expectedTree,
    observedBranch: null as string | null,
    observedHead: null as string | null,
    observedTree: null as string | null,
    staged: [] as string[],
    unstaged: [] as string[],
    untracked: [] as string[],
    authoritative: false,
    reason: null as string | null,
    probedAt: now().toISOString(),
  }

  if (!options.workspace?.path) return { ...base, reason: 'workspace-unavailable' }
  if (!options.identity.workspaceId || !options.workspace.workspaceId) {
    return { ...base, reason: 'workspace-identity-missing' }
  }
  if (
    options.identity.workspaceGeneration === null ||
    options.identity.workspaceGeneration === undefined ||
    options.workspace.workspaceGeneration === null ||
    options.workspace.workspaceGeneration === undefined
  ) {
    return { ...base, reason: 'workspace-generation-missing' }
  }
  if (!options.expectedBranch) return { ...base, reason: 'expected-branch-missing' }
  if (!options.expectedHead) return { ...base, reason: 'expected-head-missing' }
  if (!options.expectedTree) return { ...base, reason: 'expected-tree-missing' }

  const branch = await runGitProbe(options.workspace.path, ['rev-parse', '--abbrev-ref', 'HEAD'], options.signal, options.log)
  if (!branch.ok) return { ...base, reason: branch.reason }
  base.observedBranch = branch.value.trim() || null
  if (base.observedBranch === 'HEAD') return { ...base, reason: 'workspace-detached' }

  const head = await runGitProbe(options.workspace.path, ['rev-parse', 'HEAD'], options.signal, options.log)
  if (!head.ok) return { ...base, reason: head.reason }
  base.observedHead = head.value.trim() || null

  const tree = await runGitProbe(options.workspace.path, ['rev-parse', 'HEAD^{tree}'], options.signal, options.log)
  if (!tree.ok) return { ...base, reason: tree.reason }
  base.observedTree = tree.value.trim() || null

  const status = await runGitProbe(options.workspace.path, ['status', '--porcelain=v1', '-z'], options.signal, options.log)
  if (!status.ok) return { ...base, reason: status.reason }
  const paths = parsePorcelainStatus(status.value)
  base.staged = paths.staged
  base.unstaged = paths.unstaged
  base.untracked = paths.untracked

  const actualWorkspaceId = options.workspace.workspaceId
  const actualGeneration = options.workspace.workspaceGeneration
  if (actualWorkspaceId !== options.identity.workspaceId) return { ...base, reason: 'workspace-identity-mismatch' }
  if (actualGeneration !== options.identity.workspaceGeneration) return { ...base, reason: 'workspace-generation-mismatch' }
  if (base.observedBranch !== options.expectedBranch) return { ...base, reason: 'branch-mismatch' }
  if (base.observedHead !== options.expectedHead) return { ...base, reason: 'head-mismatch' }
  if (base.observedTree !== options.expectedTree) return { ...base, reason: 'tree-mismatch' }

  return { ...base, authoritative: true, reason: null }
}

export function arbitrateWorkspaceOutcome(receipt: CommitReceipt): { outcome: WorkspaceOutcome; reason: string | null } {
  if (!receipt.authoritative) return { outcome: 'unconfirmed', reason: receipt.reason ?? 'workspace-evidence-unavailable' }
  if (receipt.reason !== null) return { outcome: 'unconfirmed', reason: receipt.reason }
  if (!receipt.identity.workspaceId) return { outcome: 'unconfirmed', reason: 'workspace-identity-missing' }
  if (receipt.identity.workspaceGeneration === null || receipt.identity.workspaceGeneration === undefined) {
    return { outcome: 'unconfirmed', reason: 'workspace-generation-missing' }
  }
  if (!receipt.expectedBranch || receipt.observedBranch !== receipt.expectedBranch) {
    return { outcome: 'unconfirmed', reason: 'branch-mismatch' }
  }
  if (!receipt.expectedHead || receipt.observedHead !== receipt.expectedHead) {
    return { outcome: 'unconfirmed', reason: 'head-mismatch' }
  }
  if (!receipt.expectedTree || receipt.observedTree !== receipt.expectedTree) {
    return { outcome: 'unconfirmed', reason: 'tree-mismatch' }
  }
  if (receipt.staged.length > 0 || receipt.unstaged.length > 0 || receipt.untracked.length > 0) {
    return { outcome: 'dirty', reason: 'workspace-status-non-empty' }
  }
  return { outcome: 'committed-clean', reason: null }
}

export function buildCompletionBoundary(options: {
  work: DispatchWorkItem
  runnerId?: string
  workspace: CompletionWorkspace | null
  capture: ActionExecutionCapture
  result: WorkItemResult
  receipt: CommitReceipt
  now?: () => Date
}): WorkflowTaskCompletionBoundary {
  const identity = buildExecutionIdentity(options.work, options.workspace, options.runnerId)
  const actionCompletion = buildActionCompletion(options.capture, options.result, options.now)
  const arbitration = arbitrateWorkspaceOutcome(options.receipt)
  const unsigned = {
    version: 1 as const,
    identity,
    actionCompletion,
    commitReceipt: { ...options.receipt, identity: structuredClone(identity) },
    workspaceOutcome: arbitration.outcome,
    workspaceReason: arbitration.reason,
    cleanupScope: options.work.cleanupScope ? [...options.work.cleanupScope] : cleanupScopeFromVariables(options.work),
  }
  return { ...unsigned, fingerprint: fingerprint(unsigned) }
}

export function applyBoundaryOutcome(
  result: WorkItemResult,
  boundary: WorkflowTaskCompletionBoundary,
): WorkItemResult {
  return {
    ...result,
    workspaceOutcome: boundary.workspaceOutcome,
    workspaceReason: boundary.workspaceReason,
  }
}

async function runGitProbe(
  workDir: string,
  args: string[],
  signal: AbortSignal,
  log: TaskLogger | null | undefined,
): Promise<{ ok: true; value: string } | { ok: false; reason: string }> {
  try {
    const result = await git(workDir, args, signal, log ? { sink: { log, source: RECEIPT_PROBE_SOURCE } } : undefined)
    if (result.status === 'timeout') return { ok: false, reason: 'workspace-probe-timeout' }
    if (!result.success) {
      const output = result.combinedOutput.toLowerCase()
      return { ok: false, reason: output.includes('not a git repository') ? 'workspace-not-git' : 'workspace-probe-failed' }
    }
    return { ok: true, value: result.stdout }
  } catch (error) {
    return { ok: false, reason: `workspace-probe-threw:${errorMessage(error)}` }
  }
}

function parsePorcelainStatus(raw: string): { staged: string[]; unstaged: string[]; untracked: string[] } {
  const staged: string[] = []
  const unstaged: string[] = []
  const untracked: string[] = []
  for (const entry of raw.split('\0')) {
    if (!entry) continue
    const code = entry.slice(0, 2)
    const path = entry.slice(3).trim() || entry.slice(2).trim()
    if (!path) continue
    if (code === '??') {
      untracked.push(path)
      continue
    }
    if (code[0] && code[0] !== ' ') staged.push(path)
    if (code[1] && code[1] !== ' ') unstaged.push(path)
  }
  return { staged: unique(staged), unstaged: unique(unstaged), untracked: unique(untracked) }
}

function workspaceVariablesOf(work: DispatchWorkItem): JsonObject {
  const value = work.variables?.workspace
  return value && typeof value === 'object' && !Array.isArray(value) ? value as JsonObject : {}
}

function cleanupScopeFromVariables(work: DispatchWorkItem): string[] | null {
  const value = workspaceVariablesOf(work).cleanupScope
  if (!Array.isArray(value)) return null
  const paths = value.filter((path): path is string => typeof path === 'string' && path.trim().length > 0)
  return paths.length > 0 ? [...new Set(paths)] : []
}

function stringField(value: JsonObject, key: string): string | null {
  return typeof value[key] === 'string' && String(value[key]).trim() ? String(value[key]) : null
}

function scalarField(value: JsonObject, key: string): string | number | null {
  const candidate = value[key]
  return typeof candidate === 'string' || typeof candidate === 'number' ? candidate : null
}

function fingerprint(value: unknown): string {
  return createHash('sha256').update(stableStringify(value)).digest('hex')
}

function stableStringify(value: unknown): string {
  if (value === null || typeof value !== 'object') return JSON.stringify(value)
  if (Array.isArray(value)) return `[${value.map(stableStringify).join(',')}]`
  const record = value as Record<string, unknown>
  return `{${Object.keys(record).sort().map((key) => `${JSON.stringify(key)}:${stableStringify(record[key])}`).join(',')}}`
}

function unique(values: string[]): string[] {
  return [...new Set(values)]
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error)
}
