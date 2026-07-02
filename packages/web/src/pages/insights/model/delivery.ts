import type { DeliveryTimeMetricsResponse, StageDurationMetricsResponse, StageDurationStageDto } from '../../../entities/issue'
import {
  type FullVerdict,
  type Verdict,
  directionForDoubles,
  isFavorable,
} from './verdict'

/**
 * 交付效率 verdict — average cycle time + slowest stage.
 *
 * Sources cycle-time averages from the delivery-time surface. The slowest
 * stage is computed locally from the existing stage-duration surface
 * (D3 — no new endpoint): pick the stage with the greatest
 * `averageSeconds`, ignoring stages whose average is null (no samples).
 * When no stage has a non-null average, the slowest-stage clause is
 * omitted per the insufficient-data requirement.
 *
 * Magnitude type: relative % change (D6). Polarity: ↓ favorable (faster).
 */
export interface DeliveryInputs {
  deliveryTime: DeliveryTimeMetricsResponse | null | undefined
  stageDuration: StageDurationMetricsResponse | null | undefined
}

function emptyVerdict(): Verdict {
  return { kind: 'insufficient', label: '交付效率' }
}

interface SlowestStage {
  stage: string
  averageSeconds: number
}

function findSlowestStage(stageDuration: StageDurationMetricsResponse | null | undefined): SlowestStage | null {
  if (!stageDuration || !stageDuration.stages) return null
  let best: SlowestStage | null = null
  for (const entry of stageDuration.stages as StageDurationStageDto[]) {
    if (entry.averageSeconds == null) continue
    if (best == null || entry.averageSeconds > best.averageSeconds) {
      best = { stage: entry.stage, averageSeconds: entry.averageSeconds }
    }
  }
  return best
}

/**
 * Round to one decimal for display; the underlying computation stays
 * full-precision. Relative change stays within DOUBLE_RELATIVE_TOLERANCE
 * because `directionForDoubles` already checks raw values.
 */
function roundToOneDecimal(n: number): number {
  return Math.round(n * 10) / 10
}

export function deriveDeliveryVerdict(inputs: DeliveryInputs): Verdict {
  const points = inputs.deliveryTime?.points ?? []
  const cycleDaysValues = points
    .map((p) => p.cycleDays)
    .filter((v): v is number => v !== null && v !== undefined)

  if (cycleDaysValues.length === 0) {
    return emptyVerdict()
  }

  const sum = cycleDaysValues.reduce((acc, v) => acc + v, 0)
  const currentCycleDays = sum / cycleDaysValues.length

  const previousCycleDays = inputs.deliveryTime?.previousCycleDays

  if (previousCycleDays == null) {
    return {
      kind: 'currentOnly',
      label: '交付效率',
    }
  }

  const direction = directionForDoubles(currentCycleDays, previousCycleDays)

  const relativeChange =
    Math.abs(currentCycleDays - previousCycleDays) /
    Math.max(Math.abs(previousCycleDays), 1e-12)

  const magnitudePct = roundToOneDecimal(relativeChange * 100)

  const full: FullVerdict = {
    kind: 'full',
    label: '交付效率',
    direction,
    magnitude: magnitudePct,
    unit: 'percent',
    polarity: 'down-favorable',
  }
  return full
}

export function deliverySlowestStageName(verdict: Verdict, inputs: DeliveryInputs): string | null {
  if (verdict.kind === 'insufficient') return null
  return findSlowestStage(inputs.stageDuration)?.stage ?? null
}

/**
 * Convenience: render the cycle-time as `5.2h` / `3d` / `1.2h` (days ≥ 1
 * fall through). The verdict copy stays human-readable.
 */
export function formatCycleDays(cycleDays: number): string {
  if (cycleDays >= 1) {
    return `${roundToOneDecimal(cycleDays)}d`
  }
  const hours = cycleDays * 24
  return `${roundToOneDecimal(hours)}h`
}

export function deliveryIsFavorable(verdict: Verdict): boolean | null {
  if (verdict.kind !== 'full') return null
  return isFavorable(verdict.direction, verdict.polarity)
}