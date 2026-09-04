import { describe, expect, it, vi } from 'vitest'
import { createCancelHandler } from './cancel-handler.js'
import { ManagerExecutionRegistry } from '../runtime/manager-execution-registry.js'

function target() {
  return {
    kind: 'generic' as const,
    projectId: '__mohist_slack_manager__',
    sessionId: 'session-1',
    binding: {
      runtime: 'opencode',
      runtimeSessionId: 'isolated-session',
      runnerId: 'runner-1',
      workDir: '/work/manager',
    },
  }
}

describe('Manager follow-up cancellation', () => {
  it.each([
    ['disabled', null, { state: 'unavailable', error: 'runtime-unavailable' }],
    ['not ready', { ready: () => false }, { state: 'unavailable' }],
  ])('reports a stable control error when the binding runtime is %s', async (_case, openCodeRuntime, expected) => {
    const resolver = vi.fn(async () => ({
      projectId: '__mohist_slack_manager__',
      runtimeSessionId: 'isolated-session',
      workDir: '/work/manager',
    }))
    const receive = createCancelHandler({
      followupTargetResolver: resolver,
      openCodeRuntime: openCodeRuntime as never,
    })

    await expect(receive({ target: target() })).resolves.toEqual(expected)
    expect(resolver).not.toHaveBeenCalled()
  })

  it('cancels the isolated runtime and closes its execution lease', async () => {
    const isolatedCancel = vi.fn(async () => ({
      ok: true as const,
      value: { cancelled: true, stopConfirmed: true },
      diagnostics: [],
    }))
    const sharedCancel = vi.fn()
    const isolatedRuntime = {
      ready: () => true,
      cancel: isolatedCancel,
    }
    const sharedRuntime = {
      ready: () => true,
      cancel: sharedCancel,
    }
    const boundary = { dispose: vi.fn(async () => undefined) }
    const registry = new ManagerExecutionRegistry()
    registry.register({
      executionId: 'manager:session-1:followup-1',
      boundary: boundary as never,
      sessionId: 'session-1',
      runtimeSessionId: '',
      workDir: '/work/manager',
    })
    expect(
      registry.bindRuntime(boundary as never, {
        handle: { kind: 'opencode', runtime: isolatedRuntime as never },
        sessionId: 'session-1',
        runtimeSessionId: 'isolated-session',
        workDir: '/work/manager',
      }),
    ).toBe(true)
    const finished = vi.fn(async () => undefined)
    const receive = createCancelHandler({
      followupTargetResolver: () => ({
        projectId: '__mohist_slack_manager__',
        runtimeSessionId: 'isolated-session',
        workDir: '/work/manager',
      }),
      openCodeRuntime: sharedRuntime as never,
      managerExecutionRegistry: registry,
      onManagerExecutionFinished: finished,
    })

    const result = await receive({ target: target() })

    expect(result).toEqual({ state: 'stopped' })
    expect(isolatedCancel).toHaveBeenCalledOnce()
    expect(sharedCancel).not.toHaveBeenCalled()
    expect(boundary.dispose).toHaveBeenCalledOnce()
    expect(finished).toHaveBeenCalledWith('manager:session-1:followup-1')
    expect(registry.findForCancel('session-1', 'opencode', 'isolated-session')).toBeNull()
    await registry.dispose(boundary as never)
    expect(boundary.dispose).toHaveBeenCalledOnce()
  })
})
