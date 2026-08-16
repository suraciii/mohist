import { describe, expect, it } from 'vitest'
import {
  DEFAULT_WORK_MEMORY_MB,
  DEFAULT_WORK_TURN_BUDGET_MS,
  normalizeWorkResourceLimits,
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
})
