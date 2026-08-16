import { useEffect, useMemo, useRef, useCallback } from 'react'
import { Search } from 'lucide-react'
import { Command as CommandRoot } from 'cmdk'

import { cn } from '@/shared/lib/utils'
import { CommandEmpty, CommandGroup } from '@/shared/ui/components/command'
import { ModelVariantChips, type SelectableModel } from './model-option-list'
import type { ModelVariantMap } from './model-variants'
import { variantListFor } from './model-variants'

export interface ModelOptionListProps {
  models: SelectableModel[]
  value: string | null
  size?: 'default' | 'compact'
  id?: string
  searchPlaceholder?: string
  modelVariants?: ModelVariantMap
  modelReasoningEfforts?: ModelVariantMap
  valueVariant?: string | null
  valueReasoningEffort?: string | null
  onSelectModel: (modelId: string) => void
  onSelectModelVariant?: (modelId: string, variant: string | null) => void
  onSelectModelReasoningEffort?: (modelId: string, effort: string | null) => void
}

export function ModelOptionList({
  models,
  value,
  size = 'default',
  id,
  searchPlaceholder = 'Search models...',
  modelVariants,
  modelReasoningEfforts,
  valueVariant,
  valueReasoningEffort,
  onSelectModel,
  onSelectModelVariant,
  onSelectModelReasoningEffort,
}: ModelOptionListProps) {
  const isCompact = size === 'compact'
  const inputRef = useRef<HTMLInputElement>(null)
  const chipRefs = useRef<Record<string, Array<HTMLButtonElement | null>>>({})

  useEffect(() => {
    const timer = setTimeout(() => inputRef.current?.focus(), 0)
    return () => clearTimeout(timer)
  }, [])

  const grouped = useMemo(() => {
    const map = new Map<string, SelectableModel[]>()
    for (const m of models) {
      const provider = m.id.split('/')[0] || 'other'
      const list = map.get(provider)
      if (list) list.push(m)
      else map.set(provider, [m])
    }
    return map
  }, [models])

  const handleChipKeyDown = useCallback(
    (event: React.KeyboardEvent, modelId: string, chipIndex: number) => {
      const variants = variantListFor(modelId, modelReasoningEfforts ?? modelVariants)
      if (variants.length === 0) return

      if (event.key === 'ArrowLeft') {
        event.preventDefault()
        if (chipIndex > 0) {
          chipRefs.current[modelId]?.[chipIndex - 1]?.focus()
        } else {
          inputRef.current?.focus()
        }
      } else if (event.key === 'ArrowRight') {
        if (chipIndex < variants.length - 1) {
          event.preventDefault()
          chipRefs.current[modelId]?.[chipIndex + 1]?.focus()
        }
      } else if (event.key === 'Enter') {
        event.preventDefault()
        if (modelReasoningEfforts) onSelectModelReasoningEffort?.(modelId, variants[chipIndex] ?? null)
        else onSelectModelVariant?.(modelId, variants[chipIndex] ?? null)
      }
    },
    [modelVariants, modelReasoningEfforts, onSelectModelVariant, onSelectModelReasoningEffort, inputRef],
  )

  const handleCommandKeyDown = useCallback(
    (event: React.KeyboardEvent) => {
      const isRightOrTab = event.key === 'ArrowRight' || (event.key === 'Tab' && !event.shiftKey)
      if (!isRightOrTab) return

      const target = event.target as HTMLElement | null
      if (target?.closest('[data-variant-chip]')) return

      const activeItem = (event.currentTarget as HTMLElement).querySelector(
        '[data-selected="true"][data-model-id]',
      ) as HTMLElement | null
      if (!activeItem) return
      const activeModelId = activeItem.getAttribute('data-model-id')
      if (!activeModelId) return

      const variants = variantListFor(activeModelId, modelReasoningEfforts ?? modelVariants)
      if (variants.length === 0) return

      event.preventDefault()
      chipRefs.current[activeModelId]?.[0]?.focus()
    },
    [modelVariants, modelReasoningEfforts],
  )

  return (
    <CommandRoot onKeyDown={handleCommandKeyDown} value={value ?? undefined} label="Model selector">
      <div className={cn('p-2', isCompact && 'pb-1')}>
        <div className="relative">
          <div className="absolute left-3 top-1/2 -translate-y-1/2">
            <Search className={cn('text-muted-foreground', isCompact ? 'h-3.5 w-3.5' : 'h-4 w-4')} />
          </div>
          <CommandRoot.Input
            ref={inputRef}
            placeholder={searchPlaceholder}
            className={cn(
              'flex h-9 w-full rounded-md border border-input bg-transparent py-1 pr-3 text-base shadow-sm transition-colors outline-none placeholder:text-muted-foreground disabled:cursor-not-allowed disabled:opacity-50 md:text-sm',
              'pl-9',
              isCompact && 'h-7 text-xs',
            )}
          />
        </div>
      </div>

      <CommandRoot.List
        className={cn(
          'max-h-64 scroll-py-1 overflow-x-hidden overflow-y-auto overscroll-y-contain outline-none border-t',
          isCompact && 'max-h-56',
        )}
        label="Model options"
      >
        <CommandEmpty>
          <div className={cn('px-3 py-4 text-center text-muted-foreground', isCompact ? 'text-xs' : 'text-sm')}>
            No models found
          </div>
        </CommandEmpty>

        {Array.from(grouped.entries()).map(([provider, providerModels]) => (
          <CommandGroup
            key={provider}
            heading={provider}
            value={provider}
            className="**:[[cmdk-group-heading]]:sticky **:[[cmdk-group-heading]]:top-0 **:[[cmdk-group-heading]]:z-10 **:[[cmdk-group-heading]]:bg-muted"
          >
            {providerModels.map((model) => {
              const modelVariantsList = variantListFor(model.id, modelReasoningEfforts ?? modelVariants)
              const isSelected = model.id === value
              if (!chipRefs.current[model.id]) {
                chipRefs.current[model.id] = []
              }
              return (
                <CommandRoot.Item
                  key={model.id}
                  value={model.id}
                  keywords={[model.name, model.id]}
                  data-model-id={model.id}
                  onSelect={() => onSelectModel(model.id)}
                  className={cn(
                    'flex w-full items-center justify-between gap-2 rounded-none cursor-pointer data-selected:bg-muted data-selected:text-foreground',
                    isCompact ? 'px-2 py-1' : 'px-3 py-1.5',
                    isSelected && 'bg-accent text-accent-foreground',
                  )}
                >
                  <div className="flex min-w-0 flex-col items-start">
                    <span className={cn('w-full truncate font-medium', isCompact ? 'text-xs' : 'text-sm')}>
                      {model.name}
                    </span>
                    <span
                      className={cn('w-full truncate text-muted-foreground', isCompact ? 'text-[10px]' : 'text-xs')}
                    >
                      {model.id}
                    </span>
                  </div>
                  {modelVariantsList.length > 0 && (
                    <ModelVariantChips
                      modelId={model.id}
                      modelVariants={modelReasoningEfforts ?? modelVariants}
                      activeVariant={
                        isSelected ? ((modelReasoningEfforts ? valueReasoningEffort : valueVariant) ?? null) : null
                      }
                      size={isCompact ? 'compact' : 'default'}
                      baseTestId={id ? `${id}-row-${model.id}-variant` : undefined}
                      chipRefs={chipRefs.current[model.id]}
                      onChipKeyDown={(e, chipIndex) => handleChipKeyDown(e, model.id, chipIndex)}
                      onSelect={(mId, variant) => {
                        if (modelReasoningEfforts) onSelectModelReasoningEffort?.(mId, variant)
                        else onSelectModelVariant?.(mId, variant)
                      }}
                    />
                  )}
                </CommandRoot.Item>
              )
            })}
          </CommandGroup>
        ))}
      </CommandRoot.List>
    </CommandRoot>
  )
}
