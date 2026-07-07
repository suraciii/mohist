import { describe, it, expect } from 'vitest'
import { LEVEL_COLORS, LEVEL_CHIP_COLORS, ALL_LEVELS, getLevelColors, getLevelChipColors } from './log-levels'
import { statusTreatment } from '@/shared/status-presentation'

describe('LEVEL_COLORS', () => {
  it('ERROR routes to the danger family', () => {
    expect(LEVEL_COLORS.ERROR).toBe(statusTreatment('severity', 'ERROR').container)
  })

  it('WARN routes to the warning family', () => {
    expect(LEVEL_COLORS.WARN).toBe(statusTreatment('severity', 'WARN').container)
  })

  it('INFO routes to the info family', () => {
    expect(LEVEL_COLORS.INFO).toBe(statusTreatment('severity', 'INFO').container)
  })

  it('DEBUG routes to the muted family', () => {
    expect(LEVEL_COLORS.DEBUG).toBe(statusTreatment('severity', 'DEBUG').container)
  })

  it('has exactly 4 entries (ERROR, WARN, INFO, DEBUG)', () => {
    expect(Object.keys(LEVEL_COLORS)).toHaveLength(4)
    expect(Object.keys(LEVEL_COLORS).sort()).toEqual(['DEBUG', 'ERROR', 'INFO', 'WARN'])
  })

  it('contains no raw Tailwind palette classes', () => {
    const palette = ['emerald', 'green-', 'amber-', 'red-', 'orange-', 'gray-', 'blue-', 'yellow-']
    for (const level of ALL_LEVELS) {
      for (const p of palette) {
        expect(LEVEL_COLORS[level].includes(p), `palette ${p} should not appear for ${level}`).toBe(false)
      }
    }
  })

  it('each level resolves through the shared status-presentation layer', () => {
    const cases: Array<[typeof ALL_LEVELS[number], string]> = [
      ['ERROR', 'danger'],
      ['WARN', 'warning'],
      ['INFO', 'info'],
      ['DEBUG', 'muted'],
    ]
    for (const [level, expectedFamily] of cases) {
      const treatment = statusTreatment('severity', level)
      expect(treatment.family).toBe(expectedFamily)
      expect(LEVEL_COLORS[level]).toBe(treatment.container)
    }
  })
})

describe('LEVEL_CHIP_COLORS', () => {
  it('ERROR chip routes to the danger family', () => {
    expect(LEVEL_CHIP_COLORS.ERROR).toContain(statusTreatment('severity', 'ERROR').container)
    expect(LEVEL_CHIP_COLORS.ERROR).toContain('border-danger-border')
  })

  it('WARN chip routes to the warning family', () => {
    expect(LEVEL_CHIP_COLORS.WARN).toContain(statusTreatment('severity', 'WARN').container)
    expect(LEVEL_CHIP_COLORS.WARN).toContain('border-warning-border')
  })

  it('INFO chip routes to the info family', () => {
    expect(LEVEL_CHIP_COLORS.INFO).toContain(statusTreatment('severity', 'INFO').container)
    expect(LEVEL_CHIP_COLORS.INFO).toContain('border-info-border')
  })

  it('DEBUG chip routes to the muted family', () => {
    expect(LEVEL_CHIP_COLORS.DEBUG).toContain(statusTreatment('severity', 'DEBUG').container)
    expect(LEVEL_CHIP_COLORS.DEBUG).toContain('border-border')
  })

  it('has exactly 4 entries matching ALL_LEVELS', () => {
    expect(Object.keys(LEVEL_CHIP_COLORS)).toHaveLength(4)
    for (const level of ALL_LEVELS) {
      expect(LEVEL_CHIP_COLORS[level]).toBeDefined()
    }
  })

  it('each chip color contains bg- and border- classes from the shared layer', () => {
    for (const level of ALL_LEVELS) {
      const color = LEVEL_CHIP_COLORS[level]
      expect(color).toMatch(/^[\S ]+border(-\S+)?\s/)
    }
  })
})

describe('getLevelColors helper', () => {
  it('returns the family container for a known level', () => {
    expect(getLevelColors('ERROR')).toBe(statusTreatment('severity', 'ERROR').container)
    expect(getLevelColors('WARN')).toBe(statusTreatment('severity', 'WARN').container)
    expect(getLevelColors('INFO')).toBe(statusTreatment('severity', 'INFO').container)
    expect(getLevelColors('DEBUG')).toBe(statusTreatment('severity', 'DEBUG').container)
  })

  it('returns the muted treatment for unknown / null / undefined input (no throw)', () => {
    expect(getLevelColors('UNKNOWN')).toBe(statusTreatment('severity', 'DEBUG').container)
    expect(getLevelColors(null)).toBe(statusTreatment('severity', 'DEBUG').container)
    expect(getLevelColors(undefined)).toBe(statusTreatment('severity', 'DEBUG').container)
  })
})

describe('getLevelChipColors helper', () => {
  it('returns a bordered variant of the family container', () => {
    const errorChip = getLevelChipColors('ERROR')
    expect(errorChip).toContain('bg-danger-subtle')
    expect(errorChip).toContain('text-danger')
    expect(errorChip).toContain('border-danger-border')
  })
})

describe('ALL_LEVELS', () => {
  it('is ordered DEBUG, INFO, WARN, ERROR', () => {
    expect([...ALL_LEVELS]).toEqual(['DEBUG', 'INFO', 'WARN', 'ERROR'])
  })

  it('has exactly 4 levels', () => {
    expect(ALL_LEVELS).toHaveLength(4)
  })
})