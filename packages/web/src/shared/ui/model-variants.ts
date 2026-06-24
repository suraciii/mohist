export type ModelVariantMap = Record<string, string[]>

export function variantListFor(
  modelId: string | null | undefined,
  modelVariants: ModelVariantMap | null | undefined,
): string[] {
  if (!modelId || !modelVariants) return []
  const list = modelVariants[modelId]
  if (!Array.isArray(list)) return []
  return list
}

export function resolveVariantAgainstModel(
  modelId: string | null | undefined,
  variant: string | null | undefined,
  modelVariants: ModelVariantMap | null | undefined,
): string | null {
  if (!variant) return null
  const list = variantListFor(modelId, modelVariants)
  if (list.length === 0) return null
  return list.includes(variant) ? variant : null
}
