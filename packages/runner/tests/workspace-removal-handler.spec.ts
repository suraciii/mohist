import { describe, expect, it as vitestIt, vi } from 'vitest'
import {
  createWorkspaceRemovalHandler,
  type WorkspaceRemovalHandlerDeps,
} from '../src/server/workspace-removal-handler.js'
import type { WorkspaceRemovalFence, WorkspaceRemovalFenceResult } from '../src/runtime/workspace-removal-fence.js'
import type { WorkspaceRegistry } from '../src/runtime/workspace-registry.js'

const removalTestRuntime = vi.hoisted(() => {
  type State = {
    readonly mocks: { deleteDirectory: ReturnType<typeof vi.fn>; validateWorkspaceIdentity: ReturnType<typeof vi.fn> }
  }
  const { AsyncLocalStorage } = process.getBuiltinModule('node:async_hooks') as typeof import('node:async_hooks')
  const storage = new AsyncLocalStorage<State>()
  const current = () => {
    const state = storage.getStore()
    if (!state) throw new Error('workspace removal test context is not active')
    return state
  }
  const scoped = (name: 'deleteDirectory' | 'validateWorkspaceIdentity') => {
    const target = (() => undefined) as (...args: unknown[]) => unknown
    Object.defineProperty(target, '_isMockFunction', { value: true })
    return new Proxy(target, {
      apply(_target, thisArg, args) {
        return Reflect.apply(current().mocks[name], thisArg, args)
      },
      get(_target, property) {
        const value = Reflect.get(current().mocks[name], property)
        return typeof value === 'function' ? value.bind(current().mocks[name]) : value
      },
      set(_target, property, value) {
        return Reflect.set(current().mocks[name], property, value)
      },
    }) as unknown as ReturnType<typeof vi.fn>
  }
  return {
    storage,
    deleteDirectory: scoped('deleteDirectory'),
    validateWorkspaceIdentity: scoped('validateWorkspaceIdentity'),
  }
})

const deleteDirectory = removalTestRuntime.deleteDirectory
const validateWorkspaceIdentity = removalTestRuntime.validateWorkspaceIdentity

vi.mock('../src/system/process.js', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../src/system/process.js')>()),
  deleteDirectory: removalTestRuntime.deleteDirectory,
}))
vi.mock('../src/runtime/workspace.js', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../src/runtime/workspace.js')>()),
  validateWorkspaceIdentity: removalTestRuntime.validateWorkspaceIdentity,
}))

const workspacePath = '/runner/workspaces/wr-1'
const query = {
  workflowRunId: 'wr-1',
  gitUrl: 'https://repo.test/mohist.git',
  workspacePath,
  branch: 'mohist/run-wr-1',
  baseBranch: 'main',
}

function createRegistry(calls: string[]): WorkspaceRegistry {
  return {
    findByWorkspacePath: vi.fn(() => ({ workflowRunId: query.workflowRunId })),
    remove: vi.fn(async () => {
      calls.push('registry-remove')
      return true
    }),
  } as unknown as WorkspaceRegistry
}

function createHandler(deps: WorkspaceRemovalHandlerDeps) {
  return createWorkspaceRemovalHandler(deps)
}

function completedFence(calls: string[]): WorkspaceRemovalFence {
  return {
    async withRemovalFence<T>(path: string, callback: () => Promise<T>): Promise<WorkspaceRemovalFenceResult<T>> {
      calls.push(`fence-enter:${path}`)
      const value = await callback()
      calls.push('fence-exit')
      return { kind: 'completed', value }
    },
  }
}

describe('RemoveWorkspace removal fence', () => {
  function withRemovalMocks(body: () => Promise<void>): Promise<void> {
    return removalTestRuntime.storage.run(
      {
        mocks: {
          deleteDirectory: vi.fn(async () => undefined),
          validateWorkspaceIdentity: vi.fn(async () => undefined),
        },
      },
      body,
    )
  }

  function it(name: string, body: () => Promise<void>): void {
    vitestIt(name, async () => await withRemovalMocks(body))
  }

  it('keeps path inspection, identity, delete, and registry removal inside the idle fence', async () => {
    const calls: string[] = []
    const registry = createRegistry(calls)
    const handler = createHandler({
      runnerRoot: '/runner',
      registry,
      pathExists: vi.fn(() => {
        calls.push('path-exists')
        return true
      }),
      removalFence: () => completedFence(calls),
    })
    validateWorkspaceIdentity.mockImplementation(async () => {
      calls.push('identity')
    })
    deleteDirectory.mockImplementation(async () => {
      calls.push('delete')
    })

    await expect(handler(query)).resolves.toMatchObject({ removed: true, status: 'removed' })
    expect(calls).toEqual([
      `fence-enter:${workspacePath}`,
      'path-exists',
      'identity',
      'delete',
      'registry-remove',
      'fence-exit',
    ])
  })

  vitestIt.each([
    ['busy', 'Workspace is busy and cannot be safely released'],
    ['failed', 'Workspace cannot be safely released because the removal fence failed'],
  ] as const)(
    'returns cleanup failure and does not enter the callback when the fence is %s',
    async (kind, message) =>
      await withRemovalMocks(async () => {
        const pathExists = vi.fn(() => true)
        const registry = createRegistry([])
        const fence: WorkspaceRemovalFence = {
          async withRemovalFence<T>(): Promise<WorkspaceRemovalFenceResult<T>> {
            return { kind }
          },
        }
        const handler = createHandler({ runnerRoot: '/runner', registry, pathExists, removalFence: () => fence })

        await expect(handler(query)).resolves.toEqual({
          removed: false,
          status: 'failed',
          path: workspacePath,
          reason: 'workspace_cleanup_failed',
          message,
        })
        expect(pathExists).not.toHaveBeenCalled()
        expect(validateWorkspaceIdentity).not.toHaveBeenCalled()
        expect(deleteDirectory).not.toHaveBeenCalled()
        expect(registry.remove).not.toHaveBeenCalled()
      }),
  )

  it('drops registry identity for a missing directory only after fence admission', async () => {
    const calls: string[] = []
    const registry = createRegistry(calls)
    const handler = createHandler({
      runnerRoot: '/runner',
      registry,
      pathExists: vi.fn(() => {
        calls.push('path-exists')
        return false
      }),
      removalFence: () => completedFence(calls),
    })

    await expect(handler(query)).resolves.toEqual({
      removed: false,
      status: 'missing',
      path: workspacePath,
      reason: 'workspace_missing',
      message: 'Workspace already removed',
    })
    expect(calls).toEqual([`fence-enter:${workspacePath}`, 'path-exists', 'registry-remove', 'fence-exit'])
    expect(validateWorkspaceIdentity).not.toHaveBeenCalled()
    expect(deleteDirectory).not.toHaveBeenCalled()
  })

  it('keeps the existing behavior when no Runtime fence is available', async () => {
    const calls: string[] = []
    const registry = createRegistry(calls)
    const handler = createHandler({
      runnerRoot: '/runner',
      registry,
      pathExists: vi.fn(() => true),
    })

    await expect(handler(query)).resolves.toMatchObject({ removed: true, status: 'removed' })
    expect(registry.remove).toHaveBeenCalledOnce()
    expect(deleteDirectory).toHaveBeenCalledOnce()
  })

  it('preserves identity failure semantics inside the fence', async () => {
    const calls: string[] = []
    const registry = createRegistry(calls)
    validateWorkspaceIdentity.mockRejectedValue(new Error('marker mismatch'))
    const handler = createHandler({
      runnerRoot: '/runner',
      registry,
      pathExists: vi.fn(() => true),
      removalFence: () => completedFence(calls),
    })

    await expect(handler(query)).resolves.toEqual({
      removed: false,
      status: 'failed',
      path: workspacePath,
      reason: 'workspace_identity_mismatch',
      message: 'marker mismatch',
    })
    expect(deleteDirectory).not.toHaveBeenCalled()
    expect(registry.remove).not.toHaveBeenCalled()
  })
})
