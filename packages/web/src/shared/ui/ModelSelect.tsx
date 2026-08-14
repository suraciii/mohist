import { useState, useMemo, useCallback } from 'react'
import { ChevronDown, X } from 'lucide-react'
import { Button } from '@/shared/ui/components/button'
import { Popover, PopoverContent, PopoverTrigger } from '@/shared/ui/components/popover'
import { resolveVariantAgainstModel, type ModelVariantMap } from './model-variants'
import { ModelOptionList } from './ModelOptionList'
import { type SelectableModel } from './model-option-list'

export { ModelVariantChips, type ModelVariantChipsProps, type SelectableModel } from './model-option-list'

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
  id?: string
  'aria-labelledby'?: string
  modelVariants?: ModelVariantMap
  valueVariant?: string | null
  onChangeVariant?: (variant: string | null) => void
  onChangeModelVariant?: (model: string, variant: string | null) => void
  disabled?: boolean
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
  disabled = false,
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
      <Popover open={disabled ? false : open} onOpenChange={(nextOpen) => !disabled && setOpen(nextOpen)}>
        <PopoverTrigger
          render={
            <Button
              variant="outline"
              id={id}
              disabled={disabled}
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
