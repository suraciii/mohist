import { describe, expect, it, vi } from 'vitest'
import { RunnerWorkspaceUse, WorkspaceUseConflict } from '../src/runtime/runner-workspace-use.js'

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
