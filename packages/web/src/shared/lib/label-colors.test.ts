import { describe, expect, it } from 'vitest'
import {
  getPriorityStyle,
  getPriorityStripColor,
  getRiskStyle,
  getStripColor,
} from './label-colors'

const PRIORITIES = ['p0', 'p1', 'p2', 'p3', 'p4'] as const

describe('getPriorityStripColor', () => {
  it('returns a Tailwind utility class string for every known priority', () => {
    for (const p of PRIORITIES) {
      const cls = getPriorityStripColor(p)
      expect(cls).toMatch(/^[\w:/-]+(?:\s+[\w:/-]+)+$/)
      expect(cls).toMatch(/^border-l-/)
      expect(cls).toMatch(/dark:border-l-/)
    }
  })

  it('returns distinct class strings for p0..p4 (no two priorities share a hue)', () => {
    const classes = PRIORITIES.map((p) => getPriorityStripColor(p))
    const unique = new Set(classes)
    expect(unique.size).toBe(PRIORITIES.length)
  })

  it('falls back to the gray priority strip class for null priority', () => {
    expect(getPriorityStripColor(null)).toBe(getPriorityStripColor('p4'))
  })

  it('falls back to the gray priority strip class for undefined priority', () => {
    expect(getPriorityStripColor(undefined)).toBe(getPriorityStripColor('p4'))
  })

  it('falls back to the gray priority strip class for an unknown priority string', () => {
    expect(getPriorityStripColor('p9')).toBe(getPriorityStripColor('p4'))
    expect(getPriorityStripColor('')).toBe(getPriorityStripColor('p4'))
  })

  it('contains no inline hex literals (priority strip palette is hex-free)', () => {
    for (const p of PRIORITIES) {
      const cls = getPriorityStripColor(p)
      expect(cls).not.toMatch(/#[0-9a-f]{3,8}/i)
    }
  })

  it('preserves the ordinal hue family across light and dark themes', () => {
    expect(getPriorityStripColor('p0')).toMatch(/red/)
    expect(getPriorityStripColor('p1')).toMatch(/orange/)
    expect(getPriorityStripColor('p2')).toMatch(/yellow/)
    expect(getPriorityStripColor('p3')).toMatch(/green/)
    expect(getPriorityStripColor('p4')).toMatch(/gray/)
  })
})

describe('PRIORITY_COLORS (via getPriorityStyle)', () => {
  it('uses different class sets for p0 and p1 (dedup)', () => {
    const a = getPriorityStyle('p0')
    const b = getPriorityStyle('p1')
    expect(a.className).not.toBe(b.className)
  })

  it('returns a distinct className for every priority', () => {
    const classNames = PRIORITIES.map((p) => getPriorityStyle(p).className)
    expect(new Set(classNames).size).toBe(PRIORITIES.length)
  })

  it('contains no inline hex literals (priority chip palette is hex-free)', () => {
    for (const p of PRIORITIES) {
      const { className } = getPriorityStyle(p)
      expect(className).not.toMatch(/#[0-9a-f]{3,8}/i)
    }
  })

  it('includes a `dark:` counterpart for every priority chip (dark-mode-aware)', () => {
    for (const p of PRIORITIES) {
      const { className } = getPriorityStyle(p)
      expect(className).toMatch(/dark:/)
    }
  })

  it('preserves the ordinal priority hue in both themes (red/orange/yellow/green/gray)', () => {
    expect(getPriorityStyle('p0').className).toMatch(/red/)
    expect(getPriorityStyle('p1').className).toMatch(/orange/)
    expect(getPriorityStyle('p2').className).toMatch(/yellow/)
    expect(getPriorityStyle('p3').className).toMatch(/green/)
    expect(getPriorityStyle('p4').className).toMatch(/gray/)
  })
})

describe('RISK_COLORS (via getRiskStyle)', () => {
  it('routes risk levels through the mandated semantic families', () => {
    expect(getRiskStyle('low').className).toContain('bg-success-subtle')
    expect(getRiskStyle('low').className).toContain('text-success')

    expect(getRiskStyle('medium').className).toContain('bg-warning-subtle')
    expect(getRiskStyle('medium').className).toContain('text-warning')

    expect(getRiskStyle('high').className).toContain('bg-danger-subtle')
    expect(getRiskStyle('high').className).toContain('text-danger')
  })

  it('contains no inline hex literals (risk palette is hex-free)', () => {
    for (const r of ['low', 'medium', 'high']) {
      const { className } = getRiskStyle(r)
      expect(className).not.toMatch(/#[0-9a-f]{3,8}/i)
    }
  })

  it('falls back to a muted treatment for unknown risk levels', () => {
    const { className } = getRiskStyle('unknown-risk')
    expect(className).toContain('bg-muted')
  })
})

describe('getStripColor (label-based helper)', () => {
  it('returns the family foreground class for a known type label', () => {
    expect(getStripColor({ bug: 'true' })).toBe('bg-danger')
    expect(getStripColor({ feature: 'true' })).toBe('bg-success')
    expect(getStripColor({ enhancement: 'true' })).toBe('bg-info')
    expect(getStripColor({ 'tech-debt': 'true' })).toBe('bg-muted-foreground')
    expect(getStripColor({ performance: 'true' })).toBe('bg-warning')
  })

  it('returns the default muted-foreground class for missing/unknown labels', () => {
    expect(getStripColor(null)).toBe('bg-muted-foreground')
    expect(getStripColor({})).toBe('bg-muted-foreground')
    expect(getStripColor({ domain: 'agent' })).toBe('bg-muted-foreground')
  })

  it('contains no inline hex literals (type strip palette is hex-free)', () => {
    expect(getStripColor({ bug: 'true' })).not.toMatch(/#[0-9a-f]{3,8}/i)
    expect(getStripColor(null)).not.toMatch(/#[0-9a-f]{3,8}/i)
  })

  it('falls back through STRIP_PRIORITY for array inputs', () => {
    expect(getStripColor(['unknown', 'feature'])).toBe('bg-success')
    expect(getStripColor(['tech-debt'])).toBe('bg-muted-foreground')
  })
})