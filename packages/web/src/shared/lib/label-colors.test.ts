import { describe, expect, it } from 'vitest'
import {
  getPriorityStyle,
  getPriorityStripColor,
  getStripColor,
} from './label-colors'

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

const PRIORITIES = ['p0', 'p1', 'p2', 'p3', 'p4'] as const

describe('getPriorityStripColor', () => {
  it('returns a hex value for every known priority', () => {
    for (const p of PRIORITIES) {
      const hex = getPriorityStripColor(p)
      expect(hex).toMatch(/^#[0-9a-f]{6}$/i)
    }
  })

  it('returns distinct hexes for p0..p4 (no two priorities share a hue)', () => {
    const hexes = PRIORITIES.map((p) => getPriorityStripColor(p))
    const unique = new Set(hexes)
    expect(unique.size).toBe(PRIORITIES.length)
  })

  it('falls back to a gray for null priority', () => {
    const hex = getPriorityStripColor(null)
    expect(hex).toMatch(/^#[0-9a-f]{6}$/i)
    expect(getPriorityStripColor(null)).toBe(getPriorityStripColor('p4'))
  })

  it('falls back to a gray for undefined priority', () => {
    expect(getPriorityStripColor(undefined)).toBe(getPriorityStripColor('p4'))
  })

  it('falls back to a gray for an unknown priority string', () => {
    expect(getPriorityStripColor('p9')).toBe(getPriorityStripColor('p4'))
    expect(getPriorityStripColor('')).toBe(getPriorityStripColor('p4'))
  })
})

describe('PRIORITY_COLORS (via getPriorityStyle)', () => {
  it('uses different colors for p0 and p1 (dedup)', () => {
    const a = getPriorityStyle('p0')
    const b = getPriorityStyle('p1')
    expect(`${a.bg}|${a.text}`).not.toBe(`${b.bg}|${b.text}`)
  })

  it('returns a distinct background for every priority', () => {
    const bgs = PRIORITIES.map((p) => getPriorityStyle(p).bg)
    expect(new Set(bgs).size).toBe(PRIORITIES.length)
  })

  it.each(PRIORITIES)(
    'p%s chip background/text pair meets WCAG AA (>=4.5:1) contrast',
    (p) => {
      const { bg, text } = getPriorityStyle(p)
      const ratio = contrastRatio(bg, text)
      expect(ratio).toBeGreaterThanOrEqual(4.5)
    },
  )
})

describe('getStripColor (label-based helper is unchanged)', () => {
  it('returns the label-driven hex for a known type label', () => {
    expect(getStripColor({ bug: 'true' })).toBe('#ef4444')
  })

  it('returns the default gray for missing/unknown labels', () => {
    expect(getStripColor(null)).toBe('#6b7280')
    expect(getStripColor({})).toBe('#6b7280')
    expect(getStripColor({ domain: 'agent' })).toBe('#6b7280')
  })
})