import { join } from 'node:path'
import { exists } from '../system/process.js'
import type { ActionResult, JsonObject } from '../core/types.js'
import type { ActionHost } from './host.js'
import { stringInput } from '../core/json.js'
import { git as defaultGit, NETWORK_COMMAND_TIMEOUT_MS, type GitOptions } from './git.js'
import { isIssueFieldSource } from './issue-fields.js'
import { fail, succeed } from './action-result.js'
import { currentRunnerResources, type RunnerGitRunner } from '../system/filesystem.js'
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
interface RebaseStep {
  name: string
  command: string
  exitCode: number
  output: string
  status?: 'timeout'
  timeoutMs?: number
}
const ACTION_SOURCE = 'action:rebase'

function git(workDir: string, args: string[], signal: AbortSignal, options?: GitOptions): Promise<GitResult> {
  return (currentRunnerResources()?.rebaseGitRunner ?? currentRunnerResources()?.gitRunner ?? defaultGit)(
    workDir,
    args,
    signal,
    options,
  )
}

function pathExists(path: string): boolean {
  return (currentRunnerResources()?.rebaseExistsChecker ?? exists)(path)
}

function sinkOptions(host: ActionHost): GitOptions | undefined {
  return host.log ? { sink: { log: host.log, source: ACTION_SOURCE } } : undefined
}

function networkOptions(host: ActionHost): GitOptions | undefined {
  if (!host.log) return { timeoutMs: NETWORK_COMMAND_TIMEOUT_MS }
  return { sink: { log: host.log, source: ACTION_SOURCE }, timeoutMs: NETWORK_COMMAND_TIMEOUT_MS }
}

export type RebaseGitResult = GitResult

export async function rebaseAction(inputs: JsonObject, host: ActionHost): Promise<ActionResult> {
  const baseBranch = stringInput(inputs, 'baseBranch')
  if (!baseBranch) return fail('invalid-input', "Rebase requires input 'baseBranch'")
  const expectedBranch = stringInput(inputs, 'expectedBranch')
  if (!expectedBranch) {
    return fail(
      'invalid-input',
      "Rebase requires the engine-sourced 'expectedBranch' input resolved from workspace.branch; baseBranch is the rebase target and can never substitute for the expected run branch",
    )
  }
  const remote = stringInput(inputs, 'remote') ?? null
  const squash = booleanInput(inputs, 'squash') === true
  const baseRef = remote ? `${remote}/${baseBranch}` : baseBranch
  const opts = sinkOptions(host)
  const abortResult = await abortRebaseIfInProgress(host, opts)
  if (!abortResult.success) {
    return rebaseOutput(
      false,
      baseBranch,
      remote,
      baseRef,
      null,
      null,
      null,
      null,
      false,
      [],
      abortResult.combinedOutput,
      'abort-failed',
      abortResult.exitCode,
    )
  }
  const squashMessageResult = squash
    ? await resolveSquashMessage(inputs, host)
    : { kind: 'ok' as const, message: undefined }
  const squashMessage = squashMessageResult.kind === 'ok' ? squashMessageResult.message : undefined
  if (squashMessageResult.kind === 'failure') {
    return rebaseOutput(
      false,
      baseBranch,
      remote,
      baseRef,
      null,
      null,
      null,
      null,
      false,
      [],
      squashMessageResult.message,
      'invalid-input',
      1,
    )
  }
  if (squash && !squashMessage) {
    return fail('invalid-input', "Rebase with squash requires a non-empty commit 'message' or 'messageFrom'")
  }
  if (remote) {
    const fetch = await git(host.workDir, ['fetch', remote, baseBranch], host.signal, networkOptions(host))
    if (!fetch.success) {
      const steps = [rebaseStep('git-fetch-base', `fetch ${remote} ${baseBranch}`, fetch)]
      return rebaseOutput(
        false,
        baseBranch,
        remote,
        baseRef,
        null,
        null,
        null,
        null,
        false,
        [],
        fetch.combinedOutput,
        fetch.status === 'timeout' ? 'timeout' : 'fetch-failed',
        fetch.exitCode,
        false,
        steps,
      )
    }
  }
  const baseShaResult = await git(host.workDir, ['rev-parse', baseRef], host.signal, opts)
  if (!baseShaResult.success) {
    return rebaseOutput(
      false,
      baseBranch,
      remote,
      baseRef,
      null,
      null,
      null,
      null,
      false,
      [],
      baseShaResult.combinedOutput,
      'base-resolve-failed',
      baseShaResult.exitCode,
    )
  }
  const baseSha = baseShaResult.stdout.trim()
  const sourceCommit = await commitPendingChanges(host.workDir, `Prepare rebase onto ${baseBranch}`, host.signal, opts)
  if (!sourceCommit.success) {
    return rebaseOutput(
      false,
      baseBranch,
      remote,
      baseRef,
      baseSha,
      null,
      null,
      null,
      false,
      [],
      sourceCommit.combinedOutput,
      'prepare-failed',
      sourceCommit.exitCode,
    )
  }
  const before = await git(host.workDir, ['rev-parse', 'HEAD'], host.signal, opts)
  const beforeSha = before.success ? before.stdout.trim() : null

  const result = await git(host.workDir, ['rebase', baseRef], host.signal, opts)
  if (result.success) {
    const after = await git(host.workDir, ['rev-parse', 'HEAD'], host.signal, opts)
    const afterSha = after.success ? after.stdout.trim() : null
    return await runSquashIfRequested({
      host,
      expectedBranch,
      baseBranch,
      remote,
      baseRef,
      baseSha,
      beforeSha,
      rebasedHeadSha: afterSha,
      rebaseSucceeded: true,
      conflicts: [],
      rebaseOutput: result.combinedOutput,
      squash,
      squashMessage,
    })
  }

  let conflicts = await conflictFiles(host, opts)
  if (conflicts.length === 0) {
    return rebaseOutput(
      false,
      baseBranch,
      remote,
      baseRef,
      baseSha,
      beforeSha,
      null,
      null,
      false,
      [],
      result.combinedOutput,
      result.status === 'timeout' ? 'timeout' : 'rebase-failed',
      result.exitCode,
    )
  }

  return rebaseOutput(
    false,
    baseBranch,
    remote,
    baseRef,
    baseSha,
    beforeSha,
    null,
    null,
    false,
    conflicts,
    result.combinedOutput,
    'conflict',
    1,
    true,
  )
}

async function resolveSquashMessage(
  inputs: JsonObject,
  host: ActionHost,
): Promise<{ kind: 'ok'; message: string | undefined } | { kind: 'failure'; message: string }> {
  const literal = stringInput(inputs, 'message')
  if (literal !== undefined) return { kind: 'ok', message: literal }
  const source = stringInput(inputs, 'messageFrom')
  if (source === undefined) return { kind: 'ok', message: undefined }
  if (!isIssueFieldSource(source)) {
    return {
      kind: 'failure',
      message: `Unsupported messageFrom source '${source}'. Supported sources: issue.title, issue.body.`,
    }
  }
  if (!host.issue) {
    return { kind: 'failure', message: 'Issue field resolution requires the issue-fields capability' }
  }
  try {
    const fields = await host.issue.fields()
    return { kind: 'ok', message: source === 'issue.title' ? fields.title : fields.body }
  } catch (error) {
    return { kind: 'failure', message: errorMessage(error) }
  }
}

interface SquashRequest {
  host: ActionHost
  expectedBranch: string
  baseBranch: string
  remote: string | null
  baseRef: string
  baseSha: string
  beforeSha: string | null
  rebasedHeadSha: string | null
  rebaseSucceeded: boolean
  conflicts: string[]
  rebaseOutput: string
  squash: boolean
  squashMessage: string | undefined
}

async function runSquashIfRequested(req: SquashRequest): Promise<ActionResult> {
  if (!req.squash) {
    const integrity = await verifyRebaseCompletion(req.host, req.expectedBranch)
    if (integrity) return integrity
    return rebaseOutput(
      req.rebaseSucceeded,
      req.baseBranch,
      req.remote,
      req.baseRef,
      req.baseSha,
      req.beforeSha,
      req.rebasedHeadSha,
      null,
      false,
      req.conflicts,
      req.rebaseOutput,
      null,
      null,
    )
  }
  if (!req.squashMessage) {
    return rebaseOutput(
      false,
      req.baseBranch,
      req.remote,
      req.baseRef,
      req.baseSha,
      req.beforeSha,
      req.rebasedHeadSha,
      null,
      false,
      req.conflicts,
      req.rebaseOutput,
      'invalid-input',
      1,
    )
  }
  const softReset = await git(
    req.host.workDir,
    ['reset', '--soft', req.baseSha],
    req.host.signal,
    sinkOptions(req.host),
  )
  if (!softReset.success) {
    return rebaseOutput(
      false,
      req.baseBranch,
      req.remote,
      req.baseRef,
      req.baseSha,
      req.beforeSha,
      req.rebasedHeadSha,
      null,
      false,
      req.conflicts,
      [req.rebaseOutput, softReset.combinedOutput].filter(Boolean).join('\n\n'),
      softReset.status === 'timeout' ? 'timeout' : 'squash-failed',
      softReset.exitCode,
    )
  }
  const commit = await git(
    req.host.workDir,
    ['commit', '-m', req.squashMessage],
    req.host.signal,
    sinkOptions(req.host),
  )
  if (!commit.success) {
    return rebaseOutput(
      false,
      req.baseBranch,
      req.remote,
      req.baseRef,
      req.baseSha,
      req.beforeSha,
      req.rebasedHeadSha,
      null,
      false,
      req.conflicts,
      [req.rebaseOutput, softReset.combinedOutput, commit.combinedOutput].filter(Boolean).join('\n\n'),
      commit.status === 'timeout' ? 'timeout' : 'squash-failed',
      commit.exitCode,
    )
  }
  const squashedHead = await git(req.host.workDir, ['rev-parse', 'HEAD'], req.host.signal, sinkOptions(req.host))
  const squashedHeadSha = squashedHead.success ? squashedHead.stdout.trim() : null
  const squashOutput = [req.rebaseOutput, softReset.combinedOutput, commit.combinedOutput].filter(Boolean).join('\n\n')
  const integrity = await verifyRebaseCompletion(req.host, req.expectedBranch)
  if (integrity) return integrity
  return rebaseOutput(
    true,
    req.baseBranch,
    req.remote,
    req.baseRef,
    req.baseSha,
    req.beforeSha,
    req.rebasedHeadSha,
    squashedHeadSha,
    true,
    req.conflicts,
    squashOutput,
    null,
    null,
  )
}

type RebaseFailureCode =
  | 'abort-failed'
  | 'invalid-input'
  | 'fetch-failed'
  | 'base-resolve-failed'
  | 'prepare-failed'
  | 'rebase-failed'
  | 'conflict'
  | 'squash-failed'
  | 'timeout'
  | null

function rebaseOutput(
  rebased: boolean,
  baseBranch: string,
  remote: string | null,
  baseRef: string,
  baseSha: string | null,
  beforeSha: string | null,
  afterSha: string | null,
  squashedHeadSha: string | null,
  squashed: boolean,
  conflicts: string[],
  gitOutput: string,
  failureCode: RebaseFailureCode = null,
  exitCode: number | null = null,
  rebaseLeftInProgress: boolean = false,
  steps: RebaseStep[] = [],
): ActionResult {
  if (!rebased) {
    return fail(failureCode ?? 'rebase-failed', rebaseFailureMessage(failureCode, baseRef, conflicts, gitOutput), {
      exitCode: exitCode ?? 1,
    })
  }
  const output: JsonObject = {
    kind: 'rebase',
    status: 'completed',
    baseBranch,
    remote,
    baseRef,
    rebasedOntoSha: baseSha,
    beforeHeadSha: beforeSha,
    afterHeadSha: afterSha,
    squashed,
    squashedHeadSha,
    rebased,
    conflicts,
    rebaseLeftInProgress: false,
    output: gitOutput,
    steps: steps as unknown as JsonObject,
  }
  return succeed(output, { exitCode: exitCode ?? 0 })
}

function rebaseFailureMessage(code: RebaseFailureCode, baseRef: string, conflicts: string[], output: string): string {
  const detail = output.trim() || 'unknown error'
  if (code === 'conflict') {
    const files = conflicts.length > 0 ? ` Conflicts: ${conflicts.join(', ')}.` : ''
    return `Rebase onto ${baseRef} has unresolved conflicts.${files}`
  }
  if (code === 'fetch-failed') return `Failed to fetch ${baseRef}: ${detail}. Rebase was not started.`
  if (code === 'timeout') return `Rebase operation timed out while preparing ${baseRef}.`
  if (code === 'invalid-input') return detail
  return `Rebase onto ${baseRef} failed: ${detail}`
}

function rebaseStep(name: string, command: string, result: GitResult): RebaseStep {
  return { name, command, exitCode: result.exitCode, output: result.combinedOutput, ...timeoutMetadata(result) }
}

function timeoutMetadata(result: GitResult): Pick<RebaseStep, 'status' | 'timeoutMs'> | undefined {
  if (result.status !== 'timeout') return undefined
  return { status: 'timeout', timeoutMs: result.timeoutMs }
}

function booleanInput(input: JsonObject | null | undefined, key: string) {
  const value = input?.[key]
  if (typeof value === 'boolean') return value
  if (typeof value === 'string') {
    if (/^(true|1|yes|on)$/i.test(value)) return true
    if (/^(false|0|no|off)$/i.test(value)) return false
  }
  return undefined
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error)
}

export async function commitRebasePendingChanges(workDir: string, message: string, signal: AbortSignal) {
  return await commitPendingChanges(workDir, message, signal)
}

export async function abortRebaseIfInProgressAction(host: ActionHost) {
  return await abortRebaseIfInProgress(host)
}

export async function rebaseConflictFiles(host: ActionHost) {
  return await conflictFiles(host)
}

export async function verifyRebaseCompleteAction(host: ActionHost, baseBranch: string) {
  return await verifyRebaseComplete(host, baseBranch)
}

export function combinedRebaseGitOutput(outputs: string[]) {
  return combinedGitOutput(outputs)
}

export async function rebaseStatusAction(inputs: JsonObject, host: ActionHost): Promise<ActionResult> {
  const baseBranch = stringInput(inputs, 'baseBranch')
  if (!baseBranch) return fail('invalid-input', "Rebase status requires input 'baseBranch'")
  const remote = stringInput(inputs, 'remote') ?? null
  const baseRef = remote ? `${remote}/${baseBranch}` : baseBranch
  const opts = sinkOptions(host)
  const conflicts = await conflictFiles(host, opts)
  const rebaseInProgress = await isRebaseInProgress(host, opts)
  const head = await git(host.workDir, ['rev-parse', 'HEAD'], host.signal, opts)
  const base = await git(host.workDir, ['rev-parse', baseRef], host.signal, opts)
  const mergeBase = base.success ? await git(host.workDir, ['merge-base', baseRef, 'HEAD'], host.signal, opts) : null
  const verified =
    !rebaseInProgress &&
    conflicts.length === 0 &&
    head.success &&
    base.success &&
    mergeBase?.success === true &&
    mergeBase.stdout.trim() === base.stdout.trim()
  const output: JsonObject = {
    kind: 'rebase-status',
    status: verified ? 'verified' : 'failed',
    baseBranch,
    remote,
    baseRef,
    rebaseInProgress,
    conflicts,
    baseSha: base.success ? base.stdout.trim() : null,
    headSha: head.success ? head.stdout.trim() : null,
    mergeBaseSha: mergeBase?.success ? mergeBase.stdout.trim() : null,
    output: [base.combinedOutput, mergeBase?.combinedOutput].filter(Boolean).join('\n'),
  }
  return verified ? succeed(output) : fail('rebase-incomplete', 'Rebase is not complete or not clean')
}

async function conflictFiles(host: ActionHost, opts?: GitOptions) {
  const status = await git(host.workDir, ['diff', '--name-only', '--diff-filter=U'], host.signal, opts)
  if (!status.success || !status.stdout.trim()) return []
  return [
    ...new Set(
      status.stdout
        .split('\n')
        .map((line) => line.trim())
        .filter(Boolean),
    ),
  ]
}

interface PathProbe {
  exists: boolean
  failure: WorkspaceProbeFailure | null
}

interface HeadProbe {
  head: WorkspaceHeadState
  failure: WorkspaceProbeFailure | null
}

interface ResidualProbe {
  residual: WorkspaceResidualState
  failure: WorkspaceProbeFailure | null
}

/**
 * Capture the shared workspace-health snapshot used by the completion
 * invariant. The snapshot records residual rebase / merge / cherry-pick
 * markers, the attached branch or detached ref, worktree status, and any
 * probe failure — exactly the model `workspace-prepare` and
 * `WorkspaceManager` share.
 */
async function captureHealthSnapshot(
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

async function probeResidual(workDir: string, signal: AbortSignal, opts?: GitOptions): Promise<ResidualProbe> {
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
): Promise<PathProbe> {
  const result = await git(workDir, ['rev-parse', '--git-path', gitPath], signal, opts)
  if (!result.success) {
    return { exists: false, failure: gitFailure('residual', `git rev-parse --git-path ${gitPath}`, result) }
  }
  return { exists: pathExists(resolveGitPath(workDir, result.stdout.trim())), failure: null }
}

function gitFailure(step: string, command: string, result: GitResult): WorkspaceProbeFailure {
  return {
    step,
    message: `${command} failed: ${result.combinedOutput || `exit ${result.exitCode}`}`,
    exitCode: result.exitCode,
  }
}

/**
 * Completion invariant: after a successful rebase (and any squash) the
 * workspace must be attached to exactly the expected run branch, clean,
 * and free of every residual operation marker. Returns an ActionResult
 * branch-integrity failure when the invariant does not hold, or null when
 * the recovery is complete and may be reported as successful.
 */
async function verifyRebaseCompletion(host: ActionHost, expectedBranch: string): Promise<ActionResult | null> {
  const snapshot = await captureHealthSnapshot(host.workDir, host.signal, sinkOptions(host))
  if (snapshot.probeFailure) {
    return rebaseIntegrityFailure(
      expectedBranch,
      snapshot,
      snapshot.probeFailure.step,
      undefined,
      snapshot.probeFailure.exitCode,
    )
  }
  const evaluation = evaluateWorkspaceHealth(snapshot, expectedBranch)
  if (!evaluation.healthy) {
    return rebaseIntegrityFailure(
      expectedBranch,
      snapshot,
      'verify',
      `Health verification failed: ${evaluation.condition}`,
      1,
    )
  }
  return null
}

function rebaseIntegrityFailure(
  expectedBranch: string,
  snapshot: WorkspaceHealthSnapshot,
  operation: string,
  detail: string | undefined,
  exitCode: number | null,
): ActionResult {
  const message = workspaceHealthDiagnostic({ operation, expectedBranch, snapshot, detail })
  return fail('branch-invariant-violation', message, { exitCode: exitCode ?? 1 })
}

async function verifyRebaseComplete(host: ActionHost, baseBranch: string) {
  const opts = sinkOptions(host)
  const rebaseInProgress = await isRebaseInProgress(host, opts)
  const conflicts = await conflictFiles(host, opts)
  const head = await git(host.workDir, ['rev-parse', 'HEAD'], host.signal, opts)
  const base = await git(host.workDir, ['rev-parse', baseBranch], host.signal, opts)
  const mergeBase = base.success ? await git(host.workDir, ['merge-base', baseBranch, 'HEAD'], host.signal, opts) : null
  const branch = await git(host.workDir, ['branch', '--show-current'], host.signal, opts)
  const statusPorcelain = await git(host.workDir, ['status', '--porcelain'], host.signal, opts)

  const detached = branch.exitCode !== 0 || !branch.stdout.trim() || branch.stdout.trim() === 'HEAD'
  const dirty = statusPorcelain.success && statusPorcelain.stdout.trim().length > 0

  const ok =
    !rebaseInProgress &&
    conflicts.length === 0 &&
    !detached &&
    !dirty &&
    head.success &&
    base.success &&
    mergeBase?.success === true &&
    mergeBase.stdout.trim() === base.stdout.trim()
  const output = [
    rebaseInProgress ? 'Rebase is still in progress.' : '',
    conflicts.length > 0 ? `Conflicts remain:\n${conflicts.join('\n')}` : '',
    detached ? `HEAD is detached (branch: ${branch.stdout.trim()})` : '',
    dirty ? `Worktree is not clean:\n${statusPorcelain.stdout.trim()}` : '',
    head.combinedOutput,
    base.combinedOutput,
    mergeBase?.combinedOutput ?? '',
  ]
    .filter(Boolean)
    .join('\n')
  return { ok, output }
}

async function commitPendingChanges(workDir: string, message: string, signal: AbortSignal, opts?: GitOptions) {
  const status = await git(workDir, ['status', '--porcelain'], signal, opts)
  if (!status.success || !status.stdout.trim()) return status.success ? { ...status, combinedOutput: '' } : status

  const add = await git(workDir, ['add', '.'], signal, opts)
  if (!add.success) return add

  return await git(workDir, ['commit', '-m', message], signal, opts)
}

async function abortRebaseIfInProgress(host: ActionHost, opts?: GitOptions) {
  const inProgress = await isRebaseInProgress(host, opts)
  if (!inProgress) return okGitResult()
  return await git(host.workDir, ['rebase', '--abort'], host.signal, opts)
}

async function isRebaseInProgress(host: ActionHost, opts?: GitOptions) {
  const merge = await git(host.workDir, ['rev-parse', '--git-path', 'rebase-merge'], host.signal, opts)
  if (merge.success && pathExists(resolveGitPath(host.workDir, merge.stdout.trim()))) return true
  const apply = await git(host.workDir, ['rev-parse', '--git-path', 'rebase-apply'], host.signal, opts)
  return apply.success && pathExists(resolveGitPath(host.workDir, apply.stdout.trim()))
}

function resolveGitPath(workDir: string, path: string) {
  return path.match(/^[A-Za-z]:[\\/]|^\//) ? path : join(workDir, path)
}

function okGitResult() {
  return { success: true, stdout: '', stderr: '', exitCode: 0, combinedOutput: '' }
}

function combinedGitOutput(outputs: string[]) {
  return outputs
    .map((output) => output.trim())
    .filter(Boolean)
    .join('\n\n')
}
