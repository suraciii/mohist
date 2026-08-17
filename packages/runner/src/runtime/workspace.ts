import { constants } from 'node:fs'
import { homedir, tmpdir } from 'node:os'
import { isAbsolute, join, relative, resolve } from 'node:path'
import type { JsonObject, DispatchWorkItem } from '../core/types.js'
import { getSegments, stringAt } from '../core/json-path.js'
import { NETWORK_COMMAND_TIMEOUT_MS } from '../actions/git.js'
import { deleteDirectory, ensureDir, exists, readText, runCommand, writeText, type CommandResult } from '../system/process.js'
import { currentRunnerFileSystem, type RunnerDirectoryHandle } from '../system/filesystem.js'
import type { WorkspaceBindingIdentity, WorkspaceRegistry } from './workspace-registry.js'
import { createCredentialMaskerFromEnvironment, type TaskLogger } from './task-log.js'
import {
  redactWorkspaceDiagnostic,
  sanitizeWorkspaceDiagnostic,
  workspaceNetworkTimeout,
  WorkspaceBranchMismatchError,
  WorkspaceCorruptError,
  WorkspaceIdentityMismatchError,
  WorkspaceMissingError,
} from './workspace-errors.js'
import {
  issueWorkspacePath,
  markerPath,
  readMarker,
  workspaceBindingIdentity,
  workspaceIdentity,
  type IssueWorkspaceMarker,
} from './workspace-identity.js'
import {
  DETACHED_HEAD_REF,
  evaluateWorkspaceHealth,
  isResidualFree,
  observedBranchLabel,
  observedRefLabel,
  workspaceHealthDiagnostic,
  type WorkspaceHeadState,
  type WorkspaceHealthSnapshot,
  type WorkspaceProbeFailure,
  type WorkspaceResidualState,
} from './workspace-health.js'

export { issueWorkspacePath, readMarkerWorkflowRunId } from './workspace-identity.js'
export type { IssueWorkspaceMarker } from './workspace-identity.js'
export { WorkspaceNetworkTimeoutError } from './workspace-errors.js'

/**
 * `source` tag recorded against every captured workspace-preparation
 * line. Distinct from the action body's `action:*` tag so the web
 * viewer can phase-distinguish the clone / branch / worktree setup
 * from the action itself.
 */
export const WORKSPACE_PREP_SOURCE = 'workspace-prep'

function workspacePrepSink(log: TaskLogger | null | undefined) {
  return log ? { log, source: WORKSPACE_PREP_SOURCE } : undefined
}

// The workflow workspace is just a clone of the project repo checked out
// on a per-run branch. Preparing it is two steps: (1) have a clone at the
// workspace path, (2) be on the run branch. The run branch is the
// identity — its presence at a path means "this run is already set up
// here", so re-entering a run is cheap (just switch to its branch) and a
// new run at a reused path is a pristine re-clone. No marker file, no
// shared bare cache, no alternates.

export interface WorkspaceInfo {
  path: string
  branch?: string | null
}

export class WorkspaceManager {
  constructor(
    private readonly runnerRoot = defaultRunnerRoot(),
    private readonly registry: WorkspaceRegistry | null = null,
    private readonly runnerId = 'unknown',
  ) {}

  // Ensure this run has a usable workspace: a clone of the repo on the
  // run branch. Idempotent — a workspace already on this run's branch is
  // left alone (cheap re-entry); anything else is (re)created from the
  // latest base.
  async prepare(work: DispatchWorkItem, signal: AbortSignal, log: TaskLogger | null = null): Promise<WorkspaceInfo> {
    const variables = work.variables ?? {}
    const gitUrl = stringAt(variables, ['repository', 'gitUrl'])
    const baseBranch = stringAt(variables, ['repository', 'baseBranch'])
    const issueNumber = numberAt(variables, ['issue', 'number'])
    if (!gitUrl || !baseBranch || issueNumber === undefined) {
      throw new Error(
        `Workspace requires repository.gitUrl, repository.baseBranch, and issue.number. Got gitUrl=${gitUrl ?? 'null'}, baseBranch=${baseBranch ?? 'null'}, issueNumber=${issueNumber ?? 'undefined'}`,
      )
    }

    const runId = work.workflowRunId
    const expected = workspaceIdentity(runId)
    const binding = workspaceBindingIdentity(this.runnerRoot, this.runnerId, runId, gitUrl, baseBranch)
    this.assertExistingBinding(binding)
    const runBranch = expected.runBranch
    const workspacePath = issueWorkspacePath(this.runnerRoot, runId)
    const workspaceExistedBeforePreparation = pathExists(workspacePath)
    if (!workspaceExistedBeforePreparation) {
      await this.verifyBaseBranch(gitUrl, baseBranch, signal, log)
    }
    await withManagedWorkspaceHandle(this.runnerRoot, workspacePath, false, async (managedWorkspacePath) => {
      if (await pathExists(managedWorkspacePath)) {
        await validateWorkspaceIdentity(managedWorkspacePath, expected, gitUrl, signal, log, undefined, workspacePath)
        if (!(await this.hasRunBranch(managedWorkspacePath, runBranch, signal, log))) {
          throw new WorkspaceIdentityMismatchError(
            `Workflow workspace ${workspacePath} has no branch ${runBranch}; refusing to mutate an existing workspace.`,
            workspacePath,
            expected,
          )
        }
        await this.reenterRunBranch(managedWorkspacePath, workspacePath, runBranch, signal, log)
      } else {
        await this.bootstrap(
          managedWorkspacePath,
          workspacePath,
          gitUrl,
          baseBranch,
          expected,
          signal,
          log,
          workspaceExistedBeforePreparation,
        )
      }
    })

    if (this.registry) {
      await this.registry.register({
        issueNumber,
        workflowRunId: expected.workflowRunId,
        workspacePath,
        binding,
        runBranch: expected.runBranch,
      })
    }
    return { path: workspacePath, branch: runBranch }
  }

  async verify(work: DispatchWorkItem, signal: AbortSignal, log: TaskLogger | null = null): Promise<WorkspaceInfo> {
    const variables = work.variables ?? {}
    const issueNumber = numberAt(variables, ['issue', 'number'])
    const runId = work.workflowRunId
    const gitUrl = stringAt(variables, ['repository', 'gitUrl'])
    const baseBranch = stringAt(variables, ['repository', 'baseBranch'])
    if (!gitUrl || !baseBranch || issueNumber === undefined) {
      throw new WorkspaceIdentityMismatchError('Issue workspace identity is incomplete')
    }
    const expected = workspaceIdentity(runId)
    const binding = workspaceBindingIdentity(this.runnerRoot, this.runnerId, runId, gitUrl, baseBranch)
    this.assertExistingBinding(binding)
    const runBranch = expected.runBranch
    const workspacePath = issueWorkspacePath(this.runnerRoot, runId)
    await withManagedWorkspaceHandle(this.runnerRoot, workspacePath, true, async (managedWorkspacePath) => {
      await validateWorkspaceIdentity(managedWorkspacePath, expected, gitUrl, signal, log, undefined, workspacePath)

      if (!exists(managedWorkspacePath)) {
        throw new WorkspaceMissingError(
          `Workflow workspace ${workspacePath} is missing; workflow start materialization did not produce a bound workspace for this run.`,
          workspacePath,
        )
      }

      // Health gate: every dispatch passes through verify(), so this is
      // the per-task entry point. A residual rebase / merge / cherry-pick
      // from a prior mid-flight crash is detected and aborted here, BEFORE
      // the branch checks below — otherwise a `git checkout` from the
      // residual state would refuse with "resolve your current index
      // first" (the #166 fatality). The shared health evaluator then
      // requires the complete invariant: exact expected branch, clean
      // worktree, and no residual marker.
      await this.assertHealthyWorkspace(managedWorkspacePath, workspacePath, runBranch, signal, log)
    })

    if (this.registry) {
      await this.registry.refreshMaterializedAt(runId)
    }

    return {
      path: workspacePath,
      branch: runBranch,
    }
  }

  private async bootstrap(
    operationPath: string,
    displayPath: string,
    gitUrl: string,
    baseBranch: string,
    expected: IssueWorkspaceMarker,
    signal: AbortSignal,
    log: TaskLogger | null,
    verifyBaseBranch: boolean,
  ): Promise<void> {
    const managedPreparationPath = `${operationPath}.preparing`
    if (await pathExists(managedPreparationPath)) await deleteDirectory(managedPreparationPath)
    try {
      await assertNotSymlink(managedPreparationPath, displayPath)
      if (verifyBaseBranch) await this.verifyBaseBranch(gitUrl, baseBranch, signal, log)
      await this.cloneFresh(managedPreparationPath, displayPath, gitUrl, signal, log)
      await validateWorkspaceOrigin(managedPreparationPath, gitUrl, signal, log, displayPath)
      await this.restoreOrCreateRunBranch(
        managedPreparationPath,
        displayPath,
        baseBranch,
        expected.runBranch,
        signal,
        log,
      )
      await ensureMarkerExcluded(managedPreparationPath)
      await writeText(markerPath(managedPreparationPath), JSON.stringify(expected, null, 2))
      await validateWorkspaceIdentity(managedPreparationPath, expected, gitUrl, signal, log, undefined, displayPath)
      // Commit the prepared clone through the verified directory handle. The
      // stable path is display-only here; using it as the rename target would
      // follow a parent symlink installed after handle acquisition.
      await currentRunnerFileSystem().rename(managedPreparationPath, operationPath)
    } catch (error) {
      await deleteDirectory(managedPreparationPath).catch(() => {})
      throw error
    }
  }

  // True only when <path> is a git clone that already has <runBranch> —
  // i.e. this run is already set up here. Everything else (missing dir,
  // non-git dir, a previous run's clone) is treated as "not prepared".
  private async hasRunBranch(
    workspacePath: string,
    runBranch: string,
    signal: AbortSignal,
    log: TaskLogger | null = null,
  ): Promise<boolean> {
    if (!exists(workspacePath) || !exists(join(workspacePath, '.git'))) return false
    const sink = workspacePrepSink(log)
    const result = await runCommand(
      'git',
      ['-C', workspacePath, 'rev-parse', '--verify', `refs/heads/${runBranch}`],
      '.',
      signal,
      undefined,
      sink ? { onLine: (line) => sink.log.write(sink.source, line) } : undefined,
    )
    return result.exitCode === 0
  }

  private async cloneFresh(
    workspacePath: string,
    displayPath: string,
    gitUrl: string,
    signal: AbortSignal,
    log: TaskLogger | null = null,
  ): Promise<void> {
    if (exists(workspacePath)) await deleteDirectory(workspacePath)
    await ensureDir(join(workspacePath, '..'))
    const sink = workspacePrepSink(log)
    const result = await runCommand(
      'git',
      ['clone', '--filter=blob:none', '--no-checkout', '--no-tags', gitUrl, workspacePath],
      '.',
      signal,
      undefined,
      sink
        ? { onLine: (line) => sink.log.write(sink.source, line), timeoutMs: NETWORK_COMMAND_TIMEOUT_MS }
        : { timeoutMs: NETWORK_COMMAND_TIMEOUT_MS },
    )
    if (result.exitCode !== 0) {
      if (result.status === 'timeout')
        throw workspaceNetworkTimeout(
          'git-clone',
          `clone --filter=blob:none --no-checkout --no-tags ${gitUrl} ${displayPath}`,
          result,
          workspacePath,
          displayPath,
        )
      throw new Error(
        `git clone failed for ${redactWorkspaceDiagnostic(gitUrl)}: ${sanitizeWorkspaceDiagnostic(result.stderr || result.stdout, workspacePath, displayPath)}`,
      )
    }
  }

  // Create the run branch off the latest base. A fresh clone already has
  // up-to-date origin/<base> refs, so no separate fetch is needed.
  private async restoreOrCreateRunBranch(
    workspacePath: string,
    displayPath: string,
    baseBranch: string,
    runBranch: string,
    signal: AbortSignal,
    log: TaskLogger | null = null,
  ): Promise<void> {
    const sink = workspacePrepSink(log)
    const branchRef = `refs/remotes/origin/${runBranch}`
    const existing = await runCommand(
      'git',
      ['-C', workspacePath, 'show-ref', '--verify', '--quiet', branchRef],
      workspacePath,
      signal,
      undefined,
      sink ? { onLine: (line) => sink.log.write(sink.source, line) } : undefined,
    )
    const source = existing.exitCode === 0 ? `origin/${runBranch}` : `origin/${baseBranch}`
    const create = await runCommand(
      'git',
      ['-C', workspacePath, 'checkout', '-B', runBranch, source],
      workspacePath,
      signal,
      undefined,
      sink
        ? { onLine: (line) => sink.log.write(sink.source, line), timeoutMs: NETWORK_COMMAND_TIMEOUT_MS }
        : { timeoutMs: NETWORK_COMMAND_TIMEOUT_MS },
    )
    if (create.exitCode !== 0) {
      if (create.status === 'timeout')
        throw workspaceNetworkTimeout(
          'git-checkout',
          `checkout -B ${runBranch} ${source}`,
          create,
          workspacePath,
          displayPath,
        )
      throw new Error(`Configured base branch '${baseBranch}' cannot be resolved from repository gitUrl.`)
    }
  }

  // Fail fast — before creating anything on disk — when the configured
  // base branch genuinely does not exist at the source. A non-zero exit
  // (repo unreachable / auth) is left for the clone step to surface with
  // its own error; only a reachable repo with an absent branch fails here.
  private async verifyBaseBranch(
    gitUrl: string,
    baseBranch: string,
    signal: AbortSignal,
    log: TaskLogger | null = null,
  ): Promise<void> {
    const sink = workspacePrepSink(log)
    const result = await runCommand(
      'git',
      ['ls-remote', '--heads', gitUrl, baseBranch],
      '.',
      signal,
      undefined,
      sink
        ? { onLine: (line) => sink.log.write(sink.source, line), timeoutMs: NETWORK_COMMAND_TIMEOUT_MS }
        : { timeoutMs: NETWORK_COMMAND_TIMEOUT_MS },
    )
    if (result.status === 'timeout')
      throw workspaceNetworkTimeout('git-ls-remote', `ls-remote --heads ${gitUrl} ${baseBranch}`, result)
    if (result.exitCode === 0 && result.stdout.trim() === '') {
      throw new Error(`Configured base branch '${baseBranch}' cannot be resolved from repository gitUrl.`)
    }
  }

  // Switch an already-prepared workspace back onto its run branch,
  // following the shared workspace-health repair state machine. A healthy
  // workspace (already on the run branch, clean, non-residual) takes the
  // fast path with no mutation. Otherwise residual rebase / merge /
  // cherry-pick state is aborted in order with re-probes, a dirty tree is
  // reset and cleaned, and the existing expected branch is checked out —
  // never force-created, replaced, or re-cloned — before a complete final
  // probe confirms the invariant. Any failed step reports the shared
  // expected/observed diagnostic.
  private async reenterRunBranch(
    workspacePath: string,
    displayPath: string,
    runBranch: string,
    signal: AbortSignal,
    log: TaskLogger | null = null,
  ): Promise<void> {
    const sink = workspacePrepSink(log)
    const lineOptions = sink ? { onLine: (line: string) => sink.log.write(sink.source, line) } : undefined

    let snapshot = await this.captureHealthSnapshot(workspacePath, displayPath, signal, log)
    if (snapshot.probeFailure) {
      this.probeFailureThrow(displayPath, runBranch, snapshot)
    }
    if (evaluateWorkspaceHealth(snapshot, runBranch).healthy) {
      return
    }

    if (!isResidualFree(snapshot.residual)) {
      await this.abortResidualState(workspacePath, displayPath, snapshot.residual, runBranch, signal, log)
      snapshot = await this.captureHealthSnapshot(workspacePath, displayPath, signal, log)
      if (snapshot.probeFailure) {
        this.probeFailureThrow(displayPath, runBranch, snapshot)
      }
    }

    if (snapshot.porcelain.trim() !== '') {
      const reset = await runCommand(
        'git',
        ['-C', workspacePath, 'reset', '--hard', runBranch],
        workspacePath,
        signal,
        undefined,
        lineOptions,
      )
      if (reset.exitCode !== 0) {
        throw this.healthFailure(
          displayPath,
          runBranch,
          await this.captureHealthSnapshot(workspacePath, displayPath, signal, log),
          'reset',
          `git reset --hard ${runBranch} failed: ${sanitizeWorkspaceDiagnostic(reset.stderr || reset.stdout || `exit ${reset.exitCode}`, workspacePath, displayPath)}`,
          reset.exitCode,
        )
      }
      const clean = await runCommand(
        'git',
        ['-C', workspacePath, 'clean', '-fd'],
        workspacePath,
        signal,
        undefined,
        lineOptions,
      )
      if (clean.exitCode !== 0) {
        throw this.healthFailure(
          displayPath,
          runBranch,
          await this.captureHealthSnapshot(workspacePath, displayPath, signal, log),
          'clean',
          `git clean -fd failed: ${sanitizeWorkspaceDiagnostic(clean.stderr || clean.stdout || `exit ${clean.exitCode}`, workspacePath, displayPath)}`,
          clean.exitCode,
        )
      }
      snapshot = await this.captureHealthSnapshot(workspacePath, displayPath, signal, log)
      if (snapshot.probeFailure) {
        this.probeFailureThrow(displayPath, runBranch, snapshot)
      }
    }

    if (snapshot.head.ref !== runBranch) {
      const checkout = await runCommand(
        'git',
        ['-C', workspacePath, 'checkout', runBranch],
        workspacePath,
        signal,
        undefined,
        lineOptions,
      )
      if (checkout.exitCode !== 0) {
        throw this.healthFailure(
          displayPath,
          runBranch,
          await this.captureHealthSnapshot(workspacePath, displayPath, signal, log),
          'checkout',
          `git checkout ${runBranch} failed: ${sanitizeWorkspaceDiagnostic(checkout.stderr || checkout.stdout || `exit ${checkout.exitCode}`, workspacePath, displayPath)}`,
          checkout.exitCode,
        )
      }
    }

    const verify = await this.captureHealthSnapshot(workspacePath, displayPath, signal, log)
    if (verify.probeFailure) {
      this.probeFailureThrow(displayPath, runBranch, verify)
    }
    const evaluation = evaluateWorkspaceHealth(verify, runBranch)
    if (!evaluation.healthy) {
      throw this.healthFailure(displayPath, runBranch, verify, 'verify', `health verification failed: ${evaluation.condition}`, 1)
    }
  }

  // Abort residual rebase / merge / cherry-pick operations in a fixed
  // order, re-probing each aborted state. An abort failure or an
  // unverifiable residual state stops the repair with a shared diagnostic.
  private async abortResidualState(
    workspacePath: string,
    displayPath: string,
    residual: WorkspaceResidualState,
    runBranch: string,
    signal: AbortSignal,
    log: TaskLogger | null = null,
  ): Promise<void> {
    const sink = workspacePrepSink(log)
    const lineOptions = sink ? { onLine: (line: string) => sink.log.write(sink.source, line) } : undefined

    if (residual.rebaseMerge || residual.rebaseApply) {
      const abort = await runCommand('git', ['-C', workspacePath, 'rebase', '--abort'], workspacePath, signal, undefined, lineOptions)
      if (abort.exitCode !== 0) {
        throw this.healthFailure(
          displayPath,
          runBranch,
          await this.captureHealthSnapshot(workspacePath, displayPath, signal, log),
          'abort-rebase',
          `git rebase --abort failed: ${sanitizeWorkspaceDiagnostic(abort.stderr || abort.stdout || `exit ${abort.exitCode}`, workspacePath, displayPath)}`,
          abort.exitCode,
        )
      }
      const after = await this.detectResidualState(workspacePath, signal)
      if (after.rebaseMerge || after.rebaseApply) {
        throw this.healthFailure(
          displayPath,
          runBranch,
          await this.captureHealthSnapshot(workspacePath, displayPath, signal, log),
          'abort-rebase',
          'Rebase is still in progress after abort',
          1,
        )
      }
    }

    if (residual.mergeHead) {
      const abort = await runCommand('git', ['-C', workspacePath, 'merge', '--abort'], workspacePath, signal, undefined, lineOptions)
      if (abort.exitCode !== 0) {
        throw this.healthFailure(
          displayPath,
          runBranch,
          await this.captureHealthSnapshot(workspacePath, displayPath, signal, log),
          'abort-merge',
          `git merge --abort failed: ${sanitizeWorkspaceDiagnostic(abort.stderr || abort.stdout || `exit ${abort.exitCode}`, workspacePath, displayPath)}`,
          abort.exitCode,
        )
      }
      const after = await this.detectResidualState(workspacePath, signal)
      if (after.mergeHead) {
        throw this.healthFailure(
          displayPath,
          runBranch,
          await this.captureHealthSnapshot(workspacePath, displayPath, signal, log),
          'abort-merge',
          'Merge is still in progress after abort',
          1,
        )
      }
    }

    if (residual.cherryPickHead) {
      const abort = await runCommand('git', ['-C', workspacePath, 'cherry-pick', '--abort'], workspacePath, signal, undefined, lineOptions)
      if (abort.exitCode !== 0) {
        throw this.healthFailure(
          displayPath,
          runBranch,
          await this.captureHealthSnapshot(workspacePath, displayPath, signal, log),
          'abort-cherry-pick',
          `git cherry-pick --abort failed: ${sanitizeWorkspaceDiagnostic(abort.stderr || abort.stdout || `exit ${abort.exitCode}`, workspacePath, displayPath)}`,
          abort.exitCode,
        )
      }
      const after = await this.detectResidualState(workspacePath, signal)
      if (after.cherryPickHead) {
        throw this.healthFailure(
          displayPath,
          runBranch,
          await this.captureHealthSnapshot(workspacePath, displayPath, signal, log),
          'abort-cherry-pick',
          'Cherry-pick is still in progress after abort',
          1,
        )
      }
    }
  }

  // Capture the shared workspace-health snapshot using the manager's own
  // narrow git adapter: branch probe, detached ref, worktree status, and
  // residual markers. Probe output is sanitized to the stable display path.
  private async captureHealthSnapshot(
    workspacePath: string,
    displayPath: string,
    signal: AbortSignal,
    log: TaskLogger | null = null,
  ): Promise<WorkspaceHealthSnapshot> {
    const sink = workspacePrepSink(log)
    const lineOptions = sink ? { onLine: (line: string) => sink.log.write(sink.source, line) } : undefined
    const [branchResult, headResult, statusResult, residual] = await Promise.all([
      runCommand('git', ['-C', workspacePath, 'rev-parse', '--abbrev-ref', 'HEAD'], '.', signal, undefined, lineOptions),
      runCommand('git', ['-C', workspacePath, 'rev-parse', 'HEAD'], '.', signal, undefined, lineOptions),
      runCommand('git', ['-C', workspacePath, 'status', '--porcelain'], '.', signal, undefined, lineOptions),
      this.detectResidualState(workspacePath, signal),
    ])
    let ref = DETACHED_HEAD_REF
    if (branchResult.exitCode === 0) {
      const trimmed = branchResult.stdout.trim()
      if (trimmed !== '' && trimmed !== 'HEAD') ref = trimmed
    }
    const head: WorkspaceHeadState = {
      commit: headResult.exitCode === 0 ? headResult.stdout.trim() : '',
      ref,
    }
    const probeFailure = branchResult.exitCode !== 0
      ? this.gitProbeFailure('head-ref', 'git rev-parse --abbrev-ref HEAD', branchResult, workspacePath, displayPath)
      : headResult.exitCode !== 0
        ? this.gitProbeFailure('head', 'git rev-parse HEAD', headResult, workspacePath, displayPath)
        : statusResult.exitCode !== 0
          ? this.gitProbeFailure('status', 'git status --porcelain', statusResult, workspacePath, displayPath)
          : null
    return {
      residual,
      head,
      porcelain: statusResult.exitCode === 0 ? statusResult.stdout : '',
      probeFailure,
    }
  }

  // Verify the complete workspace-health invariant using the shared
  // evaluator. Residual operations are aborted first (with re-probes), then
  // the exact expected branch, clean worktree, and absence of every
  // residual marker is required. Any failure carries the shared
  // expected/observed diagnostic.
  private async assertHealthyWorkspace(
    workspacePath: string,
    displayPath: string,
    runBranch: string,
    signal: AbortSignal,
    log: TaskLogger | null = null,
  ): Promise<void> {
    let snapshot = await this.captureHealthSnapshot(workspacePath, displayPath, signal, log)
    if (snapshot.probeFailure) {
      this.probeFailureThrow(displayPath, runBranch, snapshot)
    }
    if (!isResidualFree(snapshot.residual)) {
      await this.abortResidualState(workspacePath, displayPath, snapshot.residual, runBranch, signal, log)
      snapshot = await this.captureHealthSnapshot(workspacePath, displayPath, signal, log)
      if (snapshot.probeFailure) {
        this.probeFailureThrow(displayPath, runBranch, snapshot)
      }
      if (!isResidualFree(snapshot.residual)) {
        throw this.healthFailure(displayPath, runBranch, snapshot, 'verify', 'residual operation state remains after abort', 1)
      }
    }
    const evaluation = evaluateWorkspaceHealth(snapshot, runBranch)
    if (!evaluation.healthy) {
      throw this.healthFailure(displayPath, runBranch, snapshot, 'verify', `health verification failed: ${evaluation.condition}`, 1)
    }
  }

  private async detectResidualState(
    workspacePath: string,
    _signal: AbortSignal,
  ): Promise<WorkspaceResidualState> {
    return {
      rebaseMerge: exists(join(workspacePath, '.git', 'rebase-merge')),
      rebaseApply: exists(join(workspacePath, '.git', 'rebase-apply')),
      mergeHead: exists(join(workspacePath, '.git', 'MERGE_HEAD')),
      cherryPickHead: exists(join(workspacePath, '.git', 'CHERRY_PICK_HEAD')),
    }
  }

  private gitProbeFailure(
    step: string,
    command: string,
    result: CommandResult,
    workspacePath: string,
    displayPath: string,
  ): WorkspaceProbeFailure {
    const output = sanitizeWorkspaceDiagnostic(
      [result.stderr.trim(), result.stdout.trim()].filter(Boolean).join('\n'),
      workspacePath,
      displayPath,
    )
    return { step, message: `${command} failed: ${output || `exit ${result.exitCode}`}`, exitCode: result.exitCode }
  }

  private healthFailure(
    displayPath: string,
    runBranch: string,
    snapshot: WorkspaceHealthSnapshot,
    operation: string,
    detail: string | undefined,
    exitCode: number | null,
  ): WorkspaceBranchMismatchError {
    const observedBranch = snapshot.probeFailure ? null : observedBranchLabel(snapshot) === '(detached)' ? null : observedBranchLabel(snapshot)
    const observedRef = snapshot.probeFailure ? null : observedRefLabel(snapshot)
    return new WorkspaceBranchMismatchError(
      workspaceHealthDiagnostic({ operation, expectedBranch: runBranch, snapshot, detail }),
      displayPath,
      runBranch,
      observedBranch,
      observedRef,
      detail,
    )
  }

  private probeFailureThrow(displayPath: string, runBranch: string, snapshot: WorkspaceHealthSnapshot): never {
    const failure = snapshot.probeFailure
    throw this.healthFailure(displayPath, runBranch, snapshot, failure?.step ?? 'probe', undefined, failure?.exitCode ?? null)
  }

  private assertExistingBinding(expected: WorkspaceBindingIdentity): void {
    const existing = this.registry?.get(expected.workflowRunId)
    const binding = existing?.binding
    if (!binding) return
    const matches =
      binding.runnerId === expected.runnerId &&
      resolve(binding.runnerRoot) === resolve(expected.runnerRoot) &&
      binding.workflowRunId === expected.workflowRunId &&
      binding.gitUrl === expected.gitUrl &&
      binding.baseBranch === expected.baseBranch
    if (!matches) {
      throw new WorkspaceIdentityMismatchError(
        `Workflow workspace ${issueWorkspacePath(this.runnerRoot, expected.workflowRunId)} binding identity does not match the requested runner or repository`,
        issueWorkspacePath(this.runnerRoot, expected.workflowRunId),
      )
    }
  }
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

async function validateWorkspaceOrigin(
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

async function assertNotSymlink(path: string, displayPath = path): Promise<void> {
  try {
    if ((await currentRunnerFileSystem().lstat(path)).isSymbolicLink()) {
      throw new WorkspaceIdentityMismatchError(`Preparation path ${displayPath} is symlinked`, displayPath)
    }
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw error
  }
}

function pathExists(path: string): boolean {
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

async function ensureMarkerExcluded(workspacePath: string) {
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

function numberAt(value: JsonObject | undefined, path: string[]): number | undefined {
  const found = getSegments(value, path)
  return typeof found === 'number' ? found : undefined
}
