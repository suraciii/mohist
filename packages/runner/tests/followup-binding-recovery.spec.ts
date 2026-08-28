import { describe, expect, it, vi } from 'vitest'
import { createFollowupHandler } from '../src/server/followup-handler.js'

function payload() {
  return {
    target: {
      kind: 'generic',
      projectId: 'project-1',
      sessionId: 'session-1',
      binding: {
        runtime: 'opencode',
        runtimeSessionId: 'runtime-old',
        runnerId: 'runner-1',
        workDir: '/work',
      },
    },
    text: 'continue this work',
    inputId: 'input-1',
    turnId: 'turn-1',
    operationId: 'operation-1',
  } as const
}

function eventQueue(order: string[]) {
  return {
    ready: () => true,
    enqueueBeforeExecution: vi.fn(async () => {
      order.push('input')
    }),
    awaitInputReceipt: vi.fn(async () => ({ type: 'session.input' })),
    enqueueProducedFact: vi.fn(async () => undefined),
  }
}

describe('follow-up Runtime binding recovery', () => {
  it('replaces a confirmed-missing binding before submitting the input', async () => {
    const order: string[] = []
    const followup = vi.fn(async (request: { target: { runtimeSessionId: string } }) => {
      order.push(`followup:${request.target.runtimeSessionId}`)
      return {
        ok: true as const,
        value: { facts: { runtimeSessionId: request.target.runtimeSessionId } },
        diagnostics: [],
      }
    })
    const runtime = {
      ready: () => true,
      resolveSession: vi.fn(async () => {
        order.push('probe')
        return { ok: false as const, error: { kind: 'missing-session', message: 'gone' }, diagnostics: [] }
      }),
      createSession: vi.fn(async () => {
        order.push('create')
        return {
          ok: true as const,
          value: { runtimeSessionId: 'runtime-new', workDir: '/work' },
          diagnostics: [],
        }
      }),
      followup,
    }
    const recover = vi.fn(async (_projectId: string, _sessionId: string, body: Record<string, unknown>) => {
      order.push('replace')
      expect(body).toEqual({
        expectedRunnerId: 'runner-1',
        expectedRuntime: 'opencode',
        expectedRuntimeSessionId: 'runtime-old',
        replacementRuntimeSessionId: 'runtime-new',
        expectedQueuedTurnId: 'turn-1',
      })
    })
    const receive = createFollowupHandler({
      followupTargetResolver: () => ({ runtimeSessionId: 'runtime-old', workDir: '/work', projectId: 'project-1' }),
      agentSessionRuntimeEventQueue: eventQueue(order) as never,
      openCodeRuntime: runtime as never,
      connection: { recoverMissingAgentSession: recover } as never,
      runnerId: 'runner-1',
    })

    await expect(receive(payload() as never)).resolves.toEqual({ accepted: true })

    expect(order.slice(0, 5)).toEqual(['probe', 'create', 'replace', 'input', 'followup:runtime-new'])
    expect(runtime.createSession).toHaveBeenCalledOnce()
    expect(recover).toHaveBeenCalledOnce()
    expect(followup).toHaveBeenCalledOnce()
  })

  it.each(['deadline-exceeded', 'unauthorized', 'server-error', 'malformed-response'])(
    'fails closed for a %s probe without creating or submitting',
    async (kind) => {
      const order: string[] = []
      const runtime = {
        ready: () => true,
        resolveSession: vi.fn(async () => ({
          ok: false as const,
          error: { kind, message: 'not authoritative missing evidence' },
          diagnostics: [],
        })),
        createSession: vi.fn(),
        followup: vi.fn(),
      }
      const queue = eventQueue(order)
      const recover = vi.fn()
      const receive = createFollowupHandler({
        followupTargetResolver: () => ({ runtimeSessionId: 'runtime-old', workDir: '/work', projectId: 'project-1' }),
        agentSessionRuntimeEventQueue: queue as never,
        openCodeRuntime: runtime as never,
        connection: { recoverMissingAgentSession: recover } as never,
        runnerId: 'runner-1',
      })

      await expect(receive(payload() as never)).resolves.toEqual({ accepted: false, error: 'unavailable' })
      expect(runtime.createSession).not.toHaveBeenCalled()
      expect(recover).not.toHaveBeenCalled()
      expect(queue.enqueueBeforeExecution).not.toHaveBeenCalled()
      expect(runtime.followup).not.toHaveBeenCalled()
    },
  )

  it('does not submit the input when replacement binding persistence fails', async () => {
    const order: string[] = []
    const runtime = {
      ready: () => true,
      resolveSession: vi.fn(async () => ({
        ok: false as const,
        error: { kind: 'missing-session', message: 'gone' },
        diagnostics: [],
      })),
      createSession: vi.fn(async () => ({
        ok: true as const,
        value: { runtimeSessionId: 'runtime-candidate', workDir: '/work' },
        diagnostics: [],
      })),
      followup: vi.fn(),
    }
    const queue = eventQueue(order)
    const receive = createFollowupHandler({
      followupTargetResolver: () => ({ runtimeSessionId: 'runtime-old', workDir: '/work', projectId: 'project-1' }),
      agentSessionRuntimeEventQueue: queue as never,
      openCodeRuntime: runtime as never,
      connection: {
        recoverMissingAgentSession: vi.fn(async () => {
          throw new Error('stale binding')
        }),
      } as never,
      runnerId: 'runner-1',
    })

    await expect(receive(payload() as never)).resolves.toEqual({ accepted: false, error: 'unavailable' })
    expect(queue.enqueueBeforeExecution).not.toHaveBeenCalled()
    expect(runtime.followup).not.toHaveBeenCalled()
  })
})
