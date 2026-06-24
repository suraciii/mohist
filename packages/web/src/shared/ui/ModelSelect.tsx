import { useState, useMemo, useEffect, useRef, useCallback } from 'react'
import { ChevronDown, Search, X } from 'lucide-react'
import { Button } from '@/shared/ui/components/button'
import { Input } from '@/shared/ui/components/input'
import { Popover, PopoverContent, PopoverTrigger } from '@/shared/ui/components/popover'
import { resolveVariantAgainstModel, variantListFor, type ModelVariantMap } from './model-variants'

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

export interface ModelVariantChipsProps {
  modelId: string | null
  modelVariants?: ModelVariantMap
  activeVariant?: string | null
  size?: 'default' | 'compact'
  baseTestId?: string
  chipRefs?: (HTMLButtonElement | null)[]
  onChipKeyDown?: (event: React.KeyboardEvent, chipIndex: number) => void
  onSelect: (modelId: string, variant: string | null) => void
}

export function ModelVariantChips({
  modelId,
  modelVariants,
  activeVariant,
  size = 'default',
  baseTestId,
  chipRefs,
  onChipKeyDown,
  onSelect,
}: ModelVariantChipsProps) {
  const variants = useMemo(
    () => variantListFor(modelId, modelVariants),
    [modelId, modelVariants],
  )
  if (!modelId || variants.length === 0) return null

  const isCompact = size === 'compact'
  const chipBase = isCompact
    ? 'min-h-11 min-w-11 px-2 text-[11px]'
    : 'min-h-11 min-w-11 px-2.5 text-xs'

  return (
    <div
      className={`flex flex-wrap items-center gap-1 ${isCompact ? 'ml-0' : 'ml-1'}`}
      role="group"
      aria-label={modelId ? `${describeModel(modelId).name} variants` : undefined}
      onPointerDown={(e) => e.stopPropagation()}
      onClick={(e) => e.stopPropagation()}
    >
      {variants.map((variant, index) => {
        const isActive = !!activeVariant && resolveVariantAgainstModel(modelId, activeVariant, modelVariants) === variant
        return (
          <button
            key={variant}
            ref={(el) => {
              if (chipRefs) chipRefs[index] = el
            }}
            type="button"
            data-variant-chip={variant}
            data-variant-active={isActive ? 'true' : 'false'}
            data-testid={baseTestId ? `${baseTestId}-${variant}` : undefined}
            onPointerDown={(e) => {
              e.stopPropagation()
            }}
            onClick={(e) => {
              e.stopPropagation()
              onSelect(modelId, variant)
            }}
            onKeyDown={(e) => onChipKeyDown?.(e, index)}
            className={`inline-flex items-center justify-center rounded-full border font-medium transition-colors ${chipBase} ${
              isActive
                ? 'border-blue-500 bg-blue-500 text-white'
                : 'border-input bg-background text-foreground hover:bg-muted'
            }`}
            aria-pressed={isActive}
            aria-label={`Select variant ${variant}`}
          >
            {variant}
          </button>
        )
      })}
    </div>
  )
}

export interface ModelSelectProps {
  value: string | null
  placeholder: string
  models: SelectableModel[] | string[]
  onChange: (model: string) => void
  onClear?: () => void
  allowClear?: boolean
  size?: 'default' | 'compact'
  id?: string
  'aria-labelledby'?: string
  modelVariants?: ModelVariantMap
  valueVariant?: string | null
  onChangeVariant?: (variant: string | null) => void
  onChangeModelVariant?: (model: string, variant: string | null) => void
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

export function ModelSelect({
  value,
  placeholder,
  models,
  onChange,
  onClear,
  allowClear,
  size = 'default',
  id,
  'aria-labelledby': ariaLabelledby,
  modelVariants,
  valueVariant,
  onChangeVariant,
  onChangeModelVariant,
}: ModelSelectProps) {
  const [open, setOpen] = useState(false)
  const [search, setSearch] = useState('')
  const [highlightedIndex, setHighlightedIndex] = useState(0)
  const [chipFocus, setChipFocus] = useState<{ modelIndex: number; chipIndex: number } | null>(null)
  const searchRef = useRef<HTMLInputElement>(null)
  const chipRefs = useRef<Record<string, Array<HTMLButtonElement | null>>>({})

  const normalizedModels = useMemo(() => normalizeModels(models), [models])

  const filtered = useMemo(() => {
    if (!search.trim()) return normalizedModels
    const q = search.toLowerCase()
    return normalizedModels.filter(
      (m) => m.name.toLowerCase().includes(q) || m.id.toLowerCase().includes(q),
    )
  }, [normalizedModels, search])

  useEffect(() => {
    setHighlightedIndex(0)
    setChipFocus(null)
  }, [search])

  const selectModel = useCallback((modelId: string) => {
    onChange(modelId)
    onChangeVariant?.(null)
    setOpen(false)
  }, [onChange, onChangeVariant])

  const selectModelAndVariant = useCallback((modelId: string, variant: string | null) => {
    if (onChangeModelVariant) onChangeModelVariant(modelId, variant)
    else {
      onChange(modelId)
      onChangeVariant?.(variant)
    }
    setOpen(false)
  }, [onChange, onChangeModelVariant, onChangeVariant])

  useEffect(() => {
    if (!open) return
    setTimeout(() => searchRef.current?.focus(), 0)
  }, [open])

  useEffect(() => {
    if (!open || !chipFocus) return
    const model = filtered[chipFocus.modelIndex]
    if (!model) return
    chipRefs.current[model.id]?.[chipFocus.chipIndex]?.focus()
  }, [chipFocus, filtered, open])

  const listNodeRef = useRef<HTMLDivElement | null>(null)

  const handleListPointerDown = useCallback(
    (e: PointerEvent) => {
      const target = e.target as HTMLElement | null
      const chipEl = target?.closest('[data-variant-chip]') as HTMLElement | null
      if (chipEl) {
        const chipModelId = chipEl.closest('[data-model-id]')?.getAttribute('data-model-id') ?? null
        const variant = chipEl.getAttribute('data-variant-chip')
        if (chipModelId && variant) {
          e.stopPropagation()
          e.preventDefault()
          selectModelAndVariant(chipModelId, variant)
        }
        return
      }
      const modelId = target?.closest('[data-model-id]')?.getAttribute('data-model-id')
      if (modelId) {
        e.stopPropagation()
        e.preventDefault()
        selectModel(modelId)
      }
    },
    [selectModel, selectModelAndVariant],
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
  const resolvedSelectedVariant = value
    ? resolveVariantAgainstModel(value, valueVariant, modelVariants)
    : null

  const handleKeyDown = useCallback(
    (e: React.KeyboardEvent) => {
      if (e.key === 'Escape') {
        e.preventDefault()
        setOpen(false)
      } else if (e.key === 'ArrowDown') {
        e.preventDefault()
        setChipFocus(null)
        setHighlightedIndex((i) => Math.min(i + 1, filtered.length - 1))
      } else if (e.key === 'ArrowUp') {
        e.preventDefault()
        setChipFocus(null)
        setHighlightedIndex((i) => Math.max(i - 1, 0))
      } else if (e.key === 'ArrowRight' || e.key === 'Tab') {
        const variants = variantListFor(filtered[highlightedIndex]?.id, modelVariants)
        if (variants.length > 0) {
          e.preventDefault()
          setChipFocus({ modelIndex: highlightedIndex, chipIndex: 0 })
        }
      } else if (e.key === 'Enter') {
        e.preventDefault()
        const m = filtered[highlightedIndex]
        if (m) selectModel(m.id)
      }
    },
    [filtered, highlightedIndex, modelVariants, selectModel],
  )

  const handleChipKeyDown = useCallback(
    (e: React.KeyboardEvent, modelIndex: number, chipIndex: number) => {
      const model = filtered[modelIndex]
      const variants = variantListFor(model?.id, modelVariants)
      if (!model || variants.length === 0) return

      if (e.key === 'Escape') {
        e.preventDefault()
        setOpen(false)
      } else if (e.key === 'Enter') {
        e.preventDefault()
        selectModelAndVariant(model.id, variants[chipIndex] ?? null)
      } else if (e.key === 'ArrowLeft') {
        e.preventDefault()
        if (chipIndex > 0) setChipFocus({ modelIndex, chipIndex: chipIndex - 1 })
        else {
          setChipFocus(null)
          searchRef.current?.focus()
        }
      } else if (e.key === 'ArrowRight' || e.key === 'Tab') {
        if (chipIndex < variants.length - 1) {
          e.preventDefault()
          setChipFocus({ modelIndex, chipIndex: chipIndex + 1 })
        }
      } else if (e.key === 'ArrowDown') {
        e.preventDefault()
        const nextIndex = Math.min(modelIndex + 1, filtered.length - 1)
        setHighlightedIndex(nextIndex)
        setChipFocus(null)
        searchRef.current?.focus()
      } else if (e.key === 'ArrowUp') {
        e.preventDefault()
        const nextIndex = Math.max(modelIndex - 1, 0)
        setHighlightedIndex(nextIndex)
        setChipFocus(null)
        searchRef.current?.focus()
      }
    },
    [filtered, modelVariants, selectModelAndVariant],
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
              id={id}
              aria-labelledby={ariaLabelledby}
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
              <span className="w-full truncate font-medium">
                {selectedDescriptor.name}
                {resolvedSelectedVariant ? ` · ${resolvedSelectedVariant}` : ''}
              </span>
              <span
                className={`w-full truncate text-muted-foreground ${isCompact ? 'text-[10px]' : 'text-xs'}`}
                title={selectedDescriptor.fullId + (resolvedSelectedVariant ? `:${resolvedSelectedVariant}` : '')}
              >
                {selectedDescriptor.fullId}
              </span>
            </div>
          ) : (
            <span className="truncate text-muted-foreground">{placeholder}</span>
          )}
          <ChevronDown className="h-4 w-4 shrink-0 text-muted-foreground" />
        </PopoverTrigger>
        <PopoverContent className={`p-0 ${isCompact ? 'w-64' : 'w-80'}`} align="end">
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

          <div ref={setListRef} className={`overflow-y-auto border-t ${isCompact ? 'max-h-56' : 'max-h-64'}`}>
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
                  const modelVariantsList = variantListFor(model.id, modelVariants)
                  const isSelected = model.id === value
                  return (
                    <div
                      key={model.id}
                      role="button"
                      tabIndex={-1}
                      data-model-id={model.id}
                      onClick={() => selectModel(model.id)}
                      onMouseEnter={() => setHighlightedIndex(globalIdx)}
                      className={`flex w-full items-center justify-between gap-2 rounded-none cursor-default ${
                        globalIdx === highlightedIndex
                          ? 'bg-blue-50 text-blue-700'
                          : isSelected
                            ? 'bg-muted text-foreground'
                            : 'text-foreground hover:bg-muted'
                      } ${isCompact ? 'px-2 py-1' : 'px-3 py-1.5'}`}
                    >
                      <div className="flex min-w-0 flex-col items-start">
                        <span className={`font-medium ${isCompact ? 'text-xs' : 'text-sm'}`}>{model.name}</span>
                        <span className={`truncate text-muted-foreground ${isCompact ? 'text-[10px]' : 'text-xs'}`}>{model.id}</span>
                      </div>
                      {modelVariantsList.length > 0 && (
                        <ModelVariantChips
                          modelId={model.id}
                          modelVariants={modelVariants}
                          activeVariant={isSelected ? valueVariant ?? null : null}
                          size={isCompact ? 'compact' : 'default'}
                          baseTestId={id ? `${id}-row-${model.id}-variant` : undefined}
                          chipRefs={chipRefs.current[model.id] ??= []}
                          onChipKeyDown={(e, chipIndex) => handleChipKeyDown(e, globalIdx, chipIndex)}
                          onSelect={selectModelAndVariant}
                        />
                      )}
                    </div>
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
