import type { QualityMetricsResponse } from '../../../entities/issue'
import {
  type FullVerdict,
  type Verdict,
  directionForDoubles,
  isFavorable,
} from './verdict'

/**
 * 质量信号 verdict — first-time-right rate.
 *
 * Sources the current rate from `quality.window30d.firstTimeRightRate`
 * and the previous rate from the new `previousFirstTimeRightRate`
 * field. The empty-result discriminator is the `previousSampleCount`
 * field — `0` ⟹ no baseline ⟹ `currentOnly` (hide trend); a genuine
 * zero/one rate requires `previousSampleCount > 0` and a non-null rate.
 *
 * Magnitude type: percentage-point delta (D6). Polarity: ↑ favorable.
 */
export interface QualityInputs {
  quality: QualityMetricsResponse | null | undefined
}

function emptyVerdict(): Verdict {
  return { kind: 'insufficient', label: '质量信号' }
}

function roundToInt(n: number): number {
  return Math.round(n)
}

export function deriveQualityVerdict(inputs: QualityInputs): Verdict {
  const quality = inputs.quality
  const currentRate = quality?.window30d?.firstTimeRightRate
  const currentSampleCount = quality?.window30d?.sampleCount ?? 0

  if (currentSampleCount === 0 || currentRate == null) {
    return emptyVerdict()
  }

  const previousSampleCount = quality?.previousSampleCount ?? 0
  const previousRate = quality?.previousFirstTimeRightRate

  if (previousSampleCount === 0 || previousRate == null) {
    return {
      kind: 'currentOnly',
      label: '质量信号',
    }
  }

  const direction = directionForDoubles(currentRate, previousRate)
  const ppDelta = roundToInt((currentRate - previousRate) * 100)

  const full: FullVerdict = {
    kind: 'full',
    label: '质量信号',
    direction,
    magnitude: ppDelta,
    unit: 'percentagePoints',
    polarity: 'up-favorable',
  }
  return full
}

export function qualityIsFavorable(verdict: Verdict): boolean | null {
  if (verdict.kind !== 'full') return null
  return isFavorable(verdict.direction, verdict.polarity)
}