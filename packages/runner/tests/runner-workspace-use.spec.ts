import { describe, expect, it, vi } from 'vitest'
import { RunnerWorkspaceUse, WorkspaceUseConflict } from '../src/runtime/runner-workspace-use.js'
import { snapshotRunnerProcessIdentities } from '../src/runtime/workspace-process-use.js'
import { runCommand } from '../src/system/process.js'
import { noteWorkspaceCommandStart } from '../src/system/process-ownership.js'
import { FakeProcessSpawner } from './support/fake-process.js'
import { withTestRunnerResources } from './support/test-resources.js'

const workspace = '/runner/workspaces/project-home'

describe('RunnerWorkspaceUse', () => {
  it('refuses removal while admitted work is running', async () => {
    const gate = new RunnerWorkspaceUse(async () => 'ready')
    const release = gate.acquire(workspace)
    expect(await gate.withRemovalFence(workspace, async () => true)).toEqual({ kind: 'busy' })
    release()
    expect(await gate.withRemovalFence(workspace, async () => true)).toEqual({ kind: 'completed', value: true })
  })

  it('excludes new use and a second removal until the first finishes', async () => {
    let finish!: () => void
    const pending = new Promise<void>((resolve) => {
      finish = resolve
    })
    const gate = new RunnerWorkspaceUse(async () => 'ready')
    const first = gate.withRemovalFence(workspace, async () => {
      await pending
      return 'removed'
    })
    expect(() => gate.acquire(workspace)).toThrow(WorkspaceUseConflict)
    expect(await gate.withRemovalFence(workspace, async () => 'duplicate')).toEqual({ kind: 'busy' })
    finish()
    expect(await first).toEqual({ kind: 'completed', value: 'removed' })
    expect(() => gate.acquire(workspace)).not.toThrow()
  })

  it('keeps removal blocked when a resource cannot be released', async () => {
    const deleteWork = vi.fn(async () => undefined)
    const gate = new RunnerWorkspaceUse(async () => 'failed')
    expect(await gate.withRemovalFence(workspace, deleteWork)).toEqual({ kind: 'failed' })
    expect(deleteWork).not.toHaveBeenCalled()
  })

  it('retains command ownership while a detached descendant is alive outside the Home', async () => {
    const spawner = new FakeProcessSpawner()
    const live = new Set(['runner:1'])
    const gate = new RunnerWorkspaceUse(
      async () => 'ready',
      (path) => path,
      undefined,
      () => 'ready',
      () => new Set(live),
    )
    await withTestRunnerResources(
      async () => {
        await gate.withUse(workspace, async () => {
          const command = runCommand('command', [], workspace, new AbortController().signal)
          live.add('descendant:2')
          spawner.children[0]!.close(0)
          await command
        })
      },
      { processSpawner: spawner.spawn, processKiller: () => true },
    )

    const deleteWork = vi.fn(async () => true)
    expect(await gate.withRemovalFence(workspace, deleteWork)).toEqual({ kind: 'busy' })
    expect(deleteWork).not.toHaveBeenCalled()
    live.delete('descendant:2')
    expect(await gate.withRemovalFence(workspace, deleteWork)).toEqual({ kind: 'completed', value: true })
  })

  it.each(['vanished', 'zombie'] as const)(
    'retains ownership when a listed command parent becomes %s during enumeration',
    async (transition) => {
      let phase: 'baseline' | 'parent' | 'child' | 'terminated' = 'baseline'
      let parentStatReads = 0
      const stat = (pid: string, state: string) =>
        `${pid} (command) ${[state, ...Array(18).fill('0'), `${pid}0`].join(' ')}`
      const readProc = (path: string): string => {
        if (path === '/proc/self/cgroup') return '0::/runner.service\n'
        if (path === '/sys/fs/cgroup/runner.service/cgroup.procs') {
          if (phase === 'baseline') return '100\n'
          if (phase === 'parent') return '100\n200\n'
          if (phase === 'child') return '100\n201\n'
          return '100\n'
        }
        if (path === '/proc/100/stat') return stat('100', 'S')
        if (path === '/proc/201/stat') return stat('201', 'S')
        if (path === '/proc/200/stat') {
          parentStatReads++
          if (transition === 'vanished') throw Object.assign(new Error('gone'), { code: 'ENOENT' })
          return stat('200', parentStatReads === 1 ? 'S' : 'Z')
        }
        throw new Error(`Unexpected proc read: ${path}`)
      }
      const gate = new RunnerWorkspaceUse(
        async () => 'ready',
        (path) => path,
        undefined,
        () => 'ready',
        () => snapshotRunnerProcessIdentities(readProc),
      )
      await gate.withUse(workspace, async () => noteWorkspaceCommandStart())

      phase = 'parent'
      const deleteWork = vi.fn(async () => true)
      expect(await gate.withRemovalFence(workspace, deleteWork)).toEqual({
        kind: 'failed',
        reason: 'process_termination_unconfirmed',
      })
      expect(deleteWork).not.toHaveBeenCalled()

      phase = 'child'
      expect(await gate.withRemovalFence(workspace, deleteWork)).toEqual({ kind: 'busy' })
      expect(deleteWork).not.toHaveBeenCalled()

      phase = 'terminated'
      expect(await gate.withRemovalFence(workspace, deleteWork)).toEqual({ kind: 'completed', value: true })
    },
  )

  it('rejects a late command from an earlier async context while removal holds the gate', async () => {
    const gate = new RunnerWorkspaceUse(async () => 'ready')
    let startLateCommand!: () => void
    let lateCommand!: Promise<void>
    await gate.withUse(workspace, async () => {
      const signal = new Promise<void>((resolve) => {
        startLateCommand = resolve
      })
      lateCommand = signal.then(() => noteWorkspaceCommandStart())
    })
    const result = await gate.withRemovalFence(workspace, async () => {
      startLateCommand()
      await expect(lateCommand).rejects.toThrow(WorkspaceUseConflict)
      return true
    })
    expect(result).toEqual({ kind: 'completed', value: true })
  })

  it('keeps a command owner unsafe when process identities cannot be inspected', async () => {
    let snapshot: Set<string> | null = new Set(['runner:1'])
    const gate = new RunnerWorkspaceUse(
      async () => 'ready',
      (path) => path,
      undefined,
      () => 'ready',
      () => snapshot,
    )
    await gate.withUse(workspace, async () => noteWorkspaceCommandStart())
    snapshot = null
    expect(await gate.withRemovalFence(workspace, async () => true)).toEqual({
      kind: 'failed',
      reason: 'process_termination_unconfirmed',
    })
  })

  it.each([
    ['busy', { kind: 'busy' }],
    ['failed', { kind: 'failed', reason: 'process_termination_unconfirmed' }],
  ] as const)('refuses removal when process inspection is %s', async (state, result) => {
    const deleteWork = vi.fn(async () => undefined)
    const gate = new RunnerWorkspaceUse(
      async () => 'ready',
      (path) => path,
      undefined,
      () => state,
    )
    expect(await gate.withRemovalFence(workspace, deleteWork)).toEqual(result)
    expect(deleteWork).not.toHaveBeenCalled()
  })

  it('refuses an old directory without previous-generation termination evidence', async () => {
    const deleteWork = vi.fn(async () => undefined)
    const gate = new RunnerWorkspaceUse(async () => 'ready')
    gate.blockPreviousGeneration([workspace])
    expect(await gate.withRemovalFence(workspace, deleteWork)).toEqual({
      kind: 'failed',
      reason: 'previous_generation_unconfirmed',
    })
    expect(deleteWork).not.toHaveBeenCalled()
  })

  it('resolves a directory handle and descendants to their stable Workspace owner', () => {
    const gate = new RunnerWorkspaceUse(
      async () => 'ready',
      (path) => (path === '/proc/123/fd/7' ? `${workspace}/REPOS/source` : path),
    )
    expect(gate.ownerForWorkDir('/proc/123/fd/7', [workspace])).toBe(workspace)
    expect(gate.ownerForWorkDir('/runner/workspaces/another-home', [workspace])).toBeNull()
  })
})
