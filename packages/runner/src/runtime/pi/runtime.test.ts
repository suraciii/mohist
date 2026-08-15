import { describe, expect, it, vi } from 'vitest'
import { PiRuntime } from './runtime.js'

function sessionFixture(overrides: Partial<Record<string, unknown>> = {}) {
  return {
    sessionFile: '/workspace/session.json',
    sessionId: 'session-1',
    messages: [
      { role: 'user', content: 'do the work' },
      { role: 'assistant', content: [{ type: 'text', text: 'adopted final text' }] },
    ],
    isStreaming: false,
    subscribe: () => () => undefined,
    prompt: vi.fn(async () => undefined),
    steer: vi.fn(async () => undefined),
    abort: vi.fn(async () => undefined),
    compact: vi.fn(async () => undefined),
    setModel: vi.fn(async () => undefined),
    setThinkingLevel: vi.fn(),
    getModel: () => undefined,
    getThinkingLevel: () => 'off',
    dispose: () => undefined,
    ...overrides,
  }
}

function runtimeFor(session: ReturnType<typeof sessionFixture>, openSession?: () => Promise<never>) {
  return new PiRuntime({
    agentDir: '/agent',
    sdkFactory: {
      create: async () => ({
        catalog: async () => [{ provider: 'provider', id: 'model' }],
        createSession: async () => session,
        openSession: openSession ?? (async () => session),
        model: () => ({ provider: 'provider', id: 'model' }),
        close: async () => undefined,
      }),
    },
  })
}

describe('PiRuntime inspectTurn', () => {
  it('reports the recorded terminal turn without running one', async () => {
    const session = sessionFixture()
    const runtime = runtimeFor(session)
    await runtime.start()

    const result = await runtime.inspectTurn({
      target: { runtime: 'pi', runtimeSessionId: '/workspace/session.json', workDir: '/workspace' },
    })

    expect(result.ok).toBe(true)
    if (!result.ok) return
    expect(result.value).toEqual({
      runtimeSessionId: '/workspace/session.json',
      workDir: '/workspace',
      activeTurn: false,
      finalAssistantText: 'adopted final text',
      failed: false,
      errorMessage: null,
    })
    expect(session.prompt).not.toHaveBeenCalled()
  })

  it('marks a recorded failure and an active turn', async () => {
    const failed = sessionFixture({
      messages: [{ role: 'assistant', content: 'nope', stopReason: 'error', errorMessage: 'provider exploded' }],
    })
    const failedRuntime = runtimeFor(failed)
    await failedRuntime.start()
    const failedResult = await failedRuntime.inspectTurn({
      target: { runtime: 'pi', runtimeSessionId: '/workspace/session.json', workDir: '/workspace' },
    })
    expect(failedResult.ok).toBe(true)
    if (!failedResult.ok) return
    expect(failedResult.value.failed).toBe(true)
    expect(failedResult.value.errorMessage).toBe('provider exploded')

    const streaming = sessionFixture({ isStreaming: true })
    const streamingRuntime = runtimeFor(streaming)
    await streamingRuntime.start()
    const streamingResult = await streamingRuntime.inspectTurn({
      target: { runtime: 'pi', runtimeSessionId: '/workspace/session.json', workDir: '/workspace' },
    })
    expect(streamingResult.ok).toBe(true)
    if (!streamingResult.ok) return
    expect(streamingResult.value.activeTurn).toBe(true)
  })

  it('surfaces a missing session file as missing-session', async () => {
    const runtime = runtimeFor(sessionFixture(), async () => {
      throw Object.assign(new Error('ENOENT: no such file'), { code: 'ENOENT' })
    })
    await runtime.start()

    const result = await runtime.inspectTurn({
      target: { runtime: 'pi', runtimeSessionId: '/workspace/missing.json', workDir: '/workspace' },
    })

    expect(result.ok).toBe(false)
    if (result.ok) return
    expect(result.error.kind).toBe('missing-session')
  })
})

describe('PiRuntime followup', () => {
  it('applies the requested model and variant before accepting an idle follow-up', async () => {
    const setModel = vi.fn(async () => undefined)
    const setThinkingLevel = vi.fn()
    const prompt = vi.fn(async (_text: string, options?: { preflight?: (accepted: boolean) => void }) => {
      options?.preflight?.(true)
    })
    const session = {
      sessionFile: '/workspace/session.json',
      sessionId: 'session-1',
      messages: [],
      isStreaming: false,
      subscribe: () => () => undefined,
      prompt,
      steer: vi.fn(async () => undefined),
      abort: vi.fn(async () => undefined),
      compact: vi.fn(async () => undefined),
      setModel,
      setThinkingLevel,
      getModel: () => undefined,
      getThinkingLevel: () => 'off',
      dispose: () => undefined,
    }
    const model = { provider: 'provider', id: 'configured-model' }
    const runtime = new PiRuntime({
      agentDir: '/agent',
      sdkFactory: {
        create: async () => ({
          catalog: async () => [{ provider: 'provider', id: 'configured-model' }],
          createSession: async () => session,
          openSession: async () => session,
          model: () => model,
          close: async () => undefined,
        }),
      },
    })
    await runtime.start()

    const result = await runtime.followup({
      target: { runtime: 'pi', runtimeSessionId: '/workspace/session.json', workDir: '/workspace' },
      prompt: 'continue',
      options: { model: 'provider/configured-model', variant: 'high' },
    })

    expect(result.ok).toBe(true)
    expect(setModel).toHaveBeenCalledWith(model)
    expect(setThinkingLevel).toHaveBeenCalledWith('high')
    expect(prompt).toHaveBeenCalledWith('continue', expect.any(Object))
  })
})
