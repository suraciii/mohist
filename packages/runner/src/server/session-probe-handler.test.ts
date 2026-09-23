import { describe, expect, it } from 'vitest'
import type { AgentRuntime } from '../core/types.js'
import type { CodexRuntime } from '../runtime/codex/index.js'
import type { OpenCodeRuntime } from '../runtime/opencode/index.js'
import type { PiRuntime } from '../runtime/pi/index.js'
import {
  createSessionProbeHandler,
  isRunnerSessionActivityProbeRequest,
  type RunnerSessionActivityProbeRequest,
  type SessionProbeHandlerDeps,
} from './session-probe-handler.js'

type ProbeResolveResult =
  | { readonly ok: true; readonly value: ProbeSession }
  | { readonly ok: false; readonly error: { readonly kind: string; readonly message: string } }

interface ProbeSession {
  readonly runtimeSessionId: string
  readonly workDir: string
  readonly activeTurn: boolean
}

interface FakeRuntime {
  readonly accessor: () => OpenCodeRuntime | PiRuntime | CodexRuntime
  readonly resolveCalls: Array<Record<string, unknown>>
  readonly mutatingCalls: string[]
  setReady(ready: boolean): void
}

/**
 * The probe is a read, so each fake records the one read it must make
 * (`resolveSession`) and every mutating surface a probe must never touch.
 */
function fakeRuntime(resolve: () => ProbeResolveResult): FakeRuntime {
  const resolveCalls: Array<Record<string, unknown>> = []
  const mutatingCalls: string[] = []
  let ready = true
  const mutating = (name: string) => async () => {
    mutatingCalls.push(name)
    return { ok: false, error: { kind: 'unavailable-runtime', message: 'a probe must not mutate' } }
  }
  const surface = {
    ready: () => ready,
    diagnostic: () => null,
    resolveSession: async (request: { target: Record<string, unknown> }) => {
      resolveCalls.push(request.target)
      return await resolve()
    },
    start: mutating('start'),
    createSession: mutating('createSession'),
    runTurn: mutating('runTurn'),
    followup: mutating('followup'),
    cancel: mutating('cancel'),
    compact: mutating('compact'),
    reset: mutating('reset'),
    shutdown: mutating('shutdown'),
  }
  return {
    accessor: () => surface as unknown as OpenCodeRuntime & PiRuntime & CodexRuntime,
    resolveCalls,
    mutatingCalls,
    setReady: (value: boolean) => {
      ready = value
    },
  }
}

function session(overrides: Partial<ProbeSession> = {}): ProbeSession {
  return { runtimeSessionId: 'runtime_session_1', workDir: '/work/run_101', activeTurn: false, ...overrides }
}

function resolved(overrides: Partial<ProbeSession> = {}): ProbeResolveResult {
  return { ok: true, value: session(overrides) }
}

function rejected(kind: string): ProbeResolveResult {
  return { ok: false, error: { kind, message: `probe rejection (${kind})` } }
}

function probeRequest(overrides: Partial<RunnerSessionActivityProbeRequest> = {}): RunnerSessionActivityProbeRequest {
  return {
    sessionId: 'session_1',
    observationId: 'observation_1',
    runnerId: 'runner_1',
    runtime: 'opencode',
    runtimeSessionId: 'runtime_session_1',
    workDir: '/work/run_101',
    bindingEpoch: 2,
    contextGeneration: 1,
    ...overrides,
  }
}

function probeDeps(overrides: Partial<SessionProbeHandlerDeps> = {}): SessionProbeHandlerDeps {
  return { runnerId: 'runner_1', enabledRuntimes: new Set<AgentRuntime>(['opencode', 'pi', 'codex']), ...overrides }
}

function depsFor(runtime: AgentRuntime, fake: FakeRuntime): SessionProbeHandlerDeps {
  const accessor = fake.accessor as never
  return probeDeps(
    runtime === 'opencode' ? { openCode: accessor } : runtime === 'pi' ? { pi: accessor } : { codex: accessor },
  )
}

describe('session.probe Activity observation', () => {
  it('answers executing and echoes the complete examined target', async () => {
    const fake = fakeRuntime(() => resolved({ activeTurn: true }))
    const handler = createSessionProbeHandler(depsFor('opencode', fake))

    await expect(handler(probeRequest())).resolves.toEqual({
      probe: probeRequest(),
      observation: 'executing',
    })
    expect(fake.resolveCalls).toEqual([
      { runtime: 'opencode', runtimeSessionId: 'runtime_session_1', workDir: '/work/run_101' },
    ])
    expect(fake.mutatingCalls).toEqual([])
  })

  it('answers idle for a resolved Runtime Session with no active turn', async () => {
    const fake = fakeRuntime(() => resolved())
    const handler = createSessionProbeHandler(depsFor('opencode', fake))

    await expect(handler(probeRequest())).resolves.toEqual({ probe: probeRequest(), observation: 'idle' })
    expect(fake.mutatingCalls).toEqual([])
  })

  it.each([
    {
      runtime: 'opencode',
      target: { runtime: 'opencode', runtimeSessionId: 'runtime_session_1', workDir: '/work/run_101' },
    },
    {
      runtime: 'pi',
      target: { runtime: 'pi', runtimeSessionId: '/sessions/one.jsonl', workDir: '/work/run_101' },
    },
    { runtime: 'codex', target: { runtimeSessionId: 'thread_1', workDir: '/work/run_101' } },
  ] as const)('reads the bound $runtime Session through its own resolve path', async ({ runtime, target }) => {
    const fake = fakeRuntime(() => resolved({ runtimeSessionId: target.runtimeSessionId }))
    const handler = createSessionProbeHandler(depsFor(runtime, fake))

    await expect(handler(probeRequest({ runtime, runtimeSessionId: target.runtimeSessionId }))).resolves.toEqual({
      probe: probeRequest({ runtime, runtimeSessionId: target.runtimeSessionId }),
      observation: 'idle',
    })
    expect(fake.resolveCalls).toEqual([target])
    expect(fake.mutatingCalls).toEqual([])
  })

  it('answers unknown-to-runner when the provider confirms the bound Session is gone', async () => {
    const fake = fakeRuntime(() => rejected('missing-session'))
    const handler = createSessionProbeHandler(depsFor('opencode', fake))

    await expect(handler(probeRequest())).resolves.toEqual({
      probe: probeRequest(),
      observation: 'unknown-to-runner',
    })
    expect(fake.mutatingCalls).toEqual([])
  })

  it('answers unknown-to-runner for a Runtime this Runner does not enable', async () => {
    const fake = fakeRuntime(() => resolved({ activeTurn: true }))
    const handler = createSessionProbeHandler(
      probeDeps({ codex: fake.accessor as never, enabledRuntimes: new Set<AgentRuntime>(['opencode']) }),
    )

    await expect(handler(probeRequest({ runtime: 'codex', runtimeSessionId: 'thread_1' }))).resolves.toEqual({
      probe: probeRequest({ runtime: 'codex', runtimeSessionId: 'thread_1' }),
      observation: 'unknown-to-runner',
    })
    expect(fake.resolveCalls).toEqual([])
    expect(fake.mutatingCalls).toEqual([])
  })

  it('leaves no answer for a probe naming a different Runner', async () => {
    const fake = fakeRuntime(() => resolved())
    const handler = createSessionProbeHandler(depsFor('opencode', fake))

    await expect(handler(probeRequest({ runnerId: 'runner_2' }))).rejects.toThrow('different Runner')
    expect(fake.resolveCalls).toEqual([])
  })

  it('leaves no answer for an enabled Runtime with no constructed handle', async () => {
    const handler = createSessionProbeHandler(probeDeps({ enabledRuntimes: new Set<AgentRuntime>(['pi']) }))

    await expect(handler(probeRequest({ runtime: 'pi' }))).rejects.toThrow('not available')
  })

  it('leaves no answer for an enabled Runtime that is not ready', async () => {
    const fake = fakeRuntime(() => resolved())
    fake.setReady(false)
    const handler = createSessionProbeHandler(depsFor('opencode', fake))

    await expect(handler(probeRequest())).rejects.toThrow('not ready')
    expect(fake.resolveCalls).toEqual([])
  })

  it.each(['turn-failed', 'unavailable-runtime', 'incompatible-runtime', 'deadline-exceeded', 'unknown'])(
    'leaves no answer when the read fails as %s',
    async (kind) => {
      const fake = fakeRuntime(() => rejected(kind))
      const handler = createSessionProbeHandler(depsFor('opencode', fake))

      await expect(handler(probeRequest())).rejects.toThrow(kind)
      expect(fake.mutatingCalls).toEqual([])
    },
  )

  it('leaves no answer when the provider throws', async () => {
    const fake = fakeRuntime(() => {
      throw new Error('transport reset by peer')
    })
    const handler = createSessionProbeHandler(depsFor('opencode', fake))

    await expect(handler(probeRequest())).rejects.toThrow('transport reset by peer')
    expect(fake.mutatingCalls).toEqual([])
  })

  it.each([
    ['Runtime Session', { runtimeSessionId: 'runtime_session_other' }],
    ['work directory', { workDir: '/work/run_other' }],
  ])('leaves no answer when the resolved %s differs from the probed binding', async (_field, value) => {
    const fake = fakeRuntime(() => resolved(value))
    const handler = createSessionProbeHandler(depsFor('opencode', fake))

    await expect(handler(probeRequest())).rejects.toThrow('does not match the captured binding')
    expect(fake.mutatingCalls).toEqual([])
  })

  it('never launders an unbound binding into absence through the Pi read path', async () => {
    const fake = fakeRuntime(() => rejected('missing-session'))
    const handler = createSessionProbeHandler(depsFor('pi', fake))

    await expect(handler(probeRequest({ runtime: 'pi', runtimeSessionId: '' }))).rejects.toThrow()
    expect(fake.resolveCalls).toEqual([])
  })

  it('drops unknown request properties from the echoed target', async () => {
    const fake = fakeRuntime(() => resolved())
    const handler = createSessionProbeHandler(depsFor('opencode', fake))

    await expect(handler({ ...probeRequest(), credential: 'must-not-travel' } as never)).resolves.toEqual({
      probe: probeRequest(),
      observation: 'idle',
    })
  })

  it.each([
    ['session identity absent', { sessionId: undefined }],
    ['empty session', { sessionId: '' }],
    ['missing observation identity', { observationId: '' }],
    ['empty binding', { runtimeSessionId: '' }],
    ['empty work directory', { workDir: '' }],
    ['unknown runtime', { runtime: 'gemini' }],
    ['negative binding epoch', { bindingEpoch: -1 }],
    ['zero context generation', { contextGeneration: 0 }],
  ])('rejects an incomplete probe (%s)', async (_case, params) => {
    const fake = fakeRuntime(() => resolved())
    const handler = createSessionProbeHandler(depsFor('opencode', fake))
    const request = { ...probeRequest(), ...params } as Record<string, unknown>

    expect(isRunnerSessionActivityProbeRequest(request)).toBe(false)
    await expect(handler(request as never)).rejects.toThrow()
    expect(fake.resolveCalls).toEqual([])
  })
})
