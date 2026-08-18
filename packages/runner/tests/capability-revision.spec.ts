import { describe, expect, it } from 'vitest'
import type { RuntimeCatalogEntry } from '../src/core/types.js'
import { deriveCapabilityRevision } from '../src/runtime/capability-revision.js'

describe('deriveCapabilityRevision', () => {
  it('is stable when catalog maps, models, and values arrive in another order', () => {
    const first: RuntimeCatalogEntry = {
      models: ['openai/gpt-5', 'anthropic/claude'],
      variants: {
        'openai/gpt-5': ['balanced', 'fast'],
        'anthropic/claude': ['extended'],
      },
      reasoningEfforts: {
        'openai/gpt-5': ['high', 'low', 'high'],
        'anthropic/claude': ['medium'],
      },
      supportsReasoningEffort: true,
      complete: true,
    }
    const reordered: RuntimeCatalogEntry = {
      models: ['anthropic/claude', 'openai/gpt-5'],
      variants: {
        'anthropic/claude': ['extended'],
        'openai/gpt-5': ['fast', 'balanced'],
      },
      reasoningEfforts: {
        'anthropic/claude': ['medium'],
        'openai/gpt-5': ['low', 'high'],
      },
      complete: true,
      supportsReasoningEffort: true,
    }

    expect(deriveCapabilityRevision(first)).toBe(deriveCapabilityRevision(reordered))
  })

  it('changes when capability content changes', () => {
    const catalog: RuntimeCatalogEntry = {
      models: ['openai/gpt-5'],
      variants: {},
      reasoningEfforts: { 'openai/gpt-5': ['low', 'high'] },
      supportsReasoningEffort: true,
      complete: true,
    }

    const firstRevision = deriveCapabilityRevision(catalog)
    const changedRevision = deriveCapabilityRevision({
      ...catalog,
      reasoningEfforts: { 'openai/gpt-5': ['low', 'high', 'max'] },
    })

    expect(changedRevision).not.toBe(firstRevision)
    expect(deriveCapabilityRevision(catalog)).toBe(firstRevision)
  })
})
