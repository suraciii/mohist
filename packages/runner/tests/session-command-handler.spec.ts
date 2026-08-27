import { describe, expect, it, vi } from 'vitest'
import {
  createSessionCommandHandler,
  type SessionCommandError,
  type SessionCommandHandler,
  type SessionCommandRequest,
  type SessionCommandResult,
} from '../src/server/session-command-handler.js'

function register(handler: SessionCommandHandler | null) {
  return createSessionCommandHandler({ handler })
}

function request(command: 'compact' | 'reset', operationId = `${command}-1`): SessionCommandRequest {
  return {
    sessionId: 'session-1',
    runtime: 'opencode',
    runtimeSessionId: 'runtime-1',
    runnerId: 'runner-1',
    workDir: '/work/project',
    command,
    operationId,
    processGeneration: 'generation-1',
    ...(command === 'reset' ? { expectedRuntimeSessionId: 'runtime-1' } : {}),
  }
}

describe('SessionCommand contract', () => {
  it('compact returns no new runtime id', async () => {
    const handler = vi.fn(async (): Promise<SessionCommandResult> => ({ ok: true }))

    await expect(register(handler)(request('compact'))).resolves.toEqual({ ok: true })
    expect(handler).toHaveBeenCalledOnce()
  })

  it('reset returns the replacement runtime session id', async () => {
    await expect(
      register(async () => ({ ok: true, runtimeSessionId: 'runtime-2' }))(request('reset')),
    ).resolves.toEqual({
      ok: true,
      runtimeSessionId: 'runtime-2',
    })
  })

  it.each(['compact', 'reset'] as const)('deduplicates concurrent %s delivery in one process', async (command) => {
    let release!: () => void
    const deferred = new Promise<void>((resolve) => {
      release = resolve
    })
    const handler = vi.fn(async (): Promise<SessionCommandResult> => {
      await deferred
      return command === 'compact' ? { ok: true } : { ok: true, runtimeSessionId: 'runtime-2' }
    })
    const invoke = register(handler)
    const commandRequest = request(command)

    const first = invoke(commandRequest)
    const duplicate = invoke(commandRequest)
    release()

    await expect(Promise.all([first, duplicate])).resolves.toEqual(
      command === 'compact'
        ? [{ ok: true }, { ok: true }]
        : [
            { ok: true, runtimeSessionId: 'runtime-2' },
            { ok: true, runtimeSessionId: 'runtime-2' },
          ],
    )
    expect(handler).toHaveBeenCalledOnce()
  })

  it('does not retain operation admission when the handler is recreated', async () => {
    const firstHandler = vi.fn(async (): Promise<SessionCommandResult> => ({ ok: true }))
    const recreatedHandler = vi.fn(async (): Promise<SessionCommandResult> => ({ ok: true }))
    const commandRequest = request('compact', 'same-id-after-restart')

    await expect(register(firstHandler)(commandRequest)).resolves.toEqual({ ok: true })
    await expect(register(recreatedHandler)(commandRequest)).resolves.toEqual({ ok: true })

    expect(firstHandler).toHaveBeenCalledOnce()
    expect(recreatedHandler).toHaveBeenCalledOnce()
  })

  it('rejects a mismatched command that reuses an in-flight operation id', async () => {
    let release!: () => void
    const deferred = new Promise<void>((resolve) => {
      release = resolve
    })
    const handler = vi.fn(async (): Promise<SessionCommandResult> => {
      await deferred
      return { ok: true }
    })
    const invoke = register(handler)
    const compact = request('compact', 'operation-1')
    const reset = {
      ...request('reset', 'operation-1'),
      runtimeSessionId: 'runtime-2',
      expectedRuntimeSessionId: 'runtime-2',
    }

    const inFlightCompact = invoke(compact)
    await expect(invoke(reset)).resolves.toEqual({ ok: false, error: 'unavailable' })
    release()

    await expect(inFlightCompact).resolves.toEqual({ ok: true })
    expect(handler).toHaveBeenCalledOnce()
  })

  it('rejects a reset without the expected binding', async () => {
    const handler = vi.fn(async (): Promise<SessionCommandResult> => ({ ok: true }))
    const invalid = { ...request('reset'), expectedRuntimeSessionId: 'runtime-stale' }

    await expect(register(handler)(invalid)).resolves.toEqual({ ok: false, error: 'unavailable' })
    expect(handler).not.toHaveBeenCalled()
  })

  it.each<SessionCommandError>(['conflict', 'missing', 'notStarted', 'unavailable'])(
    'preserves the %s error vocabulary',
    async (error) => {
      await expect(register(async () => ({ ok: false, error }))(request('compact'))).resolves.toEqual({
        ok: false,
        error,
      })
    },
  )

  it('reports unavailable when no runtime handler is installed', async () => {
    await expect(register(null)(request('compact'))).resolves.toEqual({ ok: false, error: 'unavailable' })
  })
})
