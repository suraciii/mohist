import { join } from 'node:path'
import type { JsonObject, DispatchWorkItem, WorkItemResult } from '../core/types.js'
import { exists } from '../system/process.js'
import { currentRunnerResources } from '../system/filesystem.js'
import { git, type GitOptions } from './git-probe.js'
import type { TaskLogger } from './task-log.js'
import {
  DETACHED_HEAD_REF,
  isResidualFree,
  observedBranchLabel,
  observedRefLabel,
  workspaceHealthDiagnostic,
  type WorkspaceHeadState,
  type WorkspaceHealthSnapshot,
  type WorkspaceProbeFailure,
  type WorkspaceResidualState,
} from './workspace-health.js'

type GitResult = Awaited<ReturnType<typeof git>>

/**
 * `source` label recorded against every captured branch-stability
 * line. Distinct from the action body's `action:*` tag so the web
 * viewer can phase-distinguish the boundary probe from the action
 * itself.
 */
export const BRANCH_CHECK_SOURCE = 'branch-check'

function branchCheckSink(log: TaskLogger | null | undefined) {
  return log ? { log, source: BRANCH_CHECK_SOURCE } : undefined
}

export interface BranchStabilityEvidence {
  kind: 'branch-stability'
  boundary: 'start' | 'end'
  expectedBranch: string
  observedBranch: string
  observedRef?: string | null
}

export interface BranchInvariantViolationEvidence {
  kind: 'branch-invariant-violation'
  boundary: 'start' | 'end'
  expectedBranch: string
  observedBranch: string
  observedRef?: string | null
  detail?: string
  /**
   * Full shared health diagnostic (expected/observed fields plus the
   * failed workspace condition). When present it supersedes the legacy
   * boundary message so the boundary failure reads identically to
   * action and workspace-preparation failures.
   */
  message?: string
}

export interface CurrentBranchResult {
  branch: string | null
  ref: string | null
  detached: boolean
  nonGit: boolean
  error: string | null
}

export function expectedWorkspaceBranch(variables: JsonObject): string | null {
  const workspace = variables['workspace']
  if (!workspace || typeof workspace !== 'object' || Array.isArray(workspace)) return null
  const branch = (workspace as JsonObject)['branch']
  return typeof branch === 'string' && branch.length > 0 ? branch : null
}

export async function readCurrentBranch(
  workDir: string,
  signal: AbortSignal,
  log: TaskLogger | null = null,
): Promise<CurrentBranchResult> {
  const sink = branchCheckSink(log)
  const probe = await git(workDir, ['rev-parse', '--abbrev-ref', 'HEAD'], signal, sink ? { sink } : undefined)
  if (!probe.success) {
    const stderr = (probe.stderr ?? '').toLowerCase()
    if (stderr.includes('not a git repository')) {
      return { branch: null, ref: null, detached: false, nonGit: true, error: null }
    }
    return {
      branch: null,
      ref: null,
      detached: false,
      nonGit: false,
      error: probe.combinedOutput || `exit ${probe.exitCode}`,
    }
  }
  const branch = probe.stdout.trim()
  if (branch === 'HEAD') {
    const refProbe = await git(workDir, ['rev-parse', 'HEAD'], signal, sink ? { sink } : undefined)
    return {
      branch: null,
      ref: refProbe.success ? refProbe.stdout.trim() : null,
      detached: true,
      nonGit: false,
      error: null,
    }
  }
  return { branch, ref: branch, detached: false, nonGit: false, error: null }
}

export function branchInvariantViolationFailure(
  work: DispatchWorkItem,
  evidence: BranchInvariantViolationEvidence,
): WorkItemResult {
  const label = work.title?.trim() || work.uses || work.workId
  const message = (evidence.message ?? legacyBranchInvariantMessage(evidence, label)).slice(0, 4000)
  return {
    status: 'failed',
    message,
    error: { code: 'branch-invariant-violation', message },
  }
}

function legacyBranchInvariantMessage(evidence: BranchInvariantViolationEvidence, label: string): string {
  const observed = evidence.observedBranch || `(detached at ${evidence.observedRef ?? 'unknown'})`
  const detail = evidence.detail ? `; ${evidence.detail}` : ''
  return (
    `branch-invariant violation at ${evidence.boundary} boundary for ${label}: ` +
    `expected branch '${evidence.expectedBranch}', observed '${observed}'${detail}`
  )
}

/**
 * Task boundary invariant: the workflow workspace must remain on
 * `workspace.branch` for the entire lifetime of a task.
 *
 * When no expected workspace branch is defined the boundary probe is
 * observational only — a non-Git directory is treated as clean so
 * actions outside a materialized Git workspace keep working.
 *
 * When an expected branch IS defined the boundary is judged with the
 * shared workspace-health semantics: a detached `HEAD`, a mismatched
 * branch, a failed branch probe, or an unverified (non-Git) workspace
 * fails closed at both boundaries. At the end boundary a successful
 * action must also leave the workspace free of residual rebase / merge /
 * cherry-pick state; the start boundary deliberately allows residual
 * state because `mohist/workspace-prepare` runs precisely to repair it. A
 * dirty worktree is deferred to worktree enforcement in both cases so the
 * agent-backed cleanup loop can still run.
 *
 * The start check runs before the action is invoked; the end check runs
 * after a successful action but before artifact upload and
 * `enforceCleanWorktree` so an invalid workspace is reported as a
 * branch-invariant violation (runner/action bug) rather than being
 * settled as a successful task.
 */
export async function checkBranchStability(
  work: DispatchWorkItem,
  workDir: string,
  expectedBranch: string | null,
  boundary: 'start' | 'end',
  signal: AbortSignal,
  log: TaskLogger | null = null,
): Promise<{ kind: 'ok'; evidence: BranchStabilityEvidence } | { kind: 'violation'; result: WorkItemResult }> {
  if (expectedBranch === null) {
    const observed = await readCurrentBranch(workDir, signal, log)
    const evidence: BranchStabilityEvidence = {
      kind: 'branch-stability',
      boundary,
      expectedBranch: '',
      observedBranch: observed.branch ?? '',
      observedRef: observed.ref,
    }
    return { kind: 'ok', evidence }
  }

  const snapshot = await captureHealthSnapshot(workDir, signal, log)
  const failure = snapshot.probeFailure
  if (failure) {
    return {
      kind: 'violation',
      result: branchInvariantViolationFailure(work, {
        kind: 'branch-invariant-violation',
        boundary,
        expectedBranch,
        observedBranch: observedBranchLabel(snapshot),
        observedRef: observedRefLabel(snapshot),
        message: workspaceHealthDiagnostic({ operation: failure.step, expectedBranch, snapshot }),
      }),
    }
  }
  const aligned = snapshot.head.ref === expectedBranch
  if (!aligned) {
    return {
      kind: 'violation',
      result: branchInvariantViolationFailure(work, {
        kind: 'branch-invariant-violation',
        boundary,
        expectedBranch,
        observedBranch: observedBranchLabel(snapshot),
        observedRef: observedRefLabel(snapshot),
        message: workspaceHealthDiagnostic({
          operation: boundary,
          expectedBranch,
          snapshot,
          detail: `health verification failed at ${boundary} boundary`,
        }),
      }),
    }
  }
  // At the end boundary a successful action must also leave the
  // workspace free of residual rebase / merge / cherry-pick state;
  // mid-flight operation state means the action did not actually
  // complete its recovery. The start boundary deliberately does not
  // reject residual state, because `mohist/workspace-prepare` runs
  // precisely to repair it before a business task starts. A dirty
  // worktree is deferred to worktree enforcement in both cases.
  if (boundary === 'end' && !isResidualFree(snapshot.residual)) {
    return {
      kind: 'violation',
      result: branchInvariantViolationFailure(work, {
        kind: 'branch-invariant-violation',
        boundary,
        expectedBranch,
        observedBranch: observedBranchLabel(snapshot),
        observedRef: observedRefLabel(snapshot),
        message: workspaceHealthDiagnostic({
          operation: boundary,
          expectedBranch,
          snapshot,
          detail: `health verification failed at ${boundary} boundary`,
        }),
      }),
    }
  }
  const evidence: BranchStabilityEvidence = {
    kind: 'branch-stability',
    boundary,
    expectedBranch,
    observedBranch: snapshot.head.ref,
    observedRef: snapshot.head.ref,
  }
  return { kind: 'ok', evidence }
}

/**
 * Capture the shared workspace-health snapshot for the boundary probes.
 * Mirrors the residual / head / porcelain model shared by
 * `mohist/workspace-prepare`, `mohist/rebase`, and `WorkspaceManager`,
 * using the executor's narrow git probe.
 */
async function captureHealthSnapshot(
  workDir: string,
  signal: AbortSignal,
  log: TaskLogger | null = null,
): Promise<WorkspaceHealthSnapshot> {
  const sink = branchCheckSink(log)
  const opts: GitOptions | undefined = sink ? { sink } : undefined
  const [residualProbe, headProbe, porcelainResult] = await Promise.all([
    probeResidual(workDir, signal, opts),
    captureHead(workDir, signal, opts),
    git(workDir, ['status', '--porcelain'], signal, opts),
  ])
  const statusFailure = porcelainResult.success ? null : gitFailure('status', 'git status --porcelain', porcelainResult)
  return {
    residual: residualProbe.residual,
    head: headProbe.head,
    porcelain: porcelainResult.success ? porcelainResult.stdout : '',
    probeFailure: residualProbe.failure ?? headProbe.failure ?? statusFailure,
  }
}

async function captureHead(
  workDir: string,
  signal: AbortSignal,
  opts?: GitOptions,
): Promise<{ head: WorkspaceHeadState; failure: WorkspaceProbeFailure | null }> {
  const [headResult, refResult] = await Promise.all([
    git(workDir, ['rev-parse', 'HEAD'], signal, opts),
    git(workDir, ['rev-parse', '--abbrev-ref', 'HEAD'], signal, opts),
  ])
  const commit = headResult.success ? headResult.stdout.trim() : ''
  let ref = DETACHED_HEAD_REF
  if (refResult.success) {
    const trimmed = refResult.stdout.trim()
    if (trimmed !== '' && trimmed !== 'HEAD') ref = trimmed
  }
  const failure = !headResult.success
    ? gitFailure('head', 'git rev-parse HEAD', headResult)
    : !refResult.success
      ? gitFailure('head-ref', 'git rev-parse --abbrev-ref HEAD', refResult)
      : null
  return { head: { commit, ref }, failure }
}

async function probeResidual(
  workDir: string,
  signal: AbortSignal,
  opts?: GitOptions,
): Promise<{ residual: WorkspaceResidualState; failure: WorkspaceProbeFailure | null }> {
  const [rebaseMerge, rebaseApply, mergeHead, cherryPickHead] = await Promise.all([
    probePathExists(workDir, 'rebase-merge', signal, opts),
    probePathExists(workDir, 'rebase-apply', signal, opts),
    probePathExists(workDir, 'MERGE_HEAD', signal, opts),
    probePathExists(workDir, 'CHERRY_PICK_HEAD', signal, opts),
  ])
  return {
    residual: {
      rebaseMerge: rebaseMerge.exists,
      rebaseApply: rebaseApply.exists,
      mergeHead: mergeHead.exists,
      cherryPickHead: cherryPickHead.exists,
    },
    failure: rebaseMerge.failure ?? rebaseApply.failure ?? mergeHead.failure ?? cherryPickHead.failure,
  }
}

async function probePathExists(
  workDir: string,
  gitPath: string,
  signal: AbortSignal,
  opts?: GitOptions,
): Promise<{ exists: boolean; failure: WorkspaceProbeFailure | null }> {
  const result = await git(workDir, ['rev-parse', '--git-path', gitPath], signal, opts)
  if (!result.success) {
    return { exists: false, failure: gitFailure('residual', `git rev-parse --git-path ${gitPath}`, result) }
  }
  return { exists: pathExists(resolveGitPath(workDir, result.stdout.trim())), failure: null }
}

function pathExists(path: string): boolean {
  return (currentRunnerResources()?.workspacePrepareExistsChecker ?? exists)(path)
}

function resolveGitPath(workDir: string, path: string): string {
  return path.match(/^[A-Za-z]:[\\/]|^\//) ? path : join(workDir, path)
}

function gitFailure(step: string, command: string, result: GitResult): WorkspaceProbeFailure {
  return {
    step,
    message: `${command} failed: ${result.combinedOutput || `exit ${result.exitCode}`}`,
    exitCode: result.exitCode,
  }
}
