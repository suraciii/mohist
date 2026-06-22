import { useState } from 'react'
import { ChevronRightIcon } from 'lucide-react'

const INVESTMENT_PANEL_TESTID = 'productivity-investment'
const INVESTMENT_TOGGLE_TESTID = 'productivity-investment-toggle'
const INVESTMENT_CALIBER_TESTID = 'productivity-investment-caliber'
const INVESTMENT_EMPTY_TESTID = 'productivity-investment-empty'
const INVESTMENT_CALIBER_LABEL_TESTID = 'productivity-investment-caliber-label'
const INVESTMENT_CALIBER_VALUE_TESTID = 'productivity-investment-caliber-value'

const INVESTMENT_CALIBER_BASIS =
  'per-project agent/session usage, trailing 7 days'

export function InvestmentPanel() {
  const [expanded, setExpanded] = useState(false)

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

          <p
            data-testid={INVESTMENT_EMPTY_TESTID}
            data-state="empty"
            className="text-sm text-muted-foreground"
          >
            Data unavailable — aggregated agent/session usage metrics are not
            yet provided. When the usage aggregation hook lands, figures
            (token totals, cost) will appear here against the annotated
            window above.
          </p>
        </div>
      )}
    </section>
  )
}
