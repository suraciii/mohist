import { useQualityMetrics } from '../../../entities/issue'
import type { QualityMetricsWindowDto } from '../../../entities/issue'
import type { InsightsRange } from '../model/insights-range'

const PANEL_TESTID = 'productivity-quality'
const EMPTY_TESTID = 'productivity-quality-empty'

function formatRate(rate: number | null): string {
  if (rate === null) return '—'
  return `${Math.round(rate * 100)}%`
}

interface QualityWindowProps {
  title: string
  window: QualityMetricsWindowDto
  testidSuffix: string
}

function QualityWindow({ title, window, testidSuffix }: QualityWindowProps) {
  const isEmpty = window.sampleCount === 0

  return (
    <div
      data-testid={`${PANEL_TESTID}-window-${testidSuffix}`}
      data-state={isEmpty ? 'empty' : undefined}
      className="space-y-2"
    >
      <h4 className="text-xs font-medium text-muted-foreground">{title}</h4>
      {isEmpty ? (
        <p
          data-testid={`${PANEL_TESTID}-window-${testidSuffix}-empty`}
          className="text-sm text-muted-foreground"
        >
          No shipped issues in this window.
        </p>
      ) : (
        <div className="space-y-2">
          <div className="flex items-center justify-between">
            <span className="text-sm text-muted-foreground">First-time-right</span>
            <span className="flex items-center gap-2">
              <span
                data-testid={`${PANEL_TESTID}-ftr-${testidSuffix}`}
                className="text-sm font-medium tabular-nums"
              >
                {formatRate(window.firstTimeRightRate)}
              </span>
              <span
                data-testid={`${PANEL_TESTID}-ftr-${testidSuffix}-sample`}
                className="text-xs text-muted-foreground tabular-nums"
              >
                n={window.sampleCount}
              </span>
            </span>
          </div>
          <div className="space-y-1">
            {window.stages.map((stage) => (
              <div
                key={stage.stage}
                data-testid={`${PANEL_TESTID}-stage-${stage.stage}-${testidSuffix}`}
                className="flex items-center justify-between"
              >
                <span className="text-sm text-muted-foreground capitalize">{stage.stage}</span>
                <span className="flex items-center gap-2">
                  {stage.enteredCount === 0 ? (
                    <span
                      data-testid={`${PANEL_TESTID}-stage-${stage.stage}-${testidSuffix}-empty`}
                      className="text-sm text-muted-foreground tabular-nums"
                    >
                      —
                    </span>
                  ) : (
                    <span
                      data-testid={`${PANEL_TESTID}-stage-${stage.stage}-${testidSuffix}-rate`}
                      className="text-sm font-medium tabular-nums"
                    >
                      {formatRate(stage.reworkRate)}
                    </span>
                  )}
                  <span
                    data-testid={`${PANEL_TESTID}-stage-${stage.stage}-${testidSuffix}-sample`}
                    className="text-xs text-muted-foreground tabular-nums"
                  >
                    n={stage.enteredCount}
                  </span>
                </span>
              </div>
            ))}
          </div>
        </div>
      )}
    </div>
  )
}

export function QualityPanel({ range }: { range: InsightsRange }) {
  const { data } = useQualityMetrics(range)
  const window7d = data?.window7d
  const window30d = data?.window30d
  const hasSamples = (window7d?.sampleCount ?? 0) > 0 || (window30d?.sampleCount ?? 0) > 0

  if (!window7d || !window30d || !hasSamples) {
    return (
      <section
        data-testid={PANEL_TESTID}
        data-state="empty"
        aria-label="AI Quality"
        className="rounded-lg border border-border bg-card/50 p-4"
      >
        <div className="flex items-center justify-between mb-3">
          <h3 className="text-xs font-semibold uppercase tracking-wide text-muted-foreground">
            AI Quality
          </h3>
        </div>
        <p data-testid={EMPTY_TESTID} className="text-sm text-muted-foreground">
          No quality data yet — first-time-right and rework rates appear once issues ship within the trailing window.
        </p>
      </section>
    )
  }

  return (
    <section
      data-testid={PANEL_TESTID}
      aria-label="AI Quality"
      className="rounded-lg border border-border bg-card/50 p-4"
    >
      <div className="flex items-center justify-between mb-3">
        <h3 className="text-xs font-semibold uppercase tracking-wide text-muted-foreground">
          AI Quality
        </h3>
      </div>
      <div className="space-y-4">
        <QualityWindow title="Last 7 days" window={window7d} testidSuffix="7d" />
        <QualityWindow
          title="Last 30 days"
          window={window30d}
          testidSuffix="30d"
        />
      </div>
    </section>
  )
}
