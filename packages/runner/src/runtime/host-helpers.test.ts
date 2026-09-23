import { describe, expect, it } from 'vitest'
import type { PiRuntime } from './pi/index.js'
import type { CodexRuntime } from './codex/index.js'
import type { DispatchWorkItem } from '../core/types.js'
import {
  deriveRunnerAdmissionObservation,
  runtimeKindForWork,
  runtimeReadinessWitnesses,
  RUNNER_ADMISSION_REASON_CODES,
} from './host-helpers.js'

describe('Runner admission observation', () => {
  it.each([
    [false, true, true, []],
    [true, true, false, [RUNNER_ADMISSION_REASON_CODES.providerPolicyInvalid]],
    [false, false, false, [RUNNER_ADMISSION_REASON_CODES.runtimeEventQueueUnavailable]],
    [
      true,
      false,
      false,
      [RUNNER_ADMISSION_REASON_CODES.providerPolicyInvalid, RUNNER_ADMISSION_REASON_CODES.runtimeEventQueueUnavailable],
    ],
  ] as const)('derives one consistent readiness pair', (providerInvalid, queueReady, ready, reasons) => {
    expect(deriveRunnerAdmissionObservation(providerInvalid, queueReady)).toEqual({
      admissionReady: ready,
      admissionReasonCodes: reasons,
    })
  })
})

describe('runtimeReadinessWitnesses', () => {
  it('keeps the last Pi generation when a started runtime becomes not ready', () => {
    const runtime = { ready: () => false } as PiRuntime

    expect(runtimeReadinessWitnesses(null, runtime, 1)).toEqual([{ runtime: 'pi', ready: false, generation: 1 }])
  })

  it('surfaces Codex readiness with its runtime generation', () => {
    const codex = { ready: () => true, generation: () => 7 } as CodexRuntime

    expect(runtimeReadinessWitnesses(null, null, 0, codex)).toEqual([{ runtime: 'codex', ready: true, generation: 7 }])
  })
})

describe('runtimeKindForWork', () => {
  it.each([
    [{ with: { runtime: 'codex' } }, 'codex'],
    [{ with: { runtime: 'mohist/codex' } }, 'codex'],
    [{ agentDefinition: { runtime: 'codex' } }, 'codex'],
    [{ uses: 'codex' }, 'codex'],
    [{ uses: 'mohist/codex' }, 'codex'],
    [{ with: { runtime: 'pi' } }, 'pi'],
    [{ with: { runtime: 'opencode' } }, 'opencode'],
    [{}, null],
  ] as const)('resolves %o to %s', (work, expected) => {
    expect(runtimeKindForWork(work as unknown as DispatchWorkItem)).toBe(expected)
  })

  it('prefers the declared with.runtime over the agent definition', () => {
    expect(
      runtimeKindForWork({
        with: { runtime: 'codex' },
        agentDefinition: { runtime: 'pi' },
      } as unknown as DispatchWorkItem),
    ).toBe('codex')
  })
})
