import { useMemo } from 'react'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/shared/ui/components/select'

export interface VariantPickerProps {
  modelId: string | null
  modelVariants: ReadonlyArray<string> | undefined
  value: string | null
  onChange?: ((variant: string | null) => void) | undefined
  disabled?: boolean
  id?: string
  size?: 'default' | 'sm'
  className?: string
  'aria-label'?: string
  'aria-labelledby'?: string
}

const NONE_VALUE = '__none__'

export function VariantPicker({
  modelId,
  modelVariants,
  value,
  onChange,
  disabled,
  id,
  size = 'sm',
  className,
  'aria-label': ariaLabel,
  'aria-labelledby': ariaLabelledby,
}: VariantPickerProps) {
  const variants = useMemo(() => {
    if (!modelId || !modelVariants || modelVariants.length === 0) return []
    return Array.from(new Set(modelVariants.filter((v) => typeof v === 'string' && v.length > 0)))
  }, [modelId, modelVariants])

  if (!modelId || variants.length === 0) {
    return null
  }

  const triggerValue = value && variants.includes(value) ? value : NONE_VALUE

  return (
    <Select
      value={triggerValue}
      onValueChange={(next) => {
        if (!onChange) return
        if (!next || next === NONE_VALUE) {
          onChange(null)
          return
        }
        onChange(next)
      }}
      disabled={disabled}
    >
      <SelectTrigger
        id={id}
        size={size}
        aria-label={ariaLabel}
        aria-labelledby={ariaLabelledby}
        className={className}
        data-testid={id ? `${id}-variant-trigger` : undefined}
      >
        <SelectValue placeholder="Variant">
          {triggerValue === NONE_VALUE ? 'Variant' : triggerValue}
        </SelectValue>
      </SelectTrigger>
      <SelectContent>
        <SelectItem value={NONE_VALUE}>Default</SelectItem>
        {variants.map((variant) => (
          <SelectItem key={variant} value={variant}>
            {variant}
          </SelectItem>
        ))}
      </SelectContent>
    </Select>
  )
}

export function variantListFor(modelId: string | null | undefined, modelVariants: Record<string, string[]> | null | undefined): string[] {
  if (!modelId || !modelVariants) return []
  return modelVariants[modelId] ?? []
}

export function resolveVariantAgainstModel(
  storedVariant: string | null | undefined,
  modelId: string | null | undefined,
  modelVariants: Record<string, string[]> | null | undefined,
): string | null {
  const allowed = variantListFor(modelId, modelVariants)
  if (!modelId || allowed.length === 0) return null
  if (storedVariant && allowed.includes(storedVariant)) return storedVariant
  return null
}
