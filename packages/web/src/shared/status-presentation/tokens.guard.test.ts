import { readFileSync } from 'node:fs'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'

import { FAMILIES, TOKENS, type Family, type Oklch, type Theme } from './tokens'

const INDEX_CSS_PATH = join(__dirname, '..', '..', 'app', 'styles', 'index.css')

const FAMILY_OKLCH_RE = /^--(?<family>success|warning|info|danger)(?:-(?<slot>subtle|foreground|border))?:\s*oklch\((?<L>\S+)\s+(?<C>\S+)\s+(?<H>\S+)\);\s*$/m

interface ParsedToken {
  family: Family
  slot: 'subtle' | 'foreground' | 'border' | 'base'
  oklch: Oklch
}

function parseIndexCss(): Record<Theme, Record<Family, ParsedToken[]>> {
  const css = readFileSync(INDEX_CSS_PATH, 'utf8')
  const result: Record<Theme, Record<Family, ParsedToken[]>> = {
    light: { success: [], warning: [], info: [], danger: [] },
    dark: { success: [], warning: [], info: [], danger: [] },
  }

  const rootMatch = css.match(/:root\s*\{([\s\S]*?)\}/)
  const darkMatch = css.match(/\.dark\s*\{([\s\S]*?)\}/)

  if (!rootMatch || !darkMatch) {
    throw new Error('Could not locate :root or .dark rule in index.css')
  }

  const collect = (block: string, theme: Theme) => {
    for (const line of block.split('\n')) {
      const match = line.trim().match(FAMILY_OKLCH_RE)
      if (!match || !match.groups) continue
      const { family, slot } = match.groups as { family: Family; slot?: 'subtle' | 'foreground' | 'border' }
      const oklch: Oklch = {
        L: parseFloat(match.groups.L!),
        C: parseFloat(match.groups.C!),
        H: parseFloat(match.groups.H!),
      }
      result[theme][family].push({ family, slot: slot ?? 'base', oklch })
    }
  }

  collect(rootMatch[1]!, 'light')
  collect(darkMatch[1]!, 'dark')
  return result
}

function approxEqual(a: number, b: number, epsilon = 1e-3): boolean {
  return Math.abs(a - b) <= epsilon
}

describe('shared/status-presentation tokens fixture', () => {
  it('mirrors the four families\' base/subtle/foreground/border values in index.css', () => {
    const parsed = parseIndexCss()

    for (const family of FAMILIES) {
      for (const theme of ['light', 'dark'] as const) {
        const cssEntries = parsed[theme][family]
        const cssBySlot = new Map(cssEntries.map((e) => [e.slot, e.oklch]))
        const fixture = TOKENS[theme][family]

        for (const slot of ['base', 'subtle', 'foreground', 'border'] as const) {
          const cssValue = cssBySlot.get(slot)
          expect(cssValue, `${theme} ${family}-${slot} should exist in CSS`).toBeDefined()
          const fixtureValue = fixture[slot]
          expect(approxEqual(fixtureValue.L, cssValue!.L), `${theme} ${family}-${slot}.L`).toBe(true)
          expect(approxEqual(fixtureValue.C, cssValue!.C), `${theme} ${family}-${slot}.C`).toBe(true)
          expect(approxEqual(fixtureValue.H, cssValue!.H), `${theme} ${family}-${slot}.H`).toBe(true)
        }
      }
    }
  })

  it('lists every family and slot in both themes', () => {
    const parsed = parseIndexCss()
    for (const theme of ['light', 'dark'] as const) {
      for (const family of FAMILIES) {
        const slots = new Set(parsed[theme][family].map((e) => e.slot))
        for (const slot of ['base', 'subtle', 'foreground', 'border'] as const) {
          expect(slots.has(slot), `${theme} ${family}-${slot} present in CSS`).toBe(true)
        }
      }
    }
  })
})