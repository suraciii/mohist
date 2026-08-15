import { describe, expect, it } from 'vitest'
import type { DispatchWorkItem, WorkItemResult } from '../core/types.js'
import { WorkResultJournal, workKey } from './work-result-journal.js'
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
