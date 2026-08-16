import { describe, expect, it } from 'vitest'
import type { ActionResult, DispatchWorkItem } from '../src/core/types.js'
import type { GitRunner } from '../src/runtime/git-probe.js'
import { WorkExecutor } from '../src/runtime/executor.js'
import { arbitrateWorkspaceOutcome, probeCommitReceipt } from '../src/runtime/completion-boundary.js'
import { verifyOnlyWorkspaceManager } from './support/workspace-mock.js'
import { defineTestActions } from './support/action-registry-test.js'
import { withTestRunnerResources } from './support/test-resources.js'

const workDir = '/virtual/completion-boundary'

function work(overrides: Partial<DispatchWorkItem> = {}): DispatchWorkItem {
  return {
    workflowRunId: 'wr-boundary',
    workId: 'task-boundary',
    taskRunId: 'attempt-1',
    workType: 'task',
    stage: 'build',
    ownerKind: 'workflow',
    uses: 'test/action',
    with: {},
    runnerId: 'runner-1',
    workspaceId: 'workspace-1',
    workspaceGeneration: 3,
    workspaceHead: 'head-1',
    workspaceTree: 'tree-1',
    variables: { workspace: { path: workDir, branch: 'main' } },
    ...overrides,
  }
}

function gitRunner(status = ''): GitRunner {
  return async (_workDir, args) => {
    if (args[0] === 'rev-parse' && args[1] === '--abbrev-ref') return result('main')
    if (args[0] === 'rev-parse' && args[1] === 'HEAD^{tree}') return result('tree-1')
    if (args[0] === 'rev-parse' && args[1] === 'HEAD') return result('head-1')
    if (args[0] === 'status') return result(status)
    throw new Error(`unexpected git probe: ${args.join(' ')}`)
  }
}

function result(stdout: string) {
  return { success: true, exitCode: 0, stdout, stderr: '', combinedOutput: stdout }
}

function execute(action: ActionResult | Error, git: GitRunner, overrides: Partial<DispatchWorkItem> = {}) {
  return withTestRunnerResources(
    async () => {
      const selectedUses = overrides.uses ?? 'test/action'
      const actionDefinitions = {
        'test/action': {
          run: async () => {
            if (action instanceof Error) throw action
            return action
          },
          errors: [{ code: 'action-failed', description: 'test failure' }],
        },
        ...(selectedUses !== 'test/action' && selectedUses !== 'removed/action'
          ? {
              [selectedUses]: {
                run: async () => {
                  if (action instanceof Error) throw action
                  return action
                },
                errors: [{ code: 'action-failed', description: 'test failure' }],
              },
            }
          : {}),
      }
      const executor = new WorkExecutor(
        defineTestActions(actionDefinitions),
        verifyOnlyWorkspaceManager({
          path: workDir,
          branch: 'main',
          workspaceId: 'workspace-1',
          workspaceGeneration: 3,
        }),
        {
          uploadArtifact: async () => {
            throw new Error('unexpected artifact upload')
          },
        } as never,
        workDir,
        () => new Date('2026-08-16T00:00:00.000Z'),
        null,
        null,
        null,
        undefined,
        null,
        null,
        undefined,
        undefined,
        undefined,
        'runner-1',
      )
      return await executor.executeWithLog(
        work({ ...overrides, uses: selectedUses }),
        new AbortController().signal,
        null,
      )
    },
    { gitRunner: git },
  )
}

describe('workflow task completion boundary', () => {
  it('classifies authoritative empty status as committed-clean', async () => {
    const execution = await execute({ output: { ok: true } }, gitRunner())
    expect(execution.result).toMatchObject({ status: 'completed', workspaceOutcome: 'committed-clean' })
    expect(execution.boundary).toMatchObject({
      workspaceOutcome: 'committed-clean',
      actionCompletion: { actionStarted: true, output: { ok: true }, artifactUploadIds: [] },
      commitReceipt: {
        authoritative: true,
        observedBranch: 'main',
        observedHead: 'head-1',
        observedTree: 'tree-1',
        staged: [],
        unstaged: [],
        untracked: [],
      },
    })
  })

  it.each(['test/action', 'mohist/pi', 'mohist/opencode'])(
    'uses the same boundary path for %s Workflow execution',
    async (uses) => {
      const execution = await execute({ output: { runtime: uses } }, gitRunner(), { uses })

      expect(execution.result).toMatchObject({ status: 'completed', workspaceOutcome: 'committed-clean' })
      expect(execution.boundary).toMatchObject({
        identity: { ownerKind: 'workflow', runnerId: 'runner-1', workspaceId: 'workspace-1', workspaceGeneration: 3 },
        actionCompletion: { actionStarted: true, output: { runtime: uses } },
        workspaceOutcome: 'committed-clean',
      })
    },
  )

  it('classifies authoritative staged, unstaged, and untracked evidence as dirty without failing the Action', async () => {
    const staged = await execute({ output: { ok: true } }, gitRunner('M  staged.ts'))
    const unstaged = await execute({ output: { ok: true } }, gitRunner(' M unstaged.ts'))
    const untracked = await execute({ output: { ok: true } }, gitRunner('?? new.ts'))

    expect(staged.result).toMatchObject({ status: 'completed', workspaceOutcome: 'dirty' })
    expect(unstaged.result).toMatchObject({ status: 'completed', workspaceOutcome: 'dirty' })
    expect(untracked.result).toMatchObject({ status: 'completed', workspaceOutcome: 'dirty' })
    expect(staged.boundary?.commitReceipt.staged).toEqual(['staged.ts'])
    expect(unstaged.boundary?.commitReceipt.unstaged).toEqual(['unstaged.ts'])
    expect(untracked.boundary?.commitReceipt.untracked).toEqual(['new.ts'])
  })

  it('classifies missing, mismatched, and timed-out evidence as unconfirmed with a reason', async () => {
    const missing = await execute({ output: { ok: true } }, gitRunner(), { workspaceGeneration: null })
    const mismatched = await execute({ output: { ok: true } }, gitRunner(), { workspaceHead: 'other-head' })
    const timedOut = await execute({ output: { ok: true } }, async (_dir, args) => {
      if (args[0] === 'rev-parse' && args[1] === '--abbrev-ref') return result('main')
      return { success: false, exitCode: 124, stdout: '', stderr: '', combinedOutput: '', status: 'timeout' as const }
    })

    expect(missing.result.workspaceOutcome).toBe('unconfirmed')
    expect(missing.result.workspaceReason).toBe('workspace-generation-missing')
    expect(mismatched.result.workspaceOutcome).toBe('unconfirmed')
    expect(mismatched.result.workspaceReason).toBe('head-mismatch')
    expect(timedOut.result.workspaceOutcome).toBe('unconfirmed')
    expect(timedOut.result.workspaceReason).toBe('workspace-probe-timeout')
  })

  it('retains a conclusive Action failure regardless of workspace outcome', async () => {
    const execution = await execute({ error: { code: 'action-failed', message: 'nope' } }, gitRunner(' M changed.ts'))
    expect(execution.result).toMatchObject({
      status: 'failed',
      error: { code: 'action-failed' },
      workspaceOutcome: 'dirty',
    })
    expect(execution.boundary?.actionCompletion).toMatchObject({
      actionStarted: true,
      outcome: 'failed',
      error: { code: 'action-failed', message: 'nope' },
    })
  })

  it('captures an Action throw as an authoritative failed Action completion', async () => {
    const execution = await execute(new Error('action exploded'), gitRunner())
    expect(execution.result).toMatchObject({ status: 'failed', error: { code: 'unexpected-error' } })
    expect(execution.boundary?.actionCompletion).toMatchObject({
      actionStarted: true,
      outcome: 'failed',
      phase: 'action',
      error: { code: 'unexpected-error' },
    })
  })

  it('preserves Action output when artifact capture or set-variable projection fails', async () => {
    const artifactFailure = await execute(
      { output: { ok: true } },
      gitRunner(),
      { artifacts: { files: [{ path: 'missing-artifact.md' }] } },
    )
    const setVarFailure = await execute(
      { output: { ok: true } },
      gitRunner(),
      { setVars: { result: 'output.ok' } },
    )

    expect(artifactFailure.result).toMatchObject({ status: 'completed', output: { ok: true } })
    expect(artifactFailure.boundary?.actionCompletion).toMatchObject({ outcome: 'succeeded', output: { ok: true } })
    expect(setVarFailure.result).toMatchObject({ status: 'failed', output: { ok: true } })
    expect(setVarFailure.boundary?.actionCompletion).toMatchObject({ outcome: 'succeeded', output: { ok: true } })
  })

  it('records workspace setup failure before an Action result exists', async () => {
    await withTestRunnerResources(
      async () => {
        const executor = new WorkExecutor(
          defineTestActions({ 'test/action': async () => ({ output: { ok: true } }) }),
          { prepare: async () => { throw new Error('workspace unavailable') } } as never,
          { uploadArtifact: async () => { throw new Error('unexpected artifact upload') } } as never,
          workDir,
          () => new Date('2026-08-16T00:00:00.000Z'),
          null, null, null, undefined, null, null, undefined, undefined, undefined, 'runner-1',
        )
        const execution = await executor.executeWithLog(work(), new AbortController().signal, null)
        expect(execution.result).toMatchObject({ status: 'failed', workspaceOutcome: 'unconfirmed' })
        expect(execution.boundary?.actionCompletion).toMatchObject({
          actionStarted: false,
          phase: 'workspace-setup',
          error: { code: 'workspace-setup' },
        })
        expect(execution.boundary?.commitReceipt).toMatchObject({ authoritative: false, reason: 'workspace-unavailable' })
      },
      { gitRunner: gitRunner() },
    )
  })

  it('builds a non-authoritative pre-Action boundary for an unknown Action', async () => {
    const execution = await execute({ output: { ok: true } }, gitRunner(), { uses: 'removed/action' })
    expect(execution.result).toMatchObject({ status: 'failed', workspaceOutcome: 'unconfirmed' })
    expect(execution.boundary?.actionCompletion).toMatchObject({
      actionStarted: false,
      outcome: 'failed',
      phase: 'action-resolution',
      error: { code: 'runner-failed' },
    })
  })
})

describe('workspace outcome arbitration', () => {
  it('never treats incomplete evidence as clean', () => {
    expect(arbitrateWorkspaceOutcome({
      version: 1,
      identity: {} as never,
      expectedBranch: 'main',
      expectedHead: 'head',
      expectedTree: 'tree',
      observedBranch: 'main',
      observedHead: 'head',
      observedTree: 'tree',
      staged: [],
      unstaged: [],
      untracked: [],
      authoritative: false,
      reason: 'probe-incomplete',
      probedAt: '2026-08-16T00:00:00.000Z',
    })).toEqual({ outcome: 'unconfirmed', reason: 'probe-incomplete' })
  })

  it('keeps the initial receipt separate from later cleanup observations', async () => {
    const receipt = await probeCommitReceipt({
      work: work(),
      identity: {
        workflowRunId: 'wr-boundary', stage: 'build', taskAttemptId: 'attempt-1', workId: 'task-boundary',
        ownerKind: 'workflow', ownerId: 'wr-boundary', runnerId: 'runner-1', workspaceId: 'workspace-1', workspaceGeneration: 3,
      },
      workspace: { path: workDir, branch: 'main', workspaceId: 'workspace-1', workspaceGeneration: 3 },
      expectedBranch: 'main', expectedHead: 'head-1', expectedTree: 'tree-1',
      signal: new AbortController().signal,
    })
    expect(receipt).toHaveProperty('probedAt')
    expect(receipt).not.toHaveProperty('cleanupAttempts')
  })
})
