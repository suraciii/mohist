// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'

vi.mock('./SnapshotRow', () => ({
  SnapshotRow: () => <div data-testid="snapshot-row" />,
}))

vi.mock('./EpicProgressList', () => ({
  EpicProgressList: () => <div data-testid="epic-progress-list" />,
}))

vi.mock('./CompletionTrend', () => ({
  CompletionTrend: () => <div data-testid="completion-trend" />,
}))

vi.mock('./QualityPanel', () => ({
  QualityPanel: () => <section data-testid="productivity-quality" />,
}))

vi.mock('./FtrTrendChart', () => ({
  FtrTrendChart: () => <section data-testid="ftr-trend-chart-mock" />,
}))

vi.mock('./InvestmentPanel', () => ({
  InvestmentPanel: () => <section data-testid="investment-panel" />,
}))

vi.mock('./ThroughputChart', () => ({
  ThroughputChart: () => <div data-testid="throughput-chart-mock" />,
}))

vi.mock('./CycleTimeChart', () => ({
  CycleTimeChart: () => <div data-testid="cycle-time-chart-mock" />,
}))

vi.mock('./StageDurationChart', () => ({
  StageDurationChart: () => <div data-testid="stage-duration-chart-mock" />,
}))

vi.mock('./CostTrendChart', () => ({
  CostTrendChart: () => <div data-testid="cost-trend-chart-mock" />,
}))

import { ProductivityZone } from './ProductivityZone'

describe('ProductivityZone', () => {
  afterEach(() => {
    cleanup()
  })

  it('mounts quality, completion, and investment panels together', () => {
    render(<ProductivityZone />)

    const zone = screen.getByTestId('productivity-zone')
    expect(zone).toContainElement(screen.getByTestId('productivity-quality'))
    expect(zone).toContainElement(screen.getByTestId('completion-trend'))
    expect(zone).toContainElement(screen.getByTestId('investment-panel'))
  })

  it('mounts the throughput chart after completion trend', () => {
    render(<ProductivityZone />)

    const zone = screen.getByTestId('productivity-zone')
    const completion = screen.getByTestId('completion-trend')
    const throughput = screen.getByTestId('throughput-chart-mock')
    expect(zone).toContainElement(throughput)
    expect(completion.compareDocumentPosition(throughput) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
  })

  it('mounts the cycle-time chart after the throughput chart', () => {
    render(<ProductivityZone />)

    const zone = screen.getByTestId('productivity-zone')
    const throughput = screen.getByTestId('throughput-chart-mock')
    const cycle = screen.getByTestId('cycle-time-chart-mock')
    expect(zone).toContainElement(cycle)
    expect(throughput.compareDocumentPosition(cycle) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
  })

  it('mounts the cost trend chart after investment panel', () => {
    render(<ProductivityZone />)

    const zone = screen.getByTestId('productivity-zone')
    expect(zone).toContainElement(screen.getByTestId('cost-trend-chart-mock'))
  })

  it('mounts the stage-duration chart after the cycle-time chart', () => {
    render(<ProductivityZone />)

    const zone = screen.getByTestId('productivity-zone')
    const cycle = screen.getByTestId('cycle-time-chart-mock')
    const stage = screen.getByTestId('stage-duration-chart-mock')
    expect(zone).toContainElement(stage)
    expect(cycle.compareDocumentPosition(stage) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
  })

  it('mounts the FTR trend chart immediately after QualityPanel', () => {
    render(<ProductivityZone />)

    const zone = screen.getByTestId('productivity-zone')
    const quality = screen.getByTestId('productivity-quality')
    const ftr = screen.getByTestId('ftr-trend-chart-mock')
    expect(zone).toContainElement(ftr)
    expect(quality.compareDocumentPosition(ftr) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
    expect(quality.nextElementSibling).toBe(ftr)
  })
})
