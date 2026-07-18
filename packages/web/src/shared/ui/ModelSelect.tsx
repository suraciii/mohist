import { useState, useMemo, useCallback } from 'react'
import { ChevronDown, X } from 'lucide-react'
import { Button } from '@/shared/ui/components/button'
import { Popover, PopoverContent, PopoverTrigger } from '@/shared/ui/components/popover'
import { resolveVariantAgainstModel, variantListFor, type ModelVariantMap } from './model-variants'
import { ModelOptionList } from './ModelOptionList'

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

  const normalizedModels = useMemo(() => normalizeModels(models), [models])

  const handleSelectModel = useCallback(
    (modelId: string) => {
      onChange(modelId)
      onChangeVariant?.(null)
      setOpen(false)
    },
    [onChange, onChangeVariant],
  )

  const handleSelectModelVariant = useCallback(
    (modelId: string, variant: string | null) => {
      if (onChangeModelVariant) {
        onChangeModelVariant(modelId, variant)
      } else {
        onChange(modelId)
        onChangeVariant?.(variant)
      }
      setOpen(false)
    },
    [onChange, onChangeModelVariant, onChangeVariant],
  )

  const selectedModel = value ? normalizedModels.find((m) => m.id === value) : null
  const selectedDescriptor: ModelDescriptor | null = value
    ? selectedModel
      ? {
          id: selectedModel.id,
          name: selectedModel.name,
          fullId: selectedModel.id,
          provider: describeModel(selectedModel.id).provider,
        }
      : describeModel(value)
    : null
  const resolvedSelectedVariant = value
    ? resolveVariantAgainstModel(value, valueVariant, modelVariants)
    : null

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
                title={
                  selectedDescriptor.fullId +
                  (resolvedSelectedVariant ? `:${resolvedSelectedVariant}` : '')
                }
              >
                {selectedDescriptor.fullId}
              </span>
            </div>
          ) : (
            <span className="truncate text-muted-foreground">{placeholder}</span>
          )}
          <ChevronDown className="h-4 w-4 shrink-0 text-muted-foreground" />
        </PopoverTrigger>
        <PopoverContent
          className={`p-0 ${isCompact ? 'w-64' : 'w-80'}`}
          align="end"
        >
          <ModelOptionList
            models={normalizedModels}
            value={value}
            size={size}
            id={id}
            modelVariants={modelVariants}
            valueVariant={valueVariant}
            onSelectModel={handleSelectModel}
            onSelectModelVariant={handleSelectModelVariant}
          />
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
