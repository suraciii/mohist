import type { ReactNode } from 'react'
import { ChartGroup } from './ChartGroup'
import type { InsightsRange } from '../model/insights-range'
import { ThroughputChart } from '../panels/ThroughputChart'
import { CompletionTrend } from '../panels/CompletionTrend'
import { CycleTimeChart } from '../panels/CycleTimeChart'
import { StageDurationChart } from '../panels/StageDurationChart'
import { QualityPanel } from '../panels/QualityPanel'
import { FtrTrendChart } from '../panels/FtrTrendChart'
import { CostTrendChart } from '../panels/CostTrendChart'

type DimensionId = 'output' | 'delivery' | 'quality' | 'investment'

interface GroupSpec {
  id: DimensionId
  title: string
  question: string
  render: (range: InsightsRange) => ReactNode
}

const CHART_GROUPS: readonly GroupSpec[] = [
  {
    id: 'output',
    title: '产出',
    question: '你交付了多少？',
    render: (range) => (
      <>
        <ThroughputChart range={range} />
        <CompletionTrend range={range} />
      </>
    ),
  },
  {
    id: 'delivery',
    title: '交付效率',
    question: '多快？',
    render: (range) => (
      <>
        <CycleTimeChart range={range} />
        <StageDurationChart range={range} />
      </>
    ),
  },
  {
    id: 'quality',
    title: '质量',
    question: '一次做对了吗？',
    render: (range) => (
      <>
        <QualityPanel range={range} />
        <FtrTrendChart range={range} />
      </>
    ),
  },
  {
    id: 'investment',
    title: '投入',
    question: '花了多少？',
    render: (range) => (
      <>
        <CostTrendChart range={range} />
      </>
    ),
  },
]

interface InsightsChartsProps {
  range: InsightsRange
}

export function InsightsCharts({ range }: InsightsChartsProps) {
  return (
    <div className="flex flex-col gap-6" data-testid="insights-charts" data-range={range}>
      {CHART_GROUPS.map(({ id, title, question, render }) => (
        <ChartGroup key={id} id={id} title={title} question={question}>
          {render(range)}
        </ChartGroup>
      ))}
    </div>
  )
}