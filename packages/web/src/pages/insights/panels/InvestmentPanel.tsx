import { useState } from 'react'
import { ChevronRightIcon } from 'lucide-react'
import { useCostRollup } from '../../../entities/agent'
import type { AgentCostRollupDto } from '../../../entities/agent'
import type { InsightsRange } from '../model/insights-range'
import { formatCost } from '../../../shared/lib/format-compact'

const INVESTMENT_PANEL_TESTID = 'productivity-investment'
const INVESTMENT_TOGGLE_TESTID = 'productivity-investment-toggle'
const INVESTMENT_CALIBER_TESTID = 'productivity-investment-caliber'
const INVESTMENT_EMPTY_TESTID = 'productivity-investment-empty'
const INVESTMENT_CALIBER_LABEL_TESTID = 'productivity-investment-caliber-label'
const INVESTMENT_CALIBER_VALUE_TESTID = 'productivity-investment-caliber-value'
const INVESTMENT_TOTAL_COST_TESTID = 'productivity-investment-total-cost'
const INVESTMENT_COST_PER_SHIP_TESTID = 'productivity-investment-cost-per-ship'
const INVESTMENT_COST_PER_SHIP_EMPTY_TESTID = 'productivity-investment-cost-per-ship-empty'
const INVESTMENT_DONE_ISSUES_TESTID = 'productivity-investment-done-issues'

const INVESTMENT_CALIBER_BASIS =
  'per-project agent/session usage, cumulative across project history'

function isSpendEmpty(rollup: AgentCostRollupDto | undefined): boolean {
  return !rollup || rollup.totalCost.sampleCount === 0
}

export function InvestmentPanel({ range }: { range: InsightsRange }) {
  const [expanded, setExpanded] = useState(false)
  const { data } = useCostRollup(range)
  const rollup = data
  const empty = isSpendEmpty(rollup)
  const totalCost = rollup?.totalCost
  const costPerShip = rollup?.costPerShip
  const doneIssuesCount = rollup?.doneIssuesCount ?? 0

  return (
    <section
      data-testid={INVESTMENT_PANEL_TESTID}
      aria-label="Investment"
      className="rounded-lg border border-border bg-card/50 p-4"
    >
      <div className="flex items-center justify-between mb-3">
        <h3 className="text-xs font-semibold uppercase tracking-wide text-muted-foreground">
          Investment
        </h3>
        <button
          type="button"
          onClick={() => setExpanded((prev) => !prev)}
          aria-expanded={expanded}
          aria-controls="productivity-investment-body"
          data-testid={INVESTMENT_TOGGLE_TESTID}
          className="flex items-center gap-1 text-xs font-medium text-muted-foreground hover:text-foreground"
        >
          <ChevronRightIcon
            className={`h-3.5 w-3.5 transition-transform ${expanded ? 'rotate-90' : ''}`}
            aria-hidden="true"
          />
          {expanded ? 'Collapse' : 'Expand'}
        </button>
      </div>

      {expanded && (
        <div
          id="productivity-investment-body"
          data-testid="productivity-investment-body"
          className="space-y-3"
        >
          <div
            data-testid={INVESTMENT_CALIBER_TESTID}
            className="rounded-md border border-border bg-background px-3 py-2 text-xs"
          >
            <span
              data-testid={INVESTMENT_CALIBER_LABEL_TESTID}
              className="font-semibold uppercase tracking-wide text-muted-foreground mr-2"
            >
              Window / Population
            </span>
            <span
              data-testid={INVESTMENT_CALIBER_VALUE_TESTID}
              className="text-foreground"
            >
              {INVESTMENT_CALIBER_BASIS}
            </span>
          </div>

          {empty ? (
            <p
              data-testid={INVESTMENT_EMPTY_TESTID}
              data-state="empty"
              className="text-sm text-muted-foreground"
            >
              No spend recorded yet — cumulative cost and cost-per-ship appear
              once an agent session reports usage on this project.
            </p>
          ) : (
            <div className="space-y-2">
              <div className="flex items-center justify-between">
                <span className="text-sm text-muted-foreground">
                  Cumulative spend
                </span>
                <span
                  data-testid={INVESTMENT_TOTAL_COST_TESTID}
                  className="text-sm font-medium tabular-nums"
                >
                  {formatCost(totalCost?.amount ?? null, totalCost?.currency ?? null)}
                </span>
              </div>
              <div className="flex items-center justify-between">
                <span className="text-sm text-muted-foreground">
                  Cost per ship
                </span>
                {costPerShip?.amount == null ? (
                  <span
                    data-testid={INVESTMENT_COST_PER_SHIP_EMPTY_TESTID}
                    className="text-sm text-muted-foreground tabular-nums"
                  >
                    —
                  </span>
                ) : (
                  <span
                    data-testid={INVESTMENT_COST_PER_SHIP_TESTID}
                    className="text-sm font-medium tabular-nums"
                  >
                    {formatCost(costPerShip.amount, costPerShip.currency)}
                  </span>
                )}
              </div>
              <div className="flex items-center justify-between">
                <span className="text-sm text-muted-foreground">
                  Shipped issues
                </span>
                <span
                  data-testid={INVESTMENT_DONE_ISSUES_TESTID}
                  className="text-sm font-medium tabular-nums"
                >
                  {doneIssuesCount}
                </span>
              </div>
            </div>
          )}
        </div>
      )}
    </section>
  )
}