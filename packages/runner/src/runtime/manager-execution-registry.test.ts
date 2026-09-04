import { describe, expect, it, vi } from 'vitest'
import { ManagerExecutionRegistry } from './manager-execution-registry.js'

describe('ManagerExecutionRegistry', () => {
  it('indexes a ready isolated runtime by its physical session facts', () => {
    const registry = new ManagerExecutionRegistry()
    const boundary = { dispose: vi.fn(async () => undefined) }
    const handle = { kind: 'opencode' as const, runtime: { ready: () => true } as never }
    registry.register({
      executionId: 'execution-1',
      boundary: boundary as never,
      sessionId: 'session-1',
      runtimeSessionId: '',
      workDir: '/runner',
    })

    expect(
      registry.bindRuntime(boundary as never, {
        handle,
        sessionId: 'session-1',
        runtimeSessionId: 'runtime-session-1',
        workDir: '/work/manager',
      }),
    ).toBe(true)
    expect(registry.findForCancel('session-1', 'opencode', 'runtime-session-1')).toMatchObject({
      executionId: 'execution-1',
      handle,
      workDir: '/work/manager',
    })
    expect(registry.findForCancel('session-1', 'opencode', 'wrong-session')).toBeNull()
  })

  it('disposes each registered boundary at most once across cancel and terminal cleanup', async () => {
    const registry = new ManagerExecutionRegistry()
    const boundary = { dispose: vi.fn(async () => undefined) }
    registry.register({
      executionId: 'execution-1',
      boundary: boundary as never,
      sessionId: 'session-1',
      runtimeSessionId: '',
      workDir: '/runner',
    })

    await registry.dispose(boundary as never)
    await registry.dispose(boundary as never)
    await registry.disposeAll()

    expect(boundary.dispose).toHaveBeenCalledOnce()
  })
})
