import { describe, expect, it } from 'vitest'
import { deriveInvestmentVerdict, investmentBreakdown, investmentIsFavorable } from './investment'
import type { AgentCostMetricDto, AgentCostRollupDto, AgentCostWindowedFigureDto } from '../../../entities/agent'

function makeCost(spend: number | null, spendCount = 5): AgentCostMetricDto {
  return { amount: spend, currency: 'USD', sampleCount: spendCount }
}
function makePerIssue(perIssue: number | null, perIssueCount = 5): AgentCostMetricDto {
  return { amount: perIssue, currency: 'USD', sampleCount: perIssueCount }
}
function makeWindow(spend: AgentCostMetricDto, perIssueCost: AgentCostMetricDto): AgentCostWindowedFigureDto {
  return { spend, perIssueCost }
}
function makeRollup(current?: AgentCostWindowedFigureDto, previous?: AgentCostWindowedFigureDto): AgentCostRollupDto {
  const base: AgentCostRollupDto = {
    totalCost: { amount: 0, currency: 'USD', sampleCount: 0 },
    todayCost: { amount: 0, currency: 'USD', sampleCount: 0 },
    doneIssuesCount: 5,
    costPerShip: { amount: 0, currency: 'USD', sampleCount: 0 },
  }
  if (current) base.currentWindow = current
  if (previous) base.previousWindow = previous
  return base
}

describe('investment verdict: insufficient', () => {
  it('returns insufficient when currentWindow is missing', () => {
    const v = deriveInvestmentVerdict({ cost: makeRollup() })
    expect(v.kind).toBe('insufficient')
  })

  it('returns insufficient when both currentWindow.spend and currentWindow.perIssueCost are empty', () => {
    const v = deriveInvestmentVerdict({
      cost: makeRollup(
        makeWindow(
          { amount: null, currency: 'USD', sampleCount: 0 },
          { amount: null, currency: 'USD', sampleCount: 0 },
        ),
      ),
    })
    expect(v.kind).toBe('insufficient')
  })
})

describe('investment verdict: currentOnly when no previous baseline', () => {
  it('returns currentOnly when both metrics have current but no previous', () => {
    const v = deriveInvestmentVerdict({
      cost: makeRollup(
        makeWindow(makeCost(182), makePerIssue(36)),
      ),
    })
    expect(v.kind).toBe('currentOnly')
  })
})

describe('investment verdict: full', () => {
  it('reports down when spend drops (favorable, ↓ favorable)', () => {
    const v = deriveInvestmentVerdict({
      cost: makeRollup(
        makeWindow(makeCost(182), makePerIssue(36)),
        makeWindow(makeCost(220), makePerIssue(50)),
      ),
    })
    expect(v.kind).toBe('full')
    if (v.kind === 'full') {
      expect(v.direction).toBe('down')
      expect(v.polarity).toBe('down-favorable')
      expect(v.magnitude).toBeGreaterThan(0)
      expect(investmentIsFavorable(v)).toBe(true)
    }
  })

  it('reports up when spend rises (unfavorable)', () => {
    const v = deriveInvestmentVerdict({
      cost: makeRollup(
        makeWindow(makeCost(220), makePerIssue(50)),
        makeWindow(makeCost(182), makePerIssue(36)),
      ),
    })
    expect(v.kind).toBe('full')
    if (v.kind === 'full') {
      expect(v.direction).toBe('up')
      expect(investmentIsFavorable(v)).toBe(false)
    }
  })

  it('promotes to full when at least one sub-metric has full data', () => {
    // spend has full data, perIssueCost has currentOnly (no previous).
    const v = deriveInvestmentVerdict({
      cost: makeRollup(
        makeWindow(makeCost(182), makePerIssue(36)),
        makeWindow(makeCost(220), { amount: null, currency: 'USD', sampleCount: 0 }),
      ),
    })
    expect(v.kind).toBe('full')
  })
})

describe('investmentBreakdown: independent per-metric verdicts', () => {
  it('classifies full when both metrics are full', () => {
    const detail = investmentBreakdown({
      cost: makeRollup(
        makeWindow(makeCost(182), makePerIssue(36)),
        makeWindow(makeCost(220), makePerIssue(50)),
      ),
    })
    expect(detail.kind).toBe('full')
    expect(detail.spend.kind).toBe('full')
    expect(detail.perIssueCost.kind).toBe('full')
  })

  it('classifies partial when only one metric is full', () => {
    const detail = investmentBreakdown({
      cost: makeRollup(
        makeWindow(makeCost(182), makePerIssue(36)),
        makeWindow(makeCost(220), { amount: null, currency: 'USD', sampleCount: 0 }),
      ),
    })
    expect(detail.kind).toBe('partial')
    expect(detail.spend.kind).toBe('full')
    expect(detail.perIssueCost.kind).toBe('currentOnly')
  })

  it('classifies currentOnly when no sub-metric has full data', () => {
    const detail = investmentBreakdown({
      cost: makeRollup(
        makeWindow(makeCost(182), makePerIssue(36)),
      ),
    })
    expect(detail.kind).toBe('currentOnly')
    expect(detail.spend.kind).toBe('currentOnly')
    expect(detail.perIssueCost.kind).toBe('currentOnly')
  })

  it('classifies insufficient when both metrics are empty', () => {
    const detail = investmentBreakdown({
      cost: makeRollup(
        makeWindow(
          { amount: null, currency: 'USD', sampleCount: 0 },
          { amount: null, currency: 'USD', sampleCount: 0 },
        ),
      ),
    })
    expect(detail.kind).toBe('insufficient')
  })

  it('distinguishes genuine zero spend (sampleCount>0, amount=0) from empty', () => {
    const detail = investmentBreakdown({
      cost: makeRollup(
        makeWindow(
          { amount: 0, currency: 'USD', sampleCount: 3 },
          makePerIssue(0),
        ),
        makeWindow(
          { amount: 50, currency: 'USD', sampleCount: 5 },
          makePerIssue(10),
        ),
      ),
    })
    expect(detail.spend.kind).toBe('full')
    expect(detail.spend.direction).toBe('down')
    expect(detail.perIssueCost.kind).toBe('full')
    expect(detail.perIssueCost.direction).toBe('down')
  })
})