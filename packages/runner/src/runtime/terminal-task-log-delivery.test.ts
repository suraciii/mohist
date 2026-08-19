import { describe, expect, it } from 'vitest'
import {
  TerminalTaskLogDeliveryStoreImpl,
  type TerminalTaskLogDeliveryFileSystem,
  type TerminalTaskLogDeliveryIdentity,
} from './terminal-task-log-delivery.js'

class MemoryFileSystem implements TerminalTaskLogDeliveryFileSystem {
  text: string | null = null
  writes = 0

  async readText(): Promise<string | null> {
    return this.text
  }

  async writeAtomicText(_path: string, body: string): Promise<void> {
    this.writes += 1
    this.text = body
  }
}

const workflowIdentity: TerminalTaskLogDeliveryIdentity = {
  ownerKind: 'workflow',
  ownerId: 'workflow-1',
  workId: 'work-1',
}

function snapshot(text: string) {
  return {
    identity: workflowIdentity,
    batch: {
      entries: [{ seq: 1, timestamp: new Date('2026-08-11T00:00:00.000Z'), source: 'action', text }],
      truncated: false,
    },
  }
}

async function loadedStore(fileSystem = new MemoryFileSystem()) {
  const store = new TerminalTaskLogDeliveryStoreImpl('/runner', { fileSystem })
  await store.load()
  return { store, fileSystem }
}

describe('TerminalTaskLogDeliveryStore', () => {
  it('PersistsBeforeAckAndReloadsTheExactSnapshot', async () => {
    const { store, fileSystem } = await loadedStore()
    const pending = await store.putPending(snapshot('same payload'))

    expect(store.ready()).toBe(true)
    expect(await store.listPending()).toEqual([pending])

    const restarted = new TerminalTaskLogDeliveryStoreImpl('/runner', { fileSystem })
    await restarted.load()
    expect(await restarted.listPending()).toEqual([pending])

    await restarted.acknowledge(workflowIdentity)
    expect(await restarted.listPending()).toEqual([])
    expect(JSON.parse(fileSystem.text!).deliveries).toEqual({})
  })

  it('KeepsTheFirstPendingSnapshotWhenARecoveryExecutionDiffers', async () => {
    const { store } = await loadedStore()
    await store.putPending(snapshot('original'))

    const duplicate = await store.putPending(snapshot('changed'))

    expect(duplicate.batch.entries[0]?.text).toBe('original')
    expect((await store.listPending())[0]?.batch.entries[0]?.text).toBe('original')
  })

  it('KeepsConflictFailureDiagnosableWithoutBlockingOtherWork', async () => {
    const { store, fileSystem } = await loadedStore()
    await store.putPending(snapshot('conflicting'))
    const other = { ...snapshot('other'), identity: { ...workflowIdentity, workId: 'work-2' } }
    await store.putPending(other)

    await store.markFailed(workflowIdentity, {
      kind: 'conflict',
      status: 409,
      code: 'terminal_snapshot_conflict',
      message: 'sealed content differs',
    })

    expect(await store.listPending()).toEqual([{ ...other, state: 'pending' }])
    const restarted = new TerminalTaskLogDeliveryStoreImpl('/runner', { fileSystem })
    await restarted.load()
    expect(await restarted.listPending()).toEqual([{ ...other, state: 'pending' }])
    expect(JSON.parse(fileSystem.text!).deliveries['workflow:workflow-1:work-1'].state).toBe('failed')
  })

  it('ReplacesAConflictSnapshotForTheNextExecution', async () => {
    const { store } = await loadedStore()
    await store.putPending(snapshot('first execution'))
    await store.markFailed(workflowIdentity, {
      kind: 'conflict',
      status: 409,
      code: 'terminal_snapshot_conflict',
      message: 'sealed content differs',
    })

    const replacement = await store.putPending(snapshot('next execution'))

    expect(replacement.state).toBe('pending')
    expect(replacement.batch.entries[0]?.text).toBe('next execution')
    expect((await store.listPending())[0]?.batch.entries[0]?.text).toBe('next execution')
  })

  it('SerializesConcurrentMutationsWithoutDroppingEitherWork', async () => {
    const { store } = await loadedStore()
    await Promise.all([
      store.putPending(snapshot('one')),
      store.putPending({ ...snapshot('two'), identity: { ...workflowIdentity, workId: 'work-2' } }),
    ])

    expect((await store.listPending()).map((item) => item.identity.workId).sort()).toEqual(['work-1', 'work-2'])
  })
})
