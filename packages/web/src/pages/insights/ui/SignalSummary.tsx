import type { ReactNode } from 'react'
import { ArrowDownIcon, ArrowRightIcon, ArrowUpIcon } from 'lucide-react'
import type { FullVerdict, Verdict, VerdictDirection, VerdictPolarity } from '../model/verdict'
import {
  formatCycleDays,
  type InvestmentVerdictDetails,
  type InvestmentSubVerdict,
  type SignalInputs,
  type SignalSummaryModel,
  deriveSignalSummary,
} from '../model'
import type { AgentCostMetricDto, AgentCostRollupDto, AgentCostWindowedFigureDto } from '../../../entities/agent'
import type {
  CompletionTrendResponse,
  DeliveryTimeMetricsResponse,
  QualityMetricsResponse,
  StageDurationMetricsResponse,
} from '../../../entities/issue'

interface SignalSummaryProps {
  completion: CompletionTrendResponse | null | undefined
  deliveryTime: DeliveryTimeMetricsResponse | null | undefined
  quality: QualityMetricsResponse | null | undefined
  cost: AgentCostRollupDto | null | undefined
  stageDuration: StageDurationMetricsResponse | null | undefined
  slowestStage?: string | null
}

const TONE_UP_FAVORABLE: Record<VerdictDirection, string> = {
  up: 'text-emerald-700',
  down: 'text-rose-700',
  flat: 'text-muted-foreground',
}

const TONE_DOWN_FAVORABLE: Record<VerdictDirection, string> = {
  up: 'text-rose-700',
  down: 'text-emerald-700',
  flat: 'text-muted-foreground',
}

const ARROW_LABEL: Record<VerdictDirection, string> = {
  up: '上升',
  down: '下降',
  flat: '持平',
}

const ARROW_TESTID: Record<VerdictDirection, string> = {
  up: 'insights-trend-up',
  down: 'insights-trend-down',
  flat: 'insights-trend-flat',
}

function toneClass(verdict: FullVerdict): string {
  return verdict.polarity === 'up-favorable'
    ? TONE_UP_FAVORABLE[verdict.direction]
    : TONE_DOWN_FAVORABLE[verdict.direction]
}

function TrendArrow({
  direction,
  polarity,
}: {
  direction: VerdictDirection
  polarity: VerdictPolarity
}) {
  const Icon =
    direction === 'up' ? ArrowUpIcon : direction === 'down' ? ArrowDownIcon : ArrowRightIcon
  return (
    <span
      data-testid={ARROW_TESTID[direction]}
      data-direction={direction}
      data-polarity={polarity}
      aria-label={ARROW_LABEL[direction]}
      className={`inline-flex items-center ${toneClass({
        kind: 'full',
        label: '',
        direction,
        magnitude: 0,
        unit: 'count',
        polarity,
      } satisfies FullVerdict)}`}
    >
      <Icon className="size-4" aria-hidden="true" />
    </span>
  )
}

function formatMagnitude(verdict: FullVerdict): string {
  const abs = Math.abs(verdict.magnitude)
  if (verdict.unit === 'count') {
    return `${verdict.magnitude > 0 ? '+' : ''}${verdict.magnitude}`
  }
  if (verdict.unit === 'percent') {
    return `${abs}%`
  }
  if (verdict.unit === 'percentagePoints') {
    return `${abs} 个百分点`
  }
  return `${abs}`
}

function magnitudeSuffix(verdict: FullVerdict): string {
  if (verdict.unit === 'count') return '个'
  if (verdict.unit === 'percent') return ''
  if (verdict.unit === 'percentagePoints') return ''
  return ''
}

function MagnitudeDelta({ verdict }: { verdict: FullVerdict }) {
  return (
    <span
      data-testid="insights-magnitude"
      data-magnitude={verdict.magnitude}
      data-unit={verdict.unit}
      className={toneClass(verdict)}
    >
      {verdict.direction === 'flat' ? '持平' : formatMagnitude(verdict)}
      {verdict.unit === 'count' ? magnitudeSuffix(verdict) : ''}
    </span>
  )
}

function VerdictCard({
  testId,
  verdict,
  body,
}: {
  testId: string
  verdict: Verdict
  body: ReactNode
}) {
  if (verdict.kind === 'insufficient') {
    return (
      <div
        data-testid={testId}
        data-state="insufficient"
        className="rounded-lg border bg-card p-4"
      >
        <div className="text-sm font-semibold text-foreground">{verdict.label}</div>
        <div
          data-testid="insights-insufficient"
          className="mt-1 text-sm text-muted-foreground"
        >
          数据不足
        </div>
      </div>
    )
  }

  if (verdict.kind === 'currentOnly') {
    return (
      <div
        data-testid={testId}
        data-state="current-only"
        className="rounded-lg border bg-card p-4"
      >
        <div className="text-sm font-semibold text-foreground">{verdict.label}</div>
        <div data-testid="insights-current-only" className="mt-1 text-sm text-foreground">
          {body}
        </div>
        <div className="mt-1 text-xs text-muted-foreground">
          上期数据不足，暂无趋势
        </div>
      </div>
    )
  }

  return (
    <div
      data-testid={testId}
      data-state="full"
      className="rounded-lg border bg-card p-4"
    >
      <div className="flex items-start justify-between gap-2">
        <div className="text-sm font-semibold text-foreground">{verdict.label}</div>
        <TrendArrow direction={verdict.direction} polarity={verdict.polarity} />
      </div>
      <div data-testid="insights-current" className="mt-1 text-sm text-foreground">
        {body}
      </div>
      <div className="mt-1 text-xs text-muted-foreground flex items-center gap-1">
        对比上期
        <MagnitudeDelta verdict={verdict} />
      </div>
    </div>
  )
}

function ThroughputCard({
  verdict,
  inputs,
}: {
  verdict: Verdict
  inputs: SignalInputs
}) {
  const current = inputs.completion?.currentTotal
  const currentValue = current?.sampleCount && current.sampleCount > 0 ? current.completed : null
  const body = currentValue != null ? `本周完成 ${currentValue} 个` : '本期无完成样本'
  return <VerdictCard testId="insights-throughput" verdict={verdict} body={body} />
}

function DeliveryCard({
  verdict,
  inputs,
  slowestStage,
}: {
  verdict: Verdict
  inputs: SignalInputs
  slowestStage: string | null
}) {
  const points = inputs.deliveryTime?.points ?? []
  const cycleDaysValues = points
    .map((p) => p.cycleDays)
    .filter((v): v is number => v !== null && v !== undefined)
  const currentCycleDays =
    cycleDaysValues.length > 0
      ? cycleDaysValues.reduce((acc, v) => acc + v, 0) / cycleDaysValues.length
      : null
  const stageSuffix = slowestStage ? `；最慢是 ${slowestStage} 阶段` : ''
  const body =
    currentCycleDays != null
      ? `平均 cycle time ${formatCycleDays(currentCycleDays)}${stageSuffix}`
      : '本期无交付样本'
  return <VerdictCard testId="insights-delivery" verdict={verdict} body={body} />
}

function QualityCard({ verdict, inputs }: { verdict: Verdict; inputs: SignalInputs }) {
  const current = inputs.quality?.window30d
  const rate = current?.firstTimeRightRate
  const sampleCount = current?.sampleCount ?? 0
  const currentText =
    rate != null && sampleCount > 0
      ? `首次正确率 ${Math.round(rate * 100)}%`
      : '本期无质量样本'
  return <VerdictCard testId="insights-quality" verdict={verdict} body={currentText} />
}

function formatCurrencyAmount(amount: number): string {
  return `$${Math.round(amount)}`
}

function metricShortText(metric: AgentCostMetricDto | undefined, _unit: 'spend' | 'perIssue'): string {
  if (!metric || metric.amount == null) return '无'
  return formatCurrencyAmount(metric.amount)
}

function InvestmentCard({
  verdict,
  cost,
  details,
}: {
  verdict: Verdict
  cost: AgentCostRollupDto | null | undefined
  details: InvestmentVerdictDetails
}) {
  const currentWindow: AgentCostWindowedFigureDto | undefined = cost?.currentWindow
  const body = (() => {
    if (!currentWindow) return '本期无花费样本'
    const spend = currentWindow.spend
    const perIssue = currentWindow.perIssueCost
    const spendText = spend.amount != null ? formatCurrencyAmount(spend.amount) : null
    const perIssueText = perIssue.amount != null ? formatCurrencyAmount(perIssue.amount) : null
    if (spendText != null && perIssueText != null) {
      return `本周 ${spendText}，单 issue ${perIssueText}`
    }
    if (spendText != null) return `本周 ${spendText}`
    if (perIssueText != null) return `单 issue ${perIssueText}`
    return '本期无花费样本'
  })()
  return (
    <div
      data-testid="insights-investment"
      data-state={verdict.kind}
      data-breakdown={details.kind}
      className="rounded-lg border bg-card p-4"
    >
      <div className="flex items-start justify-between gap-2">
        <div className="text-sm font-semibold text-foreground">{verdict.label}</div>
        {verdict.kind === 'full' ? (
          <TrendArrow direction={verdict.direction} polarity={verdict.polarity} />
        ) : null}
      </div>
      <div data-testid="insights-current" className="mt-1 text-sm text-foreground">
        {body}
      </div>
      {verdict.kind === 'full' ? (
        <div className="mt-1 text-xs text-muted-foreground flex items-center gap-1">
          对比上期
          <MagnitudeDelta verdict={verdict} />
        </div>
      ) : verdict.kind === 'insufficient' ? (
        <div data-testid="insights-insufficient" className="mt-1 text-sm text-muted-foreground">
          数据不足
        </div>
      ) : (
        <div className="mt-1 text-xs text-muted-foreground">上期数据不足，暂无趋势</div>
      )}
      {verdict.kind !== 'insufficient' ? (
        <div data-testid="insights-investment-sub-trends" className="mt-2 space-y-1 text-xs">
          <SubTrendRow
            label="总花费"
            metric={details.spend}
            unit="spend"
            current={currentWindow?.spend}
          />
          <SubTrendRow
            label="单 issue 成本"
            metric={details.perIssueCost}
            unit="perIssue"
            current={currentWindow?.perIssueCost}
          />
        </div>
      ) : null}
    </div>
  )
}

function SubTrendRow({
  label,
  metric,
  unit,
  current,
}: {
  label: string
  metric: InvestmentSubVerdict
  unit: 'spend' | 'perIssue'
  current: AgentCostMetricDto | undefined
}) {
  return (
    <div
      data-testid="insights-investment-sub-trend"
      data-metric={unit}
      data-state={metric.kind}
      className="flex items-center gap-2 text-muted-foreground"
    >
      <span className="text-foreground">{label}</span>
      <span>{metricShortText(current, unit)}</span>
      {metric.kind === 'full' ? (
        <span data-testid="insights-investment-sub-trend-delta" className="ml-auto">
          <TrendArrow direction={metric.direction!} polarity="down-favorable" />
          <span className="ml-1">{metric.magnitude}%</span>
        </span>
      ) : metric.kind === 'currentOnly' ? (
        <span className="ml-auto text-xs">无上期</span>
      ) : null}
    </div>
  )
}

export function SignalSummary(props: SignalSummaryProps) {
  const inputs: SignalInputs = {
    completion: props.completion,
    deliveryTime: props.deliveryTime,
    quality: props.quality,
    cost: props.cost,
    stageDuration: props.stageDuration,
  }
  const summary: SignalSummaryModel = deriveSignalSummary(inputs)
  const slowestStage = props.slowestStage ?? summary.slowestStage

  return (
    <div
      data-testid="signal-summary"
      data-verdicts={summary.throughput.kind + ',' + summary.delivery.kind + ',' + summary.quality.kind + ',' + summary.investment.kind}
      className="grid gap-3 md:gap-4 grid-cols-1 md:grid-cols-2"
    >
      <ThroughputCard verdict={summary.throughput} inputs={inputs} />
      <DeliveryCard verdict={summary.delivery} inputs={inputs} slowestStage={slowestStage} />
      <QualityCard verdict={summary.quality} inputs={inputs} />
      <InvestmentCard verdict={summary.investment} cost={props.cost} details={summary.investmentDetails} />
    </div>
  )
}
