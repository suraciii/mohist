// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { describe, expect, it } from 'vitest'
import { STATUS_PILL_PAIRS } from './IssueCard'

function srgbChannel(c: number): number {
  const s = c / 255
  return s <= 0.03928 ? s / 12.92 : Math.pow((s + 0.055) / 1.055, 2.4)
}

function relativeLuminance(hex: string): number {
  const m = /^#?([0-9a-f]{6})$/i.exec(hex.trim())
  if (!m) throw new Error(`Invalid hex: ${hex}`)
  const v = m[1]!
  const r = parseInt(v.slice(0, 2), 16)
  const g = parseInt(v.slice(2, 4), 16)
  const b = parseInt(v.slice(4, 6), 16)
  return 0.2126 * srgbChannel(r) + 0.7152 * srgbChannel(g) + 0.0722 * srgbChannel(b)
}

function contrastRatio(a: string, b: string): number {
  const la = relativeLuminance(a)
  const lb = relativeLuminance(b)
  const [hi, lo] = la >= lb ? [la, lb] : [lb, la]
  return (hi + 0.05) / (lo + 0.05)
}

const VARIANTS = ['blocked', 'cancelled', 'approval', 'running', 'waiting', 'drift'] as const

describe('StatusPill background/text pairs', () => {
  it.each(VARIANTS)(
    '%s pill background/text combination reaches WCAG AA (>=4.5:1) contrast',
    (variant) => {
      const pair = STATUS_PILL_PAIRS[variant]
      expect(pair).toBeDefined()
      const ratio = contrastRatio(pair.bg, pair.text)
      expect(ratio).toBeGreaterThanOrEqual(4.5)
    },
  )

  it('exposes a pair for every documented indicator', () => {
    for (const v of VARIANTS) {
      expect(STATUS_PILL_PAIRS[v]).toBeDefined()
      expect(STATUS_PILL_PAIRS[v].bg).toMatch(/^#[0-9a-f]{6}$/i)
      expect(STATUS_PILL_PAIRS[v].text).toMatch(/^#[0-9a-f]{6}$/i)
    }
  })
})
