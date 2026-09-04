import { describe, expect, it } from 'vitest'
import type { PiRuntime } from './pi/index.js'
import { runtimeReadinessWitnesses } from './host-helpers.js'

describe('runtimeReadinessWitnesses', () => {
  it('keeps the last Pi generation when a started runtime becomes not ready', () => {
    const runtime = { ready: () => false } as PiRuntime

    expect(runtimeReadinessWitnesses(null, runtime, 1)).toEqual([{ runtime: 'pi', ready: false, generation: 1 }])
  })
})
