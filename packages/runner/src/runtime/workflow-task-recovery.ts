import type {
  CommitReceipt,
  WorkflowTaskCleanupLease,
  WorkflowTaskCleanupOperation,
  WorkflowTaskCompletionBoundary,
  WorkflowTaskSourceAdoption,
  WorkflowTaskSourceAdoptionRequest,
  WorkspaceVerification,
} from '../core/types.js'
import type { ServerConnection } from '../server/connection.js'
import { git, type GitRunner } from './git-probe.js'
import {
  ScopedWorkspaceCleanup,
  AdoptTaskSourceChanges,
  type ScopedCleanupResult,
  type SourceAdoptionResult,
} from './workspace-recovery.js'
import { readMarker } from './workspace-identity.js'

export class WorkflowTaskRecoveryCoordinator {
  constructor(
    private readonly connection: Pick<
      ServerConnection,
      | 'acquireWorkflowTaskCleanupLease'
      | 'recordWorkflowTaskCleanup'
      | 'authorizeTaskSourceAdoption'
      | 'recordTaskSourceAdoption'
      | 'verifyWorkflowWorkspace'
    >,
    private readonly cleanup: ScopedWorkspaceCleanup,
    private readonly adoption: AdoptTaskSourceChanges,
    private readonly gitRunner: GitRunner = git,
    private readonly now: () => Date = () => new Date(),
  ) {}

  async cleanupWorkspace(
    boundary: WorkflowTaskCompletionBoundary,
    workspacePath: string,
    operationId: string,
    signal: AbortSignal,
    workBudget = 16,
  ): Promise<ScopedCleanupResult> {
    const leaseResponse = await this.connection.acquireWorkflowTaskCleanupLease(
      {
        operationId,
        identity: boundary.identity,
        boundaryFingerprint: boundary.fingerprint,
        cleanupScope: boundary.cleanupScope ?? [],
        workBudget,
      },
      signal,
    )
    if (leaseResponse.operation) {
      return { rejected: false, operation: structuredClone(leaseResponse.operation) }
    }
    if (!leaseResponse.accepted || !leaseResponse.lease) {
      return {
        rejected: true,
        operation: rejectedCleanupOperation(
          boundary,
          operationId,
          leaseResponse.reason ?? 'cleanup-lease-rejected',
          this.now(),
        ),
      }
    }
    const local = await this.cleanup.execute(leaseResponse.lease, workspacePath)
    const recorded = await this.connection.recordWorkflowTaskCleanup(local.operation, signal)
    if (!recorded.accepted && !recorded.replay) {
      return {
        rejected: true,
        operation: {
          ...local.operation,
          applied: false,
          clean: false,
          reason: recorded.reason ?? 'cleanup-result-rejected',
        },
      }
    }
    return local
  }

  async adoptTaskSourceChanges(
    request: WorkflowTaskSourceAdoptionRequest,
    workspacePath: string,
    signal: AbortSignal,
  ): Promise<SourceAdoptionResult> {
    const authorization = await this.connection.authorizeTaskSourceAdoption(request, signal)
    if (!authorization.accepted || !authorization.operation) {
      return {
        rejected: true,
        operation: rejectedAdoptionOperation(request, authorization.reason ?? 'adoption-rejected', this.now()),
      }
    }
    const authorizedRequest = {
      ...request,
      sourcePaths: authorization.operation.sourcePaths,
      operatorId: authorization.operation.operatorId,
    }
    const local = await this.adoption.execute(authorizedRequest, workspacePath)
    const recorded = await this.connection.recordTaskSourceAdoption(local.operation, signal)
    if (!recorded.accepted && !recorded.replay) {
      return {
        rejected: true,
        operation: {
          ...local.operation,
          accepted: false,
          completed: false,
          resultingHead: null,
          reason: recorded.reason ?? 'adoption-result-rejected',
        },
      }
    }
    return local
  }

  async verifyWorkspace(
    boundary: WorkflowTaskCompletionBoundary,
    workspacePath: string,
    fence: string,
    idempotencyKey: string,
    signal: AbortSignal,
    options: { verifier?: string; source?: string; sourceAdoptionOperationId?: string | null } = {},
  ): Promise<WorkspaceVerification> {
    const verification = await probeVerification(
      boundary,
      workspacePath,
      fence,
      idempotencyKey,
      signal,
      this.gitRunner,
      this.now,
      options,
    )
    await this.connection.verifyWorkflowWorkspace(verification, signal)
    return verification
  }
}

async function probeVerification(
  boundary: WorkflowTaskCompletionBoundary,
  workspacePath: string,
  fence: string,
  idempotencyKey: string,
  signal: AbortSignal,
  gitRunner: GitRunner,
  now: () => Date,
  options: { verifier?: string; source?: string; sourceAdoptionOperationId?: string | null },
): Promise<WorkspaceVerification> {
  const base = {
    idempotencyKey,
    identity: structuredClone(boundary.identity),
    boundaryFingerprint: boundary.fingerprint,
    observedBranch: null as string | null,
    observedHead: null as string | null,
    observedTree: null as string | null,
    staged: [] as string[],
    unstaged: [] as string[],
    untracked: [] as string[],
    authoritative: false,
    reason: null as string | null,
    verifier: options.verifier ?? 'runner',
    source: options.source ?? 'workspace-verification',
    sourceAdoptionOperationId: options.sourceAdoptionOperationId ?? null,
    fence,
  }
  const marker = await readMarker(workspacePath)
  if (!marker || marker.workflowRunId !== boundary.identity.workflowRunId)
    return { ...base, reason: 'workspace-marker-mismatch' }
  if (boundary.identity.workspaceId !== null && marker.workspaceId !== boundary.identity.workspaceId) {
    return { ...base, reason: 'workspace-marker-identity-mismatch' }
  }
  if (
    boundary.identity.workspaceGeneration !== null &&
    marker.workspaceGeneration !== boundary.identity.workspaceGeneration
  ) {
    return { ...base, reason: 'workspace-marker-generation-mismatch' }
  }
  const branch = await run(gitRunner, workspacePath, ['rev-parse', '--abbrev-ref', 'HEAD'], signal)
  if (!branch.ok) return { ...base, reason: branch.reason }
  const head = await run(gitRunner, workspacePath, ['rev-parse', 'HEAD'], signal)
  if (!head.ok) return { ...base, reason: head.reason }
  const tree = await run(gitRunner, workspacePath, ['rev-parse', 'HEAD^{tree}'], signal)
  if (!tree.ok) return { ...base, reason: tree.reason }
  const status = await run(gitRunner, workspacePath, ['status', '--porcelain=v1', '-z'], signal)
  if (!status.ok) return { ...base, reason: status.reason }
  const paths = parseStatus(status.value)
  return {
    ...base,
    observedBranch: branch.value.trim() || null,
    observedHead: head.value.trim() || null,
    observedTree: tree.value.trim() || null,
    staged: paths.staged,
    unstaged: paths.unstaged,
    untracked: paths.untracked,
    authoritative: true,
  }
}

async function run(
  runner: GitRunner,
  workDir: string,
  args: string[],
  signal: AbortSignal,
): Promise<{ ok: true; value: string } | { ok: false; reason: string }> {
  try {
    const result = await runner(workDir, args, signal)
    if (result.status === 'timeout') return { ok: false, reason: 'workspace-verification-timeout' }
    if (!result.success) return { ok: false, reason: 'workspace-verification-probe-failed' }
    return { ok: true, value: result.stdout }
  } catch {
    return { ok: false, reason: 'workspace-verification-probe-threw' }
  }
}

function parseStatus(raw: string): { staged: string[]; unstaged: string[]; untracked: string[] } {
  const staged: string[] = []
  const unstaged: string[] = []
  const untracked: string[] = []
  for (const entry of raw.split('\0')) {
    if (!entry) continue
    const code = entry.slice(0, 2)
    const path = entry.slice(3).trim() || entry.slice(2).trim()
    if (!path) continue
    if (code === '??') untracked.push(path)
    else {
      if (code[0] && code[0] !== ' ') staged.push(path)
      if (code[1] && code[1] !== ' ') unstaged.push(path)
    }
  }
  return { staged: [...new Set(staged)], unstaged: [...new Set(unstaged)], untracked: [...new Set(untracked)] }
}

function rejectedCleanupOperation(
  boundary: WorkflowTaskCompletionBoundary,
  operationId: string,
  reason: string,
  now: Date,
): WorkflowTaskCleanupOperation {
  return {
    operationId,
    fence: '',
    identity: structuredClone(boundary.identity),
    applied: false,
    clean: false,
    mutations: 0,
    removedPaths: [],
    reason,
    recordedAt: now.toISOString(),
  }
}

function rejectedAdoptionOperation(
  request: WorkflowTaskSourceAdoptionRequest,
  reason: string,
  now: Date,
): WorkflowTaskSourceAdoption {
  return {
    operationId: request.operationId,
    fence: request.fence,
    identity: structuredClone(request.identity),
    operatorId: request.operatorId,
    sourcePaths: [...request.sourcePaths],
    accepted: false,
    completed: false,
    resultingHead: null,
    reason,
    recordedAt: now.toISOString(),
  }
}
