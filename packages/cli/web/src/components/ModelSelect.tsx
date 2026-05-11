import { useState, useMemo, useEffect, useRef, useCallback } from 'react'
import { Popover } from '@headlessui/react'
import type { Model } from '../lib/types'

function SearchIcon({ className }: { className?: string }) {
  return (
    <svg className={className} viewBox="0 0 20 20" fill="currentColor">
      <path fillRule="evenodd" d="M9 3.5a5.5 5.5 0 100 11 5.5 5.5 0 000-11zM2 9a7 7 0 1112.452 4.391l3.328 3.329a.75.75 0 11-1.06 1.06l-3.329-3.328A7 7 0 012 9z" clipRule="evenodd" />
    </svg>
  )
}

function ChevronDownIcon({ className }: { className?: string }) {
  return (
    <svg className={className} viewBox="0 0 20 20" fill="currentColor">
      <path fillRule="evenodd" d="M5.23 7.21a.75.75 0 011.06.02L10 11.168l3.71-3.938a.75.75 0 111.08 1.04l-4.25 4.5a.75.75 0 01-1.08 0l-4.25-4.5a.75.75 0 01.02-1.06z" clipRule="evenodd" />
    </svg>
  )
}

function XIcon({ className }: { className?: string }) {
  return (
    <svg className={className} viewBox="0 0 20 20" fill="currentColor">
      <path d="M6.28 5.22a.75.75 0 00-1.06 1.06L8.94 10l-3.72 3.72a.75.75 0 101.06 1.06L10 11.06l3.72 3.72a.75.75 0 101.06-1.06L11.06 10l3.72-3.72a.75.75 0 00-1.06-1.06L10 8.94 6.28 5.22z" />
    </svg>
  )
}

export interface ModelSelectProps {
  value: string | null
  placeholder: string
  models: Model[] | string[]
  onChange: (model: string) => void
  onClear?: () => void
  allowClear?: boolean
  size?: 'default' | 'compact'
}

function normalizeModels(models: Model[] | string[]): Model[] {
  if (typeof models[0] === 'string') {
    return (models as string[]).map((id): Model => ({
      id,
      name: id.split('/').pop() || id,
      badges: [],
      contextWindow: 0,
    }))
  }
  return models as Model[]
}

export function ModelSelect({ value, placeholder, models, onChange, onClear, allowClear, size = 'default' }: ModelSelectProps) {
  const [search, setSearch] = useState('')
  const [highlightedIndex, setHighlightedIndex] = useState(0)
  const searchRef = useRef<HTMLInputElement>(null)

  const normalizedModels = useMemo(() => normalizeModels(models), [models])

  const filtered = useMemo(() => {
    if (!search.trim()) return normalizedModels
    const q = search.toLowerCase()
    return normalizedModels.filter(
      (m) => m.name.toLowerCase().includes(q) || m.id.toLowerCase().includes(q),
    )
  }, [normalizedModels, search])

  useEffect(() => { setHighlightedIndex(0) }, [search])

  const displayText = value
    ? normalizedModels.find((m) => m.id === value)?.name || value.split('/').pop() || value
    : placeholder

  const handleKeyDown = useCallback(
    (e: React.KeyboardEvent) => {
      if (e.key === 'ArrowDown') {
        e.preventDefault()
        setHighlightedIndex((i) => Math.min(i + 1, filtered.length - 1))
      } else if (e.key === 'ArrowUp') {
        e.preventDefault()
        setHighlightedIndex((i) => Math.max(i - 1, 0))
      } else if (e.key === 'Enter') {
        e.preventDefault()
        const m = filtered[highlightedIndex]
        if (m) onChange(m.id)
      }
    },
    [filtered, highlightedIndex, onChange],
  )

  const grouped = useMemo(() => {
    const map = new Map<string, Model[]>()
    for (const m of filtered) {
      const provider = m.id.split('/')[0] || 'other'
      const list = map.get(provider) || []
      list.push(m)
      map.set(provider, list)
    }
    return map
  }, [filtered])

  const isCompact = size === 'compact'

  return (
    <Popover as="div" className="relative">
      {({ open }) => (
        <>
          <div className="flex items-center gap-2">
            <Popover.Button
              className={`flex-1 inline-flex items-center justify-between gap-1.5 rounded-md border px-3 py-2 text-sm font-medium transition-colors min-h-[44px] md:min-h-0 ${
                open
                  ? 'border-blue-500 bg-blue-50 text-blue-700'
                  : value
                    ? 'border-gray-300 bg-white text-gray-900 hover:bg-gray-50'
                    : 'border-gray-300 bg-white text-gray-500 hover:bg-gray-50'
              }`}
            >
              <span className="truncate">{displayText}</span>
              <ChevronDownIcon className="h-4 w-4 shrink-0 text-gray-400" />
            </Popover.Button>
            {allowClear && value && onClear && (
              <button
                onClick={onClear}
                className="inline-flex items-center justify-center p-2 text-gray-400 hover:text-red-500 hover:bg-red-50 rounded-md transition-colors"
                title="Clear"
              >
                <XIcon className="h-4 w-4" />
              </button>
            )}
          </div>

          <Popover.Panel portal={false} className={`fixed inset-x-2 top-auto z-50 mt-1 md:absolute md:inset-x-auto md:right-0 md:w-72 origin-top-right rounded-lg bg-white shadow-lg ring-1 ring-black/5 focus:outline-none ${isCompact ? 'md:w-56' : ''}`}>
              <div className="p-2">
                <div className="relative">
                  <div className="absolute left-3 top-1/2 -translate-y-1/2">
                    <SearchIcon className={isCompact ? 'h-3.5 w-3.5 text-gray-400' : 'h-4 w-4 text-gray-400'} />
                  </div>
                  <input
                    ref={searchRef}
                    type="text"
                    value={search}
                    onChange={(e) => setSearch(e.target.value)}
                    onKeyDown={handleKeyDown}
                    placeholder="Search models..."
                    className={`w-full rounded-md border border-gray-300 pl-9 pr-3 text-gray-900 placeholder-gray-400 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500 ${isCompact ? 'py-1.5 text-xs' : 'py-1.5 text-sm'}`}
                    autoFocus
                  />
                </div>
              </div>

              <div className={`overflow-y-auto border-t border-gray-100 ${isCompact ? 'max-h-48' : 'max-h-64'}`}>
                {filtered.length === 0 && (
                  <div className={`px-3 py-4 text-center text-gray-400 ${isCompact ? 'text-xs' : 'text-sm'}`}>
                    No models found
                  </div>
                )}
                {Array.from(grouped.entries()).map(([provider, providerModels]) => (
                  <div key={provider}>
                    <div className={`px-3 py-1 text-xs font-medium text-gray-400 uppercase tracking-wider bg-gray-50 ${isCompact ? 'py-0.5' : ''}`}>
                      {provider}
                    </div>
                    {providerModels.map((model) => {
                      const globalIdx = filtered.indexOf(model)
                      return (
                        <button
                          key={model.id}
                          onClick={() => onChange(model.id)}
                          onMouseEnter={() => setHighlightedIndex(globalIdx)}
                          className={`w-full flex items-center justify-between transition-colors ${
                            globalIdx === highlightedIndex
                              ? 'bg-blue-50 text-blue-700'
                              : model.id === value
                                ? 'bg-gray-50 text-gray-900'
                                : 'text-gray-700 hover:bg-gray-50'
                          } ${isCompact ? 'px-2 py-1' : 'px-3 py-1.5'}`}
                        >
                          <div className="flex flex-col items-start">
                            <span className={`font-medium ${isCompact ? 'text-xs' : 'text-sm'}`}>{model.name}</span>
                            <span className={`text-gray-400 ${isCompact ? 'text-[10px]' : 'text-xs'}`}>{model.id}</span>
                          </div>
                        </button>
                      )
                    })}
                  </div>
                ))}
              </div>
            </Popover.Panel>
        </>
      )}
    </Popover>
  )
}