import { describe, expect, it } from 'vitest'
import { makeFakeCodexRuntime } from './support/codex-runtime-fixture.js'

describe('Codex runtime fixture', () => {
  it('records every Session and AgentJob seam with deterministic results', async () => {
    const fixture = makeFakeCodexRuntime()
    const target = { runtime: 'codex' as const, runtimeSessionId: 'thread_fixture', workDir: '/workspace' }

    await fixture.runtime.runTurn({
      target: { ...target, runtimeSessionId: null },
      prompt: 'run once',
      clientUserMessageId: 'input_1',
    })
    await fixture.runtime.followup({ target, prompt: 'follow up', clientUserMessageId: 'input_2' })
    await fixture.runtime.cancel({ target })
    await fixture.runtime.compact({ target })
    await fixture.runtime.reset({ target })
    await fixture.runtime.resolveSession({
      target: { runtimeSessionId: target.runtimeSessionId!, workDir: target.workDir },
    })

    expect(fixture.runTurnCalls[0]).toMatchObject({ prompt: 'run once', clientUserMessageId: 'input_1' })
    expect(fixture.followupCalls[0]).toMatchObject({ prompt: 'follow up', clientUserMessageId: 'input_2' })
    expect(fixture.cancelCalls).toHaveLength(1)
    expect(fixture.compactCalls).toHaveLength(1)
    expect(fixture.resetCalls).toHaveLength(1)
    expect(fixture.resolveSessionCalls).toEqual([{ runtimeSessionId: 'thread_fixture', workDir: '/workspace' }])
  })

  it('allows readiness, catalog, and result outcomes to be changed without changing the seam', async () => {
    const fixture = makeFakeCodexRuntime()
    fixture.setReady(false)
    fixture.setCatalog({ models: [], complete: false, capabilityRevision: 'empty' })
    fixture.setRunTurnResult({
      ok: false,
      error: { kind: 'unknown', message: 'submission is uncertain', diagnostics: [] },
      diagnostics: [],
    })

    expect(fixture.runtime.ready()).toBe(false)
    expect(fixture.runtime.catalog()).toEqual({ models: [], complete: false, capabilityRevision: 'empty' })
    await expect(
      fixture.runtime.runTurn({
        target: { runtime: 'codex', runtimeSessionId: null, workDir: '/workspace' },
        prompt: 'uncertain',
        clientUserMessageId: 'input_uncertain',
      }),
    ).resolves.toMatchObject({ ok: false, error: { kind: 'unknown' } })
  })
})
