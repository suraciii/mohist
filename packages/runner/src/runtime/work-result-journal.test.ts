import { describe, expect, it } from 'vitest'
import type { DispatchWorkItem, WorkItemResult, WorkflowTaskCompletionBoundary } from '../core/types.js'
import { WorkResultJournal, workKey } from './work-result-journal.js'
import { runnerRestartedResult } from './work-report.js'
import { MemoryFileSystem } from '../../tests/support/memory-filesystem.js'
import { withTestRunnerResources } from '../../tests/support/test-resources.js'
import type { RunnerFileSystem } from '../system/filesystem.js'

const work: DispatchWorkItem = {
  workflowRunId: 'workflow-1',
  workId: 'work-1',
  taskRunId: 'task-1',
  workType: 'task',
  ownerKind: 'workflow',
  uses: 'mohist/pi',
  variables: { workspace: { path: '/workspace' } },
}

const result: WorkItemResult = {
  status: 'completed',
  output: { answer: 'done' },
}

const boundary: WorkflowTaskCompletionBoundary = {
  version: 1,
  identity: {
    workflowRunId: 'workflow-1',
    stage: 'build',
    taskAttemptId: 'task-1',
    workId: 'work-1',
    ownerKind: 'workflow',
    ownerId: 'workflow-1',
    runnerId: 'runner-1',
    workspaceId: 'workspace-1',
    workspaceGeneration: 2,
  },
  actionCompletion: {
    version: 1,
    actionStarted: true,
    outcome: 'succeeded',
    phase: 'action',
    output: { answer: 'done' },
    error: null,
    artifactUploadIds: ['artifact-1'],
    capturedOutputs: null,
    completedAt: '2026-08-16T00:00:00.000Z',
  },
  commitReceipt: {
    version: 1,
    identity: {
      workflowRunId: 'workflow-1', stage: 'build', taskAttemptId: 'task-1', workId: 'work-1',
      ownerKind: 'workflow', ownerId: 'workflow-1', runnerId: 'runner-1', workspaceId: 'workspace-1', workspaceGeneration: 2,
    },
    expectedBranch: 'main', expectedHead: 'head-1', expectedTree: 'tree-1',
    observedBranch: 'main', observedHead: 'head-1', observedTree: 'tree-1',
    staged: [], unstaged: [], untracked: [], authoritative: true, reason: null,
    probedAt: '2026-08-16T00:00:00.000Z',
  },
  workspaceOutcome: 'committed-clean',
  workspaceReason: null,
  fingerprint: 'boundary-fingerprint',
}

class FailingWriteFileSystem extends MemoryFileSystem {
  failWrites = false

  override async writeText(path: string, content: string): Promise<void> {
    if (this.failWrites) throw new Error('disk full')
    await super.writeText(path, content)
  }
}

describe('WorkResultJournal', () => {
  it('ReloadsCompletedResultForIdentityRedeliveryAndRemovesOnlyAfterAcknowledgement', async () => {
    await withTestRunnerResources(async (_fileSystem) => {
      const journal = new WorkResultJournal('/runner')
      await journal.load()
      expect(await journal.begin(work)).toBe('new')
      await journal.complete(work, result)

      const restarted = new WorkResultJournal('/runner')
      await restarted.load()
      expect(restarted.completed()).toEqual([{ work, state: 'completed', result }])
      await restarted.acknowledge(work)
      expect(restarted.completed()).toEqual([])
    })
  })

  it('PersistsTheImmutableCompletionBoundaryAndRejectsConflictingReplay', async () => {
    await withTestRunnerResources(async () => {
      const journal = new WorkResultJournal('/runner')
      await journal.load()
      await journal.begin(work)
      await journal.complete(work, result, boundary)

      const restarted = new WorkResultJournal('/runner')
      await restarted.load()
      expect(restarted.completed()[0]).toMatchObject({ work, result, boundary })
      await expect(restarted.complete(work, result, boundary)).resolves.toEqual({ state: 'durable' })
      await expect(restarted.complete(work, result, { ...boundary, workspaceOutcome: 'dirty' })).rejects.toThrow('conflict')
      expect(restarted.completed()[0]?.boundary).toEqual(boundary)
    })
  })

  it('FencesStartedWorkAfterRestartWithoutReplayingThePhysicalEffect', async () => {
    await withTestRunnerResources(async (_fileSystem) => {
      const journal = new WorkResultJournal('/runner')
      await journal.load()
      expect(await journal.begin(work)).toBe('new')

      const restarted = new WorkResultJournal('/runner')
      await restarted.load()
      expect(await restarted.begin(work)).toBe('started')
      expect(restarted.completed()).toEqual([])
      expect(restarted.started()).toEqual([{ work, state: 'started' }])
      await expect(restarted.acknowledge(work)).rejects.toThrow('unfinished work')
      await restarted.acknowledgeUnconfirmed(work)
      expect(restarted.started()).toEqual([])
      expect(await restarted.begin(work)).toBe('new')
      expect(workKey(work)).toBe('workflow:workflow-1:work-1')
    })
  })

  it('CompletesAStartedFenceWithAnInterruptionFactAndRetiresItAfterAck', async () => {
    await withTestRunnerResources(async (_fileSystem) => {
      const journal = new WorkResultJournal('/runner')
      await journal.load()
      await journal.begin(work)
      const interruption = runnerRestartedResult(work)
      await journal.completeInterrupted(work, interruption.result, interruption.interruption)

      const restarted = new WorkResultJournal('/runner')
      await restarted.load()
      expect(restarted.completed()).toEqual([
        expect.objectContaining({
          work,
          state: 'completed',
          result: expect.objectContaining({ status: 'unknown', message: 'runner-restarted' }),
          interruption: expect.objectContaining({ reason: 'runner-restarted', workId: work.workId }),
        }),
      ])
      await restarted.acknowledge(work)
      expect(restarted.completed()).toEqual([])
    })
  })

  it('RejectsAResultForADifferentDispatchIdentity', async () => {
    await withTestRunnerResources(async (_fileSystem) => {
      const journal = new WorkResultJournal('/runner')
      await journal.load()
      await journal.begin(work)
      await expect(journal.complete({ ...work, workId: 'other-work' }, result)).rejects.toThrow('unknown work')
    })
  })

  it('TreatsCorruptStateAsUnavailableInsteadOfReplacingIt', async () => {
    await withTestRunnerResources(async (fileSystem: RunnerFileSystem) => {
      await fileSystem.writeText('/runner/.mohist/runner-state/work-results.json', '{not-json')
      const journal = new WorkResultJournal('/runner')
      await journal.load()
      expect(journal.ready()).toBe(false)
      expect(() => journal.completed()).toThrow('unavailable')
      await expect(journal.begin(work)).rejects.toThrow('unavailable')
      expect(await fileSystem.readText('/runner/.mohist/runner-state/work-results.json')).toBe('{not-json')
    })
  })

  it('RearmsAStartedFenceForADriftedRecoveryDispatchInsteadOfRefusing', async () => {
    await withTestRunnerResources(async () => {
      const journal = new WorkResultJournal('/runner')
      await journal.load()
      await journal.begin(work)
      await expect(journal.begin({ ...work, variables: { workspace: { path: '/workspace-2' } } })).rejects.toThrow(
        'identity conflict',
      )

      const recovery: DispatchWorkItem = {
        ...work,
        variables: { workspace: { path: '/workspace-2' } },
        agentRecovery: { runtime: 'pi', runtimeSessionId: '/sessions/pi-1' },
      }
      expect(await journal.beginRecovery(recovery)).toBe('started')
      expect(journal.started()).toEqual([{ work: recovery, state: 'started' }])
      await journal.complete(recovery, result)
      await journal.acknowledge(recovery)
      expect(journal.started()).toEqual([])
    })
  })

  it('KeepsACompletedEntryUnarmedForARecoveryDispatch', async () => {
    await withTestRunnerResources(async () => {
      const journal = new WorkResultJournal('/runner')
      await journal.load()
      await journal.begin(work)
      await journal.complete(work, result)

      const recovery: DispatchWorkItem = {
        ...work,
        agentRecovery: { runtime: 'pi', runtimeSessionId: '/sessions/pi-1' },
      }
      expect(await journal.beginRecovery(recovery)).toBe('completed')
      expect(journal.completed()).toEqual([{ work, state: 'completed', result }])
    })
  })

  it('FencesAFreshRecoveryDispatchLikeBegin', async () => {
    await withTestRunnerResources(async () => {
      const journal = new WorkResultJournal('/runner')
      await journal.load()

      const recovery: DispatchWorkItem = {
        ...work,
        agentRecovery: { runtime: 'pi', runtimeSessionId: '/sessions/pi-1' },
      }
      expect(await journal.beginRecovery(recovery)).toBe('new')
      expect(journal.started()).toEqual([{ work: recovery, state: 'started' }])
    })
  })

  it('RestoresTheOriginalPayloadWhenReArmPersistenceFails', async () => {
    const fileSystem = new FailingWriteFileSystem()
    await withTestRunnerResources(
      async () => {
        const journal = new WorkResultJournal('/runner')
        await journal.load()
        await journal.begin(work)
        fileSystem.failWrites = true

        const recovery: DispatchWorkItem = {
          ...work,
          agentRecovery: { runtime: 'pi', runtimeSessionId: '/sessions/pi-1' },
        }
        await expect(journal.beginRecovery(recovery)).rejects.toThrow('disk full')
        expect(journal.ready()).toBe(false)

        fileSystem.failWrites = false
        await journal.retryPendingPersistence()
        expect(journal.ready()).toBe(true)
        expect(journal.started()).toEqual([{ work, state: 'started' }])
      },
      { fileSystem },
    )
  })

  it('RetainsASettledResultUntilTemporaryPersistenceRecovers', async () => {
    const fileSystem = new FailingWriteFileSystem()
    await withTestRunnerResources(
      async () => {
        const journal = new WorkResultJournal('/runner')
        await journal.load()
        await journal.begin(work)
        fileSystem.failWrites = true

        const completion = await journal.complete(work, result)
        expect(completion.state).toBe('pending')
        expect(journal.ready()).toBe(false)
        expect(journal.needsPersistenceRecovery()).toBe(true)
        await expect(journal.acknowledge(work)).rejects.toThrow('unavailable')

        // The retained result is process-local. A restarted runner observes
        // only the durable started fence and must not replay or infer it.
        const restarted = new WorkResultJournal('/runner')
        await restarted.load()
        expect(await restarted.begin(work)).toBe('started')

        fileSystem.failWrites = false
        await expect(journal.retryPendingPersistence()).resolves.toEqual({ state: 'durable' })
        expect(journal.ready()).toBe(true)
        expect(journal.completed()).toEqual([{ work, state: 'completed', result }])
      },
      { fileSystem },
    )
  })
})
