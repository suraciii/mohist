import { describe, expect, it, vi } from 'vitest'
import {
  CleanupLoop,
  type CleanupEntry,
  type CleanupRegistry,
  type CleanupRunner,
} from '../src/runtime/cleanup-loop.js'
import type { WorkspaceRemovalFence, WorkspaceRemovalFenceResult } from '../src/runtime/workspace-removal-fence.js'

interface FenceEntry extends CleanupEntry {
  entryKey: string
}

const entry: FenceEntry = {
  entryKey: 'ws:project-1:fenced',
  workspacePath: '/runner/workspace',
  phase: 'eligible',
  terminalAt: '2026-07-02T00:00:00.000Z',
}

function createFixture(calls: string[] = []) {
  const registry: CleanupRegistry<FenceEntry> = {
    list: vi.fn(() => [entry]),
    entryKey: vi.fn(() => entry.entryKey),
    markStuck: vi.fn(async () => entry),
    remove: vi.fn(async () => {
      calls.push('registry-remove')
      return true
    }),
  }
  const runner: CleanupRunner = {
    isUnderRunnerRoot: vi.fn(() => {
      calls.push('guard-root')
      return true
    }),
    pathExists: vi.fn(() => {
      calls.push('path-exists')
      return true
    }),
    readWorkspaceIdentity: vi.fn(async () => {
      calls.push('guard-marker')
      return entry.entryKey
    }),
    deleteDirectory: vi.fn(async () => {
      calls.push('delete')
    }),
    computeDirectorySize: vi.fn(async () => 0),
  }
  return { registry, runner }
}

describe('CleanupLoop removal fence', () => {
  const passFence: WorkspaceRemovalFence = {
    async withRemovalFence<T>(_path: string, callback: () => Promise<T>): Promise<WorkspaceRemovalFenceResult<T>> {
      return { kind: 'completed', value: await callback() }
    },
  }

  it('reports inspection failure before deletion without calling it a deletion failure', async () => {
    const fixture = createFixture()
    const outcomes: Array<{ outcome: string; reason?: string }> = []
    fixture.runner.validateAndDeleteWorkspace = vi.fn(async () => {
      throw new Error('eligibility unavailable')
    })
    const loop = new CleanupLoop(
      fixture.registry,
      fixture.runner,
      '/runner',
      () => passFence,
      async (_entry, outcome, reason) => {
        outcomes.push({ outcome, reason })
      },
    )

    expect(await loop.safeRemove(entry)).toBe(false)
    expect(outcomes).toEqual([{ outcome: 'unsafe', reason: 'eligibility unavailable' }])
    expect(fixture.runner.deleteDirectory).not.toHaveBeenCalled()
    expect(fixture.registry.remove).not.toHaveBeenCalled()
  })

  it('reports an unreadable path before any deletion attempt', async () => {
    const fixture = createFixture()
    const outcomes: Array<{ outcome: string; reason?: string }> = []
    fixture.runner.pathExists = vi.fn(() => {
      throw new Error('stat unavailable')
    })
    const loop = new CleanupLoop(
      fixture.registry,
      fixture.runner,
      '/runner',
      () => passFence,
      async (_entry, outcome, reason) => {
        outcomes.push({ outcome, reason })
      },
    )

    expect(await loop.safeRemove(entry)).toBe(false)
    expect(outcomes).toEqual([{ outcome: 'unsafe', reason: 'stat unavailable' }])
    expect(fixture.runner.deleteDirectory).not.toHaveBeenCalled()
  })

  it('reports a failure after deletion starts as potentially partial', async () => {
    const fixture = createFixture()
    const outcomes: Array<{ outcome: string; reason?: string }> = []
    fixture.runner.validateAndDeleteWorkspace = vi.fn(async (_entry, onDeleteStarted) => {
      onDeleteStarted?.()
      throw new Error('disk error')
    })
    const loop = new CleanupLoop(
      fixture.registry,
      fixture.runner,
      '/runner',
      () => passFence,
      async (_entry, outcome, reason) => {
        outcomes.push({ outcome, reason })
      },
    )

    expect(await loop.safeRemove(entry)).toBe(false)
    expect(outcomes).toEqual([{ outcome: 'deletion_failed', reason: 'disk error' }])
    expect(fixture.registry.remove).not.toHaveBeenCalled()
  })

  it('runs final guards, deletion, and registry removal inside the fence', async () => {
    const calls: string[] = []
    const orderedFixture = createFixture(calls)
    const fence: WorkspaceRemovalFence = {
      async withRemovalFence<T>(_path: string, callback: () => Promise<T>): Promise<WorkspaceRemovalFenceResult<T>> {
        calls.push('fence-enter')
        const value = await callback()
        calls.push('fence-exit')
        return { kind: 'completed', value }
      },
    }
    const withRemovalFence = vi.spyOn(fence, 'withRemovalFence')
    const loop = new CleanupLoop(orderedFixture.registry, orderedFixture.runner, '/runner', () => fence)

    const removed = await loop.safeRemove(entry)

    expect(removed).toBe(true)
    expect(calls).toEqual([
      'fence-enter',
      'path-exists',
      'guard-root',
      'guard-marker',
      'delete',
      'registry-remove',
      'fence-exit',
    ])
    expect(orderedFixture.runner.deleteDirectory).toHaveBeenCalledWith(entry.workspacePath)
    expect(orderedFixture.registry.remove).toHaveBeenCalledWith(entry.entryKey)
    expect(withRemovalFence).toHaveBeenCalledWith(entry.workspacePath, expect.any(Function))
  })

  it('does not delete when the fresh fence reports busy', async () => {
    const fixture = createFixture()
    const fence: WorkspaceRemovalFence = {
      async withRemovalFence<T>(_path: string, _callback: () => Promise<T>): Promise<WorkspaceRemovalFenceResult<T>> {
        return { kind: 'busy' }
      },
    }
    const withRemovalFence = vi.spyOn(fence, 'withRemovalFence')
    const loop = new CleanupLoop(fixture.registry, fixture.runner, '/runner', () => fence)

    expect(await loop.safeRemove({ ...entry, entryKey: 'ws:project-1:busy' })).toBe(false)
    expect(withRemovalFence).toHaveBeenCalledOnce()
    expect(fixture.runner.deleteDirectory).not.toHaveBeenCalled()
    expect(fixture.registry.remove).not.toHaveBeenCalled()
  })
})
