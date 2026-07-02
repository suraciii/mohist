import { describe, expect, it } from 'vitest'
import { deriveQualityVerdict, qualityIsFavorable } from './quality'
import type { QualityMetricsResponse } from '../../../entities/issue'

function makeQuality(args: {
  currentRate?: number | null
  currentSampleCount?: number
  previousRate?: number | null
  previousSampleCount?: number
}): QualityMetricsResponse {
  const base: QualityMetricsResponse = {
    window7d: { from: '2026-06-23T00:00:00Z', to: '2026-06-30T00:00:00Z', sampleCount: 0, firstTimeRightRate: null, stages: [] },
    window30d: {
      from: '2026-06-01T00:00:00Z',
      to: '2026-07-01T00:00:00Z',
      sampleCount: args.currentSampleCount ?? 0,
      firstTimeRightRate: args.currentRate ?? null,
      stages: [],
    },
  }
  if (args.previousRate !== undefined) base.previousFirstTimeRightRate = args.previousRate
  if (args.previousSampleCount !== undefined) base.previousSampleCount = args.previousSampleCount
  return base
}

describe('quality verdict: insufficient', () => {
  it('returns insufficient when quality is undefined', () => {
    const v = deriveQualityVerdict({ quality: undefined })
    expect(v.kind).toBe('insufficient')
    expect(v.label).toBe('质量信号')
  })

  it('returns insufficient when current sampleCount is 0', () => {
    const v = deriveQualityVerdict({ quality: makeQuality({}) })
    expect(v.kind).toBe('insufficient')
  })

  it('returns insufficient when current rate is null', () => {
    const v = deriveQualityVerdict({
      quality: makeQuality({ currentSampleCount: 4, currentRate: null }),
    })
    expect(v.kind).toBe('insufficient')
  })
})

describe('quality verdict: currentOnly when no previous baseline', () => {
  it('returns currentOnly when previousSampleCount is 0', () => {
    const v = deriveQualityVerdict({
      quality: makeQuality({ currentRate: 0.73, currentSampleCount: 4, previousSampleCount: 0 }),
    })
    expect(v.kind).toBe('currentOnly')
  })

  it('returns currentOnly when previousFirstTimeRightRate is null', () => {
    const v = deriveQualityVerdict({
      quality: makeQuality({ currentRate: 0.73, currentSampleCount: 4, previousSampleCount: 4, previousRate: null }),
    })
    expect(v.kind).toBe('currentOnly')
  })

  it('returns currentOnly when previous fields are undefined (older server)', () => {
    const v = deriveQualityVerdict({
      quality: makeQuality({ currentRate: 0.73, currentSampleCount: 4 }),
    })
    expect(v.kind).toBe('currentOnly')
  })
})

describe('quality verdict: full', () => {
  it('reports down + pp=-8 when current=73%, previous=81%', () => {
    const v = deriveQualityVerdict({
      quality: makeQuality({
        currentRate: 0.73,
        currentSampleCount: 10,
        previousRate: 0.81,
        previousSampleCount: 8,
      }),
    })
    expect(v.kind).toBe('full')
    if (v.kind === 'full') {
      expect(v.direction).toBe('down')
      expect(v.magnitude).toBe(-8)
      expect(v.unit).toBe('percentagePoints')
      expect(v.polarity).toBe('up-favorable')
      expect(qualityIsFavorable(v)).toBe(false)
    }
  })

  it('reports up + pp=5 when current=85%, previous=80%', () => {
    const v = deriveQualityVerdict({
      quality: makeQuality({
        currentRate: 0.85,
        currentSampleCount: 10,
        previousRate: 0.80,
        previousSampleCount: 8,
      }),
    })
    expect(v.kind).toBe('full')
    if (v.kind === 'full') {
      expect(v.direction).toBe('up')
      expect(v.magnitude).toBe(5)
      expect(qualityIsFavorable(v)).toBe(true)
    }
  })

  it('reports flat when current equals previous', () => {
    const v = deriveQualityVerdict({
      quality: makeQuality({
        currentRate: 0.8,
        currentSampleCount: 10,
        previousRate: 0.8,
        previousSampleCount: 10,
      }),
    })
    expect(v.kind).toBe('full')
    if (v.kind === 'full') {
      expect(v.direction).toBe('flat')
      expect(v.magnitude).toBe(0)
    }
  })
})