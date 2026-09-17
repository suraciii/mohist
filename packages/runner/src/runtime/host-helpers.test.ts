import { describe, expect, it } from 'vitest'
import type { PiRuntime } from './pi/index.js'
import {
  deriveRunnerAdmissionObservation,
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
})
