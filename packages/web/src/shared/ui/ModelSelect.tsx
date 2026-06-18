import { useState, useMemo, useEffect, useRef, useCallback } from 'react'
import { ChevronDown, Search, X } from 'lucide-react'
import { Button } from '@/shared/ui/components/button'
import { Input } from '@/shared/ui/components/input'
import { Popover, PopoverContent, PopoverTrigger } from '@/shared/ui/components/popover'

export interface SelectableModel {
  id: string
  name: string
  badges: string[]
  contextWindow: number
}

export interface ModelDescriptor {
  id: string
  name: string
  fullId: string
  provider: string | null
}

export function describeModel(id: string): ModelDescriptor {
  const slashIdx = id.indexOf('/')
  if (slashIdx === -1) {
    return { id, name: id, fullId: id, provider: null }
  }
  return {
    id,
    name: id.slice(slashIdx + 1),
    fullId: id,
    provider: id.slice(0, slashIdx),
  }
}

export interface ModelSelectProps {
  value: string | null
  placeholder: string
  models: SelectableModel[] | string[]
  onChange: (model: string) => void
  onClear?: () => void
  allowClear?: boolean
  size?: 'default' | 'compact'
}

function normalizeModels(models: SelectableModel[] | string[]): SelectableModel[] {
  if (models.length === 0) return []
  if (typeof models[0] === 'string') {
    return (models as string[]).map((id): SelectableModel => {
      const described = describeModel(id)
      return {
        id,
        name: described.name,
        badges: [],
        contextWindow: 0,
      }
    })
  }
  return models as SelectableModel[]
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

  const selectModel = useCallback((modelId: string) => {
    onChange(modelId)
    setOpen(false)
  }, [onChange])

  useEffect(() => {
    if (!open) return
    setTimeout(() => searchRef.current?.focus(), 0)
  }, [open])

  const listNodeRef = useRef<HTMLDivElement | null>(null)

  const handleListPointerDown = useCallback(
    (e: PointerEvent) => {
      const target = e.target as HTMLElement | null
      const modelId = target?.closest('[data-model-id]')?.getAttribute('data-model-id')
      if (modelId) {
        e.stopPropagation()
        e.preventDefault()
        selectModel(modelId)
      }
    },
    [selectModel],
  )

  const setListRef = useCallback(
    (el: HTMLDivElement | null) => {
      if (listNodeRef.current === el) return
      if (listNodeRef.current) {
        listNodeRef.current.removeEventListener('pointerdown', handleListPointerDown)
      }
      listNodeRef.current = el
      if (el) {
        el.addEventListener('pointerdown', handleListPointerDown)
      }
    },
    [handleListPointerDown],
  )

  useEffect(() => {
    return () => {
      if (listNodeRef.current) {
        listNodeRef.current.removeEventListener('pointerdown', handleListPointerDown)
        listNodeRef.current = null
      }
    }
  }, [handleListPointerDown])

  const selectedModel = value ? normalizedModels.find((m) => m.id === value) : null
  const selectedDescriptor: ModelDescriptor | null = value
    ? selectedModel
      ? { id: selectedModel.id, name: selectedModel.name, fullId: selectedModel.id, provider: describeModel(selectedModel.id).provider }
      : describeModel(value)
    : null

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
    const map = new Map<string, SelectableModel[]>()
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
          {selectedDescriptor ? (
            <div className="flex min-w-0 flex-1 flex-col items-start gap-0 text-left leading-tight">
              <span className="w-full truncate font-medium">{selectedDescriptor.name}</span>
              <span
                className={`w-full truncate text-muted-foreground ${isCompact ? 'text-[10px]' : 'text-xs'}`}
                title={selectedDescriptor.fullId}
              >
                {selectedDescriptor.fullId}
              </span>
            </div>
          ) : (
            <span className="truncate text-muted-foreground">{placeholder}</span>
          )}
          <ChevronDown className="h-4 w-4 shrink-0 text-muted-foreground" />
        </PopoverTrigger>
        <PopoverContent className={`p-0 ${isCompact ? 'w-56' : 'w-72'}`} align="end">
          <div className="p-2">
            <div className="relative">
              <div className="absolute left-3 top-1/2 -translate-y-1/2">
                <Search className={isCompact ? 'h-3.5 w-3.5 text-muted-foreground' : 'h-4 w-4 text-muted-foreground'} />
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

          <div ref={setListRef} className={`overflow-y-auto border-t ${isCompact ? 'max-h-48' : 'max-h-64'}`}>
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
                      data-model-id={model.id}
                      onClick={() => selectModel(model.id)}
                      onMouseEnter={() => setHighlightedIndex(globalIdx)}
                      className={`w-full justify-between rounded-none h-auto ${
                        globalIdx === highlightedIndex
                          ? 'bg-blue-50 text-blue-700'
                          : model.id === value
                            ? 'bg-muted text-foreground'
                            : 'text-foreground hover:bg-muted'
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
          <X className="h-4 w-4" />
        </Button>
      )}
    </div>
  )
}
