import { join } from 'node:path'
import type { ActionResult, JsonObject } from '../core/types.js'
import type { ActionHost } from './host.js'
import { stringInput } from '../core/json.js'
import { exists } from '../system/process.js'
import { currentRunnerResources, type RunnerGitRunner } from '../system/filesystem.js'
import { git as defaultGit, type GitOptions } from './git.js'
import { fail, succeed } from './action-result.js'
import {
  DETACHED_HEAD_REF,
  evaluateWorkspaceHealth,
  workspaceHealthDiagnostic,
  type WorkspaceHeadState,
  type WorkspaceHealthSnapshot,
  type WorkspaceProbeFailure,
  type WorkspaceResidualState,
} from '../runtime/workspace-health.js'

type GitRunner = RunnerGitRunner
type GitResult = Awaited<ReturnType<GitRunner>>

const ACTION_SOURCE = 'action:workspace-prepare'

export type WorkspacePrepareGitResult = GitResult

function git(workDir: string, args: string[], signal: AbortSignal, options?: GitOptions): Promise<GitResult> {
  return (currentRunnerResources()?.workspacePrepareGitRunner ?? currentRunnerResources()?.gitRunner ?? defaultGit)(
    workDir,
    args,
    signal,
    options,
  )
}

function pathExists(path: string): boolean {
  return (currentRunnerResources()?.workspacePrepareExistsChecker ?? exists)(path)
}

interface ResidualProbe {
  residual: Pick<WorkspaceResidualState, 'rebaseMerge' | 'rebaseApply'>
  failure: WorkspaceProbeFailure | null
}

interface PathProbe {
  exists: boolean
  failure: WorkspaceProbeFailure | null
}

interface HeadProbe {
  head: WorkspaceHeadState
  failure: WorkspaceProbeFailure | null
}

const DETACHED_REF = DETACHED_HEAD_REF

function sinkOptions(host: ActionHost): GitOptions | undefined {
  return host.log ? { sink: { log: host.log, source: ACTION_SOURCE } } : undefined
}

/**
 * Workspace preparation repairs a detached or mismatched workspace back
 * onto the expected run branch using the shared workspace-health
 * contract. The state machine is deterministic:
 *
 *  1. probe branch attachment, detached ref, worktree status, and every
 *     residual operation marker;
 *  2. return success immediately only for the exact expected branch with
 *     a clean, non-residual workspace (fast path, no mutation);
 *  3. abort residual operations in a fixed order (rebase, merge,
 *     cherry-pick), re-probing each aborted state;
 *  4. reset and clean a dirty worktree;
 *  5. check out the existing expected branch (never force-create one);
 *  6. run a complete final probe — only the exact expected branch, clean
 *     status, and absence of every residual marker can return success.
 */
export async function workspacePrepareAction(inputs: JsonObject, host: ActionHost): Promise<ActionResult> {
  const expectedBranch = stringInput(inputs, 'expectedBranch')
  const workDir = host.workDir
  const opts = sinkOptions(host)

  if (!expectedBranch) {
    const snapshot = await captureSnapshot(workDir, host.signal, opts)
    return failureOutput('(none)', snapshot, 'resolve', 'Workspace branch is not defined in with.expectedBranch', 1)
  }

  const initial = await captureSnapshot(workDir, host.signal, opts)
  const initialProbeFailure = probeFailureResult(expectedBranch, initial)
  if (initialProbeFailure) return initialProbeFailure

  if (evaluateWorkspaceHealth(initial, expectedBranch).healthy) {
    return successOutput(workDir, expectedBranch, initial)
  }

  let current = initial

  if (current.residual.rebaseMerge || current.residual.rebaseApply) {
    const abort = await git(workDir, ['rebase', '--abort'], host.signal, opts)
    if (!abort.success) {
      const after = await captureSnapshot(workDir, host.signal, opts)
      return failureOutput(
        expectedBranch,
        after,
        'abort-rebase',
        `git rebase --abort failed: ${abort.combinedOutput}`,
        abort.exitCode,
      )
    }
    const reprobe = await probeRebaseDirs(workDir, host.signal, opts)
    if (reprobe.failure) {
      const after = await captureSnapshot(workDir, host.signal, opts)
      return failureOutput(expectedBranch, after, 'abort-rebase', reprobe.failure.message, reprobe.failure.exitCode)
    }
    if (reprobe.residual.rebaseMerge || reprobe.residual.rebaseApply) {
      const after = await captureSnapshot(workDir, host.signal, opts)
      return failureOutput(expectedBranch, after, 'abort-rebase', 'Rebase is still in progress after abort', 1)
    }
    current = await captureSnapshot(workDir, host.signal, opts)
    const currentProbeFailure = probeFailureResult(expectedBranch, current)
    if (currentProbeFailure) return currentProbeFailure
  }

  if (current.residual.mergeHead) {
    const abort = await git(workDir, ['merge', '--abort'], host.signal, opts)
    if (!abort.success) {
      const after = await captureSnapshot(workDir, host.signal, opts)
      return failureOutput(
        expectedBranch,
        after,
        'abort-merge',
        `git merge --abort failed: ${abort.combinedOutput}`,
        abort.exitCode,
      )
    }
    const reprobe = await probeMergeHead(workDir, host.signal, opts)
    if (reprobe.failure) {
      const after = await captureSnapshot(workDir, host.signal, opts)
      return failureOutput(expectedBranch, after, 'abort-merge', reprobe.failure.message, reprobe.failure.exitCode)
    }
    if (reprobe.exists) {
      const after = await captureSnapshot(workDir, host.signal, opts)
      return failureOutput(expectedBranch, after, 'abort-merge', 'Merge is still in progress after abort', 1)
    }
    current = await captureSnapshot(workDir, host.signal, opts)
    const currentProbeFailure = probeFailureResult(expectedBranch, current)
    if (currentProbeFailure) return currentProbeFailure
  }

  if (current.residual.cherryPickHead) {
    const abort = await git(workDir, ['cherry-pick', '--abort'], host.signal, opts)
    if (!abort.success) {
      const after = await captureSnapshot(workDir, host.signal, opts)
      return failureOutput(
        expectedBranch,
        after,
        'abort-cherry-pick',
        `git cherry-pick --abort failed: ${abort.combinedOutput}`,
        abort.exitCode,
      )
    }
    const reprobe = await probeCherryPickHead(workDir, host.signal, opts)
    if (reprobe.failure) {
      const after = await captureSnapshot(workDir, host.signal, opts)
      return failureOutput(
        expectedBranch,
        after,
        'abort-cherry-pick',
        reprobe.failure.message,
        reprobe.failure.exitCode,
      )
    }
    if (reprobe.exists) {
      const after = await captureSnapshot(workDir, host.signal, opts)
      return failureOutput(
        expectedBranch,
        after,
        'abort-cherry-pick',
        'Cherry-pick is still in progress after abort',
        1,
      )
    }
    current = await captureSnapshot(workDir, host.signal, opts)
    const currentProbeFailure = probeFailureResult(expectedBranch, current)
    if (currentProbeFailure) return currentProbeFailure
  }

  if (current.porcelain.trim() !== '') {
    const reset = await git(workDir, ['reset', '--hard', 'HEAD'], host.signal, opts)
    if (!reset.success) {
      const after = await captureSnapshot(workDir, host.signal, opts)
      return failureOutput(
        expectedBranch,
        after,
        'reset',
        `git reset --hard HEAD failed: ${reset.combinedOutput}`,
        reset.exitCode,
      )
    }
    const clean = await git(workDir, ['clean', '-fd'], host.signal, opts)
    if (!clean.success) {
      const after = await captureSnapshot(workDir, host.signal, opts)
      return failureOutput(
        expectedBranch,
        after,
        'clean',
        `git clean -fd failed: ${clean.combinedOutput}`,
        clean.exitCode,
      )
    }
    current = await captureSnapshot(workDir, host.signal, opts)
    const currentProbeFailure = probeFailureResult(expectedBranch, current)
    if (currentProbeFailure) return currentProbeFailure
  }

  if (current.head.ref !== expectedBranch) {
    const checkout = await git(workDir, ['checkout', expectedBranch], host.signal, opts)
    if (!checkout.success) {
      const after = await captureSnapshot(workDir, host.signal, opts)
      return failureOutput(
        expectedBranch,
        after,
        'checkout',
        `git checkout ${expectedBranch} failed: ${checkout.combinedOutput}`,
        checkout.exitCode,
      )
    }
  }

  const verify = await captureSnapshot(workDir, host.signal, opts)
  const verifyProbeFailure = probeFailureResult(expectedBranch, verify)
  if (verifyProbeFailure) return verifyProbeFailure
  const evaluation = evaluateWorkspaceHealth(verify, expectedBranch)
  if (!evaluation.healthy) {
    return failureOutput(expectedBranch, verify, 'verify', `Health verification failed: ${evaluation.condition}`, 1)
  }

  return successOutput(workDir, expectedBranch, verify)
}

async function captureSnapshot(
  workDir: string,
  signal: AbortSignal,
  opts?: GitOptions,
): Promise<WorkspaceHealthSnapshot> {
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

async function captureHead(workDir: string, signal: AbortSignal, opts?: GitOptions): Promise<HeadProbe> {
  const [headResult, refResult] = await Promise.all([
    git(workDir, ['rev-parse', 'HEAD'], signal, opts),
    git(workDir, ['rev-parse', '--abbrev-ref', 'HEAD'], signal, opts),
  ])
  const commit = headResult.success ? headResult.stdout.trim() : ''
  let ref = DETACHED_REF
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

async function probeRebaseDirs(workDir: string, signal: AbortSignal, opts?: GitOptions): Promise<ResidualProbe> {
  const [rebaseMerge, rebaseApply] = await Promise.all([
    probePathExists(workDir, 'rebase-merge', signal, opts),
    probePathExists(workDir, 'rebase-apply', signal, opts),
  ])
  return {
    residual: { rebaseMerge: rebaseMerge.exists, rebaseApply: rebaseApply.exists },
    failure: rebaseMerge.failure ?? rebaseApply.failure,
  }
}

async function probeMergeHead(workDir: string, signal: AbortSignal, opts?: GitOptions): Promise<PathProbe> {
  return await probePathExists(workDir, 'MERGE_HEAD', signal, opts)
}

async function probeCherryPickHead(workDir: string, signal: AbortSignal, opts?: GitOptions): Promise<PathProbe> {
  return await probePathExists(workDir, 'CHERRY_PICK_HEAD', signal, opts)
}

async function probePathExists(
  workDir: string,
  gitPath: string,
  signal: AbortSignal,
  opts?: GitOptions,
): Promise<PathProbe> {
  const result = await git(workDir, ['rev-parse', '--git-path', gitPath], signal, opts)
  if (!result.success) {
    return { exists: false, failure: gitFailure('residual', `git rev-parse --git-path ${gitPath}`, result) }
  }
  return { exists: pathExists(resolveGitPath(workDir, result.stdout.trim())), failure: null }
}

function resolveGitPath(workDir: string, path: string): string {
  return path.match(/^[A-Za-z]:[\\/]|^\//) ? path : join(workDir, path)
}

function probeFailureResult(expectedBranch: string, snapshot: WorkspaceHealthSnapshot): ActionResult | null {
  const failure = snapshot.probeFailure
  if (!failure) return null
  return failureOutput(expectedBranch, snapshot, failure.step, undefined, failure.exitCode)
}

function gitFailure(step: string, command: string, result: GitResult): WorkspaceProbeFailure {
  return {
    step,
    message: `${command} failed: ${result.combinedOutput || `exit ${result.exitCode}`}`,
    exitCode: result.exitCode,
  }
}

function successOutput(workDir: string, expectedBranch: string, snapshot: WorkspaceHealthSnapshot): ActionResult {
  const output: JsonObject = {
    kind: 'workspace-prepare',
    status: 'success',
    expectedBranch,
    head: snapshot.head as unknown as JsonObject,
    residual: snapshot.residual as unknown as JsonObject,
    porcelain: snapshot.porcelain,
    step: null,
    workDir,
  }
  return succeed(output, { exitCode: 0 })
}

function failureOutput(
  expectedBranch: string,
  snapshot: WorkspaceHealthSnapshot,
  step: string,
  detail: string | undefined,
  exitCode: number | null,
): ActionResult {
  const message = workspaceHealthDiagnostic({ operation: step, expectedBranch, snapshot, detail })
  return fail('workspace-setup', message, { exitCode: exitCode ?? 1 })
}
