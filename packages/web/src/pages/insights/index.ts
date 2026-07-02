export { InsightsPage } from './ui/InsightsPage'
export { SignalSummary } from './ui/SignalSummary'
export {
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
  relativeDelta,
} from './model'
export type {
  FullVerdict,
  Verdict,
  VerdictDirection,
  VerdictPolarity,
  InvestmentVerdictDetails,
  InvestmentSubVerdict,
  SignalInputs,
  SignalSummaryModel,
} from './model'