import { describe, it, expect } from 'vitest'
import {
  PROVIDER_CATEGORIES,
  getProviderCategory,
  getCategoryMeta,
  type ProviderCategory,
} from '../src/lib/provider-categories'

describe('provider-categories', () => {
  describe('PROVIDER_CATEGORIES', () => {
    it('should map at least 30 provider IDs', () => {
      const keys = Object.keys(PROVIDER_CATEGORIES)
      expect(keys.length).toBeGreaterThanOrEqual(30)
    })

    it('should have recommended category for known providers', () => {
      const recommended = ['openai', 'anthropic', 'deepseek', 'google', 'groq', 'mistral']
      for (const id of recommended) {
        expect(PROVIDER_CATEGORIES[id]).toBeDefined()
        expect(PROVIDER_CATEGORIES[id].category).toBe('recommended')
      }
    })

    it('should have coding-plan category for coding plan providers', () => {
      const codingPlan = ['kimi-for-coding', 'minimax-coding-plan']
      for (const id of codingPlan) {
        expect(PROVIDER_CATEGORIES[id]).toBeDefined()
        expect(PROVIDER_CATEGORIES[id].category).toBe('coding-plan')
      }
    })

    it('should have china region for china providers', () => {
      const china = ['zhipuai', 'alibaba', 'minimax', 'siliconflow']
      for (const id of china) {
        expect(PROVIDER_CATEGORIES[id]).toBeDefined()
        expect(PROVIDER_CATEGORIES[id].region).toBe('china')
      }
    })
  })

  describe('getProviderCategory', () => {
    it('should return category info for known providers', () => {
      const info = getProviderCategory('openai')
      expect(info.category).toBe('recommended')
      expect(info.region).toBe('international')
    })

    it('should default to international for unknown providers', () => {
      const info = getProviderCategory('unknown-provider-xyz')
      expect(info.category).toBe('international')
      expect(info.region).toBe('international')
    })
  })

  describe('getCategoryMeta', () => {
    it('should return meta for all categories', () => {
      const categories: ProviderCategory[] = ['recommended', 'coding-plan', 'china', 'international']
      for (const cat of categories) {
        const meta = getCategoryMeta(cat)
        expect(meta).toBeDefined()
        expect(meta.label).toBeTruthy()
        expect(meta.order).toBeGreaterThanOrEqual(0)
      }
    })

    it('should have correct ordering: recommended < coding-plan < china < international', () => {
      expect(getCategoryMeta('recommended').order).toBeLessThan(getCategoryMeta('coding-plan').order)
      expect(getCategoryMeta('coding-plan').order).toBeLessThan(getCategoryMeta('china').order)
      expect(getCategoryMeta('china').order).toBeLessThan(getCategoryMeta('international').order)
    })
  })
})
