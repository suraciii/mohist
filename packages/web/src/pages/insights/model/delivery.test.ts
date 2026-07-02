import { describe, expect, it } from 'vitest'
import {
  deliveryIsFavorable,
  deliverySlowestStageName,
  deriveDeliveryVerdict,
  formatCycleDays,
} from './delivery'
import type {
  DeliveryTimeMetricsResponse,
  DeliveryTimePointDto,
  StageDurationMetricsResponse,
  StageDurationStageDto,
} from '../../../entities/issue'

function makeDelivery(
  points: DeliveryTimePointDto[],
  previousCycleDays?: number | null,
): DeliveryTimeMetricsResponse {
  const base: DeliveryTimeMetricsResponse = { points }
  if (previousCycleDays !== undefined) base.previousCycleDays = previousCycleDays
  return base
}

function makeStage(stage: string, averageSeconds: number | null, sampleCount = 1): StageDurationStageDto {
  return { stage, sampleCount, averageSeconds, medianSeconds: null }
}

function makeStageDuration(stages: StageDurationStageDto[]): StageDurationMetricsResponse {
  return {
    window: { from: '2026-06-01T00:00:00+00:00', to: '2026-07-01T00:00:00+00:00' },
    stages,
    flowEfficiencyRatio: null,
    waitBreakout: null,
  }
}

describe('delivery verdict: insufficient', () => {
  it('returns insufficient when deliveryTime is undefined', () => {
    const v = deriveDeliveryVerdict({ deliveryTime: undefined, stageDuration: undefined })
    expect(v.kind).toBe('insufficient')
  })

  it('returns insufficient when no point has a non-null cycleDays', () => {
    const v = deriveDeliveryVerdict({
      deliveryTime: makeDelivery([
        { issueNumber: 1, completedAt: '2026-06-10T00:00:00Z', leadDays: 1, cycleDays: null },
      ]),
      stageDuration: undefined,
    })
    expect(v.kind).toBe('insufficient')
  })
})

describe('delivery verdict: currentOnly when no previous baseline', () => {
  it('returns currentOnly when previousCycleDays is undefined', () => {
    const v = deriveDeliveryVerdict({
      deliveryTime: makeDelivery([
        { issueNumber: 1, completedAt: '2026-06-10T00:00:00Z', leadDays: 1, cycleDays: 1 },
      ]),
      stageDuration: undefined,
    })
    expect(v.kind).toBe('currentOnly')
  })

  it('returns currentOnly when previousCycleDays is null', () => {
    const v = deriveDeliveryVerdict({
      deliveryTime: makeDelivery(
        [{ issueNumber: 1, completedAt: '2026-06-10T00:00:00Z', leadDays: 1, cycleDays: 1 }],
        null,
      ),
      stageDuration: undefined,
    })
    expect(v.kind).toBe('currentOnly')
  })
})

describe('delivery verdict: full', () => {
  it('reports down + relative % when current < previous (faster, favorable)', () => {
    const v = deriveDeliveryVerdict({
      deliveryTime: makeDelivery(
        [
          { issueNumber: 1, completedAt: '2026-06-10T00:00:00Z', leadDays: 1, cycleDays: 5.2 / 24 },
          { issueNumber: 2, completedAt: '2026-06-12T00:00:00Z', leadDays: 1, cycleDays: 5.2 / 24 },
        ],
        6.3 / 24,
      ),
      stageDuration: undefined,
    })
    expect(v.kind).toBe('full')
    if (v.kind === 'full') {
      expect(v.direction).toBe('down')
      expect(v.unit).toBe('percent')
      expect(v.polarity).toBe('down-favorable')
      expect(v.magnitude).toBeGreaterThan(15)
      expect(v.magnitude).toBeLessThan(20)
      expect(deliveryIsFavorable(v)).toBe(true)
    }
  })

  it('reports up + relative % when current > previous (slower, unfavorable)', () => {
    const v = deriveDeliveryVerdict({
      deliveryTime: makeDelivery(
        [{ issueNumber: 1, completedAt: '2026-06-10T00:00:00Z', leadDays: 1, cycleDays: 6.3 / 24 }],
        5.2 / 24,
      ),
      stageDuration: undefined,
    })
    expect(v.kind).toBe('full')
    if (v.kind === 'full') {
      expect(v.direction).toBe('up')
      expect(v.polarity).toBe('down-favorable')
      expect(deliveryIsFavorable(v)).toBe(false)
    }
  })

  it('reports flat for float-near-equal cycle times', () => {
    const v = deriveDeliveryVerdict({
      deliveryTime: makeDelivery(
        [{ issueNumber: 1, completedAt: '2026-06-10T00:00:00Z', leadDays: 1, cycleDays: 0.3 }],
        0.2 + 0.1, // classic float noise
      ),
      stageDuration: undefined,
    })
    expect(v.kind).toBe('full')
    if (v.kind === 'full') {
      expect(v.direction).toBe('flat')
    }
  })

  it('averages the cycle days across multiple points', () => {
    const v = deriveDeliveryVerdict({
      deliveryTime: makeDelivery(
        [
          { issueNumber: 1, completedAt: '2026-06-10T00:00:00Z', leadDays: 1, cycleDays: 1 },
          { issueNumber: 2, completedAt: '2026-06-12T00:00:00Z', leadDays: 1, cycleDays: 1 },
        ],
        1,
      ),
      stageDuration: undefined,
    })
    expect(v.kind).toBe('full')
    if (v.kind === 'full') {
      expect(v.direction).toBe('flat')
    }
  })
})

describe('delivery verdict: slowest stage', () => {
  it('names the stage with the greatest averageSeconds', () => {
    const v = deriveDeliveryVerdict({
      deliveryTime: makeDelivery(
        [{ issueNumber: 1, completedAt: '2026-06-10T00:00:00Z', leadDays: 1, cycleDays: 1 }],
        1.2,
      ),
      stageDuration: makeStageDuration([
        makeStage('plan', 60),
        makeStage('build', 600),
        makeStage('review', 120),
      ]),
    })
    expect(deliverySlowestStageName(v, {
      deliveryTime: undefined,
      stageDuration: makeStageDuration([
        makeStage('plan', 60),
        makeStage('build', 600),
        makeStage('review', 120),
      ]),
    })).toBe('build')
  })

  it('omits the slowest stage when no stage-duration samples exist', () => {
    const v = deriveDeliveryVerdict({
      deliveryTime: makeDelivery(
        [{ issueNumber: 1, completedAt: '2026-06-10T00:00:00Z', leadDays: 1, cycleDays: 1 }],
        1.2,
      ),
      stageDuration: makeStageDuration([
        makeStage('plan', null, 0),
        makeStage('build', null, 0),
      ]),
    })
    expect(deliverySlowestStageName(v, {
      deliveryTime: undefined,
      stageDuration: makeStageDuration([
        makeStage('plan', null, 0),
        makeStage('build', null, 0),
      ]),
    })).toBeNull()
  })

  it('omits the slowest stage when stageDuration is missing', () => {
    const v = deriveDeliveryVerdict({
      deliveryTime: makeDelivery(
        [{ issueNumber: 1, completedAt: '2026-06-10T00:00:00Z', leadDays: 1, cycleDays: 1 }],
        1.2,
      ),
      stageDuration: undefined,
    })
    expect(deliverySlowestStageName(v, {
      deliveryTime: undefined,
      stageDuration: undefined,
    })).toBeNull()
  })

  it('skips null averages when computing the slowest stage', () => {
    const v = deriveDeliveryVerdict({
      deliveryTime: makeDelivery(
        [{ issueNumber: 1, completedAt: '2026-06-10T00:00:00Z', leadDays: 1, cycleDays: 1 }],
        1.2,
      ),
      stageDuration: makeStageDuration([
        makeStage('plan', null, 0),
        makeStage('build', 200, 5),
        makeStage('review', 100, 4),
      ]),
    })
    expect(deliverySlowestStageName(v, {
      deliveryTime: undefined,
      stageDuration: makeStageDuration([
        makeStage('plan', null, 0),
        makeStage('build', 200, 5),
        makeStage('review', 100, 4),
      ]),
    })).toBe('build')
  })
})

describe('formatCycleDays', () => {
  it('formats <1 day in hours with one decimal', () => {
    expect(formatCycleDays(5.2 / 24)).toBe('5.2h')
  })

  it('formats ≥1 day in days with one decimal', () => {
    expect(formatCycleDays(1)).toBe('1d')
    expect(formatCycleDays(2.5)).toBe('2.5d')
  })
})