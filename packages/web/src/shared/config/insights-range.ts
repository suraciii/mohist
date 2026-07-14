export const INSIGHTS_RANGES = ['7d', '30d', '90d'] as const
export type InsightsRange = (typeof INSIGHTS_RANGES)[number]

export const DEFAULT_INSIGHTS_RANGE: InsightsRange = '30d'

export function isInsightsRange(value: unknown): value is InsightsRange {
  return typeof value === 'string' && (INSIGHTS_RANGES as readonly string[]).includes(value)
}
