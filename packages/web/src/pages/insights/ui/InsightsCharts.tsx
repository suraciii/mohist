import type { ComponentType } from 'react'
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
  charts: readonly (keyof InsightsChartsComponents)[]
}

export interface InsightsChartsComponents {
  ThroughputChart: ComponentType<{ range: InsightsRange }>
  CompletionTrend: ComponentType<{ range: InsightsRange }>
  CycleTimeChart: ComponentType<{ range: InsightsRange }>
  StageDurationChart: ComponentType<{ range: InsightsRange }>
  QualityPanel: ComponentType<{ range: InsightsRange }>
  FtrTrendChart: ComponentType<{ range: InsightsRange }>
  CostTrendChart: ComponentType<{ range: InsightsRange }>
}

const DEFAULT_COMPONENTS: InsightsChartsComponents = {
  ThroughputChart,
  CompletionTrend,
  CycleTimeChart,
  StageDurationChart,
  QualityPanel,
  FtrTrendChart,
  CostTrendChart,
}

const CHART_GROUPS: readonly GroupSpec[] = [
  {
    id: 'output',
    title: '产出',
    question: '你交付了多少？',
    charts: ['ThroughputChart', 'CompletionTrend'],
  },
  {
    id: 'delivery',
    title: '交付效率',
    question: '多快？',
    charts: ['CycleTimeChart', 'StageDurationChart'],
  },
  {
    id: 'quality',
    title: '质量',
    question: '一次做对了吗？',
    charts: ['QualityPanel', 'FtrTrendChart'],
  },
  {
    id: 'investment',
    title: '投入',
    question: '花了多少？',
    charts: ['CostTrendChart'],
  },
]

interface InsightsChartsProps {
  range: InsightsRange
  components?: Partial<InsightsChartsComponents>
}

export function InsightsCharts({ range, components }: InsightsChartsProps) {
  const resolvedComponents: InsightsChartsComponents = {
    ...DEFAULT_COMPONENTS,
    ...components,
  }

  return (
    <div className="flex flex-col gap-6" data-testid="insights-charts" data-range={range}>
      {CHART_GROUPS.map(({ id, title, question, charts }) => (
        <ChartGroup key={id} id={id} title={title} question={question}>
          {charts.map((chartName) => {
            const Chart = resolvedComponents[chartName]
            return <Chart key={chartName} range={range} />
          })}
        </ChartGroup>
      ))}
    </div>
  )
}
