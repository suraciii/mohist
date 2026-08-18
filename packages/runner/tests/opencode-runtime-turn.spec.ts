import { afterEach, describe, expect, it, vi } from 'vitest'
import { OpenCodeRuntime } from '../src/runtime/opencode/index.js'
import { buildRuntime, DEFAULT_SESSION_ID } from './support/opencode-turn-test-support.js'

function deferred<T>() {
  let resolve!: (value: T) => void
  const promise = new Promise<T>((resolvePromise) => {
    resolve = resolvePromise
  })
  return { promise, resolve }
}

afterEach(() => {
  vi.useRealTimers()
})

describe('OpenCodeRuntime.runTurn — happy path + turn fact', () => {
  it('Resolves a fresh Session, runs the awaited prompt, and populates finalAssistantText', async () => {
    const { deps, client } = buildRuntime()
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()
    const result = await runtime.runTurn(
      {
        target: { runtime: 'opencode', runtimeSessionId: null, workDir: '/tmp/projA' },
        prompt: 'do the work',
        options: {
          model: { providerID: 'openai', modelID: 'gpt-5' },
          variant: 'high',
          unknownKeys: undefined,
        },
      },
      new AbortController().signal,
    )

    expect(result.ok).toBe(true)
    if (!result.ok) return
    expect(result.value.facts.finalAssistantText).toBe('hello from opencode')
    expect(result.value.facts.runtimeSessionId).toMatch(/^ses_/)
    expect(result.value.facts.workDir).toBe('/tmp/projA')
    expect(client.sessionCreate).toHaveBeenCalledTimes(1)
    expect(client.sessionPrompt).toHaveBeenCalledTimes(1)
    expect(client.sessionAbort).not.toHaveBeenCalled()
    const promptArg = client.sessionPrompt.mock.calls[0]?.[0] as {
      sessionID: string
      directory: string
      model?: unknown
      parts: unknown[]
      system?: string
      variant?: string
    }
    expect(promptArg.sessionID).toMatch(/^ses_/)
    expect(promptArg.directory).toBe('/tmp/projA')
    expect(promptArg.model).toEqual({ providerID: 'openai', modelID: 'gpt-5' })
    expect(promptArg.variant).toBe('high')
    expect(promptArg.system).toBeUndefined()
    expect(promptArg.parts).toEqual([{ type: 'text', text: 'do the work' }])
  })

  it('Returns a null finalAssistantText when the prompt has no text parts', async () => {
    const { deps } = buildRuntime({
      promptResult: { data: { info: { id: 'msg_1' }, parts: [{ type: 'step-start' }] } },
    })
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()
    const result = await runtime.runTurn(
      {
        target: { runtime: 'opencode', runtimeSessionId: null, workDir: '/tmp/projA' },
        prompt: 'do the work',
      },
      new AbortController().signal,
    )
    expect(result.ok).toBe(true)
    if (!result.ok) return
    expect(result.value.facts.finalAssistantText).toBeNull()
  })
})

describe('OpenCodeRuntime.runTurn — model/variant non-rotation', () => {
  it('A model change reuses the same physical Session id (no rotation, no extra createSession)', async () => {
    const { deps, client } = buildRuntime()
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()
    const first = await runtime.runTurn(
      {
        target: { runtime: 'opencode', runtimeSessionId: null, workDir: '/tmp/projA' },
        prompt: 'first',
        options: { model: { providerID: 'openai', modelID: 'gpt-5' }, variant: 'high', unknownKeys: undefined },
      },
      new AbortController().signal,
    )
    expect(first.ok).toBe(true)
    if (!first.ok) return
    const firstSessionId = first.value.facts.runtimeSessionId
    client.sessionGet.mockImplementationOnce(async () => ({ data: { id: firstSessionId } }))

    const second = await runtime.runTurn(
      {
        target: { runtime: 'opencode', runtimeSessionId: firstSessionId, workDir: '/tmp/projA' },
        prompt: 'second',
        options: {
          model: { providerID: 'anthropic', modelID: 'claude-sonnet-4' },
          variant: null,
          unknownKeys: undefined,
        },
      },
      new AbortController().signal,
    )
    expect(second.ok).toBe(true)
    if (!second.ok) return

    expect(second.value.facts.runtimeSessionId).toBe(firstSessionId)
    expect(client.sessionCreate).toHaveBeenCalledTimes(1)
    expect(client.sessionPrompt).toHaveBeenCalledTimes(2)
    const secondPrompt = client.sessionPrompt.mock.calls[1]?.[0] as { model?: unknown; system?: string }
    expect(secondPrompt.model).toEqual({ providerID: 'anthropic', modelID: 'claude-sonnet-4' })
    expect(secondPrompt.system).toBeUndefined()
  })

  it('Variant change reuses the same physical Session id and updates the prompt variant', async () => {
    const { deps, client } = buildRuntime()
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()
    const first = await runtime.runTurn(
      {
        target: { runtime: 'opencode', runtimeSessionId: null, workDir: '/tmp/projA' },
        prompt: 'first',
        options: { model: null, variant: 'high', unknownKeys: undefined },
      },
      new AbortController().signal,
    )
    if (!first.ok) throw new Error('first turn failed')
    const sessionId = first.value.facts.runtimeSessionId
    client.sessionGet.mockImplementationOnce(async () => ({ data: { id: sessionId } }))

    const second = await runtime.runTurn(
      {
        target: { runtime: 'opencode', runtimeSessionId: sessionId, workDir: '/tmp/projA' },
        prompt: 'second',
        options: { model: null, variant: 'low', unknownKeys: undefined },
      },
      new AbortController().signal,
    )
    expect(second.ok).toBe(true)
    if (!second.ok) return
    expect(second.value.facts.runtimeSessionId).toBe(sessionId)
    expect(client.sessionCreate).toHaveBeenCalledTimes(1)
    const secondPrompt = client.sessionPrompt.mock.calls[1]?.[0] as { system?: string; variant?: string }
    expect(secondPrompt.variant).toBe('low')
    expect(secondPrompt.system).toBeUndefined()
  })
})

describe('OpenCodeRuntime.runTurn — input validation', () => {
  it('Multi-slash model is passed through as provider + remaining id without rotation', async () => {
    const { deps, client } = buildRuntime()
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()
    const result = await runtime.runTurn(
      {
        target: { runtime: 'opencode', runtimeSessionId: null, workDir: '/tmp/projA' },
        prompt: 'do',
        options: {
          model: { providerID: 'openrouter', modelID: 'vendor/family/model' },
          variant: null,
          unknownKeys: undefined,
        },
      },
      new AbortController().signal,
    )
    expect(result.ok).toBe(true)
    if (!result.ok) return
    const promptArg = client.sessionPrompt.mock.calls[0]?.[0] as { model?: unknown }
    expect(promptArg.model).toEqual({ providerID: 'openrouter', modelID: 'vendor/family/model' })
  })

  it('Unknown option keys are surfaced as info diagnostics and do not fail the turn', async () => {
    const { deps, client } = buildRuntime()
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()
    const result = await runtime.runTurn(
      {
        target: { runtime: 'opencode', runtimeSessionId: null, workDir: '/tmp/projA' },
        prompt: 'do',
        options: {
          model: null,
          variant: null,
          unknownKeys: ['type', 'livenessQuietThresholdMs'],
        },
      },
      new AbortController().signal,
    )
    expect(result.ok).toBe(true)
    if (!result.ok) return
    expect(result.value.diagnostics.some((d) => d.code === 'options-unknown-keys')).toBe(true)
    expect(client.sessionPrompt).toHaveBeenCalledTimes(1)
  })

  it('A non-string variant fails actionably with invalid-input before any SDK call', async () => {
    const { deps, client } = buildRuntime()
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()
    const result = await runtime.runTurn(
      {
        target: { runtime: 'opencode', runtimeSessionId: null, workDir: '/tmp/projA' },
        prompt: 'do',
        options: { model: null, variant: 42 as unknown as string, unknownKeys: undefined },
      },
      new AbortController().signal,
    )
    expect(result.ok).toBe(false)
    if (result.ok) return
    expect(result.error.kind).toBe('invalid-input')
    expect(result.error.message).toMatch(/options\.variant/)
    expect(client.sessionCreate).not.toHaveBeenCalled()
    expect(client.sessionPrompt).not.toHaveBeenCalled()
  })

  it('rejects an explicit reasoning effort before it can reach model or variant fields', async () => {
    const { deps, client } = buildRuntime()
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()
    const result = await runtime.runTurn(
      {
        target: { runtime: 'opencode', runtimeSessionId: null, workDir: '/tmp/projA' },
        prompt: 'do',
        options: {
          model: { providerID: 'openai', modelID: 'gpt-5' },
          variant: 'high',
          reasoningEffort: 'high',
        },
      },
      new AbortController().signal,
    )

    expect(result.ok).toBe(false)
    if (result.ok) return
    expect(result.error.kind).toBe('unsupported-execution-configuration')
    expect(result.error.diagnostics).toEqual(
      expect.arrayContaining([expect.objectContaining({ code: 'unsupported_execution_configuration' })]),
    )
    expect(result.diagnostics).toEqual(
      expect.arrayContaining([expect.objectContaining({ code: 'unsupported_execution_configuration' })]),
    )
    expect(client.sessionCreate).not.toHaveBeenCalled()
    expect(client.sessionPrompt).not.toHaveBeenCalled()
  })

  it('rejects an explicit reasoning effort on follow-up before resolving the session', async () => {
    const { deps, client } = buildRuntime()
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()
    const result = await runtime.followup({
      target: { runtime: 'opencode', runtimeSessionId: 'ses-existing', workDir: '/tmp/projA' },
      prompt: 'continue',
      options: { variant: 'balanced', reasoningEffort: 'high' },
    })

    expect(result.ok).toBe(false)
    if (result.ok) return
    expect(result.error.kind).toBe('unsupported-execution-configuration')
    expect(result.error.diagnostics).toEqual(
      expect.arrayContaining([expect.objectContaining({ code: 'unsupported_execution_configuration' })]),
    )
    expect(client.sessionGet).not.toHaveBeenCalled()
    expect(client.sessionCreate).not.toHaveBeenCalled()
    expect(client.sessionPrompt).not.toHaveBeenCalled()
  })
})

describe('OpenCodeRuntime.runTurn — unrestorable binding', () => {
  it('Reusing a Session whose physical id no longer resolves returns missing-session with a Reset hint', async () => {
    const { deps, client } = buildRuntime()
    client.sessionGet.mockImplementationOnce(async () => {
      const err = new Error('not found') as Error & { status?: number }
      err.status = 404
      throw err
    })
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()
    const result = await runtime.runTurn(
      {
        target: { runtime: 'opencode', runtimeSessionId: 'ses_gone', workDir: '/tmp/projA' },
        prompt: 'do',
      },
      new AbortController().signal,
    )
    expect(result.ok).toBe(false)
    if (result.ok) return
    expect(result.error.kind).toBe('missing-session')
    expect(result.error.diagnostics.some((d) => /reset/i.test(d.message))).toBe(true)
    expect(client.sessionCreate).not.toHaveBeenCalled()
    expect(client.sessionPrompt).not.toHaveBeenCalled()
  })
})

describe('OpenCodeRuntime.runTurn — single in-flight work prompt', () => {
  it('Two concurrent work prompts on the same binding are rejected for the second', async () => {
    const { deps, client } = buildRuntime()
    client.sessionGet.mockImplementation(async () => ({ data: { id: 'ses_same' } }))
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()

    let resolvePrompt: (value: unknown) => void = () => {}
    const slowPrompt = new Promise((resolve) => {
      resolvePrompt = resolve
    })
    client.sessionPrompt.mockImplementationOnce(async () => {
      await slowPrompt
      return { data: { info: { id: 'msg_1' }, parts: [{ type: 'text', text: 'first' }] } }
    })

    const first = runtime.runTurn(
      {
        target: { runtime: 'opencode', runtimeSessionId: 'ses_same', workDir: '/tmp/projA' },
        prompt: 'first',
      },
      new AbortController().signal,
    )
    await new Promise((resolve) => setImmediate(resolve))

    const second = await runtime.runTurn(
      {
        target: { runtime: 'opencode', runtimeSessionId: 'ses_same', workDir: '/tmp/projA' },
        prompt: 'second',
      },
      new AbortController().signal,
    )
    expect(second.ok).toBe(false)
    if (second.ok) return
    expect(second.error.kind).toBe('unavailable-runtime')
    expect(second.error.diagnostics.some((d) => d.code === 'in-flight')).toBe(true)

    resolvePrompt({})
    await first
  })
})

describe('OpenCodeRuntime.runTurn — deadline abort on silent hang', () => {
  it('keeps directory release busy when abort wins before the prompt settles', async () => {
    const { deps, client } = buildRuntime()
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()
    const promptStarted = deferred<void>()
    const promptFinished = deferred<unknown>()
    client.sessionPrompt.mockImplementationOnce(async () => {
      promptStarted.resolve()
      return await promptFinished.promise
    })

    const controller = new AbortController()
    const turn = runtime.runTurn(
      {
        target: { runtime: 'opencode', runtimeSessionId: null, workDir: '/tmp/projA' },
        prompt: 'abort before prompt settles',
      },
      controller.signal,
    )
    await promptStarted.promise
    controller.abort()

    const result = await turn
    expect(result.ok).toBe(false)
    expect((await runtime.release('/tmp/projA')).outcome).toBe('busy')
    expect(client.instanceDispose).not.toHaveBeenCalled()

    promptFinished.resolve({ data: { parts: [] } })
    await runtime.shutdown()
  })

  it('keeps directory release busy when abort wins before initial status settles', async () => {
    const { deps, client } = buildRuntime()
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()
    const statusStarted = deferred<void>()
    const statusFinished = deferred<{ data: unknown }>()
    client.sessionStatus.mockImplementationOnce(async () => {
      statusStarted.resolve()
      return await statusFinished.promise
    })

    const controller = new AbortController()
    const turn = runtime.runTurn(
      {
        target: { runtime: 'opencode', runtimeSessionId: null, workDir: '/tmp/projA' },
        prompt: 'abort before status settles',
      },
      controller.signal,
    )
    await statusStarted.promise
    controller.abort()

    const result = await turn
    expect(result.ok).toBe(false)
    expect(client.sessionPrompt).not.toHaveBeenCalled()
    expect((await runtime.release('/tmp/projA')).outcome).toBe('busy')
    expect(client.instanceDispose).not.toHaveBeenCalled()

    statusFinished.resolve({ data: {} })
    await runtime.shutdown()
  })

  it('A silently hanging turn is aborted via client.session.abort() and returns interrupted when the executor signal aborts', async () => {
    vi.useFakeTimers()
    try {
      const { deps, client, subscription } = buildRuntime()
      const runtime = new OpenCodeRuntime(deps)
      await runtime.start()
      client.sessionPrompt.mockImplementationOnce(() => new Promise(() => {}))

      const controller = new AbortController()
      const turnPromise = runtime.runTurn(
        {
          target: { runtime: 'opencode', runtimeSessionId: null, workDir: '/tmp/projA' },
          prompt: 'silent hang',
        },
        controller.signal,
      )
      let settled = false
      void turnPromise.then(() => {
        settled = true
      })
      await vi.advanceTimersByTimeAsync(10)
      subscription.emit({ type: 'session.idle', sessionID: 'ses_/tmp/projA' })

      controller.abort()
      const result = await turnPromise

      expect(settled).toBe(true)
      expect(result.ok).toBe(false)
      if (result.ok) return
      expect(result.error.kind).toBe('interrupted')
      expect(client.sessionAbort).toHaveBeenCalledTimes(1)
    } finally {
      vi.useRealTimers()
    }
  })

  it('An idle event while the prompt is in flight does not complete the turn', async () => {
    const { deps, client, subscription } = buildRuntime()
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()
    let resolvePrompt: (value: unknown) => void = () => {}
    client.sessionPrompt.mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          resolvePrompt = resolve
        }),
    )

    const turnPromise = runtime.runTurn(
      {
        target: { runtime: 'opencode', runtimeSessionId: null, workDir: '/tmp/projA' },
        prompt: 'in flight',
      },
      new AbortController().signal,
    )
    await new Promise((resolve) => setImmediate(resolve))

    subscription.emit({ type: 'session.idle', sessionID: 'ses_/tmp/projA' })
    await new Promise((resolve) => setImmediate(resolve))

    let settled = false
    void turnPromise.then(() => {
      settled = true
    })
    expect(settled).toBe(false)

    resolvePrompt({ data: { info: { id: 'msg_1' }, parts: [{ type: 'text', text: 'ok' }] } })
    const result = await turnPromise
    expect(result.ok).toBe(true)
    if (!result.ok) return
    expect(result.value.facts.finalAssistantText).toBe('ok')
  })
})

describe('OpenCodeRuntime.runTurn — provider-error failure policy', () => {
  it('Quota/credit/billing pattern on the first retry event aborts and fails the turn with the provider message as diagnostics', async () => {
    const { deps, client, subscription } = buildRuntime()
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()
    client.sessionPrompt.mockImplementationOnce(() => new Promise(() => {}))

    const turnPromise = runtime.runTurn(
      {
        target: { runtime: 'opencode', runtimeSessionId: null, workDir: '/tmp/projA' },
        prompt: 'do',
      },
      new AbortController().signal,
    )
    await new Promise((resolve) => setImmediate(resolve))

    subscription.emit({
      type: 'session.status',
      sessionID: 'ses_/tmp/projA',
      payload: {
        sessionID: 'ses_/tmp/projA',
        status: { type: 'retry', attempt: 1, message: 'OpenAI quota exceeded', next: 5000 },
      },
    })

    const result = await turnPromise
    expect(result.ok).toBe(false)
    if (result.ok) return
    expect(result.error.kind).toBe('turn-failed')
    expect(result.error.diagnostics.some((d) => /OpenAI quota/.test(d.message))).toBe(true)
    expect(result.error.diagnostics.some((d) => d.code === 'provider-quota-exhausted')).toBe(true)
    expect(client.sessionAbort).toHaveBeenCalledTimes(1)
    expect(client.sessionAbort).toHaveBeenCalledWith(
      { sessionID: 'ses_/tmp/projA', directory: '/tmp/projA' },
      { throwOnError: true },
    )
  })

  it('The provider wording from the failed workflow triggers first-occurrence failure', async () => {
    const { deps, client, subscription } = buildRuntime()
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()
    client.sessionPrompt.mockImplementationOnce(() => new Promise(() => {}))
    const turnPromise = runtime.runTurn(
      {
        target: { runtime: 'opencode', runtimeSessionId: null, workDir: '/tmp/projA' },
        prompt: 'do',
      },
      new AbortController().signal,
    )
    await new Promise((resolve) => setImmediate(resolve))

    subscription.emit({
      type: 'session.status',
      sessionID: 'ses_/tmp/projA',
      payload: {
        sessionID: 'ses_/tmp/projA',
        status: {
          type: 'retry',
          attempt: 1,
          message: '您已达到每周/每月使用上限，您的限额将在 2026-07-19 11:32:48 重置。',
          next: 1000,
        },
      },
    })

    const result = await turnPromise
    expect(result.ok).toBe(false)
    if (result.ok) return
    expect(result.error.kind).toBe('turn-failed')
    expect(result.error.diagnostics.some((d) => d.code === 'provider-quota-exhausted')).toBe(true)

    client.sessionGet.mockResolvedValueOnce({ data: { id: 'ses_/tmp/projA' } })
    const next = await runtime.runTurn(
      {
        target: { runtime: 'opencode', runtimeSessionId: 'ses_/tmp/projA', workDir: '/tmp/projA' },
        prompt: 'continue with another model',
        options: { model: { providerID: 'openai', modelID: 'gpt-5' }, variant: null },
      },
      new AbortController().signal,
    )
    expect(next.ok).toBe(true)
    expect(client.sessionCreate).toHaveBeenCalledTimes(1)
    expect(client.sessionPrompt.mock.calls[1]?.[0]).toMatchObject({
      sessionID: 'ses_/tmp/projA',
      model: { providerID: 'openai', modelID: 'gpt-5' },
    })
  })

  it('A quota retry for another Session does not abort the current turn', async () => {
    const { deps, client, subscription } = buildRuntime()
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()
    let resolvePrompt: (value: unknown) => void = () => {}
    client.sessionPrompt.mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          resolvePrompt = resolve
        }),
    )
    const turnPromise = runtime.runTurn(
      {
        target: { runtime: 'opencode', runtimeSessionId: null, workDir: '/tmp/projA' },
        prompt: 'do',
      },
      new AbortController().signal,
    )
    await new Promise((resolve) => setImmediate(resolve))

    subscription.emit({
      type: 'session.status',
      sessionID: DEFAULT_SESSION_ID,
      directory: '/tmp/other-project',
      payload: { sessionID: DEFAULT_SESSION_ID, status: { type: 'retry', attempt: 1, message: 'quota exceeded' } },
    })
    subscription.emit({
      type: 'session.status',
      sessionID: 'ses_other',
      payload: {
        sessionID: 'ses_other',
        status: { type: 'retry', attempt: 1, message: 'quota exceeded', next: 1000 },
      },
    })
    await new Promise((resolve) => setImmediate(resolve))
    expect(client.sessionAbort).not.toHaveBeenCalled()
    resolvePrompt({ data: { parts: [{ type: 'text', text: 'done' }] } })
    expect((await turnPromise).ok).toBe(true)
  })

  it('An unconfirmed abort reports abort-unconfirmed instead of a stopped turn', async () => {
    const { deps, client, subscription } = buildRuntime()
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()
    client.sessionPrompt.mockImplementationOnce(() => new Promise(() => {}))
    client.sessionAbort.mockResolvedValueOnce({ data: false })
    const turnPromise = runtime.runTurn(
      {
        target: { runtime: 'opencode', runtimeSessionId: null, workDir: '/tmp/projA' },
        prompt: 'do',
      },
      new AbortController().signal,
    )
    await new Promise((resolve) => setImmediate(resolve))
    subscription.emit({
      type: 'session.status',
      sessionID: 'ses_/tmp/projA',
      payload: {
        sessionID: 'ses_/tmp/projA',
        status: { type: 'retry', attempt: 1, message: 'quota exceeded', next: 1000 },
      },
    })

    const result = await turnPromise
    expect(result.ok).toBe(false)
    if (result.ok) return
    expect(result.error.diagnostics.some((d) => d.code === 'abort-unconfirmed')).toBe(true)
    expect(result.error.diagnostics.some((d) => d.code === 'provider-quota-exhausted')).toBe(true)
  })

  it('A Session that remains busy after abort reports abort-unconfirmed', async () => {
    const { deps, client, subscription } = buildRuntime()
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()
    client.sessionPrompt.mockImplementationOnce(() => new Promise(() => {}))
    const turnPromise = runtime.runTurn(
      {
        target: { runtime: 'opencode', runtimeSessionId: null, workDir: '/tmp/projA' },
        prompt: 'do',
      },
      new AbortController().signal,
    )
    await new Promise((resolve) => setImmediate(resolve))
    client.sessionStatus.mockResolvedValueOnce({ data: { 'ses_/tmp/projA': { type: 'busy' } } })
    subscription.emit({
      type: 'session.status',
      sessionID: 'ses_/tmp/projA',
      payload: { sessionID: 'ses_/tmp/projA', status: { type: 'retry', attempt: 1, message: 'quota exceeded' } },
    })

    const result = await turnPromise
    expect(result.ok).toBe(false)
    if (result.ok) return
    expect(result.error.diagnostics.some((d) => d.code === 'abort-unconfirmed')).toBe(true)
  })

  it('A reconnected event stream restores a quota verdict from session.status', async () => {
    const { deps, client, subscription } = buildRuntime()
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()
    client.sessionPrompt.mockImplementationOnce(() => new Promise(() => {}))
    const turnPromise = runtime.runTurn(
      {
        target: { runtime: 'opencode', runtimeSessionId: null, workDir: '/tmp/projA' },
        prompt: 'do',
      },
      new AbortController().signal,
    )
    await new Promise((resolve) => setImmediate(resolve))
    client.sessionStatus.mockResolvedValueOnce({
      data: {
        'ses_/tmp/projA': {
          type: 'retry',
          attempt: 1,
          message: 'Token Plan usage limit reached',
          next: 1000,
        },
      },
    })
    subscription.emit({ type: 'server.connected', payload: {} })

    const result = await turnPromise
    expect(result.ok).toBe(false)
    if (result.ok) return
    expect(result.error.diagnostics.some((d) => d.code === 'provider-quota-exhausted')).toBe(true)
    expect(client.sessionAbort).toHaveBeenCalledTimes(1)
  })

  it('A recoverable transient error that completes within N retries continues', async () => {
    const { deps, client, subscription } = buildRuntime()
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()
    let resolvePrompt: (value: unknown) => void = () => {}
    client.sessionPrompt.mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          resolvePrompt = resolve
        }),
    )
    const turnPromise = runtime.runTurn(
      {
        target: { runtime: 'opencode', runtimeSessionId: null, workDir: '/tmp/projA' },
        prompt: 'do',
      },
      new AbortController().signal,
    )
    await new Promise((resolve) => setImmediate(resolve))

    for (let attempt = 1; attempt <= 4; attempt += 1) {
      subscription.emit({
        type: 'session.status',
        sessionID: 'ses_/tmp/projA',
        payload: {
          sessionID: 'ses_/tmp/projA',
          status: { type: 'retry', attempt, message: 'rate limit exceeded', next: 200 },
        },
      })
    }

    resolvePrompt({ data: { info: { id: 'msg_1' }, parts: [{ type: 'text', text: 'after 4 retries' }] } })
    const result = await turnPromise
    expect(result.ok).toBe(true)
    if (!result.ok) return
    expect(result.value.facts.finalAssistantText).toBe('after 4 retries')
    expect(client.sessionAbort).not.toHaveBeenCalled()
  })

  it('A recoverable error that retries past the consecutive-retry threshold aborts and fails', async () => {
    const { deps, client, subscription } = buildRuntime()
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()
    client.sessionPrompt.mockImplementationOnce(() => new Promise(() => {}))

    const turnPromise = runtime.runTurn(
      {
        target: { runtime: 'opencode', runtimeSessionId: null, workDir: '/tmp/projA' },
        prompt: 'do',
      },
      new AbortController().signal,
    )
    await new Promise((resolve) => setImmediate(resolve))

    for (let attempt = 1; attempt <= 5; attempt += 1) {
      subscription.emit({
        type: 'session.status',
        sessionID: 'ses_/tmp/projA',
        payload: {
          sessionID: 'ses_/tmp/projA',
          status: { type: 'retry', attempt, message: 'transient 5xx', next: 1000 },
        },
      })
    }

    const result = await turnPromise
    expect(result.ok).toBe(false)
    if (result.ok) return
    expect(result.error.kind).toBe('turn-failed')
    expect(result.error.diagnostics.some((d) => d.code === 'provider-retry-threshold')).toBe(true)
    expect(client.sessionAbort).toHaveBeenCalledTimes(1)
  })

  it('Custom policy: configurable threshold and patterns are honoured', async () => {
    const { deps, client, subscription } = buildRuntime({
      policy: { nonRecoverablePatterns: [/^payment-required$/], consecutiveRetryThreshold: 2 },
    })
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()
    let resolvePrompt: (value: unknown) => void = () => {}
    client.sessionPrompt.mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          resolvePrompt = resolve
        }),
    )
    const turnPromise = runtime.runTurn(
      {
        target: { runtime: 'opencode', runtimeSessionId: null, workDir: '/tmp/projA' },
        prompt: 'do',
      },
      new AbortController().signal,
    )
    await new Promise((resolve) => setImmediate(resolve))

    subscription.emit({
      type: 'session.status',
      sessionID: 'ses_/tmp/projA',
      payload: {
        sessionID: 'ses_/tmp/projA',
        status: { type: 'retry', attempt: 1, message: 'OpenAI quota exceeded', next: 1000 },
      },
    })
    await new Promise((resolve) => setImmediate(resolve))

    subscription.emit({
      type: 'session.status',
      sessionID: 'ses_/tmp/projA',
      payload: {
        sessionID: 'ses_/tmp/projA',
        status: { type: 'retry', attempt: 2, message: 'still retrying', next: 1000 },
      },
    })
    const result = await turnPromise
    expect(result.ok).toBe(false)
    if (result.ok) return
    expect(result.error.kind).toBe('turn-failed')
    expect(result.error.diagnostics.some((d) => d.code === 'provider-retry-threshold')).toBe(true)
    expect(client.sessionAbort).toHaveBeenCalledTimes(1)
    resolvePrompt({})
  })
})

describe('OpenCodeRuntime.runTurn — restart reconciliation', () => {
  it('holds directory release while reconnect reconciliation is pending after the prompt returns', async () => {
    const { deps, client, subscription } = buildRuntime()
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()

    const promptStarted = deferred<void>()
    const promptFinished = deferred<unknown>()
    client.sessionPrompt.mockImplementationOnce(async () => {
      promptStarted.resolve()
      return await promptFinished.promise
    })
    const reconciliationStarted = deferred<void>()
    const reconciliationFinished = deferred<{ data: unknown }>()

    const turn = runtime.runTurn(
      {
        target: { runtime: 'opencode', runtimeSessionId: null, workDir: '/tmp/projA' },
        prompt: 'do',
      },
      new AbortController().signal,
    )
    await promptStarted.promise
    client.sessionStatus.mockImplementationOnce(async () => {
      reconciliationStarted.resolve()
      return await reconciliationFinished.promise
    })
    subscription.emit({ type: 'server.connected', payload: {} })
    await reconciliationStarted.promise

    promptFinished.resolve({ data: { parts: [{ type: 'text', text: 'done' }] } })
    const result = await turn
    expect(result.ok).toBe(true)
    expect((await runtime.release('/tmp/projA')).outcome).toBe('busy')
    expect(client.instanceDispose).not.toHaveBeenCalled()

    reconciliationFinished.resolve({ data: {} })
    await runtime.shutdown()
  })

  it('Rebuilds after a transport failure whose abort confirmation cannot reach the server', async () => {
    vi.useFakeTimers()
    try {
      const { deps, client } = buildRuntime({ rebuildDelayMs: 50 })
      const serverFactory = vi.fn(deps.serverFactory)
      const runtime = new OpenCodeRuntime({ ...deps, serverFactory })
      await runtime.start()
      client.sessionPrompt.mockRejectedValueOnce(new TypeError('fetch failed'))
      client.sessionAbort.mockRejectedValueOnce(new TypeError('fetch failed'))

      const result = await runtime.runTurn(
        {
          target: { runtime: 'opencode', runtimeSessionId: null, workDir: '/tmp/projA' },
          prompt: 'do',
        },
        new AbortController().signal,
      )

      expect(result.ok).toBe(false)
      if (result.ok) return
      expect(result.error.diagnostics.some((diagnostic) => diagnostic.code === 'opencode-transport-failed')).toBe(true)
      expect(runtime.ready()).toBe(false)

      await vi.advanceTimersByTimeAsync(50)
      expect(serverFactory).toHaveBeenCalledTimes(2)
      expect(runtime.ready()).toBe(true)
    } finally {
      vi.useRealTimers()
    }
  })

  it('Reconciles state from session.status/get/messages on reconnect without V2 replay state', async () => {
    vi.useFakeTimers()
    try {
      const { deps, client, subscription } = buildRuntime({ rebuildDelayMs: 50 })
      const runtime = new OpenCodeRuntime(deps)
      await runtime.start()

      const first = await runtime.runTurn(
        {
          target: { runtime: 'opencode', runtimeSessionId: null, workDir: '/tmp/projA' },
          prompt: 'first',
        },
        new AbortController().signal,
      )
      if (!first.ok) throw new Error('first turn failed')
      const sessionId = first.value.facts.runtimeSessionId
      client.sessionGet.mockImplementation(async () => ({ data: { id: sessionId } }))

      subscription.emit({ type: 'server.disconnected', payload: {} })
      expect(runtime.ready()).toBe(false)
      await vi.advanceTimersByTimeAsync(60)
      expect(runtime.ready()).toBe(true)

      client.sessionStatus.mockClear()
      client.sessionGet.mockClear()
      client.sessionMessages.mockClear()
      client.sessionGet.mockImplementation(async () => ({ data: { id: sessionId } }))

      const reconnect = await runtime.runTurn(
        {
          target: { runtime: 'opencode', runtimeSessionId: sessionId, workDir: '/tmp/projA' },
          prompt: 'second after reconnect',
        },
        new AbortController().signal,
      )
      expect(reconnect.ok).toBe(true)
      if (!reconnect.ok) return
      expect(reconnect.value.facts.runtimeSessionId).toBe(sessionId)
      expect(client.sessionCreate).toHaveBeenCalledTimes(1)
    } finally {
      vi.useRealTimers()
    }
  })
})

describe('OpenCodeRuntime.runTurn — no auto-replay on uncertain prompt admission', () => {
  it('Does not auto-resubmit when the awaited prompt rejects with an unknown error', async () => {
    const { deps, client } = buildRuntime()
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()
    client.sessionPrompt.mockImplementationOnce(async () => {
      throw new Error('connection reset before result')
    })
    const result = await runtime.runTurn(
      {
        target: { runtime: 'opencode', runtimeSessionId: null, workDir: '/tmp/projA' },
        prompt: 'do',
      },
      new AbortController().signal,
    )
    expect(result.ok).toBe(false)
    if (result.ok) return
    expect(result.error.kind).toBe('turn-failed')
    expect(client.sessionPrompt).toHaveBeenCalledTimes(1)
  })
})
