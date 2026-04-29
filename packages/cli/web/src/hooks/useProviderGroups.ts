import { useMemo } from 'react'
import fuzzysort from 'fuzzysort'
import type { Provider } from '../lib/provider-api'
import {
  type GroupedProvider,
  getProviderCategory,
} from '../lib/provider-categories'

export interface ProviderGroupsResult {
  groups: GroupedProvider<Provider>[]
  isSearching: boolean
}

const GROUP_ORDER: { key: string; label: string }[] = [
  { key: 'connected', label: 'Connected' },
  { key: 'recommended', label: 'Recommended' },
  { key: 'coding-plan', label: 'Coding Plan' },
  { key: 'china', label: 'China' },
  { key: 'international', label: 'International' },
  { key: 'custom', label: 'Custom' },
]

function assignGroupKey(provider: Provider): string {
  if (provider.configured) return 'connected'
  if (!provider.isBuiltin) return 'custom'
  const info = getProviderCategory(provider.id)
  return info.category
}

export function useProviderGroups(
  providers: Provider[],
  searchQuery?: string,
): ProviderGroupsResult {
  const trimmed = searchQuery?.trim() ?? ''
  const isSearching = trimmed.length > 0

  return useMemo(() => {
    const filtered = isSearching
      ? fuzzysort
          .go(trimmed, providers, { keys: ['name', 'id'] })
          .map((r) => r.obj)
      : providers

    const buckets = new Map<string, Provider[]>()

    for (const p of filtered) {
      const groupKey = assignGroupKey(p)
      let arr = buckets.get(groupKey)
      if (!arr) {
        arr = []
        buckets.set(groupKey, arr)
      }
      arr.push(p)
    }

    for (const arr of buckets.values()) {
      arr.sort((a, b) => a.name.localeCompare(b.name))
    }

    const groups: GroupedProvider<Provider>[] = []

    for (const def of GROUP_ORDER) {
      const items = buckets.get(def.key)
      if (items && items.length > 0) {
        groups.push({
          key: def.key,
          label: `${def.label} (${items.length})`,
          providers: items,
        })
      }
    }

    return { groups, isSearching }
  }, [providers, trimmed, isSearching])
}
