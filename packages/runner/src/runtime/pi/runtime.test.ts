import { describe, expect, it, vi } from 'vitest'
import { mkdtempSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { tmpdir } from 'node:os'
import { PiRuntime } from './runtime.js'

type FixtureMessage = {
  role: string
  content: unknown
  stopReason?: string
  errorMessage?: string
}

function sessionFixture(overrides: Partial<Record<string, unknown>> = {}) {
  return {
    sessionFile: '/workspace/session.json',
    sessionId: 'session-1',
    messages: [
      { role: 'user', content: 'do the work' },
      { role: 'assistant', content: [{ type: 'text', text: 'adopted final text' }] },
    ] as FixtureMessage[],
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

describe('PiRuntime runTurn deadline', () => {
  it('settles deadline-exceeded even when prompt and abort never resolve', async () => {
    vi.useFakeTimers()
    try {
      const never = () => new Promise<void>(() => {})
      const prompt = vi.fn(never)
      const abort = vi.fn(never)
      const session = {
        ...sessionFixture({
          messages: [{ role: 'user', content: 'do the work' }],
          isStreaming: false,
          prompt,
          abort,
        }),
      }
      const runtime = runtimeFor(session)
      await runtime.start()

      const controller = new AbortController()
      const turn = runtime.runTurn(
        {
          target: { runtime: 'pi', runtimeSessionId: '/workspace/session.json', workDir: '/workspace' },
          prompt: 'do the work',
          durationMs: 25,
        },
        controller.signal,
      )
      const pending = turn.then(() => false, () => false).catch(() => false)
      await vi.advanceTimersByTimeAsync(25)
      const settled = await Promise.race([pending, Promise.resolve(true)])
      // Give the microtask queue a chance to settle the turn after the timer.
      await vi.advanceTimersByTimeAsync(0)
      const result = await turn

      expect(result.ok).toBe(false)
      if (result.ok) return
      expect(result.error.kind).toBe('deadline-exceeded')
      // abort was attempted (bounded) even though it never resolves
      expect(abort).toHaveBeenCalled()
      void settled
    } finally {
      vi.useRealTimers()
    }
  })

  it('settles a terminal session as success when prompt never resolves', async () => {
    vi.useFakeTimers()
    try {
      const session = {
        ...sessionFixture({
          messages: [{ role: 'user', content: 'do the work' }],
          isStreaming: true,
          prompt: vi.fn(() => new Promise<void>(() => {})),
        }),
      }
      const runtime = runtimeFor(session)
      await runtime.start()

      const controller = new AbortController()
      const turn = runtime.runTurn(
        {
          target: { runtime: 'pi', runtimeSessionId: '/workspace/session.json', workDir: '/workspace' },
          prompt: 'do the work',
        },
        controller.signal,
      )
      // Let runTurn reach the point where the initial message count is
      // captured and the prompt is in flight, then simulate the model
      // finishing with a terminal message while the prompt stays stuck and
      // isStreaming stays true (the hang shape seen in production).
      for (let i = 0; i < 6; i++) await Promise.resolve()
      expect(session.prompt).toHaveBeenCalled()
      session.messages = [
        { role: 'user', content: 'do the work' },
        { role: 'assistant', content: [{ type: 'text', text: 'the final answer' }], stopReason: 'stop' },
      ] as never
      await vi.advanceTimersByTimeAsync(30_000)

      const result = await turn
      expect(result.ok).toBe(true)
      if (!result.ok) return
      expect(result.value.facts.finalAssistantText).toBe('the final answer')
    } finally {
      vi.useRealTimers()
    }
  })

  it('settles a terminal error session as failure when prompt never resolves', async () => {
    vi.useFakeTimers()
    try {
      const session = {
        ...sessionFixture({
          messages: [{ role: 'user', content: 'do the work' }],
          isStreaming: true,
          prompt: vi.fn(() => new Promise<void>(() => {})),
        }),
      }
      const runtime = runtimeFor(session)
      await runtime.start()

      const controller = new AbortController()
      const turn = runtime.runTurn(
        {
          target: { runtime: 'pi', runtimeSessionId: '/workspace/session.json', workDir: '/workspace' },
          prompt: 'do the work',
        },
        controller.signal,
      )
      for (let i = 0; i < 6; i++) await Promise.resolve()
      expect(session.prompt).toHaveBeenCalled()
      session.messages = [
        { role: 'user', content: 'do the work' },
        { role: 'assistant', content: [], stopReason: 'error', errorMessage: 'provider exploded' },
      ] as never
      await vi.advanceTimersByTimeAsync(30_000)

      const result = await turn
      expect(result.ok).toBe(false)
      if (result.ok) return
      expect(result.error.kind).toBe('turn-failed')
    } finally {
      vi.useRealTimers()
    }
  })

  it('settles from a terminal session file when memory diverges from it', async () => {
    vi.useFakeTimers()
    try {
      const dir = mkdtempSync(join(tmpdir(), 'pi-file-settle-'))
      const sessionFile = join(dir, 'session.jsonl')
      writeFileSync(
        sessionFile,
        [
          JSON.stringify({ type: 'message', message: { role: 'user', content: 'do the work' } }),
          JSON.stringify({ type: 'message', message: { role: 'toolResult', content: [] } }),
        ].join('\n') + '\n',
      )
      const appendTerminal = () =>
        writeFileSync(
          sessionFile,
          JSON.stringify({
            type: 'message',
            message: { role: 'assistant', content: [{ type: 'text', text: 'file final' }], stopReason: 'stop' },
          }) + '\n',
          { flag: 'a' },
        )
      // Memory keeps a stale toolResult as its last message: overflow recovery
      // can remove the terminal assistant message from agent state while the
      // file keeps it, and the follow-up continue call can hang before any
      // event reaches memory.
      const session = {
        ...sessionFixture({
          messages: [{ role: 'user', content: 'do the work' }],
          isStreaming: true,
          prompt: vi.fn(() => new Promise<void>(() => {})),
        }),
      }
      const runtime = runtimeFor(session)
      await runtime.start()

      const controller = new AbortController()
      const turn = runtime.runTurn(
        {
          target: { runtime: 'pi', runtimeSessionId: sessionFile, workDir: '/workspace' },
          prompt: 'do the work',
        },
        controller.signal,
      )
      for (let i = 0; i < 6; i++) await Promise.resolve()
      expect(session.prompt).toHaveBeenCalled()
      // Memory never gains a terminal message, but the file grows a terminal
      // assistant message while the prompt stays stuck.
      appendTerminal()
      await vi.advanceTimersByTimeAsync(30_000)

      const result = await turn
      expect(result.ok).toBe(true)
    } finally {
      vi.useRealTimers()
    }
  })

  it('does not settle from a stale terminal message before this turn produces one', async () => {
    vi.useFakeTimers()
    try {
      const session = {
        ...sessionFixture({
          // Reused session: the previous turn already ended with a terminal
          // assistant message. The new turn has not produced anything yet.
          messages: [
            { role: 'user', content: 'old' },
            { role: 'assistant', content: [{ type: 'text', text: 'old answer' }], stopReason: 'stop' },
          ],
          isStreaming: true,
          prompt: vi.fn(() => new Promise<void>(() => {})),
        }),
      }
      const runtime = runtimeFor(session)
      await runtime.start()

      const controller = new AbortController()
      const turn = runtime.runTurn(
        {
          target: { runtime: 'pi', runtimeSessionId: '/workspace/session.json', workDir: '/workspace' },
          prompt: 'do the work',
        },
        controller.signal,
      )
      await vi.advanceTimersByTimeAsync(60_000)
      // The turn is still in flight with no new terminal message; the stale
      // terminal message must not settle it early.
      await Promise.resolve()
      await Promise.resolve()
      const settled = await Promise.race([turn.then(() => true), Promise.resolve(false)])
      expect(settled).toBe(false)
    } finally {
      vi.useRealTimers()
    }
  })

  it('lets a healthy prompt win the race before the settle guard fires', async () => {
    vi.useFakeTimers()
    try {
      const session = {
        ...sessionFixture({
          isStreaming: false,
          prompt: vi.fn(async () => undefined),
        }),
      }
      const runtime = runtimeFor(session)
      await runtime.start()

      const controller = new AbortController()
      const turn = runtime.runTurn(
        {
          target: { runtime: 'pi', runtimeSessionId: '/workspace/session.json', workDir: '/workspace' },
          prompt: 'do the work',
        },
        controller.signal,
      )
      const result = await turn
      expect(result.ok).toBe(true)
      if (!result.ok) return
      expect(result.value.facts.finalAssistantText).toBe('adopted final text')
    } finally {
      vi.useRealTimers()
    }
  })
})

describe('PiRuntime shutdown', () => {
  it('abandons a non-terminating services.close at the configured deadline', async () => {
    vi.useFakeTimers()
    try {
      const close = vi.fn(() => new Promise<void>(() => {}))
      const runtime = new PiRuntime({
        agentDir: '/agent',
        runtimeShutdownTimeoutMs: 25,
        sdkFactory: {
          create: async () => ({
            catalog: async () => [],
            createSession: async () => {
              throw new Error('not used')
            },
            openSession: async () => {
              throw new Error('not used')
            },
            model: () => undefined,
            close,
          }),
        },
      })
      await runtime.start()
      const shutdown = runtime.shutdown()
      await vi.advanceTimersByTimeAsync(25)
      await expect(shutdown).resolves.toBeUndefined()
      expect(close).toHaveBeenCalledOnce()
      expect(runtime.ready()).toBe(false)
    } finally {
      vi.useRealTimers()
    }
  })
})

describe('PiRuntime followup', () => {
  it('applies the requested model and reasoningEffort before accepting an idle follow-up', async () => {
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
      options: { model: 'provider/configured-model', reasoningEffort: 'high' },
    })

    expect(result.ok).toBe(true)
    expect(setModel).toHaveBeenCalledWith(model)
    expect(setThinkingLevel).toHaveBeenCalledWith('high')
    expect(prompt).toHaveBeenCalledWith('continue', expect.any(Object))
  })
})
