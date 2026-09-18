import { describe, expect, it, vi } from 'vitest'
import {
  callCancel,
  callFollowup,
  createSessionCommandRouter,
  readCancelFacts,
  resolveCommandRuntime,
  type CommandRuntimeAccessors,
} from './command-runtime.js'
import { isValidSessionCommandResult, type SessionCommandRequest } from './session-command-handler.js'
import { makeFakeCodexRuntime } from '../../tests/support/codex-runtime-fixture.js'

const BINDING = { runtime: 'codex' as const }

function codexAccessors(runtime: ReturnType<typeof makeFakeCodexRuntime>['runtime']): CommandRuntimeAccessors {
  return { codex: runtime }
}

function commandRequest(overrides: Partial<SessionCommandRequest> = {}): SessionCommandRequest {
  return {
    sessionId: 'session-1',
    runtime: 'codex',
    runtimeSessionId: 'thread_fixture',
    runnerId: 'runner-1',
    workDir: '/workspace',
    command: 'compact',
    operationId: 'op-1',
    projectId: 'project-1',
    processGeneration: 'generation-1',
    ...overrides,
  }
}

function healthyOutbox() {
  return {
    ready: () => true,
    enqueueProducedFact: vi.fn(async () => undefined),
  }
}

describe('resolveCommandRuntime codex discriminator', () => {
  it('resolves a codex binding to the kind:codex handle', () => {
    const fixture = makeFakeCodexRuntime()
    const handle = resolveCommandRuntime(BINDING, codexAccessors(fixture.runtime))
    expect(handle).toMatchObject({ kind: 'codex' })
  })

  it('returns null when the codex accessor is not supplied', () => {
    expect(resolveCommandRuntime(BINDING, {})).toBeNull()
  })

  it('resolves a late-bound codex accessor through its getter', () => {
    const fixture = makeFakeCodexRuntime()
    const handle = resolveCommandRuntime(BINDING, { codex: () => fixture.runtime })
    expect(handle).toMatchObject({ kind: 'codex' })
  })
})

describe('codex follow-up and cancel call surfaces', () => {
  it('forwards the caller inputId as the Codex clientUserMessageId', async () => {
    const fixture = makeFakeCodexRuntime()
    const handle = resolveCommandRuntime(BINDING, codexAccessors(fixture.runtime))
    if (!handle) throw new Error('expected a codex handle')

    const result = await callFollowup(
      handle,
      {
        target: { runtime: 'codex', runtimeSessionId: 'thread_fixture', workDir: '/workspace' },
        prompt: 'continue',
        inputId: 'input-9',
      },
      null,
    )

    expect(result).toMatchObject({ ok: true })
    expect(fixture.followupCalls).toHaveLength(1)
    expect(fixture.followupCalls[0]).toMatchObject({
      clientUserMessageId: 'input-9',
      prompt: 'continue',
      target: { runtime: 'codex', runtimeSessionId: 'thread_fixture', workDir: '/workspace' },
    })
  })

  it('reads the cancelled and stopConfirmed facts from a codex cancel result', async () => {
    const fixture = makeFakeCodexRuntime()
    const handle = resolveCommandRuntime(BINDING, codexAccessors(fixture.runtime))
    if (!handle) throw new Error('expected a codex handle')

    const result = await callCancel(handle, {
      runtime: 'codex',
      runtimeSessionId: 'thread_fixture',
      workDir: '/workspace',
    })

    expect(readCancelFacts(result)).toEqual({ cancelled: true, stopConfirmed: true })
    fixture.setCancelResult({
      ok: true,
      value: {
        facts: { runtimeSessionId: 'thread_fixture', workDir: '/workspace', cancelled: true, stopConfirmed: false },
        diagnostics: [],
      },
      diagnostics: [],
    })
    const unconfirmed = await callCancel(handle, {
      runtime: 'codex',
      runtimeSessionId: 'thread_fixture',
      workDir: '/workspace',
    })
    expect(readCancelFacts(unconfirmed)).toEqual({ cancelled: true, stopConfirmed: false })
  })
})

describe('codex session command routing', () => {
  it('dispatches compact to thread/compact/start through the codex runtime', async () => {
    const fixture = makeFakeCodexRuntime()
    const router = createSessionCommandRouter(codexAccessors(fixture.runtime), healthyOutbox() as never)

    await expect(router(commandRequest())).resolves.toEqual({ ok: true })
    expect(fixture.compactCalls).toHaveLength(1)
    expect(fixture.compactCalls[0]).toMatchObject({
      target: { runtime: 'codex', runtimeSessionId: 'thread_fixture', workDir: '/workspace' },
    })
  })

  it('maps an idle-gate compact failure to unavailable', async () => {
    const fixture = makeFakeCodexRuntime()
    fixture.setCompactResult({
      ok: false,
      error: { kind: 'turn-failed', message: 'compact is only available while idle', diagnostics: [] },
      diagnostics: [],
    })
    const router = createSessionCommandRouter(codexAccessors(fixture.runtime), healthyOutbox() as never)

    await expect(router(commandRequest())).resolves.toEqual({ ok: false, error: 'unavailable' })
  })

  it('dispatches reset and returns the replacement Thread id for the binding CAS', async () => {
    const fixture = makeFakeCodexRuntime()
    const router = createSessionCommandRouter(codexAccessors(fixture.runtime), healthyOutbox() as never)

    await expect(
      router(commandRequest({ command: 'reset', expectedRuntimeSessionId: 'thread_fixture', operationId: 'op-2' })),
    ).resolves.toEqual({ ok: true, runtimeSessionId: 'thread_fixture_reset' })
    expect(fixture.resetCalls).toHaveLength(1)
  })

  it('rejects a codex command when the runtime accessor is missing or not ready', async () => {
    const missing = createSessionCommandRouter({}, healthyOutbox() as never)
    await expect(missing(commandRequest())).resolves.toEqual({ ok: false, error: 'runtime-unavailable' })

    const fixture = makeFakeCodexRuntime()
    fixture.setReady(false)
    const notReady = createSessionCommandRouter(codexAccessors(fixture.runtime), healthyOutbox() as never)
    await expect(notReady(commandRequest())).resolves.toEqual({ ok: false, error: 'unavailable' })
  })

  it('requires an event observer for codex compact', async () => {
    const fixture = makeFakeCodexRuntime()
    const router = createSessionCommandRouter(codexAccessors(fixture.runtime), {
      ready: () => false,
    } as never)

    await expect(router(commandRequest())).resolves.toEqual({ ok: false, error: 'unavailable' })
    expect(fixture.compactCalls).toHaveLength(0)
  })
})

describe('session command result validation', () => {
  it('accepts a reset result only when the returned Thread id changes', () => {
    const reset = commandRequest({ command: 'reset', expectedRuntimeSessionId: 'thread_fixture', operationId: 'op-2' })
    expect(isValidSessionCommandResult(reset, { ok: true, runtimeSessionId: 'thread_fixture_reset' })).toBe(true)
    expect(isValidSessionCommandResult(reset, { ok: true, runtimeSessionId: 'thread_fixture' })).toBe(false)
  })

  it('accepts a compact result only when no replacement Thread id is returned', () => {
    const compact = commandRequest()
    expect(isValidSessionCommandResult(compact, { ok: true })).toBe(true)
    expect(isValidSessionCommandResult(compact, { ok: true, runtimeSessionId: 'thread_other' })).toBe(false)
  })
})
