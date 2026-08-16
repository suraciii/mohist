import { createHash } from 'node:crypto'
import type { RuntimeCatalogEntry } from '../core/types.js'

function compareStrings(left: string, right: string): number {
  if (left < right) return -1
  if (left > right) return 1
  return 0
}

function canonicalMap(source: Record<string, string[]> | undefined): Record<string, string[]> {
  return Object.fromEntries(
    Object.entries(source ?? {})
      .sort(([left], [right]) => compareStrings(left, right))
      .map(([key, values]) => [key, [...new Set(values)].sort(compareStrings)]),
  )
}

/**
 * Derives the immutable revision for a catalog's capability content.
 *
 * Catalog producers may enumerate models, maps, and values in different
 * orders across reconnects. Canonicalizing those collections before hashing
 * makes a revision identify capability content rather than transport order.
 */
export function deriveCapabilityRevision(entry: RuntimeCatalogEntry): string {
  const canonical = JSON.stringify({
    models: [...new Set(entry.models)].sort(compareStrings),
    variants: canonicalMap(entry.variants),
    supportsReasoningEffort: entry.supportsReasoningEffort ?? null,
    complete: entry.complete ?? null,
  })
  return createHash('sha256').update(canonical).digest('hex')
}
