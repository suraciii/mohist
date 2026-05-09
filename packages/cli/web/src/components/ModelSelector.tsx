import { useState, useEffect, useRef, useCallback, Fragment } from 'react'
import { Popover, Transition } from '@headlessui/react'
import fuzzysort from 'fuzzysort'
import { useModels } from '../hooks/useModels'
import { api } from '../lib/api'
import { useQueryClient } from '@tanstack/react-query'
import type { Model } from '../lib/types'

const RECENT_MODELS_KEY = 'mohist:recent-models'
const MAX_RECENT_MODELS = 5

interface Props {
  sessionId: string
  currentModel?: string
  currentVariant?: string
}

function getRecentModels(): string[] {
  try {
    return JSON.parse(localStorage.getItem(RECENT_MODELS_KEY) || '[]')
  } catch {
    return []
  }
}

function addToRecentModels(modelId: string) {
  const recent = getRecentModels().filter(id => id !== modelId)
  recent.unshift(modelId)
  localStorage.setItem(RECENT_MODELS_KEY, JSON.stringify(recent.slice(0, MAX_RECENT_MODELS)))
}

function SearchIcon() {
  return (
    <svg className="h-4 w-4 text-gray-400" viewBox="0 0 20 20" fill="currentColor">
      <path
        fillRule="evenodd"
        d="M9 3.5a5.5 5.5 0 100 11 5.5 5.5 0 000-11zM2 9a7 7 0 1112.452 4.391l3.328 3.329a.75.75 0 11-1.06 1.06l-3.329-3.328A7 7 0 012 9z"
        clipRule="evenodd"
      />
    </svg>
  )
}

function ChevronDownIcon() {
  return (
    <svg className="h-4 w-4" viewBox="0 0 20 20" fill="currentColor">
      <path
        fillRule="evenodd"
        d="M5.23 7.21a.75.75 0 011.06.02L10 11.168l3.71-3.938a.75.75 0 111.08 1.04l-4.25 4.5a.75.75 0 01-1.08 0l-4.25-4.5a.75.75 0 01.02-1.06z"
        clipRule="evenodd"
      />
    </svg>
  )
}

function Badge({ type }: { type: 'free' | 'latest' }) {
  if (type === 'free') {
    return (
      <span className="inline-flex items-center px-1.5 py-0.5 rounded text-xs font-medium bg-green-100 text-green-700">
        Free
      </span>
    )
  }
  return (
    <span className="inline-flex items-center px-1.5 py-0.5 rounded text-xs font-medium bg-blue-100 text-blue-700">
      Latest
    </span>
  )
}

interface ModelListItemProps {
  model: Model
  isSelected: boolean
  isHighlighted: boolean
  onSelect: () => void
  onMouseEnter: () => void
}

function ModelListItem({ model, isSelected, isHighlighted, onSelect, onMouseEnter }: ModelListItemProps) {
  return (
    <button
      onClick={onSelect}
      onMouseEnter={onMouseEnter}
      className={`w-full flex items-center justify-between px-3 py-2 text-sm transition-colors ${
        isHighlighted ? 'bg-blue-50 text-blue-700' : isSelected ? 'bg-gray-50 text-gray-900' : 'text-gray-700 hover:bg-gray-50'
      }`}
    >
      <div className="flex flex-col items-start gap-1">
        <span className="font-medium">{model.name}</span>
        <span className="text-xs text-gray-400">{model.id}</span>
      </div>
      <div className="flex items-center gap-1">
        {model.badges.map(badge => (
          <Badge key={badge} type={badge} />
        ))}
      </div>
    </button>
  )
}

export function ModelSelector({ sessionId, currentModel, currentVariant }: Props) {
  const queryClient = useQueryClient()
  const { data: providers } = useModels()
  const [searchQuery, setSearchQuery] = useState('')
  const [highlightedIndex, setHighlightedIndex] = useState(0)
  const searchInputRef = useRef<HTMLInputElement>(null)
  const listRef = useRef<HTMLDivElement>(null)

  const recentModelIds = getRecentModels()

  const recentModels: Model[] = []
  const flattenedModels: Model[] = []

  if (providers) {
    for (const provider of providers) {
      if (!provider.configured) continue
      for (const model of provider.models) {
        flattenedModels.push(model)
        if (recentModelIds.includes(model.id)) {
          recentModels.push(model)
        }
      }
    }
  }

  const filteredResults = searchQuery.trim()
    ? fuzzysort.go(searchQuery, flattenedModels, { keys: ['name', 'id'] }).map(r => r.obj)
    : []

  const displayedModels = searchQuery.trim() ? filteredResults : flattenedModels

  const groupedByProvider: Map<string, Model[]> = new Map()
  for (const model of displayedModels) {
    const provider = providers?.find(p => p.models.some(m => m.id === model.id))
    if (provider) {
      const existing = groupedByProvider.get(provider.name) || []
      existing.push(model)
      groupedByProvider.set(provider.name, existing)
    }
  }

  const handleSelect = useCallback(
    async (model: Model) => {
      try {
        await api.updateSessionModel(sessionId, model.id, currentVariant)
        addToRecentModels(model.id)
        queryClient.invalidateQueries({ queryKey: ['explore', sessionId] })
      } catch (err) {
        console.error('Failed to update session model:', err)
      }
    },
    [sessionId, currentVariant, queryClient],
  )

  const handleKeyDown = useCallback(
    (e: React.KeyboardEvent) => {
      if (e.key === 'ArrowDown') {
        e.preventDefault()
        setHighlightedIndex(i => Math.min(i + 1, displayedModels.length - 1))
      } else if (e.key === 'ArrowUp') {
        e.preventDefault()
        setHighlightedIndex(i => Math.max(i - 1, 0))
      } else if (e.key === 'Enter') {
        e.preventDefault()
        if (displayedModels[highlightedIndex]) {
          handleSelect(displayedModels[highlightedIndex])
        }
      }
    },
    [displayedModels, highlightedIndex, handleSelect],
  )

  useEffect(() => {
    setHighlightedIndex(0)
  }, [searchQuery])

  const currentModelDisplay = currentModel
    ? flattenedModels.find(m => m.id === currentModel)?.name || currentModel.split('/').pop() || currentModel
    : 'Select model'

  return (
    <Popover as="div" className="relative">
      {({ open }) => (
        <>
          <Popover.Button
            className={`inline-flex items-center gap-1.5 rounded-md border px-3 py-1.5 text-sm font-medium transition-colors shadow-sm min-h-[44px] md:min-h-0 ${
              open
                ? 'border-blue-500 bg-blue-50 text-blue-700'
                : 'border-gray-300 bg-white text-gray-700 hover:bg-gray-50'
            }`}
          >
            <span className="max-w-[120px] md:max-w-none truncate">{currentModelDisplay}</span>
            <ChevronDownIcon />
          </Popover.Button>

          <Transition
            as={Fragment}
            enter="transition ease-out duration-100"
            enterFrom="transform opacity-0 scale-95"
            enterTo="transform opacity-100 scale-100"
            leave="transition ease-in duration-75"
            leaveFrom="transform opacity-100 scale-100"
            leaveTo="transform opacity-0 scale-95"
          >
            <Popover.Panel portal={false} className="fixed inset-x-2 top-auto z-50 mt-2 md:absolute md:inset-x-auto md:right-0 md:w-80 origin-top-right rounded-lg bg-white shadow-lg ring-1 ring-black/5 focus:outline-none">
              <div className="p-2">
                <div className="relative">
                  <div className="absolute left-3 top-1/2 -translate-y-1/2">
                    <SearchIcon />
                  </div>
                  <input
                    ref={searchInputRef}
                    type="text"
                    value={searchQuery}
                    onChange={e => setSearchQuery(e.target.value)}
                    onKeyDown={handleKeyDown}
                    placeholder="Search models..."
                    className="w-full rounded-md border border-gray-300 pl-9 pr-3 py-2 text-sm text-gray-900 placeholder-gray-400 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
                    autoFocus
                  />
                </div>
              </div>

              <div ref={listRef} className="max-h-80 overflow-y-auto border-t border-gray-100">
                {recentModels.length > 0 && !searchQuery.trim() && (
                  <div>
                    <div className="px-3 py-1.5 text-xs font-medium text-gray-400 uppercase tracking-wider bg-gray-50">
                      Recent
                    </div>
                    {recentModels.map((model, i) => (
                      <ModelListItem
                        key={model.id}
                        model={model}
                        isSelected={model.id === currentModel}
                        isHighlighted={i === highlightedIndex}
                        onSelect={() => handleSelect(model)}
                        onMouseEnter={() => setHighlightedIndex(i)}
                      />
                    ))}
                    <div className="border-t border-gray-100 my-1" />
                  </div>
                )}

                {displayedModels.length === 0 && searchQuery.trim() && (
                  <div className="px-3 py-6 text-center text-sm text-gray-400">
                    No models found
                  </div>
                )}

                {!searchQuery.trim() &&
                  Array.from(groupedByProvider.entries()).map(([providerName, models]) => (
                    <div key={providerName}>
                      <div className="px-3 py-1.5 text-xs font-medium text-gray-400 uppercase tracking-wider bg-gray-50">
                        {providerName}
                      </div>
                      {models.map(model => {
                        const globalIndex = displayedModels.indexOf(model)
                        return (
                          <ModelListItem
                            key={model.id}
                            model={model}
                            isSelected={model.id === currentModel}
                            isHighlighted={globalIndex === highlightedIndex}
                            onSelect={() => handleSelect(model)}
                            onMouseEnter={() => setHighlightedIndex(globalIndex)}
                          />
                        )
                      })}
                    </div>
                  ))}

                {searchQuery.trim() &&
                  displayedModels.map((model, i) => (
                    <ModelListItem
                      key={model.id}
                      model={model}
                      isSelected={model.id === currentModel}
                      isHighlighted={i === highlightedIndex}
                      onSelect={() => handleSelect(model)}
                      onMouseEnter={() => setHighlightedIndex(i)}
                    />
                  ))}
              </div>

              <div className="border-t border-gray-100 p-2 text-xs text-gray-400 text-center">
                Use ↑↓ to navigate, Enter to select, Esc to close
              </div>
            </Popover.Panel>
          </Transition>
        </>
      )}
    </Popover>
  )
}
