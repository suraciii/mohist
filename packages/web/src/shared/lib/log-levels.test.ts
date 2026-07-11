import { describe, it, expect } from 'vitest'
import { LEVEL_COLORS, LEVEL_CHIP_COLORS, ALL_LEVELS } from './log-levels'

describe('LEVEL_COLORS', () => {
  it('has ERROR with red-600 classes', () => {
    expect(LEVEL_COLORS.ERROR).toBe('text-red-600 bg-red-50')
  })

  it('has WARN with yellow-600 classes', () => {
    expect(LEVEL_COLORS.WARN).toBe('text-yellow-600 bg-yellow-50')
  })

  it('has INFO with blue-600 classes', () => {
    expect(LEVEL_COLORS.INFO).toBe('text-blue-600 bg-blue-50')
  })

  it('has DEBUG with gray-500 classes', () => {
    expect(LEVEL_COLORS.DEBUG).toBe('text-gray-500 bg-gray-100')
  })

  it('has exactly 4 entries (ERROR, WARN, INFO, DEBUG)', () => {
    expect(Object.keys(LEVEL_COLORS)).toHaveLength(4)
    expect(Object.keys(LEVEL_COLORS).sort()).toEqual(['DEBUG', 'ERROR', 'INFO', 'WARN'])
  })

  it('ERROR uses red-600 NOT red-500', () => {
    expect(LEVEL_COLORS.ERROR).toContain('text-red-600')
    expect(LEVEL_COLORS.ERROR).not.toContain('red-500')
  })

  it('WARN uses yellow-600 NOT yellow-500', () => {
    expect(LEVEL_COLORS.WARN).toContain('text-yellow-600')
    expect(LEVEL_COLORS.WARN).not.toContain('yellow-500')
  })
})

describe('LEVEL_CHIP_COLORS', () => {
  it('has ERROR with red chip classes', () => {
    expect(LEVEL_CHIP_COLORS.ERROR).toBe('bg-red-100 text-red-700 border-red-200')
  })

  it('has WARN with yellow chip classes', () => {
    expect(LEVEL_CHIP_COLORS.WARN).toBe('bg-yellow-100 text-yellow-700 border-yellow-200')
  })

  it('has INFO with blue chip classes', () => {
    expect(LEVEL_CHIP_COLORS.INFO).toBe('bg-blue-100 text-blue-700 border-blue-200')
  })

  it('has DEBUG with gray chip classes', () => {
    expect(LEVEL_CHIP_COLORS.DEBUG).toBe('bg-gray-100 text-gray-600 border-gray-200')
  })

  it('has exactly 4 entries matching ALL_LEVELS', () => {
    expect(Object.keys(LEVEL_CHIP_COLORS)).toHaveLength(4)
    for (const level of ALL_LEVELS) {
      expect(LEVEL_CHIP_COLORS[level]).toBeDefined()
    }
  })

  it('each chip color contains bg- text- and border- classes', () => {
    for (const level of ALL_LEVELS) {
      const color = LEVEL_CHIP_COLORS[level]
      expect(color).toMatch(/^bg-\S+ \S+ border-\S+$/)
    }
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
