import { describe, expect, it, vi } from 'vitest'
import { createFollowupHandler } from '../src/server/followup-handler.js'

function fixture(kind: 'pi' | 'opencode') {
  const order: string[] = []
  const runtime = {
    ready: () => true,
    resolveSession: vi.fn(async () => ({ ok: true as const, value: { activeTurn: true }, diagnostics: [] })),
    createSession: vi.fn(async () => {
      order.push('create')
      return {
        ok: true as const,
        value: { runtimeSessionId: 'runtime-new', workDir: '/work' },
        diagnostics: [],
      }
    }),
    followup: vi.fn(async (request: { target: { runtimeSessionId: string } }) => {
      order.push(`followup:${request.target.runtimeSessionId}`)
      return {
        ok: true as const,
        value: { facts: { runtimeSessionId: request.target.runtimeSessionId } },
        diagnostics: [],
      }
    }),
  }
  const recover = vi.fn(async (_project: string, _session: string, _body: Record<string, unknown>) => {
    order.push('replace')
  })
  const outbox = {
    ready: () => true,
    enqueueBeforeExecution: vi.fn(async () => {
      order.push('input')
    }),
    awaitInputReceipt: vi.fn(async () => ({ type: 'session.input' })),
    enqueueProducedFact: vi.fn(async () => undefined),
  }
  const payload = {
    target: {
      kind: 'generic' as const,
      projectId: 'project-1',
      sessionId: 'session-1',
      binding: { runtime: kind, runtimeSessionId: 'runtime-old', runnerId: 'runner-1', workDir: '/work' },
    },
    text: 'continue the accepted work',
    inputId: 'input-1',
    turnId: 'turn-1',
    operationId: 'operation-1',
    executionSource: 'non-slack',
    requiresBindingRecovery: true,
  }
  const deps = {
    followupTargetResolver: () => ({ runtimeSessionId: 'runtime-old', workDir: '/work', projectId: 'project-1' }),
    agentSessionRuntimeEventQueue: outbox as never,
    ...(kind === 'pi' ? { piRuntime: runtime as never } : { openCodeRuntime: runtime as never }),
    connection: { recoverMissingAgentSession: recover } as never,
    runnerId: 'runner-1',
  }
  return { order, runtime, recover, outbox, payload, deps }
}

describe('follow-up after durable missing evidence', () => {
  it.each(['pi', 'opencode'] as const)(
    'replaces the %s binding even when its old physical Session still exists',
    async (kind) => {
      const f = fixture(kind)
      const receive = createFollowupHandler(f.deps)

      await expect(receive(f.payload)).resolves.toEqual({ accepted: true })

      expect(f.runtime.resolveSession).not.toHaveBeenCalled()
      expect(f.runtime.createSession).toHaveBeenCalledOnce()
      expect(f.recover).toHaveBeenCalledWith(
        'project-1',
        'session-1',
        {
          expectedRunnerId: 'runner-1',
          expectedRuntime: kind,
          expectedRuntimeSessionId: 'runtime-old',
          replacementRuntimeSessionId: 'runtime-new',
          expectedQueuedTurnId: 'turn-1',
        },
        expect.any(AbortSignal),
      )
      expect(f.order).toEqual(['create', 'replace', 'input', 'followup:runtime-new'])
      expect(f.outbox.enqueueBeforeExecution).toHaveBeenCalledWith(
        expect.objectContaining({
          id: 'input-1',
          sessionTurnId: 'turn-1',
          runtimeSessionId: 'runtime-new',
          event: expect.objectContaining({
            payload: expect.objectContaining({ text: 'continue the accepted work', operationId: 'operation-1' }),
          }),
        }),
      )
      expect(f.runtime.followup).toHaveBeenCalledOnce()
    },
  )

  it('does not submit to an unconfirmed replacement after a lost CAS response', async () => {
    const f = fixture('pi')
    f.recover.mockRejectedValueOnce(new Error('response lost after commit'))

    await expect(createFollowupHandler(f.deps)(f.payload)).resolves.toEqual({ accepted: false, error: 'unavailable' })

    expect(f.runtime.createSession).toHaveBeenCalledOnce()
    expect(f.runtime.followup).not.toHaveBeenCalled()
    expect(f.outbox.enqueueBeforeExecution).not.toHaveBeenCalled()
  })

  it('waits for the bound Runtime when it is disabled instead of failing the accepted Turn', async () => {
    const f = fixture('pi')

    await expect(createFollowupHandler({ ...f.deps, piRuntime: null })(f.payload)).resolves.toEqual({
      accepted: false,
      error: 'unavailable',
    })

    expect(f.recover).not.toHaveBeenCalled()
    expect(f.outbox.enqueueBeforeExecution).not.toHaveBeenCalled()
  })

  it('requires a canonical recovery connection before creating or submitting', async () => {
    const f = fixture('pi')

    await expect(createFollowupHandler({ ...f.deps, connection: null })(f.payload)).resolves.toEqual({
      accepted: false,
      error: 'unavailable',
    })

    expect(f.runtime.createSession).not.toHaveBeenCalled()
    expect(f.runtime.followup).not.toHaveBeenCalled()
  })

  it('does not treat a differently owned Runner binding as missing authority', async () => {
    const f = fixture('pi')

    await expect(createFollowupHandler({ ...f.deps, runnerId: 'other-runner' })(f.payload)).resolves.toEqual({
      accepted: false,
      error: 'unavailable',
    })

    expect(f.runtime.createSession).not.toHaveBeenCalled()
    expect(f.recover).not.toHaveBeenCalled()
  })
})
