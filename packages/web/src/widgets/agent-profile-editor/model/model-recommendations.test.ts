import { describe, expect, it } from 'vitest'
import { recommendModels } from './model-recommendations'

describe('recommendModels', () => {
  it('ranks only models returned by the Runtime catalog for the selected task use', () => {
    const catalog = ['provider/general', 'provider/code-engineer', 'provider/review-reasoner']
    expect(recommendModels(catalog, 'coding', '')).toEqual([
      'provider/code-engineer',
      'provider/general',
      'provider/review-reasoner',
    ])
    expect(recommendModels(catalog, 'review', '')).toEqual([
      'provider/review-reasoner',
      'provider/general',
      'provider/code-engineer',
    ])
  })

  it('uses purpose text as a task signal without inventing catalog entries', () => {
    const catalog = ['provider/fast', 'provider/gemini']
    expect(recommendModels(catalog, 'general', 'Research architecture options')).toEqual(catalog)
    expect(recommendModels(catalog, 'research', 'Research architecture options')).toEqual([
      'provider/gemini',
      'provider/fast',
    ])
  })
})
