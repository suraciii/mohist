/**
 * Format a number compactly for display (e.g. 12400 → "12.4k")
 */
export function formatCompact(n: number | null | undefined): string {
  if (n == null || Number.isNaN(n)) return ''
  const abs = Math.abs(n)
  if (abs >= 1_000_000) {
    return `${(n / 1_000_000).toFixed(1)}M`
  }
  if (abs >= 1_000) {
    return `${(n / 1_000).toFixed(1)}k`
  }
  return String(n)
}

/**
 * Format a cost amount with currency (e.g. 0.18, "USD" → "$0.18")
 */
export function formatCost(amount: number | null | undefined, currency: string | null | undefined): string {
  if (amount == null || Number.isNaN(amount)) return ''
  const symbol = currency === 'USD' ? '$' : currency === 'EUR' ? '€' : currency === 'GBP' ? '£' : currency ? `${currency} ` : ''
  return `${symbol}${amount.toFixed(2)}`
}
