import { describe, expect, it } from 'vitest'
import {
  DEFAULT_WORK_MEMORY_MB,
  DEFAULT_WORK_TURN_BUDGET_MS,
  FULL_VERIFY_MEMORY_MB,
  FULL_VERIFY_RESOURCE_PROFILE,
  normalizeWorkResourceLimits,
  resolveActionResourceProfile,
} from './resource-containment.js'

describe('work resource limits', () => {
  it('provides bounded defaults and preserves explicit null disables', () => {
    expect(normalizeWorkResourceLimits()).toMatchObject({
      memoryMb: DEFAULT_WORK_MEMORY_MB,
      turnBudgetMs: DEFAULT_WORK_TURN_BUDGET_MS,
    })
    expect(normalizeWorkResourceLimits({ memoryMb: null, turnBudgetMs: null })).toMatchObject({
      memoryMb: null,
      turnBudgetMs: null,
    })
  })

  it('resolves the full verification profile to a finite larger memory bound', () => {
    expect(
      resolveActionResourceProfile(FULL_VERIFY_RESOURCE_PROFILE, {
        memoryMb: DEFAULT_WORK_MEMORY_MB,
        wallClockMs: 60_000,
        watchdogIntervalMs: 250,
      }),
    ).toEqual({
      ok: true,
      resourceLimits: {
        memoryMb: FULL_VERIFY_MEMORY_MB,
        wallClockMs: 60_000,
        watchdogIntervalMs: 250,
      },
    })
  })

  it('rejects an unknown action resource profile', () => {
    expect(resolveActionResourceProfile('unbounded-verify', undefined)).toEqual({
      ok: false,
      message: "Unsupported resource profile 'unbounded-verify'. Supported profiles: full-verify",
    })
  })
})
