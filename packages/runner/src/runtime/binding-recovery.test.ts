import { describe, expect, it, vi } from 'vitest'
import {
  BindingRecoveryCoordinator,
  resolveOrRecoverBinding,
  type RecoverableRuntime,
  type RuntimeBinding,
} from './binding-recovery.js'
import type { CodexRuntime } from './codex/index.js'

type CreateSession = CodexRuntime['createSession']

const EXPECTED: RuntimeBinding = {
  runnerId: 'runner-1',
  runtime: 'codex',
  runtimeSessionId: 'thread-1',
  workDir: '/work',
}

function codexRecoverable(createSession?: CreateSession): {
  readonly runtime: RecoverableRuntime
  readonly createSession: ReturnType<typeof vi.fn>
} {
  const mock = (createSession ??
    vi.fn(async () => ({
      ok: true as const,
      value: { runtimeSessionId: 'thread-2', workDir: '/work' },
      diagnostics: [],
    }))) as unknown as ReturnType<typeof vi.fn>
  return {
    runtime: { kind: 'codex', runtime: { createSession: mock } as unknown as CodexRuntime },
    createSession: mock,
  }
}

describe('Codex binding recovery evidence', () => {
  it('reuses the bound Thread when the probe resolves it', async () => {
    const probe = vi.fn(async () => ({ ok: true as const, activeTurn: false }))
    const replace = vi.fn(async () => undefined)
    const { runtime, createSession } = codexRecoverable()

    const result = await resolveOrRecoverBinding({
      runnerId: 'runner-1',
      expected: EXPECTED,
      runtime,
      probe,
      replace,
    })

    expect(result).toEqual({ ok: true, binding: EXPECTED, recovered: false })
    expect(probe).toHaveBeenCalledOnce()
    expect(createSession).not.toHaveBeenCalled()
    expect(replace).not.toHaveBeenCalled()
  })

  it('creates exactly one replacement Thread after structured thread_not_found', async () => {
    const probe = vi.fn(async () => ({ ok: false as const, kind: 'missing-session', message: 'thread_not_found' }))
    const replace = vi.fn(async () => undefined)
    const { runtime, createSession } = codexRecoverable()

    const result = await resolveOrRecoverBinding({
      runnerId: 'runner-1',
      expected: EXPECTED,
      runtime,
      probe,
      replace,
    })

    expect(result).toMatchObject({
      ok: true,
      recovered: true,
      binding: { ...EXPECTED, runtimeSessionId: 'thread-2' },
    })
    expect(createSession).toHaveBeenCalledOnce()
    expect(replace).toHaveBeenCalledOnce()
    expect(replace).toHaveBeenCalledWith(EXPECTED, { ...EXPECTED, runtimeSessionId: 'thread-2' })
  })

  it.each([
    ['transport failure', 'unknown'],
    ['authentication failure', 'unavailable-runtime'],
    ['protocol mismatch', 'incompatible-runtime'],
  ])('keeps a %s as an unknown result and never replaces the binding', async (_case, kind) => {
    const probe = vi.fn(async () => ({ ok: false as const, kind, message: `${kind} from the bound Runner` }))
    const replace = vi.fn(async () => undefined)
    const { runtime, createSession } = codexRecoverable()

    const result = await resolveOrRecoverBinding({
      runnerId: 'runner-1',
      expected: EXPECTED,
      runtime,
      probe,
      replace,
    })

    expect(result).toMatchObject({ ok: false, kind })
    expect(createSession).not.toHaveBeenCalled()
    expect(replace).not.toHaveBeenCalled()
  })

  it('refuses recovery on a different Runner without probing', async () => {
    const probe = vi.fn(async () => ({ ok: false as const, kind: 'missing-session', message: 'gone' }))
    const replace = vi.fn(async () => undefined)
    const { runtime, createSession } = codexRecoverable()

    const result = await resolveOrRecoverBinding({
      runnerId: 'runner-2',
      expected: EXPECTED,
      runtime,
      probe,
      replace,
    })

    expect(result).toMatchObject({ ok: false, kind: 'different-runner' })
    expect(probe).not.toHaveBeenCalled()
    expect(createSession).not.toHaveBeenCalled()
    expect(replace).not.toHaveBeenCalled()
  })

  it('surfaces a failed replacement Thread creation without replacing the binding', async () => {
    const probe = vi.fn(async () => ({ ok: false as const, kind: 'missing-session', message: 'gone' }))
    const replace = vi.fn(async () => undefined)
    const { runtime } = codexRecoverable(
      vi.fn(async () => ({
        ok: false as const,
        error: { kind: 'unknown' as const, message: 'thread/start lost', diagnostics: [] },
        diagnostics: [],
      })) as unknown as CreateSession,
    )

    const result = await resolveOrRecoverBinding({
      runnerId: 'runner-1',
      expected: EXPECTED,
      runtime,
      probe,
      replace,
    })

    expect(result).toMatchObject({ ok: false, kind: 'unknown' })
    expect(replace).not.toHaveBeenCalled()
  })

  it('reports a candidate-unbound failure when the binding CAS rejects the replacement', async () => {
    const probe = vi.fn(async () => ({ ok: false as const, kind: 'missing-session', message: 'gone' }))
    const replace = vi.fn(async () => {
      throw new Error('binding revision changed')
    })
    const { runtime } = codexRecoverable()

    const result = await resolveOrRecoverBinding({
      runnerId: 'runner-1',
      expected: EXPECTED,
      runtime,
      probe,
      replace,
    })

    expect(result).toMatchObject({
      ok: false,
      kind: 'candidate-unbound',
      candidateRuntimeSessionId: 'thread-2',
    })
  })

  it('coalesces concurrent recovery attempts for the same key through the coordinator', async () => {
    const coordinator = new BindingRecoveryCoordinator()
    const probe = vi.fn(async () => ({ ok: false as const, kind: 'missing-session', message: 'gone' }))
    const replace = vi.fn(async () => undefined)
    const { runtime, createSession } = codexRecoverable()
    const request = {
      runnerId: 'runner-1',
      expected: EXPECTED,
      runtime,
      probe,
      replace,
      coordinator,
      recoveryKey: 'session-1:thread-1',
    }

    const [first, second] = await Promise.all([resolveOrRecoverBinding(request), resolveOrRecoverBinding(request)])

    expect(first).toEqual(second)
    expect(probe).toHaveBeenCalledOnce()
    expect(createSession).toHaveBeenCalledOnce()
    expect(replace).toHaveBeenCalledOnce()
  })
})
