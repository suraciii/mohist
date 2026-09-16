import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { PiRuntime } from './runtime.js'
import { CredentialMasker } from '../task-log.js'
import { CANCEL_CONFIRMATION_TIMEOUT_MS } from './runtime-clock.js'
import { ControlledPiSession, controlledPiSdk, type PiTestMessage } from '../../../tests/support/pi-turn-session.js'
import { deferred } from '../../../tests/support/deferred.js'

const terminal = (text = 'the final answer'): PiTestMessage => ({
  role: 'assistant',
  content: [{ type: 'text', text }],
  stopReason: 'stop',
})

function run(
  runtime: PiRuntime,
  session: ControlledPiSession,
  controller = new AbortController(),
  durationMs?: number,
) {
  return runtime.runTurn(
    {
      target: { runtime: 'pi', runtimeSessionId: session.sessionFile, workDir: '/workspace' },
      prompt: 'do the work',
      durationMs,
    },
    controller.signal,
  )
}

async function createRuntime(session: ControlledPiSession, masker?: CredentialMasker) {
  const runtime = new PiRuntime({ agentDir: '/agent', sdkFactory: controlledPiSdk(session), masker })
  expect((await runtime.start()).ok).toBe(true)
  return runtime
}

function expectCleanedUp(session: ControlledPiSession) {
  expect(session.listeners.size).toBe(0)
  expect(vi.getTimerCount()).toBe(0)
}

describe('PiRuntime bounded turn completion', () => {
  beforeEach(() => vi.useFakeTimers())
  afterEach(() => vi.restoreAllMocks())

  it('keeps normal prompt completion and removes its deadline, observer, and abort listener', async () => {
    const session = new ControlledPiSession()
    const runtime = await createRuntime(session)
    const controller = new AbortController()
    const removeListener = vi.spyOn(controller.signal, 'removeEventListener')
    const turn = run(runtime, session, controller, 60_000)
    await session.promptEntered.promise

    session.messages.push(terminal())
    session.isStreaming = false
    session.promptCompletion.resolve()

    await expect(turn).resolves.toMatchObject({
      ok: true,
      value: { facts: { finalAssistantText: 'the final answer', runtimeSessionId: session.sessionFile } },
    })
    expect(session.prompt).toHaveBeenCalledExactlyOnceWith('do the work', { expandPromptTemplates: false })
    expect(removeListener).toHaveBeenCalledWith('abort', expect.any(Function))
    expectCleanedUp(session)
    controller.abort()
    await vi.advanceTimersByTimeAsync(60_000)
    expect(session.abort).not.toHaveBeenCalled()
  })

  it.each(['agent_settled', 'message_end', 'agent_end'])(
    'settles once from idle terminal facts observed at %s while prompt remains pending',
    async (eventType) => {
      const session = new ControlledPiSession()
      const runtime = await createRuntime(session)
      const controller = new AbortController()
      const settled = vi.fn()
      const events = vi.fn()
      const turn = runtime.runTurn(
        {
          target: { runtime: 'pi', runtimeSessionId: session.sessionFile, workDir: '/workspace' },
          prompt: 'do the work',
          durationMs: 60_000,
        },
        controller.signal,
        { onEvent: events },
      )
      void turn.then(settled)
      await session.promptEntered.promise
      session.messages.push(terminal())
      session.isStreaming = false
      session.emit({ type: eventType, message: session.messages.at(-1) })
      await vi.advanceTimersByTimeAsync(5_000)

      await expect(turn).resolves.toMatchObject({
        ok: true,
        value: { facts: { finalAssistantText: 'the final answer' } },
      })
      expect(settled).toHaveBeenCalledTimes(1)
      expectCleanedUp(session)
      const eventsAtCompletion = events.mock.calls.length
      session.promptCompletion.reject(new Error('late stream rejection'))
      session.emit({ type: 'agent_settled' })
      controller.abort()
      await vi.advanceTimersByTimeAsync(60_000)
      expect(settled).toHaveBeenCalledTimes(1)
      expect(events).toHaveBeenCalledTimes(eventsAtCompletion)
      expect(session.prompt).toHaveBeenCalledTimes(1)
      expect(session.abort).not.toHaveBeenCalled()
      expectCleanedUp(session)
    },
  )

  it.each([
    ['error', 'turn-failed'],
    ['aborted', 'interrupted'],
  ] as const)('fails an idle %s terminal message with its safe underlying reason', async (stopReason, kind) => {
    const session = new ControlledPiSession()
    const masker = new CredentialMasker()
    masker.registerSecret('provider-secret')
    const runtime = await createRuntime(session, masker)
    const turn = run(runtime, session)
    await session.promptEntered.promise
    session.messages.push({ role: 'assistant', content: [], stopReason, errorMessage: 'provider-secret stream ended' })
    session.isStreaming = false
    session.emit({ type: 'agent_settled' })
    await vi.advanceTimersByTimeAsync(5_000)

    const result = await turn
    expect(result).toMatchObject({ ok: false, error: { kind } })
    expect(JSON.stringify(result)).toContain('stream ended')
    expect(JSON.stringify(result)).not.toContain('provider-secret')
    expect(session.prompt).toHaveBeenCalledTimes(1)
    expectCleanedUp(session)
  })

  it.each(['stop', 'length', 'error', 'aborted', 'toolUse'])(
    'does not use a %s assistant message or agent_end to settle a still-streaming session',
    async (stopReason) => {
      const session = new ControlledPiSession()
      const runtime = await createRuntime(session)
      const controller = new AbortController()
      const settled = vi.fn()
      const turn = run(runtime, session, controller)
      void turn.then(settled)
      await session.promptEntered.promise
      session.messages.push({ ...terminal(), stopReason })
      session.emit({ type: 'agent_end', messages: session.messages })
      session.emit({ type: 'agent_settled' })
      await vi.advanceTimersByTimeAsync(10_000)

      expect(settled).not.toHaveBeenCalled()
      controller.abort()
      await vi.advanceTimersByTimeAsync(CANCEL_CONFIRMATION_TIMEOUT_MS)
      await expect(turn).resolves.toMatchObject({ ok: false, error: { kind: 'interrupted' } })
      expect(session.prompt).toHaveBeenCalledTimes(1)
      expectCleanedUp(session)
    },
  )

  it('does not settle an idle session from a tool-use assistant message', async () => {
    const session = new ControlledPiSession()
    const runtime = await createRuntime(session)
    const settled = vi.fn()
    const turn = run(runtime, session)
    void turn.then(settled)
    await session.promptEntered.promise
    session.messages.push({ ...terminal(), stopReason: 'toolUse' })
    session.isStreaming = false
    session.emit({ type: 'agent_settled' })
    await vi.advanceTimersByTimeAsync(5_000)
    expect(settled).not.toHaveBeenCalled()

    session.messages.push(terminal('after tools'))
    session.emit({ type: 'agent_settled' })
    await expect(turn).resolves.toMatchObject({ ok: true, value: { facts: { finalAssistantText: 'after tools' } } })
    expectCleanedUp(session)
  })

  it('preserves normal success semantics for an idle length-limited terminal message', async () => {
    const session = new ControlledPiSession()
    const runtime = await createRuntime(session)
    const turn = run(runtime, session)
    await session.promptEntered.promise
    session.messages.push({ ...terminal('length-limited answer'), stopReason: 'length' })
    session.isStreaming = false
    session.emit({ type: 'agent_settled' })

    await expect(turn).resolves.toMatchObject({
      ok: true,
      value: { facts: { finalAssistantText: 'length-limited answer' } },
    })
    expectCleanedUp(session)
  })

  it('observes compaction_end after the initial check expires and the SDK subsequently becomes idle', async () => {
    const session = new ControlledPiSession()
    const runtime = await createRuntime(session)
    const settled = vi.fn()
    const turn = run(runtime, session)
    void turn.then(settled)
    await session.promptEntered.promise
    await vi.advanceTimersByTimeAsync(10_000)
    expect(settled).not.toHaveBeenCalled()
    session.messages = [terminal('compacted answer')]
    session.emit({ type: 'compaction_end' })
    session.isStreaming = false
    await vi.advanceTimersByTimeAsync(5_000)

    await expect(turn).resolves.toMatchObject({
      ok: true,
      value: { facts: { finalAssistantText: 'compacted answer' } },
    })
    expectCleanedUp(session)
  })

  it('gives the latest lifecycle boundary its full idle-observation delay', async () => {
    const session = new ControlledPiSession()
    const runtime = await createRuntime(session)
    const settled = vi.fn()
    const turn = run(runtime, session)
    void turn.then(settled)
    await session.promptEntered.promise
    session.messages.push(terminal())
    session.emit({ type: 'message_end', message: session.messages.at(-1) })
    await vi.advanceTimersByTimeAsync(4_999)
    session.emit({ type: 'agent_end' })
    session.isStreaming = false
    await vi.advanceTimersByTimeAsync(1)
    expect(settled).not.toHaveBeenCalled()
    await vi.advanceTimersByTimeAsync(4_999)

    await expect(turn).resolves.toMatchObject({ ok: true })
    expectCleanedUp(session)
  })

  it('excludes a previous turn terminal and still observes a shorter compacted message array', async () => {
    const session = new ControlledPiSession()
    session.messages = [{ role: 'user', content: 'old prompt' }, terminal('old answer')]
    session.prompt.mockImplementation(() => {
      session.promptEntered.resolve()
      return session.promptCompletion.promise
    })
    const runtime = await createRuntime(session)
    const settled = vi.fn()
    const turn = run(runtime, session)
    void turn.then(settled)
    await session.promptEntered.promise
    session.emit({ type: 'agent_settled' })
    await vi.advanceTimersByTimeAsync(5_000)
    expect(settled).not.toHaveBeenCalled()

    session.messages = [terminal('new answer after compaction')]
    session.emit({ type: 'agent_settled' })
    await expect(turn).resolves.toMatchObject({
      ok: true,
      value: { facts: { finalAssistantText: 'new answer after compaction' } },
    })
    expectCleanedUp(session)
  })

  it('starts a queued prompt after idle settlement while the previous SDK prompt remains pending', async () => {
    const session = new ControlledPiSession()
    const secondEntered = deferred()
    const secondCompletion = deferred()
    session.prompt
      .mockImplementationOnce(() => {
        session.isStreaming = true
        session.promptEntered.resolve()
        return session.promptCompletion.promise
      })
      .mockImplementationOnce(() => {
        secondEntered.resolve()
        return secondCompletion.promise
      })
    const runtime = await createRuntime(session)
    const first = run(runtime, session)
    await session.promptEntered.promise
    const firstPromptSettled = vi.fn()
    void session.promptCompletion.promise.then(firstPromptSettled, firstPromptSettled)
    const second = run(runtime, session)
    const secondSettled = vi.fn()
    void second.then(secondSettled)
    session.messages.push(terminal('first answer'))
    session.isStreaming = false
    session.emit({ type: 'agent_settled' })
    await expect(first).resolves.toMatchObject({ ok: true, value: { facts: { finalAssistantText: 'first answer' } } })
    await secondEntered.promise
    expect(firstPromptSettled).not.toHaveBeenCalled()
    expect(session.prompt).toHaveBeenCalledTimes(2)
    session.emit({ type: 'agent_settled' })
    await vi.advanceTimersByTimeAsync(5_000)
    expect(secondSettled).not.toHaveBeenCalled()
    session.promptCompletion.reject(new Error('previous prompt disconnected late'))
    await vi.advanceTimersByTimeAsync(0)
    expect(secondSettled).not.toHaveBeenCalled()

    session.messages.push(terminal('second answer'))
    session.emit({ type: 'agent_settled' })
    await expect(second).resolves.toMatchObject({ ok: true, value: { facts: { finalAssistantText: 'second answer' } } })
    expect(session.prompt).toHaveBeenCalledTimes(2)
    expectCleanedUp(session)
  })

  it('cancels a queued turn without submitting it or aborting the active owner', async () => {
    const session = new ControlledPiSession()
    const runtime = await createRuntime(session)
    const first = run(runtime, session)
    await session.promptEntered.promise
    const controller = new AbortController()
    const queued = run(runtime, session, controller)
    controller.abort()

    await expect(queued).resolves.toMatchObject({ ok: false, error: { kind: 'interrupted' } })
    expect(session.prompt).toHaveBeenCalledTimes(1)
    expect(session.abort).not.toHaveBeenCalled()
    session.messages.push(terminal('active owner answer'))
    session.isStreaming = false
    session.promptCompletion.resolve()
    await expect(first).resolves.toMatchObject({ ok: true })
    await vi.advanceTimersByTimeAsync(0)
    expect(session.prompt).toHaveBeenCalledTimes(1)
    expect(session.abort).not.toHaveBeenCalled()
    expectCleanedUp(session)
  })

  it('bounds a deadline failure when both prompt and abort remain pending, with stable diagnostics', async () => {
    const session = new ControlledPiSession()
    const abortCompletion = deferred()
    session.abort.mockImplementation(() => abortCompletion.promise)
    const runtime = await createRuntime(session)
    const settled = vi.fn()
    const turn = run(runtime, session, new AbortController(), 25)
    void turn.then(settled)
    await session.promptEntered.promise
    await vi.advanceTimersByTimeAsync(25)
    expect(session.abort).toHaveBeenCalledTimes(1)
    await vi.advanceTimersByTimeAsync(CANCEL_CONFIRMATION_TIMEOUT_MS - 1)
    expect(settled).not.toHaveBeenCalled()
    await vi.advanceTimersByTimeAsync(1)

    const result = await turn
    expect(result).toMatchObject({
      ok: false,
      error: { kind: 'deadline-exceeded' },
      diagnostics: expect.arrayContaining([expect.objectContaining({ code: 'abort-unconfirmed' })]),
    })
    expectCleanedUp(session)
    const snapshot = JSON.stringify(result)
    abortCompletion.reject(new Error('late abort failure'))
    session.promptCompletion.resolve()
    session.emit({ type: 'agent_settled' })
    await vi.advanceTimersByTimeAsync(60_000)
    expect(JSON.stringify(result)).toBe(snapshot)
    expect(settled).toHaveBeenCalledTimes(1)
    expect(session.prompt).toHaveBeenCalledTimes(1)
    expectCleanedUp(session)
  })

  it.each(['resolved', 'rejected', 'pending'] as const)(
    'preserves a redacted stream failure once when abort is %s',
    async (abortOutcome) => {
      const session = new ControlledPiSession()
      const abortCompletion = deferred()
      if (abortOutcome === 'rejected') session.abort.mockRejectedValue(new Error('provider-secret abort rejected'))
      if (abortOutcome === 'pending') session.abort.mockImplementation(() => abortCompletion.promise)
      const masker = new CredentialMasker()
      masker.registerSecret('provider-secret')
      const runtime = await createRuntime(session, masker)
      const controller = new AbortController()
      const settled = vi.fn()
      const turn = run(runtime, session, controller, 60_000)
      void turn.then(settled)
      await session.promptEntered.promise
      const streamError = Object.assign(new Error('provider-secret SSE disconnected'), {
        code: 'ECONNRESET',
        statusCode: 502,
        headers: { authorization: 'top-header-only-secret' },
        metadata: { private: 'top-metadata-only-secret' },
        cause: Object.assign(new Error('provider-secret socket closed'), {
          code: 'EPIPE',
          status: 503,
          headers: { authorization: 'nested-header-only-secret' },
          metadata: { private: 'nested-metadata-only-secret' },
        }),
      })
      session.promptCompletion.reject(streamError)
      await vi.advanceTimersByTimeAsync(CANCEL_CONFIRMATION_TIMEOUT_MS)

      const result = await turn
      expect(result).toMatchObject({ ok: false, error: { kind: 'turn-failed' } })
      expect(result.diagnostics.filter((item) => item.code === 'turn-failed')).toHaveLength(1)
      expect(result.diagnostics.find((item) => item.code === 'turn-failed')).toEqual({
        code: 'turn-failed',
        severity: 'error',
        message: '*** SSE disconnected',
        details: {
          phase: 'prompt',
          name: 'Error',
          message: '*** SSE disconnected',
          code: 'ECONNRESET',
          statusCode: 502,
          cause: { name: 'Error', message: '*** socket closed', code: 'EPIPE', status: 503 },
        },
      })
      expect(result.diagnostics.filter((item) => item.code === 'abort-unconfirmed')).toHaveLength(
        abortOutcome === 'resolved' ? 0 : 1,
      )
      expect(JSON.stringify(result)).not.toContain('provider-secret')
      expect(JSON.stringify(result)).not.toContain('header-only-secret')
      expect(JSON.stringify(result)).not.toContain('metadata-only-secret')
      expect(session.abort).toHaveBeenCalledTimes(1)
      expectCleanedUp(session)
      const snapshot = JSON.stringify(result)
      abortCompletion.reject(new Error('late abort error'))
      if (abortOutcome !== 'pending') void abortCompletion.promise.catch(() => {})
      controller.abort()
      session.emit({ type: 'agent_settled' })
      await vi.advanceTimersByTimeAsync(60_000)
      expect(JSON.stringify(result)).toBe(snapshot)
      expect(settled).toHaveBeenCalledTimes(1)
      expect(session.prompt).toHaveBeenCalledTimes(1)
      expectCleanedUp(session)
    },
  )

  it('retains interruption when abort rejects and does not expose the credential', async () => {
    const session = new ControlledPiSession()
    session.abort.mockRejectedValue(new Error('provider-secret abort rejected'))
    const masker = new CredentialMasker()
    masker.registerSecret('provider-secret')
    const runtime = await createRuntime(session, masker)
    const controller = new AbortController()
    const turn = run(runtime, session, controller)
    await session.promptEntered.promise
    controller.abort()

    const result = await turn
    expect(result).toMatchObject({
      ok: false,
      error: { kind: 'interrupted' },
      diagnostics: expect.arrayContaining([
        expect.objectContaining({ code: 'abort-unconfirmed', message: expect.stringContaining('abort rejected') }),
      ]),
    })
    expect(JSON.stringify(result)).not.toContain('provider-secret')
    expectCleanedUp(session)
  })

  it.each(['throws', 'rejects'] as const)(
    'preserves convergence when the event observer %s during final reconciliation',
    async (failure) => {
      const session = new ControlledPiSession()
      const masker = new CredentialMasker()
      masker.registerSecret('observer-secret')
      const runtime = await createRuntime(session, masker)
      const observerFailure = new Error('observer-secret event delivery failed')
      const onEvent = vi.fn(() => {
        if (failure === 'throws') throw observerFailure
        return Promise.reject(observerFailure)
      })
      const turn = runtime.runTurn(
        {
          target: { runtime: 'pi', runtimeSessionId: session.sessionFile, workDir: '/workspace' },
          prompt: 'do the work',
        },
        new AbortController().signal,
        { onEvent },
      )
      await session.promptEntered.promise
      session.messages.push(terminal())
      session.isStreaming = false
      session.promptCompletion.resolve()
      await vi.advanceTimersByTimeAsync(CANCEL_CONFIRMATION_TIMEOUT_MS)

      const result = await turn
      if (failure === 'throws') {
        expect(result).toMatchObject({ ok: false, error: { kind: 'turn-failed' } })
        expect(JSON.stringify(result)).toContain('event delivery failed')
      } else {
        expect(result).toMatchObject({ ok: true, value: { facts: { finalAssistantText: 'the final answer' } } })
      }
      expect(JSON.stringify(result)).not.toContain('observer-secret')
      expect(session.abort).toHaveBeenCalledTimes(failure === 'throws' ? 1 : 0)
      expectCleanedUp(session)
    },
  )

  it('does not submit or abort a prompt whose signal was already cancelled', async () => {
    const session = new ControlledPiSession()
    const runtime = await createRuntime(session)
    const controller = new AbortController()
    controller.abort()

    await expect(run(runtime, session, controller)).resolves.toMatchObject({
      ok: false,
      error: { kind: 'interrupted' },
    })
    expect(session.prompt).not.toHaveBeenCalled()
    expect(session.abort).not.toHaveBeenCalled()
    expectCleanedUp(session)
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
