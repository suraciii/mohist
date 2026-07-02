import type { ReactNode } from 'react'
import { ChartGroup } from './ChartGroup'
import { EpicProgressList } from '../panels/EpicProgressList'
import { ThroughputChart } from '../panels/ThroughputChart'
import { CompletionTrend } from '../panels/CompletionTrend'
import { CumulativeFlowChart } from '../panels/CumulativeFlowChart'
import { CycleTimeChart } from '../panels/CycleTimeChart'
import { StageDurationChart } from '../panels/StageDurationChart'
import { QualityPanel } from '../panels/QualityPanel'
import { FtrTrendChart } from '../panels/FtrTrendChart'
import { InvestmentPanel } from '../panels/InvestmentPanel'
import { CostTrendChart } from '../panels/CostTrendChart'

type DimensionId = 'output' | 'delivery' | 'quality' | 'investment'

interface GroupSpec {
  id: DimensionId
  title: string
  question: string
  render: () => ReactNode
}

const CHART_GROUPS: readonly GroupSpec[] = [
  {
    id: 'output',
    title: '产出',
    question: '你交付了多少？',
    render: () => (
      <>
        <EpicProgressList />
        <ThroughputChart />
        <CompletionTrend />
        <CumulativeFlowChart />
      </>
    ),
  },
  {
    id: 'delivery',
    title: '交付效率',
    question: '多快？',
    render: () => (
      <>
        <CycleTimeChart />
        <StageDurationChart />
      </>
    ),
  },
  {
    id: 'quality',
    title: '质量',
    question: '一次做对了吗？',
    render: () => (
      <>
        <QualityPanel />
        <FtrTrendChart />
      </>
    ),
  },
  {
    id: 'investment',
    title: '投入',
    question: '花了多少？',
    render: () => (
      <>
        <InvestmentPanel />
        <CostTrendChart />
      </>
    ),
  },
] as const

export function InsightsCharts() {
  return (
    <div className="flex flex-col gap-6" data-testid="insights-charts">
      {CHART_GROUPS.map(({ id, title, question, render }) => (
        <ChartGroup key={id} id={id} title={title} question={question}>
          {render()}
        </ChartGroup>
      ))}
    </div>
  )
}