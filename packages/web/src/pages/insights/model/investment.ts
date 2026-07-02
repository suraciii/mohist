import type { AgentCostMetricDto, AgentCostRollupDto, AgentCostWindowedFigureDto } from '../../../entities/agent'
import {
  type FullVerdict,
  type Verdict,
  directionForDoubles,
  isFavorable,
} from './verdict'

/**
 * 投入信号 verdict — spend and per-issue cost.
 *
 * The investment verdict has TWO independent sub-trends (spend and
 * per-issue cost). Each has its own empty-state discriminator
 * (`sampleCount`) and its own direction (D6: ↓ favorable/cheaper, each
 * metric). A sub-trend with no baseline degrades to `currentOnly`; a
 * sub-trend with no current samples marks the whole verdict as
 * `insufficient`. When both sub-trends have full data, both contribute
 * (independent per-metric trends). When only one has full data, the
 * other degrades and the verdict still renders.
 *
 * Magnitude type per metric: relative % change (D6). Polarity: ↓
 * favorable.
 */

export interface InvestmentSubVerdict {
  kind: 'full' | 'currentOnly' | 'empty'
  direction?: 'up' | 'down' | 'flat'
  magnitude?: number
}

export interface InvestmentVerdictDetails {
  kind: 'full' | 'partial' | 'currentOnly' | 'insufficient'
  label: string
  spend: InvestmentSubVerdict
  perIssueCost: InvestmentSubVerdict
}

function emptyMetric(): InvestmentSubVerdict {
  return { kind: 'empty' }
}

function deriveMetric(
  current: AgentCostMetricDto | undefined | null,
  previous: AgentCostMetricDto | undefined | null,
): InvestmentSubVerdict {
  if (!current || current.sampleCount === 0 || current.amount == null) {
    return emptyMetric()
  }
  if (!previous || previous.sampleCount === 0 || previous.amount == null) {
    return { kind: 'currentOnly' }
  }
  const direction = directionForDoubles(current.amount, previous.amount)
  const denom = Math.max(Math.abs(previous.amount), 1e-12)
  const magnitudePct = Math.round((Math.abs(current.amount - previous.amount) / denom) * 1000) / 10
  return { kind: 'full', direction, magnitude: magnitudePct }
}

export interface InvestmentInputs {
  cost: AgentCostRollupDto | null | undefined
}

export function deriveInvestmentVerdict(inputs: InvestmentInputs): Verdict {
  const cost = inputs.cost
  const currentWindow: AgentCostWindowedFigureDto | undefined = cost?.currentWindow
  const previousWindow: AgentCostWindowedFigureDto | undefined = cost?.previousWindow

  if (!currentWindow) {
    return { kind: 'insufficient', label: '投入信号' }
  }

  const spend = deriveMetric(currentWindow.spend, previousWindow?.spend)
  const perIssueCost = deriveMetric(currentWindow.perIssueCost, previousWindow?.perIssueCost)

  if (spend.kind === 'empty' && perIssueCost.kind === 'empty') {
    return { kind: 'insufficient', label: '投入信号' }
  }

  const fullCount = (spend.kind === 'full' ? 1 : 0) + (perIssueCost.kind === 'full' ? 1 : 0)
  if (fullCount === 0) {
    return { kind: 'currentOnly', label: '投入信号' }
  }

  // full or partial. Render as a 'full' Verdict with magnitude = spend
  // magnitude when spend has full data; otherwise perIssueCost's
  // magnitude. Components can choose to show whichever sub-trend has
  // full data. We always promote the verdict to 'full' so the page
  // doesn't degrade the entire investment verdict when one sub-metric
  // has no baseline.
  const primary: InvestmentSubVerdict = spend.kind === 'full' ? spend : perIssueCost
  const detail: FullVerdict = {
    kind: 'full',
    label: '投入信号',
    direction: primary.direction!,
    magnitude: primary.magnitude!,
    unit: 'percent',
    polarity: 'down-favorable',
  }
  return detail
}

export function investmentBreakdown(
  inputs: InvestmentInputs,
): InvestmentVerdictDetails {
  const cost = inputs.cost
  const currentWindow = cost?.currentWindow
  const previousWindow = cost?.previousWindow
  const spend = deriveMetric(currentWindow?.spend, previousWindow?.spend)
  const perIssueCost = deriveMetric(currentWindow?.perIssueCost, previousWindow?.perIssueCost)

  let kind: InvestmentVerdictDetails['kind']
  const hasFull = spend.kind === 'full' || perIssueCost.kind === 'full'
  const hasEmpty = spend.kind === 'empty' && perIssueCost.kind === 'empty'
  if (hasEmpty) kind = 'insufficient'
  else if (hasFull) {
    kind = spend.kind === 'full' && perIssueCost.kind === 'full' ? 'full' : 'partial'
  } else {
    kind = 'currentOnly'
  }
  return { kind, label: '投入信号', spend, perIssueCost }
}

export function investmentIsFavorable(verdict: Verdict): boolean | null {
  if (verdict.kind !== 'full') return null
  return isFavorable(verdict.direction, verdict.polarity)
}