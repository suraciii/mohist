import { useQualityMetrics } from '../../../entities/issue'
import type { QualityMetricsWindowDto } from '../../../entities/issue'
import type { InsightsRange } from '../model/insights-range'

const PANEL_TESTID = 'productivity-quality'
const EMPTY_TESTID = 'productivity-quality-empty'

function formatRate(rate: number | null): string {
  if (rate === null) return '—'
  return `${Math.round(rate * 100)}%`
}

function formatWindowDayLabel(iso: string): string {
  const match = /^(\d{4})-(\d{2})-(\d{2})/.exec(iso)
  if (!match) return iso
  const d = new Date(Number(match[1]), Number(match[2]) - 1, Number(match[3]))
  return d.toLocaleDateString('en-US', { month: 'short', day: 'numeric' })
}

export function formatWindowTitle(window: QualityMetricsWindowDto): string {
  return `${formatWindowDayLabel(window.from)} – ${formatWindowDayLabel(window.to)}`
}

interface QualityWindowProps {
  title: string
  window: QualityMetricsWindowDto
}

function QualityWindow({ title, window }: QualityWindowProps) {
  return (
    <div
      data-testid={`${PANEL_TESTID}-window`}
      className="space-y-2"
    >
      <h4 className="text-xs font-medium text-muted-foreground">{title}</h4>
      <div className="space-y-2">
        <div className="flex items-center justify-between">
          <span className="text-sm text-muted-foreground">First-time-right</span>
          <span className="flex items-center gap-2">
            <span
              data-testid={`${PANEL_TESTID}-ftr`}
              className="text-sm font-medium tabular-nums"
            >
              {formatRate(window.firstTimeRightRate)}
            </span>
            <span
              data-testid={`${PANEL_TESTID}-ftr-sample`}
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
              data-testid={`${PANEL_TESTID}-stage-${stage.stage}`}
              className="flex items-center justify-between"
            >
              <span className="text-sm text-muted-foreground capitalize">{stage.stage}</span>
              <span className="flex items-center gap-2">
                {stage.enteredCount === 0 ? (
                  <span
                    data-testid={`${PANEL_TESTID}-stage-${stage.stage}-empty`}
                    className="text-sm text-muted-foreground tabular-nums"
                  >
                    —
                  </span>
                ) : (
                  <span
                    data-testid={`${PANEL_TESTID}-stage-${stage.stage}-rate`}
                    className="text-sm font-medium tabular-nums"
                  >
                    {formatRate(stage.reworkRate)}
                  </span>
                )}
                <span
                  data-testid={`${PANEL_TESTID}-stage-${stage.stage}-sample`}
                  className="text-xs text-muted-foreground tabular-nums"
                >
                  n={stage.enteredCount}
                </span>
              </span>
            </div>
          ))}
        </div>
      </div>
    </div>
  )
}

export type QualityMetricsDataHook = (
  range?: InsightsRange,
) => Pick<ReturnType<typeof useQualityMetrics>, 'data'>

export function QualityPanel({
  range,
  qualityMetricsHook = useQualityMetrics,
}: {
  range: InsightsRange
  qualityMetricsHook?: QualityMetricsDataHook
}) {
  const { data } = qualityMetricsHook(range)
  const window = data?.window
  const hasSamples = (window?.sampleCount ?? 0) > 0

  if (!window || !hasSamples) {
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
      <QualityWindow title={formatWindowTitle(window)} window={window} />
    </section>
  )
}
