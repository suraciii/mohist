import { useState, useMemo, useEffect, useRef, useCallback } from 'react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover'
import type { Model } from '../api/types'

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
  if (models.length === 0) return []
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
  const [open, setOpen] = useState(false)
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
  useEffect(() => {
    if (!open) return
    setTimeout(() => searchRef.current?.focus(), 0)
  }, [open])

  const displayText = value
    ? normalizedModels.find((m) => m.id === value)?.name || value.split('/').pop() || value
    : placeholder

  const selectModel = useCallback((modelId: string) => {
    onChange(modelId)
    setOpen(false)
  }, [onChange])

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
        if (m) selectModel(m.id)
      }
    },
    [filtered, highlightedIndex, selectModel],
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
    <div className="flex items-center gap-2">
      <Popover open={open} onOpenChange={setOpen}>
        <PopoverTrigger
          render={
            <Button
              variant="outline"
              className={`flex-1 justify-between gap-1.5 min-h-[44px] md:min-h-0 ${
                open
                  ? 'border-blue-500 bg-blue-50 text-blue-700'
                  : value
                    ? 'text-foreground'
                    : 'text-muted-foreground'
              }`}
            />
          }
        >
          <span className="truncate">{displayText}</span>
          <ChevronDownIcon className="h-4 w-4 shrink-0 text-muted-foreground" />
        </PopoverTrigger>
        <PopoverContent className={`p-0 ${isCompact ? 'w-56' : 'w-72'}`} align="end">
          <div className="p-2">
            <div className="relative">
              <div className="absolute left-3 top-1/2 -translate-y-1/2">
                <SearchIcon className={isCompact ? 'h-3.5 w-3.5 text-muted-foreground' : 'h-4 w-4 text-muted-foreground'} />
              </div>
              <Input
                ref={searchRef}
                value={search}
                onChange={(e) => setSearch(e.target.value)}
                onKeyDown={handleKeyDown}
                placeholder="Search models..."
                className={`pl-9 ${isCompact ? 'h-7 text-xs' : ''}`}
              />
            </div>
          </div>

          <div className={`overflow-y-auto border-t ${isCompact ? 'max-h-48' : 'max-h-64'}`}>
            {filtered.length === 0 && (
              <div className={`px-3 py-4 text-center text-muted-foreground ${isCompact ? 'text-xs' : 'text-sm'}`}>
                No models found
              </div>
            )}
            {Array.from(grouped.entries()).map(([provider, providerModels]) => (
              <div key={provider}>
                <div className={`px-3 py-1 text-xs font-medium text-muted-foreground uppercase tracking-wider bg-muted ${isCompact ? 'py-0.5' : ''}`}>
                  {provider}
                </div>
                {providerModels.map((model) => {
                  const globalIdx = filtered.indexOf(model)
                  return (
                    <Button
                      key={model.id}
                      variant="ghost"
                      onClick={() => selectModel(model.id)}
                      onMouseEnter={() => setHighlightedIndex(globalIdx)}
                      className={`w-full justify-between rounded-none h-auto ${
                        globalIdx === highlightedIndex
                          ? 'bg-blue-50 text-blue-700'
                          : model.id === value
                            ? 'bg-muted text-foreground'
                            : 'text-foreground/80 hover:bg-muted'
                      } ${isCompact ? 'px-2 py-1' : 'px-3 py-1.5'}`}
                    >
                      <div className="flex min-w-0 flex-col items-start">
                        <span className={`font-medium ${isCompact ? 'text-xs' : 'text-sm'}`}>{model.name}</span>
                        <span className={`truncate text-muted-foreground ${isCompact ? 'text-[10px]' : 'text-xs'}`}>{model.id}</span>
                      </div>
                    </Button>
                  )
                })}
              </div>
            ))}
          </div>
        </PopoverContent>
      </Popover>
      {allowClear && value && onClear && (
        <Button
          variant="ghost"
          size="icon"
          onClick={onClear}
          className="text-muted-foreground hover:bg-red-50 hover:text-red-500"
          title="Clear"
        >
          <XIcon className="h-4 w-4" />
        </Button>
      )}
    </div>
  )
}
