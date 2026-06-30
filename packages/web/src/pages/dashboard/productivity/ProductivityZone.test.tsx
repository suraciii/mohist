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

vi.mock('./InvestmentPanel', () => ({
  InvestmentPanel: () => <section data-testid="investment-panel" />,
}))

vi.mock('./ThroughputChart', () => ({
  ThroughputChart: () => <div data-testid="throughput-chart-mock" />,
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

  it('mounts the cost trend chart after investment panel', () => {
    render(<ProductivityZone />)

    const zone = screen.getByTestId('productivity-zone')
    expect(zone).toContainElement(screen.getByTestId('cost-trend-chart-mock'))
  })
})
