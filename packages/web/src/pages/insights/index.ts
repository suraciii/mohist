export { InsightsPage } from './ui/InsightsPage'
export { SignalSummary } from './ui/SignalSummary'
export {
  DEFAULT_INSIGHTS_RANGE,
  INSIGHTS_RANGES,
  deriveSignalSummary,
  deriveThroughputVerdict,
  deriveDeliveryVerdict,
  deriveQualityVerdict,
  deriveInvestmentVerdict,
  investmentBreakdown,
  deliverySlowestStageName,
  formatCycleDays,
  DOUBLE_RELATIVE_TOLERANCE,
  directionForCounts,
  directionForDoubles,
  isFavorable,
  isFlatDouble,
  isInsightsRange,
  relativeDelta,
} from './model'
export type {
  FullVerdict,
  InsightsRange,
  Verdict,
  VerdictDirection,
  VerdictPolarity,
  InvestmentVerdictDetails,
  InvestmentSubVerdict,
  SignalInputs,
  SignalSummaryModel,
} from './model'