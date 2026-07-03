import type {
  CompletionTrendResponse,
  DeliveryTimeMetricsResponse,
  QualityMetricsResponse,
  StageDurationMetricsResponse,
} from '../../../entities/issue'
import type { AgentCostRollupDto } from '../../../entities/agent'
import { deriveThroughputVerdict } from './throughput'
import { deriveDeliveryVerdict } from './delivery'
import { deriveQualityVerdict } from './quality'
import { deriveInvestmentVerdict, investmentBreakdown } from './investment'
import type { Verdict } from './verdict'
import type { InvestmentVerdictDetails } from './investment'

export {
  type FullVerdict,
  type Verdict,
  type VerdictDirection,
  type VerdictPolarity,
  directionForCounts,
  directionForDoubles,
  isFavorable,
  isFlatDouble,
  relativeDelta,
  DOUBLE_RELATIVE_TOLERANCE,
} from './verdict'
export { deriveThroughputVerdict } from './throughput'
export { deriveDeliveryVerdict, deliverySlowestStageName, formatCycleDays } from './delivery'
export { deriveQualityVerdict } from './quality'
export { deriveInvestmentVerdict, investmentBreakdown } from './investment'
export type { InvestmentVerdictDetails, InvestmentSubVerdict } from './investment'
export {
  DEFAULT_INSIGHTS_RANGE,
  INSIGHTS_RANGES,
  isInsightsRange,
  type InsightsRange,
} from './insights-range'

export interface SignalInputs {
  completion: CompletionTrendResponse | null | undefined
  deliveryTime: DeliveryTimeMetricsResponse | null | undefined
  quality: QualityMetricsResponse | null | undefined
  cost: AgentCostRollupDto | null | undefined
  stageDuration: StageDurationMetricsResponse | null | undefined
}

export interface SignalSummaryModel {
  throughput: Verdict
  delivery: Verdict
  quality: Verdict
  investment: Verdict
  investmentDetails: InvestmentVerdictDetails
  slowestStage: string | null
}

/**
 * Compose the four verdicts from the four metrics surfaces. Each verdict
 * degrades independently per design D5 — never throws, never fabricates
 * data, always returns a populated `Verdict` union.
 */
export function deriveSignalSummary(inputs: SignalInputs): SignalSummaryModel {
  const throughput = deriveThroughputVerdict({ completion: inputs.completion })
  const delivery = deriveDeliveryVerdict({
    deliveryTime: inputs.deliveryTime,
    stageDuration: inputs.stageDuration,
  })
  const quality = deriveQualityVerdict({ quality: inputs.quality })
  const investment = deriveInvestmentVerdict({ cost: inputs.cost })
  const investmentDetails = investmentBreakdown({ cost: inputs.cost })

  const slowestStage =
    delivery.kind === 'insufficient'
      ? null
      : deriveSlowestStageName(inputs.stageDuration)

  return { throughput, delivery, quality, investment, investmentDetails, slowestStage }
}

function deriveSlowestStageName(
  stageDuration: StageDurationMetricsResponse | null | undefined,
): string | null {
  if (!stageDuration || !stageDuration.stages) return null
  let best: { stage: string; avg: number } | null = null
  for (const s of stageDuration.stages) {
    if (s.averageSeconds == null) continue
    if (best == null || s.averageSeconds > best.avg) {
      best = { stage: s.stage, avg: s.averageSeconds }
    }
  }
  return best?.stage ?? null
}