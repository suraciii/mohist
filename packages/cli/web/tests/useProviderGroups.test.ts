import { describe, it, expect } from 'vitest'
import { renderHook } from '@testing-library/react'
import { useProviderGroups } from '../src/hooks/useProviderGroups'
import type { Provider } from '../src/lib/provider-api'

function makeProvider(overrides: Partial<Provider> = {}): Provider {
  return {
    id: 'test-provider',
    name: 'Test Provider',
    baseURL: 'https://api.test.com',
    configured: false,
    source: 'none',
    isBuiltin: true,
    isDefault: false,
    apiKeyMasked: null,
    ...overrides,
  }
}

describe('useProviderGroups', () => {
  it('should group providers by category', () => {
    const providers = [
      makeProvider({ id: 'openai', name: 'OpenAI', configured: true }),
      makeProvider({ id: 'anthropic', name: 'Anthropic' }),
      makeProvider({ id: 'zhipuai', name: 'Zhipu' }),
    ]

    const { result } = renderHook(() => useProviderGroups(providers))

    const groups = result.current.groups
    expect(groups.length).toBeGreaterThanOrEqual(2)

    const connectedGroup = groups.find(g => g.key === 'connected')
    expect(connectedGroup).toBeDefined()
    expect(connectedGroup!.providers).toHaveLength(1)
    expect(connectedGroup!.providers[0].id).toBe('openai')

    const recommendedGroup = groups.find(g => g.key === 'recommended')
    expect(recommendedGroup).toBeDefined()
    expect(recommendedGroup!.providers).toHaveLength(1)
    expect(recommendedGroup!.providers[0].id).toBe('anthropic')
  })

  it('should return groups in correct order', () => {
    const providers = [
      makeProvider({ id: 'xai', name: 'xAI' }),
      makeProvider({ id: 'openai', name: 'OpenAI', configured: true }),
      makeProvider({ id: 'zhipuai', name: 'Zhipu' }),
    ]

    const { result } = renderHook(() => useProviderGroups(providers))
    const keys = result.current.groups.map(g => g.key)

    const connectedIdx = keys.indexOf('connected')
    const chinaIdx = keys.indexOf('china')
    const intlIdx = keys.indexOf('international')

    expect(connectedIdx).toBeLessThan(chinaIdx)
    expect(connectedIdx).toBeLessThan(intlIdx)
  })

  it('should exclude empty groups', () => {
    const providers = [
      makeProvider({ id: 'openai', name: 'OpenAI', configured: true }),
    ]

    const { result } = renderHook(() => useProviderGroups(providers))
    const keys = result.current.groups.map(g => g.key)

    expect(keys).toContain('connected')
    expect(keys).not.toContain('china')
    expect(keys).not.toContain('custom')
  })

  it('should place non-builtin providers in custom group', () => {
    const providers = [
      makeProvider({ id: 'my-custom', name: 'My Custom', isBuiltin: false }),
    ]

    const { result } = renderHook(() => useProviderGroups(providers))
    const customGroup = result.current.groups.find(g => g.key === 'custom')

    expect(customGroup).toBeDefined()
    expect(customGroup!.providers).toHaveLength(1)
    expect(customGroup!.providers[0].id).toBe('my-custom')
  })

  it('should filter providers by search query', () => {
    const providers = [
      makeProvider({ id: 'openai', name: 'OpenAI' }),
      makeProvider({ id: 'anthropic', name: 'Anthropic' }),
      makeProvider({ id: 'deepseek', name: 'DeepSeek' }),
    ]

    const { result } = renderHook(() => useProviderGroups(providers, 'deep'))

    expect(result.current.isSearching).toBe(true)
    const allProviders = result.current.groups.flatMap(g => g.providers)
    expect(allProviders).toHaveLength(1)
    expect(allProviders[0].id).toBe('deepseek')
  })

  it('should set isSearching to false when no query', () => {
    const { result } = renderHook(() => useProviderGroups([]))

    expect(result.current.isSearching).toBe(false)
  })

  it('should sort providers within each group alphabetically', () => {
    const providers = [
      makeProvider({ id: 'zhipuai', name: 'Zhipu' }),
      makeProvider({ id: 'alibaba', name: 'Alibaba' }),
      makeProvider({ id: 'minimax', name: 'Minimax' }),
    ]

    const { result } = renderHook(() => useProviderGroups(providers))
    const chinaGroup = result.current.groups.find(g => g.key === 'china')

    expect(chinaGroup).toBeDefined()
    expect(chinaGroup!.providers.map(p => p.name)).toEqual(['Alibaba', 'Minimax', 'Zhipu'])
  })
})
