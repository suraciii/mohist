import { useMemo } from 'react'
import {
  resolveVariantAgainstModel,
  variantListFor,
  type ModelVariantMap,
} from './model-variants'

export interface SelectableModel {
  id: string
  name: string
  badges: string[]
  contextWindow: number
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
  const slashIndex = modelId.indexOf('/')
  const modelName = slashIndex === -1 ? modelId : modelId.slice(slashIndex + 1)

  return (
    <div
      className={`flex flex-wrap items-center gap-1 ${isCompact ? 'ml-0' : 'ml-1'}`}
      role="group"
      aria-label={`${modelName} variants`}
      onPointerDown={(event) => event.stopPropagation()}
      onClick={(event) => event.stopPropagation()}
    >
      {variants.map((variant, index) => {
        const isActive =
          !!activeVariant &&
          resolveVariantAgainstModel(modelId, activeVariant, modelVariants) === variant
        return (
          <button
            key={variant}
            ref={(element) => {
              if (chipRefs) chipRefs[index] = element
            }}
            type="button"
            data-variant-chip={variant}
            data-variant-active={isActive ? 'true' : 'false'}
            data-testid={baseTestId ? `${baseTestId}-${variant}` : undefined}
            onPointerDown={(event) => event.stopPropagation()}
            onClick={(event) => {
              event.stopPropagation()
              onSelect(modelId, variant)
            }}
            onKeyDown={(event) => onChipKeyDown?.(event, index)}
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
