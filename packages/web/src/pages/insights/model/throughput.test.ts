import { describe, expect, it } from 'vitest'
import { deriveThroughputVerdict, throughputIsFavorable } from './throughput'
import type { CompletionTrendResponse } from '../../../entities/issue'

function makeCompletion(
  current?: { completed: number; failed: number; sampleCount: number } | null,
  previous?: { completed: number; failed: number; sampleCount: number } | null,
): CompletionTrendResponse {
  const base: CompletionTrendResponse = {
    bucket: 'day',
    window: { from: '2026-06-01T00:00:00+00:00', to: '2026-07-01T00:00:00+00:00' },
    buckets: [],
  }
  if (current !== undefined) base.currentTotal = current ?? undefined
  if (previous !== undefined) base.previousTotal = previous ?? undefined
  return base
}

describe('throughput verdict: insufficient when no current samples', () => {
  it('returns insufficient when currentTotal is missing', () => {
    const verdict = deriveThroughputVerdict({ completion: makeCompletion() })
    expect(verdict.kind).toBe('insufficient')
    expect(verdict.label).toBe('产出节奏')
  })

  it('returns insufficient when currentTotal.sampleCount is 0', () => {
    const verdict = deriveThroughputVerdict({
      completion: makeCompletion({ completed: 0, failed: 0, sampleCount: 0 }),
    })
    expect(verdict.kind).toBe('insufficient')
  })

  it('returns insufficient when completion is undefined', () => {
    expect(deriveThroughputVerdict({ completion: undefined }).kind).toBe('insufficient')
    expect(deriveThroughputVerdict({ completion: null }).kind).toBe('insufficient')
  })
})

describe('throughput verdict: currentOnly when no previous baseline', () => {
  it('returns currentOnly when current exists but previous is missing', () => {
    const verdict = deriveThroughputVerdict({
      completion: makeCompletion({ completed: 5, failed: 0, sampleCount: 5 }),
    })
    expect(verdict.kind).toBe('currentOnly')
  })

  it('returns currentOnly when previous exists but previous.sampleCount is 0', () => {
    const verdict = deriveThroughputVerdict({
      completion: makeCompletion(
        { completed: 5, failed: 0, sampleCount: 5 },
        { completed: 0, failed: 0, sampleCount: 0 },
      ),
    })
    expect(verdict.kind).toBe('currentOnly')
  })
})

describe('throughput verdict: full with delta', () => {
  it('reports up + magnitude=2 when current=5, previous=3', () => {
    const verdict = deriveThroughputVerdict({
      completion: makeCompletion(
        { completed: 5, failed: 0, sampleCount: 5 },
        { completed: 3, failed: 0, sampleCount: 3 },
      ),
    })
    expect(verdict.kind).toBe('full')
    if (verdict.kind === 'full') {
      expect(verdict.direction).toBe('up')
      expect(verdict.magnitude).toBe(2)
      expect(verdict.unit).toBe('count')
      expect(verdict.polarity).toBe('up-favorable')
      expect(throughputIsFavorable(verdict)).toBe(true)
    }
  })

  it('reports down + negative magnitude when current=2, previous=5', () => {
    const verdict = deriveThroughputVerdict({
      completion: makeCompletion(
        { completed: 2, failed: 0, sampleCount: 2 },
        { completed: 5, failed: 0, sampleCount: 5 },
      ),
    })
    expect(verdict.kind).toBe('full')
    if (verdict.kind === 'full') {
      expect(verdict.direction).toBe('down')
      expect(verdict.magnitude).toBe(-3)
      expect(throughputIsFavorable(verdict)).toBe(false)
    }
  })

  it('reports flat when current equals previous (integer equality)', () => {
    const verdict = deriveThroughputVerdict({
      completion: makeCompletion(
        { completed: 5, failed: 0, sampleCount: 5 },
        { completed: 5, failed: 0, sampleCount: 5 },
      ),
    })
    expect(verdict.kind).toBe('full')
    if (verdict.kind === 'full') {
      expect(verdict.direction).toBe('flat')
      expect(verdict.magnitude).toBe(0)
    }
  })

  it('treats genuine zero completion (sampleCount>0, completed=0) as flat, not insufficient', () => {
    const verdict = deriveThroughputVerdict({
      completion: makeCompletion(
        { completed: 0, failed: 4, sampleCount: 4 },
        { completed: 0, failed: 2, sampleCount: 2 },
      ),
    })
    expect(verdict.kind).toBe('full')
    if (verdict.kind === 'full') {
      expect(verdict.direction).toBe('flat')
      expect(verdict.magnitude).toBe(0)
    }
  })
})