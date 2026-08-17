import { join, resolve } from 'node:path'
import type { DispatchWorkItem } from '../core/types.js'
import { stringAt } from '../core/json-path.js'
import { NETWORK_COMMAND_TIMEOUT_MS } from '../actions/git.js'
import { deleteDirectory, ensureDir, exists, runCommand, writeText, type CommandResult } from '../system/process.js'
import { currentRunnerFileSystem } from '../system/filesystem.js'
import type { WorkspaceBindingIdentity, WorkspaceRegistry } from './workspace-registry.js'
import type { TaskLogger } from './task-log.js'
import {
  redactWorkspaceDiagnostic,
  sanitizeWorkspaceDiagnostic,
  workspaceNetworkTimeout,
  WorkspaceBranchMismatchError,
  WorkspaceIdentityMismatchError,
  WorkspaceMissingError,
} from './workspace-errors.js'
import {
  issueWorkspacePath,
  markerPath,
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
import {
  assertNotSymlink,
  defaultRunnerRoot,
  ensureMarkerExcluded,
  numberAt,
  pathExists,
  validateWorkspaceIdentity,
  validateWorkspaceOrigin,
  withManagedWorkspaceHandle,
  workspacePrepSink,
} from './workspace-managed.js'

export { issueWorkspacePath, readMarkerWorkflowRunId } from './workspace-identity.js'
export type { IssueWorkspaceMarker } from './workspace-identity.js'
export { WorkspaceNetworkTimeoutError } from './workspace-errors.js'
export {
  WORKSPACE_PREP_SOURCE,
  assertManagedWorkspacePath,
  defaultRunnerRoot,
  runnerVariables,
  slugify,
  validateWorkspaceIdentity,
  withManagedWorkspacePath,
  withManagedWorkspaceHandle,
} from './workspace-managed.js'

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
      throw this.healthFailure(
        displayPath,
        runBranch,
        verify,
        'verify',
        `health verification failed: ${evaluation.condition}`,
        1,
      )
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
      const abort = await runCommand(
        'git',
        ['-C', workspacePath, 'rebase', '--abort'],
        workspacePath,
        signal,
        undefined,
        lineOptions,
      )
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
      const abort = await runCommand(
        'git',
        ['-C', workspacePath, 'merge', '--abort'],
        workspacePath,
        signal,
        undefined,
        lineOptions,
      )
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
      const abort = await runCommand(
        'git',
        ['-C', workspacePath, 'cherry-pick', '--abort'],
        workspacePath,
        signal,
        undefined,
        lineOptions,
      )
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
      runCommand(
        'git',
        ['-C', workspacePath, 'rev-parse', '--abbrev-ref', 'HEAD'],
        '.',
        signal,
        undefined,
        lineOptions,
      ),
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
    const probeFailure =
      branchResult.exitCode !== 0
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
        throw this.healthFailure(
          displayPath,
          runBranch,
          snapshot,
          'verify',
          'residual operation state remains after abort',
          1,
        )
      }
    }
    const evaluation = evaluateWorkspaceHealth(snapshot, runBranch)
    if (!evaluation.healthy) {
      throw this.healthFailure(
        displayPath,
        runBranch,
        snapshot,
        'verify',
        `health verification failed: ${evaluation.condition}`,
        1,
      )
    }
  }

  private async detectResidualState(workspacePath: string, _signal: AbortSignal): Promise<WorkspaceResidualState> {
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
    const observedBranch = snapshot.probeFailure
      ? null
      : observedBranchLabel(snapshot) === '(detached)'
        ? null
        : observedBranchLabel(snapshot)
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
    throw this.healthFailure(
      displayPath,
      runBranch,
      snapshot,
      failure?.step ?? 'probe',
      undefined,
      failure?.exitCode ?? null,
    )
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
