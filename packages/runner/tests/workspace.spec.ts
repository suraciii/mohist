import { basename, join } from 'node:path'
import { describe, expect, it as vitestIt } from 'vitest'
import { NETWORK_COMMAND_TIMEOUT_MS } from '../src/actions/git.js'
import { issueWorkspacePath, withManagedWorkspacePath, WorkspaceManager, slugify } from '../src/runtime/workspace.js'
import { WorkspaceRegistry } from '../src/runtime/workspace-registry.js'
import { DefaultCleanupRunner } from '../src/runtime/cleanup-loop.js'
import { currentRunnerFileSystem } from '../src/system/filesystem.js'
import { createTestTempDir } from './support/temp-dir.js'
import { MemoryDirectoryHandleFileSystem } from './support/memory-filesystem.js'
import {
  FakeGitRunner,
  fileSystem,
  gitRunner,
  it,
  commandResult,
  withWorkspaceFileSystem,
  work,
  managedPath,
  managedPathPattern,
  cleanupEntry,
} from './workspace-test-helpers.js'

describe('WorkspaceManager.prepare', () => {
  it.runIf(process.platform === 'linux')('PublicManagedPath_NeverExposesTheProcessFdPath', async () => {
    const root = await createTestTempDir('mohist-workspace-')
    const runnerRoot = join(root, 'runner')
    const workspacePath = issueWorkspacePath(runnerRoot, 'wr-public-path')

    const observed = await withManagedWorkspacePath(runnerRoot, workspacePath, false, async (path) => path)

    expect(observed).toBe(workspacePath)
    expect(observed).not.toMatch(/\/proc\/\d+\/fd\/\d+/)
  })

  it('FreshRun_CreatesRunBranchAndWorkspaceMarker', async () => {
    const root = await createTestTempDir('mohist-workspace-')
    const runnerRoot = join(root, 'runner')
    const manager = new WorkspaceManager(runnerRoot)

    const workspace = await manager.prepare(work('wr-1'), new AbortController().signal)

    const expectedPath = issueWorkspacePath(runnerRoot, 'wr-1')
    expect(workspace).toEqual({
      path: expectedPath,
      branch: 'mohist/run-wr-1',
    })
    expect(gitRunner.commandArgs()).toContainEqual([
      'ls-remote',
      '--heads',
      'https://example.test/mohist.git',
      'master',
    ])
    expect(gitRunner.commandArgs()).toContainEqual([
      'clone',
      '--filter=blob:none',
      '--no-checkout',
      '--no-tags',
      'https://example.test/mohist.git',
      managedPath(`${expectedPath}.preparing`),
    ])
    expect(gitRunner.commandArgs()).toContainEqual([
      '-C',
      managedPath(`${expectedPath}.preparing`),
      'checkout',
      '-B',
      'mohist/run-wr-1',
      'origin/master',
    ])
    expect(await fileSystem.readText(join(workspace.path, '.mohist', 'workspace.json'))).toBe(
      JSON.stringify(
        {
          workflowRunId: 'wr-1',
          runBranch: 'mohist/run-wr-1',
        },
        null,
        2,
      ),
    )
  })

  it('MissingWorkspace_RestoresExistingRemoteRunBranch', async () => {
    const root = await createTestTempDir('mohist-workspace-')
    const runnerRoot = join(root, 'runner')
    gitRunner.remoteBranches.add('mohist/run-wr-restore')

    await new WorkspaceManager(runnerRoot).prepare(work('wr-restore'), new AbortController().signal)

    const workspacePath = issueWorkspacePath(runnerRoot, 'wr-restore')
    expect(gitRunner.commandArgs()).toContainEqual([
      '-C',
      managedPath(`${workspacePath}.preparing`),
      'checkout',
      '-B',
      'mohist/run-wr-restore',
      'origin/mohist/run-wr-restore',
    ])
  })

  it('DirectoryHandlePath_UsesTheInjectedDirectoryHandle', async () => {
    await withWorkspaceFileSystem(new MemoryDirectoryHandleFileSystem(), async () => {
      const root = await createTestTempDir('mohist-workspace-')
      const runnerRoot = join(root, 'runner')
      await new WorkspaceManager(runnerRoot).prepare(work('wr-child-path'), new AbortController().signal)

      const clone = gitRunner.commandArgs().find((args) => args[0] === 'clone')
      expect(clone?.at(-1)).toMatch(/\/memory-handle-\d+\/wr-child-path\.preparing$/)
    })
  })

  it('SameRunReentry_ReusesWorkspaceWithoutRecloning', async () => {
    const root = await createTestTempDir('mohist-workspace-')
    const manager = new WorkspaceManager(join(root, 'runner'))
    const item = work('wr-1')

    const first = await manager.prepare(item, new AbortController().signal)
    await fileSystem.writeText(join(first.path, 'draft.txt'), 'draft\n')
    gitRunner.calls.length = 0

    const second = await manager.prepare(item, new AbortController().signal)

    expect(second.path).toBe(first.path)
    expect(await fileSystem.readText(join(second.path, 'draft.txt'))).toBe('draft\n')
    expect(gitRunner.commandArgs()).not.toContainEqual(['clone', 'https://example.test/mohist.git', first.path])
    // Healthy re-entry takes the shared-health fast path: no checkout, no reset,
    // no abort — the already-attached clean workspace is left untouched.
    expect(gitRunner.commandArgs()).not.toContainEqual(['-C', managedPath(first.path), 'checkout', 'mohist/run-wr-1'])
    expect(gitRunner.commandArgs().some((args) => args.includes('-B'))).toBe(false)
    expect(gitRunner.commandArgs().some((args) => args.includes('reset'))).toBe(false)
    expect(gitRunner.commandArgs().some((args) => args.includes('--abort'))).toBe(false)
  })

  it('RestartWithNewRun_UsesADistinctRunWorkspace', async () => {
    const root = await createTestTempDir('mohist-workspace-')
    const manager = new WorkspaceManager(join(root, 'runner'))

    const first = await manager.prepare(work('wr-old'), new AbortController().signal)
    await fileSystem.writeText(join(first.path, 'stale.txt'), 'old run data\n')
    gitRunner.calls.length = 0

    const second = await manager.prepare(work('wr-new'), new AbortController().signal)

    expect(second.path).not.toBe(first.path)
    expect(fileSystem.exists(join(first.path, 'stale.txt'))).toBe(true)
    expect(second.branch).toBe('mohist/run-wr-new')
    expect(gitRunner.commandArgs()).toContainEqual([
      'clone',
      '--filter=blob:none',
      '--no-checkout',
      '--no-tags',
      'https://example.test/mohist.git',
      managedPath(`${second.path}.preparing`),
    ])
  })

  it('MissingBaseBranch_FailsBeforeClone', async () => {
    const root = await createTestTempDir('mohist-workspace-')
    const manager = new WorkspaceManager(join(root, 'runner'))
    const item = work('wr-1', 'does-not-exist')

    await expect(manager.prepare(item, new AbortController().signal)).rejects.toThrow(/cannot be resolved/)

    expect(gitRunner.commandArgs()).not.toContainEqual(expect.arrayContaining(['clone']))
    expect(fileSystem.exists(join(root, 'runner', 'workspaces'))).toBe(false)
  })

  it('CloneFailure_IsFatalAndDropsPartialWorkspace', async () => {
    const root = await createTestTempDir('mohist-workspace-')
    const runnerRoot = join(root, 'runner')
    const manager = new WorkspaceManager(runnerRoot)
    gitRunner.cloneResult = commandResult(1, '', 'remote unavailable')

    await expect(manager.prepare(work('wr-first'), new AbortController().signal)).rejects.toThrow(/git clone failed/)

    expect(fileSystem.exists(issueWorkspacePath(runnerRoot, 'wr-first'))).toBe(false)
  })

  it('BaseBranchLsRemoteTimeout_FailsBeforeCloneWithStructuredStep', async () => {
    const root = await createTestTempDir('mohist-workspace-')
    const manager = new WorkspaceManager(join(root, 'runner'))
    gitRunner.lsRemoteResult = {
      exitCode: 124,
      stdout: '',
      stderr: `Command timed out after ${NETWORK_COMMAND_TIMEOUT_MS / 1000}s\n`,
      status: 'timeout',
      timeoutMs: NETWORK_COMMAND_TIMEOUT_MS,
    }

    await expect(manager.prepare(work('wr-timeout'), new AbortController().signal)).rejects.toMatchObject({
      kind: 'workspace-network-timeout',
      step: {
        name: 'git-ls-remote',
        command: 'ls-remote --heads https://example.test/mohist.git master',
        exitCode: 124,
        status: 'timeout',
        timeoutMs: NETWORK_COMMAND_TIMEOUT_MS,
      },
    })
    expect(gitRunner.commandArgs().some((args) => args[0] === 'clone')).toBe(false)
  })

  it('CloneTimeout_FailsWithStructuredStep', async () => {
    const root = await createTestTempDir('mohist-workspace-')
    const runnerRoot = join(root, 'runner')
    const manager = new WorkspaceManager(runnerRoot)
    gitRunner.cloneResult = {
      exitCode: 124,
      stdout: '',
      stderr: `Command timed out after ${NETWORK_COMMAND_TIMEOUT_MS / 1000}s\n`,
      status: 'timeout',
      timeoutMs: NETWORK_COMMAND_TIMEOUT_MS,
    }

    await expect(manager.prepare(work('wr-timeout'), new AbortController().signal)).rejects.toMatchObject({
      name: 'WorkspaceNetworkTimeoutError',
      step: {
        name: 'git-clone',
        command: `clone --filter=blob:none --no-checkout --no-tags https://example.test/mohist.git ${issueWorkspacePath(runnerRoot, 'wr-timeout')}`,
        exitCode: 124,
        status: 'timeout',
        timeoutMs: NETWORK_COMMAND_TIMEOUT_MS,
      },
    })
    expect(gitRunner.commandArgs()).toContainEqual([
      'clone',
      '--filter=blob:none',
      '--no-checkout',
      '--no-tags',
      'https://example.test/mohist.git',
      managedPath(`${issueWorkspacePath(runnerRoot, 'wr-timeout')}.preparing`),
    ])
    const workspacePath = issueWorkspacePath(runnerRoot, 'wr-timeout')
    const clone = gitRunner.calls.find((call) => call.args[0] === 'clone')
    expect(clone?.args.at(-1)).toBe(managedPath(`${workspacePath}.preparing`))
    expect(clone?.timeoutMs).toBe(NETWORK_COMMAND_TIMEOUT_MS)
    expect(fileSystem.exists(workspacePath)).toBe(false)
    expect(fileSystem.exists(`${workspacePath}.preparing`)).toBe(false)
  })

  it('CheckoutTimeout_FailsWithStructuredStepAndDropsPartialWorkspace', async () => {
    const root = await createTestTempDir('mohist-workspace-')
    const runnerRoot = join(root, 'runner')
    const manager = new WorkspaceManager(runnerRoot)
    gitRunner.branchCheckoutResult = {
      exitCode: 124,
      stdout: '',
      stderr: `Command timed out after ${NETWORK_COMMAND_TIMEOUT_MS / 1000}s\n`,
      status: 'timeout',
      timeoutMs: NETWORK_COMMAND_TIMEOUT_MS,
    }

    await expect(manager.prepare(work('wr-checkout-timeout'), new AbortController().signal)).rejects.toMatchObject({
      name: 'WorkspaceNetworkTimeoutError',
      step: {
        name: 'git-checkout',
        command: 'checkout -B mohist/run-wr-checkout-timeout origin/master',
        exitCode: 124,
        status: 'timeout',
        timeoutMs: NETWORK_COMMAND_TIMEOUT_MS,
      },
    })
    const workspacePath = issueWorkspacePath(runnerRoot, 'wr-checkout-timeout')
    expect(fileSystem.exists(workspacePath)).toBe(false)
    expect(fileSystem.exists(`${workspacePath}.preparing`)).toBe(false)
    expect(gitRunner.calls.find((call) => call.args.includes('checkout') && call.args.includes('-B'))?.timeoutMs).toBe(
      NETWORK_COMMAND_TIMEOUT_MS,
    )
  })

  it('Preparation_DoesNotUseGitWorktreeCommands', async () => {
    const root = await createTestTempDir('mohist-workspace-')
    const manager = new WorkspaceManager(join(root, 'runner'))

    await manager.prepare(work('wr-1'), new AbortController().signal)

    expect(gitRunner.commandArgs().filter((args) => args.includes('worktree'))).toEqual([])
  })

  it('ServerSuppliedPath_IsIgnoredForIssueRuns', async () => {
    const root = await createTestTempDir('mohist-workspace-')
    const runnerRoot = join(root, 'runner')
    const suppliedWorkspacePath = join(runnerRoot, 'supplied', 'workspaces', 'issue-9')
    const item = work('wr-supplied')
    ;(item.variables as Record<string, unknown>).workspace = { path: suppliedWorkspacePath }
    const manager = new WorkspaceManager(runnerRoot)

    const result = await manager.prepare(item, new AbortController().signal)

    expect(result.path).toBe(issueWorkspacePath(runnerRoot, 'wr-supplied'))
    expect(result.branch).toBe('mohist/run-wr-supplied')
    expect(gitRunner.commandArgs()).toContainEqual([
      'clone',
      '--filter=blob:none',
      '--no-checkout',
      '--no-tags',
      'https://example.test/mohist.git',
      managedPath(`${result.path}.preparing`),
    ])
  })

  it('SymlinkedWorkspaceParent_IsRejectedBeforeClone', async () => {
    const root = await createTestTempDir('mohist-workspace-')
    const runnerRoot = join(root, 'runner')
    const outside = join(root, 'outside')
    await fileSystem.ensureDir(outside)
    await fileSystem.ensureDir(runnerRoot)
    await fileSystem.symlink(outside, join(runnerRoot, 'workspaces'))
    const manager = new WorkspaceManager(runnerRoot)

    await expect(
      manager.prepare(work('wr-symlink', 'issue-symlink'), new AbortController().signal),
    ).rejects.toMatchObject({ kind: 'workspace-identity-mismatch' })
    expect(gitRunner.commandArgs().some((args) => args[0] === 'clone')).toBe(false)
  })

  it('WorkspaceParentReplacement_CloneRemainsInsideVerifiedDirectory', async () => {
    await withWorkspaceFileSystem(new MemoryDirectoryHandleFileSystem(), async () => {
      const root = await createTestTempDir('mohist-workspace-')
      const runnerRoot = join(root, 'runner')
      const workspaces = join(runnerRoot, 'workspaces')
      const heldWorkspaces = join(runnerRoot, 'workspaces-held')
      const outside = join(root, 'outside')
      await fileSystem.ensureDir(outside)
      gitRunner.beforeClone = async () => {
        await fileSystem.rename(workspaces, heldWorkspaces)
        await fileSystem.symlink(outside, workspaces)
      }

      await new WorkspaceManager(runnerRoot).prepare(
        work('wr-parent-swap', 'issue-parent-swap'),
        new AbortController().signal,
      )

      const publicPath = issueWorkspacePath(runnerRoot, 'wr-parent-swap')
      expect(fileSystem.exists(join(outside, basename(publicPath)))).toBe(false)
      expect(fileSystem.exists(join(heldWorkspaces, basename(publicPath)))).toBe(true)
      const clone = gitRunner.commandArgs().find((args) => args[0] === 'clone')
      expect(clone?.at(-1)).toMatch(/\/memory-handle-\d+\/wr-parent-swap\.preparing$/)
    })
  })

  it('ExistingMarkerMismatch_IsRejectedBeforeBranchMutation', async () => {
    const root = await createTestTempDir('mohist-workspace-')
    const manager = new WorkspaceManager(join(root, 'runner'))
    const item = work('wr-mismatch', 'issue-mismatch')
    const first = await manager.prepare(item, new AbortController().signal)
    await fileSystem.writeText(join(first.path, '.mohist', 'workspace.json'), '{}')
    gitRunner.calls.length = 0

    await expect(manager.prepare(item, new AbortController().signal)).rejects.toMatchObject({
      kind: 'workspace-identity-mismatch',
    })
    expect(gitRunner.commandArgs()).toEqual([])
  })

  it('RegistryFailureAfterAtomicRename_IsRecoveredOnRetry', async () => {
    const root = await createTestTempDir('mohist-workspace-')
    const runnerRoot = join(root, 'runner')
    const registry = new WorkspaceRegistry(runnerRoot)
    await registry.load()
    const register = registry.register.bind(registry)
    let fail = true
    registry.register = async (input) => {
      if (fail) {
        fail = false
        throw new Error('registry interrupted')
      }
      return register(input)
    }
    const manager = new WorkspaceManager(runnerRoot, registry)
    const item = work('wr-recover-registry', 'issue-recover-registry')

    await expect(manager.prepare(item, new AbortController().signal)).rejects.toThrow('registry interrupted')
    const path = issueWorkspacePath(runnerRoot, item.workflowRunId)
    expect(fileSystem.exists(path)).toBe(true)

    await manager.prepare(item, new AbortController().signal)
    expect(registry.get(item.workflowRunId)).toMatchObject({ workspacePath: path, workflowRunId: item.workflowRunId })
  })

  it('RunnerRestart_DropsStaleFdBindingAndRematerializesStableWorkspace', async () => {
    const root = await createTestTempDir('mohist-workspace-')
    const runnerRoot = join(root, 'runner')
    const registry = new WorkspaceRegistry(runnerRoot, { runnerId: 'runner-1' })
    await fileSystem.ensureDir(join(runnerRoot, '.mohist', 'runner-state'))
    await fileSystem.writeText(
      join(runnerRoot, '.mohist', 'runner-state', 'workspaces.json'),
      JSON.stringify(
        {
          version: 3,
          entries: {
            'wr-restart': {
              issueNumber: 558,
              workflowRunId: 'wr-restart',
              workspacePath: '/proc/79181/fd/30/wr_restart',
              binding: {
                runnerId: 'runner-1',
                runnerRoot,
                workflowRunId: 'wr-restart',
                gitUrl: 'https://example.test/mohist.git',
                baseBranch: 'master',
              },
              runBranch: 'mohist/run-wr-restart',
              phase: 'active',
              materializedAt: '2026-08-11T00:00:00.000Z',
              terminalAt: null,
            },
          },
        },
        null,
        2,
      ),
    )

    await registry.load()
    expect(registry.get('wr-restart')).toBeNull()

    const result = await new WorkspaceManager(runnerRoot, registry, 'runner-1').prepare(
      work('wr-restart'),
      new AbortController().signal,
    )

    const stablePath = issueWorkspacePath(runnerRoot, 'wr-restart')
    expect(result.path).toBe(stablePath)
    const persisted = JSON.parse(await fileSystem.readText(registry.getFilePath()))
    expect(persisted.entries['wr-restart']).toMatchObject({
      workspacePath: stablePath,
      binding: {
        runnerId: 'runner-1',
        runnerRoot,
        workflowRunId: 'wr-restart',
        gitUrl: 'https://example.test/mohist.git',
        baseBranch: 'master',
      },
    })
    expect(JSON.stringify(persisted)).not.toContain('/proc/79181/fd/30')
  })

  it('RepositoryIdentityMismatch_IsRejectedBeforeReusingTheStableWorkspace', async () => {
    const root = await createTestTempDir('mohist-workspace-')
    const runnerRoot = join(root, 'runner')
    const registry = new WorkspaceRegistry(runnerRoot, { runnerId: 'runner-1' })
    await registry.load()
    const manager = new WorkspaceManager(runnerRoot, registry, 'runner-1')
    const first = await manager.prepare(work('wr-repo-mismatch'), new AbortController().signal)
    gitRunner.calls.length = 0

    const mismatched = work('wr-repo-mismatch')
    ;(mismatched.variables.repository as Record<string, unknown>).gitUrl = 'https://example.test/other.git'

    await expect(manager.prepare(mismatched, new AbortController().signal)).rejects.toMatchObject({
      kind: 'workspace-identity-mismatch',
    })
    expect(first.path).toBe(issueWorkspacePath(runnerRoot, 'wr-repo-mismatch'))
    expect(gitRunner.commandArgs()).toEqual([])
  })

  it.runIf(process.platform === 'linux')('FreshOriginMismatch_UsesFdForGitButStablePathForDiagnostics', async () => {
    const root = await createTestTempDir('mohist-workspace-')
    const runnerRoot = join(root, 'runner')
    const stablePath = issueWorkspacePath(runnerRoot, 'wr-origin-fresh')
    gitRunner.remoteUrl = 'https://example.test/other.git'

    const failure = await new WorkspaceManager(runnerRoot)
      .prepare(work('wr-origin-fresh'), new AbortController().signal)
      .catch((error) => error)

    expect(failure).toMatchObject({
      kind: 'workspace-identity-mismatch',
      workspacePath: stablePath,
      originDiagnostic: {
        kind: 'value-mismatch',
        exitCode: 0,
      },
    })
    expect(failure.message).toContain(`Workflow workspace ${stablePath}`)
    expect(failure.message).not.toMatch(/\/proc\/\d+\/fd\/\d+/)
    expect(gitRunner.commandArgs()).toContainEqual([
      '-C',
      managedPath(`${stablePath}.preparing`),
      'remote',
      'get-url',
      'origin',
    ])
  })

  it.runIf(process.platform === 'linux')('ReentryOriginProbeFailure_PreservesExitCodeWithoutFdLeak', async () => {
    const root = await createTestTempDir('mohist-workspace-')
    const runnerRoot = join(root, 'runner')
    const manager = new WorkspaceManager(runnerRoot)
    const item = work('wr-origin-reentry')
    const first = await manager.prepare(item, new AbortController().signal)
    gitRunner.calls.length = 0
    gitRunner.remoteOriginResult = commandResult(
      17,
      '',
      'fatal: cannot read <workspace> origin at https://user:secret@example.test/repository.git',
    )

    const failure = await manager.prepare(item, new AbortController().signal).catch((error) => error)

    expect(failure).toMatchObject({
      kind: 'workspace-identity-mismatch',
      workspacePath: first.path,
      originDiagnostic: {
        kind: 'probe-failed',
        exitCode: 17,
        diagnostic: 'fatal: cannot read ' + first.path + ' origin at https://***@example.test/repository.git',
      },
    })
    expect(failure.message).toContain(`origin probe failed (exit 17)`)
    expect(failure.message).toContain(first.path)
    expect(failure.message).not.toContain('secret')
    expect(failure.message).not.toMatch(/\/proc\/\d+\/fd\/\d+/)
    expect(gitRunner.commandArgs()).toEqual([['-C', managedPath(first.path), 'remote', 'get-url', 'origin']])
  })

  it.runIf(process.platform === 'linux')(
    'VerifyOriginProbeFailure_UsesFdForGitButStablePathForDiagnostics',
    async () => {
      const root = await createTestTempDir('mohist-workspace-')
      const runnerRoot = join(root, 'runner')
      const manager = new WorkspaceManager(runnerRoot)
      const item = work('wr-origin-verify')
      const prepared = await manager.prepare(item, new AbortController().signal)
      gitRunner.calls.length = 0
      gitRunner.remoteOriginResult = commandResult(19, '', 'fatal: origin unavailable at <workspace>')

      const failure = await manager.verify(item, new AbortController().signal).catch((error) => error)

      expect(failure).toMatchObject({
        kind: 'workspace-identity-mismatch',
        workspacePath: prepared.path,
        originDiagnostic: {
          kind: 'probe-failed',
          exitCode: 19,
          diagnostic: 'fatal: origin unavailable at ' + prepared.path,
        },
      })
      expect(failure.message).toContain(prepared.path)
      expect(failure.message).not.toMatch(/\/proc\/\d+\/fd\/\d+/)
      expect(gitRunner.commandArgs()).toEqual([['-C', managedPath(prepared.path), 'remote', 'get-url', 'origin']])
    },
  )

  it('ConcurrentRuns_MaterializeIndependentStableWorkspaces', async () => {
    const root = await createTestTempDir('mohist-workspace-')
    const runnerRoot = join(root, 'runner')
    const manager = new WorkspaceManager(runnerRoot)

    const [first, second] = await Promise.all([
      manager.prepare(work('wr-concurrent-a'), new AbortController().signal),
      manager.prepare(work('wr-concurrent-b'), new AbortController().signal),
    ])

    expect(first.path).toBe(issueWorkspacePath(runnerRoot, 'wr-concurrent-a'))
    expect(second.path).toBe(issueWorkspacePath(runnerRoot, 'wr-concurrent-b'))
    expect(first.path).not.toBe(second.path)
    expect(gitRunner.commandArgs().filter((args) => args[0] === 'clone')).toHaveLength(2)
  })
})

describe('WorkspaceManager.prepare recovery', () => {
  it('TransientCheckoutFailure_FailsThenExactRetryRepairsSameWorkspace', async () => {
    const root = await createTestTempDir('mohist-workspace-')
    const manager = new WorkspaceManager(join(root, 'runner'))
    const item = work('wr-recover')
    const workspace = await manager.prepare(item, new AbortController().signal)
    gitRunner.calls.length = 0
    // Simulate the #628 failure mode: the workspace ended up detached with a
    // residual rebase, and the repair checkout transiently fails.
    await gitRunner.setDetached(workspace.path, 'detached-head-sha')
    await gitRunner.setResidual(workspace.path, ['rebase-merge'])
    gitRunner.failedCheckouts = 1

    const failure = await manager.prepare(item, new AbortController().signal).catch((error) => error)

    // First attempt is a durable, actionable failure identifying the expected
    // branch, the observed detached ref, and the failed checkout operation.
    expect(failure).toMatchObject({ kind: 'branch-invariant-violation', expectedBranch: 'mohist/run-wr-recover' })
    expect(failure.message).toContain('operation=checkout')
    expect(failure.message).toContain('expectedBranch=mohist/run-wr-recover')
    expect(failure.message).toContain('observedRef=detached-head-sha')
    // Residual rebase was aborted before the failing checkout; no replacement
    // workspace or force-created branch was produced.
    expect(gitRunner.commandArgs()).toContainEqual(['-C', managedPath(workspace.path), 'rebase', '--abort'])
    expect(gitRunner.commandArgs().some((args) => args[0] === 'clone')).toBe(false)
    expect(gitRunner.commandArgs().some((args) => args.includes('-B'))).toBe(false)

    // Exact retry after the transient checkout failure is removed repairs the
    // same path and branch.
    const recovered = await manager.prepare(item, new AbortController().signal)
    expect(recovered).toMatchObject({ path: workspace.path, branch: 'mohist/run-wr-recover' })
    expect(gitRunner.commandArgs().some((args) => args[0] === 'clone')).toBe(false)
    expect(gitRunner.commandArgs().some((args) => args.includes('-B'))).toBe(false)
  })

  it('CleanReentry_TakesFastPathWithoutMutation', async () => {
    const root = await createTestTempDir('mohist-workspace-')
    const manager = new WorkspaceManager(join(root, 'runner'))
    const item = work('wr-clean')
    const workspace = await manager.prepare(item, new AbortController().signal)
    gitRunner.calls.length = 0

    const second = await manager.prepare(item, new AbortController().signal)

    expect(second).toMatchObject({ path: workspace.path, branch: 'mohist/run-wr-clean' })
    // Health probes run but the healthy workspace is left untouched: no
    // checkout, no force-create, no clone, no abort, no reset.
    expect(gitRunner.commandArgs()).toContainEqual([
      '-C',
      managedPath(workspace.path),
      'rev-parse',
      '--abbrev-ref',
      'HEAD',
    ])
    expect(gitRunner.commandArgs()).toContainEqual(['-C', managedPath(workspace.path), 'status', '--porcelain'])
    expect(gitRunner.commandArgs()).not.toContainEqual([
      '-C',
      managedPath(workspace.path),
      'checkout',
      'mohist/run-wr-clean',
    ])
    expect(gitRunner.commandArgs().some((args) => args[0] === 'clone')).toBe(false)
    expect(gitRunner.commandArgs().some((args) => args.includes('-B'))).toBe(false)
    expect(gitRunner.commandArgs().some((args) => args.includes('reset'))).toBe(false)
    expect(gitRunner.commandArgs().some((args) => args.includes('--abort'))).toBe(false)
  })
})

describe('WorkspaceManager health contract', () => {
  it('DetachedWorkspace_RepairsByCheckoutWithoutReplacement', async () => {
    const root = await createTestTempDir('mohist-workspace-')
    const manager = new WorkspaceManager(join(root, 'runner'))
    const item = work('wr-detached')
    const workspace = await manager.prepare(item, new AbortController().signal)
    gitRunner.calls.length = 0
    await gitRunner.setDetached(workspace.path, 'detached-head-sha')

    const repaired = await manager.prepare(item, new AbortController().signal)

    expect(repaired).toMatchObject({ path: workspace.path, branch: 'mohist/run-wr-detached' })
    expect(gitRunner.commandArgs()).toContainEqual([
      '-C',
      managedPath(workspace.path),
      'checkout',
      'mohist/run-wr-detached',
    ])
    // Repair uses only the existing expected branch: no clone, no force-create.
    expect(gitRunner.commandArgs().some((args) => args[0] === 'clone')).toBe(false)
    expect(gitRunner.commandArgs().some((args) => args.includes('-B'))).toBe(false)
    expect(gitRunner.commandArgs().some((args) => args.includes('reset'))).toBe(false)
    expect(gitRunner.commandArgs().some((args) => args.includes('--abort'))).toBe(false)
  })

  it('DirtyMismatchedWorkspace_RepairsInOrderResetCleanCheckout', async () => {
    const root = await createTestTempDir('mohist-workspace-')
    const manager = new WorkspaceManager(join(root, 'runner'))
    const item = work('wr-dirty')
    const workspace = await manager.prepare(item, new AbortController().signal)
    gitRunner.calls.length = 0
    await gitRunner.setBranch(workspace.path, 'feature/other')
    await gitRunner.setDirty(workspace.path, ' M dirty.txt\n?? untracked.txt\n')

    const repaired = await manager.prepare(item, new AbortController().signal)

    expect(repaired).toMatchObject({ path: workspace.path, branch: 'mohist/run-wr-dirty' })
    const args = gitRunner.commandArgs()
    const resetIdx = args.findIndex((call) => call.includes('reset') && call.includes('--hard'))
    const cleanIdx = args.findIndex((call) => call.includes('clean') && call.includes('-fd'))
    const checkoutIdx = args.findIndex((call) => call[2] === 'checkout' && call[3] === 'mohist/run-wr-dirty')
    expect(resetIdx).toBeGreaterThanOrEqual(0)
    expect(cleanIdx).toBeGreaterThanOrEqual(0)
    expect(checkoutIdx).toBeGreaterThanOrEqual(0)
    expect(resetIdx).toBeLessThan(cleanIdx)
    expect(cleanIdx).toBeLessThan(checkoutIdx)
    expect(args.some((call) => call[0] === 'clone')).toBe(false)
    expect(args.some((call) => call.includes('-B'))).toBe(false)
  })

  it('ResidualRebase_AbortsAndReprobesBeforeRepair', async () => {
    const root = await createTestTempDir('mohist-workspace-')
    const manager = new WorkspaceManager(join(root, 'runner'))
    const item = work('wr-rebase')
    const workspace = await manager.prepare(item, new AbortController().signal)
    gitRunner.calls.length = 0
    await gitRunner.setDetached(workspace.path, 'detached-head-sha')
    await gitRunner.setResidual(workspace.path, ['rebase-merge'])

    const repaired = await manager.prepare(item, new AbortController().signal)

    expect(repaired).toMatchObject({ path: workspace.path, branch: 'mohist/run-wr-rebase' })
    expect(gitRunner.commandArgs()).toContainEqual(['-C', managedPath(workspace.path), 'rebase', '--abort'])
    expect(gitRunner.commandArgs()).toContainEqual([
      '-C',
      managedPath(workspace.path),
      'checkout',
      'mohist/run-wr-rebase',
    ])
    expect(gitRunner.residualMarkers(workspace.path)).toEqual([])
    expect(gitRunner.commandArgs().some((args) => args[0] === 'clone')).toBe(false)
  })

  it('ResidualAbortReProbeFailure_ReturnsDurableFailure', async () => {
    const root = await createTestTempDir('mohist-workspace-')
    const manager = new WorkspaceManager(join(root, 'runner'))
    const item = work('wr-stubborn')
    const workspace = await manager.prepare(item, new AbortController().signal)
    gitRunner.calls.length = 0
    await gitRunner.setDetached(workspace.path, 'detached-head-sha')
    await gitRunner.setResidual(workspace.path, ['rebase-merge'])
    await gitRunner.makeStubborn(workspace.path, ['rebase-merge'])

    const failure = await manager.prepare(item, new AbortController().signal).catch((error) => error)

    expect(failure).toMatchObject({ kind: 'branch-invariant-violation', expectedBranch: 'mohist/run-wr-stubborn' })
    expect(failure.message).toContain('operation=abort-rebase')
    expect(failure.message).toContain('still in progress')
    expect(gitRunner.commandArgs().some((args) => args[0] === 'clone')).toBe(false)
    expect(gitRunner.commandArgs().some((args) => args.includes('-B'))).toBe(false)
  })

  it('Verify_RejectsDetachedHeadWithSharedDiagnostic', async () => {
    const root = await createTestTempDir('mohist-workspace-')
    const manager = new WorkspaceManager(join(root, 'runner'))
    const item = work('wr-verify-detached')
    const workspace = await manager.prepare(item, new AbortController().signal)
    gitRunner.calls.length = 0
    await gitRunner.setDetached(workspace.path, 'detached-head-sha')

    const failure = await manager.verify(item, new AbortController().signal).catch((error) => error)

    expect(failure).toMatchObject({
      kind: 'branch-invariant-violation',
      expectedBranch: 'mohist/run-wr-verify-detached',
    })
    expect(failure.observedBranch).toBeNull()
    expect(failure.observedRef).toBe('detached-head-sha')
    expect(failure.message).toContain('operation=verify')
    expect(failure.message).toContain('observedRef=detached-head-sha')
  })

  it('Verify_RejectsWrongBranchWithSharedDiagnostic', async () => {
    const root = await createTestTempDir('mohist-workspace-')
    const manager = new WorkspaceManager(join(root, 'runner'))
    const item = work('wr-verify-branch')
    const workspace = await manager.prepare(item, new AbortController().signal)
    gitRunner.calls.length = 0
    await gitRunner.setBranch(workspace.path, 'feature/other')

    const failure = await manager.verify(item, new AbortController().signal).catch((error) => error)

    expect(failure).toMatchObject({ kind: 'branch-invariant-violation', expectedBranch: 'mohist/run-wr-verify-branch' })
    expect(failure.observedBranch).toBe('feature/other')
    expect(failure.message).toContain('observedBranch=feature/other')
  })

  it('Verify_BranchProbeFailure_ReturnsActionableDiagnostic', async () => {
    const root = await createTestTempDir('mohist-workspace-')
    const manager = new WorkspaceManager(join(root, 'runner'))
    const item = work('wr-verify-probe')
    const workspace = await manager.prepare(item, new AbortController().signal)
    gitRunner.calls.length = 0
    gitRunner.failCommand(
      (args) => args[0] === '-C' && args[2] === 'rev-parse' && args[3] === '--abbrev-ref',
      'fatal: not a git repository',
    )

    const failure = await manager.verify(item, new AbortController().signal).catch((error) => error)

    expect(failure).toMatchObject({ kind: 'branch-invariant-violation', expectedBranch: 'mohist/run-wr-verify-probe' })
    expect(failure.message).toContain('git rev-parse --abbrev-ref HEAD failed')
    expect(failure.message).toContain('expectedBranch=mohist/run-wr-verify-probe')
  })

  it('Verify_AbortsResidualThenPasses', async () => {
    const root = await createTestTempDir('mohist-workspace-')
    const manager = new WorkspaceManager(join(root, 'runner'))
    const item = work('wr-verify-residual')
    const workspace = await manager.prepare(item, new AbortController().signal)
    gitRunner.calls.length = 0
    await gitRunner.setResidual(workspace.path, ['MERGE_HEAD'])

    const verified = await manager.verify(item, new AbortController().signal)

    expect(verified).toMatchObject({ path: workspace.path, branch: 'mohist/run-wr-verify-residual' })
    expect(gitRunner.commandArgs()).toContainEqual(['-C', managedPath(workspace.path), 'merge', '--abort'])
    expect(gitRunner.residualMarkers(workspace.path)).toEqual([])
  })

  it('Verify_HealthyWorkspacePassesWithoutMutation', async () => {
    const root = await createTestTempDir('mohist-workspace-')
    const manager = new WorkspaceManager(join(root, 'runner'))
    const item = work('wr-verify-healthy')
    const workspace = await manager.prepare(item, new AbortController().signal)
    gitRunner.calls.length = 0

    const verified = await manager.verify(item, new AbortController().signal)

    expect(verified).toMatchObject({ path: workspace.path, branch: 'mohist/run-wr-verify-healthy' })
    expect(gitRunner.commandArgs().some((call) => call.includes('checkout'))).toBe(false)
    expect(gitRunner.commandArgs().some((call) => call.includes('reset'))).toBe(false)
    expect(gitRunner.commandArgs().some((call) => call.includes('--abort'))).toBe(false)
    expect(gitRunner.commandArgs().some((call) => call[0] === 'clone')).toBe(false)
  })
})

describe('DefaultCleanupRunner', () => {
  it('WorkspaceParentReplacement_ValidationAndDeleteRemainInsideVerifiedDirectory', async () => {
    const root = await createTestTempDir('mohist-workspace-')
    const runnerRoot = join(root, 'runner')
    const workflowRunId = 'wr-cleanup-parent-swap'
    const workspacePath = issueWorkspacePath(runnerRoot, workflowRunId)
    const workspaces = join(runnerRoot, 'workspaces')
    const heldWorkspaces = join(runnerRoot, 'workspaces-held')
    const outside = join(root, 'outside')
    const entry = cleanupEntry(workspacePath, workflowRunId)
    await fileSystem.ensureDir(join(workspacePath, '.mohist'))
    await fileSystem.ensureDir(join(workspacePath, '.git'))
    await fileSystem.writeText(
      join(workspacePath, '.mohist', 'workspace.json'),
      JSON.stringify({
        workflowRunId,
        runBranch: entry.runBranch,
      }),
    )
    await fileSystem.ensureDir(outside)
    let swapped = false
    gitRunner.beforeRemote = async () => {
      if (!swapped) {
        swapped = true
        await fileSystem.rename(workspaces, heldWorkspaces)
        await fileSystem.symlink(outside, workspaces)
      }
    }

    const removed = await new DefaultCleanupRunner(runnerRoot).validateAndDeleteWorkspace(entry)

    expect(removed).toBe(true)
    expect(fileSystem.exists(join(outside, basename(workspacePath)))).toBe(false)
    expect(fileSystem.exists(join(heldWorkspaces, basename(workspacePath)))).toBe(false)
  })
})

describe('WorkspaceManager.slugify', () => {
  it.each([
    ['my-project', 'my-project'],
    ['My Project!', 'my-project'],
    ['  spaced  out  ', 'spaced-out'],
    ['foo_bar.baz', 'foo-bar-baz'],
    ['Café', 'caf'],
    ['测试-project', 'project'],
    ['', 'project'],
  ])('slugify(%j) === %j', (input, expected) => {
    expect(slugify(input)).toBe(expected)
  })
})
