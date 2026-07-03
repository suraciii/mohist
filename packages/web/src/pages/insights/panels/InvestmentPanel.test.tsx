// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import type { AgentCostRollupDto } from '../../../entities/agent'

const useCostRollupMock = vi.fn()
vi.mock('../../../entities/agent/api/cost-rollup', () => ({
  useCostRollup: (...args: unknown[]) => useCostRollupMock(...args),
}))

import { InvestmentPanel } from './InvestmentPanel'

function makeMetric(
  amount: number | null,
  currency: string | null,
  sampleCount: number,
): AgentCostRollupDto['totalCost'] {
  return { amount, currency, sampleCount }
}

function makeRollup(
  overrides?: Partial<AgentCostRollupDto>,
): AgentCostRollupDto {
  return {
    totalCost: makeMetric(1.5, 'USD', 3),
    todayCost: makeMetric(0.25, 'USD', 1),
    doneIssuesCount: 6,
    costPerShip: makeMetric(0.25, 'USD', 1),
    ...overrides,
  }
}

function renderPanel() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider>
        <MemoryRouter initialEntries={['/']}>
          <InvestmentPanel range="30d" />
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

describe('InvestmentPanel', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    useCostRollupMock.mockReturnValue({ data: undefined })
  })

  afterEach(() => {
    cleanup()
  })

  it('is collapsed on first render with the toggle exposing aria-expanded=false', () => {
    renderPanel()

    const section = screen.getByTestId('productivity-investment')
    expect(section).toBeInTheDocument()

    const toggle = screen.getByTestId('productivity-investment-toggle')
    expect(toggle).toHaveAttribute('aria-expanded', 'false')

    expect(screen.queryByTestId('productivity-investment-body')).not.toBeInTheDocument()
    expect(screen.queryByTestId('productivity-investment-caliber')).not.toBeInTheDocument()
    expect(screen.queryByTestId('productivity-investment-empty')).not.toBeInTheDocument()
    expect(screen.queryByTestId('productivity-investment-total-cost')).not.toBeInTheDocument()

    expect(section).not.toHaveAttribute('data-state', 'empty')
  })

  it('reveals a labeled caliber annotation describing the cumulative window', () => {
    renderPanel()

    const toggle = screen.getByTestId('productivity-investment-toggle')
    fireEvent.click(toggle)

    expect(toggle).toHaveAttribute('aria-expanded', 'true')

    const body = screen.getByTestId('productivity-investment-body')
    expect(body).toBeInTheDocument()

    const caliber = screen.getByTestId('productivity-investment-caliber')
    expect(caliber).toBeInTheDocument()

    const label = screen.getByTestId('productivity-investment-caliber-label')
    expect(label).toBeInTheDocument()
    expect(label.textContent ?? '').toMatch(/window/i)
    expect(label.textContent ?? '').toMatch(/population/i)

    const value = screen.getByTestId('productivity-investment-caliber-value')
    expect(value).toBeInTheDocument()
    expect(value.textContent ?? '').not.toMatch(/^\s*$/)
    expect(value.textContent ?? '').toMatch(/cumulative/i)
  })

  it('renders the no-spend empty state when the rollup returns no totalCost sample', () => {
    useCostRollupMock.mockReturnValue({
      data: makeRollup({
        totalCost: makeMetric(null, null, 0),
        todayCost: makeMetric(null, null, 0),
        costPerShip: makeMetric(null, null, 0),
      }),
    })

    renderPanel()

    fireEvent.click(screen.getByTestId('productivity-investment-toggle'))

    const empty = screen.getByTestId('productivity-investment-empty')
    expect(empty).toBeInTheDocument()
    expect(empty).toHaveAttribute('data-state', 'empty')
    expect(empty.textContent ?? '').toMatch(/no spend recorded yet/i)

    expect(screen.queryByTestId('productivity-investment-total-cost')).not.toBeInTheDocument()
    expect(screen.queryByTestId('productivity-investment-cost-per-ship')).not.toBeInTheDocument()
    expect(screen.queryByTestId('productivity-investment-done-issues')).not.toBeInTheDocument()
  })

  it('renders the no-spend empty state when the hook returns no data', () => {
    useCostRollupMock.mockReturnValue({ data: undefined })

    renderPanel()

    fireEvent.click(screen.getByTestId('productivity-investment-toggle'))

    const empty = screen.getByTestId('productivity-investment-empty')
    expect(empty).toBeInTheDocument()
    expect(empty).toHaveAttribute('data-state', 'empty')
    expect(empty.textContent ?? '').toMatch(/no spend recorded yet/i)
  })

  it('renders cumulative spend, cost-per-ship, and the done-issue count when the rollup has samples', () => {
    useCostRollupMock.mockReturnValue({
      data: makeRollup(),
    })

    renderPanel()

    fireEvent.click(screen.getByTestId('productivity-investment-toggle'))

    const totalCost = screen.getByTestId('productivity-investment-total-cost')
    expect(totalCost).toBeInTheDocument()
    expect(totalCost).toHaveTextContent('$1.50')

    const costPerShip = screen.getByTestId('productivity-investment-cost-per-ship')
    expect(costPerShip).toBeInTheDocument()
    expect(costPerShip).toHaveTextContent('$0.25')

    const doneIssues = screen.getByTestId('productivity-investment-done-issues')
    expect(doneIssues).toBeInTheDocument()
    expect(doneIssues).toHaveTextContent('6')

    expect(screen.queryByTestId('productivity-investment-empty')).not.toBeInTheDocument()
    expect(screen.queryByTestId('productivity-investment-cost-per-ship-empty')).not.toBeInTheDocument()
  })

  it('renders an em-dash for cost-per-ship when costPerShip.amount is null (zero shipped issues)', () => {
    useCostRollupMock.mockReturnValue({
      data: makeRollup({
        doneIssuesCount: 0,
        costPerShip: makeMetric(null, null, 0),
      }),
    })

    renderPanel()

    fireEvent.click(screen.getByTestId('productivity-investment-toggle'))

    const costPerShipEmpty = screen.getByTestId('productivity-investment-cost-per-ship-empty')
    expect(costPerShipEmpty).toBeInTheDocument()
    expect(costPerShipEmpty.textContent ?? '').toMatch(/—/)

    expect(screen.queryByTestId('productivity-investment-cost-per-ship')).not.toBeInTheDocument()

    const totalCost = screen.getByTestId('productivity-investment-total-cost')
    expect(totalCost).toBeInTheDocument()

    const doneIssues = screen.getByTestId('productivity-investment-done-issues')
    expect(doneIssues).toHaveTextContent('0')

    expect(screen.queryByTestId('productivity-investment-empty')).not.toBeInTheDocument()
  })

  it('renders a real $0.00 for totalCost when sampleCount > 0, distinct from the empty state', () => {
    useCostRollupMock.mockReturnValue({
      data: makeRollup({
        totalCost: makeMetric(0, 'USD', 2),
        todayCost: makeMetric(null, null, 0),
        costPerShip: makeMetric(0, 'USD', 1),
        doneIssuesCount: 4,
      }),
    })

    renderPanel()

    fireEvent.click(screen.getByTestId('productivity-investment-toggle'))

    const totalCost = screen.getByTestId('productivity-investment-total-cost')
    expect(totalCost).toBeInTheDocument()
    expect(totalCost).toHaveTextContent('$0.00')

    const costPerShip = screen.getByTestId('productivity-investment-cost-per-ship')
    expect(costPerShip).toBeInTheDocument()
    expect(costPerShip).toHaveTextContent('$0.00')

    const doneIssues = screen.getByTestId('productivity-investment-done-issues')
    expect(doneIssues).toHaveTextContent('4')

    expect(screen.queryByTestId('productivity-investment-empty')).not.toBeInTheDocument()
  })

  it('collapses again on a second toggle click and hides the body', () => {
    renderPanel()

    const toggle = screen.getByTestId('productivity-investment-toggle')
    fireEvent.click(toggle)
    expect(toggle).toHaveAttribute('aria-expanded', 'true')
    expect(screen.getByTestId('productivity-investment-body')).toBeInTheDocument()

    fireEvent.click(toggle)
    expect(toggle).toHaveAttribute('aria-expanded', 'false')
    expect(screen.queryByTestId('productivity-investment-body')).not.toBeInTheDocument()
    expect(screen.queryByTestId('productivity-investment-caliber')).not.toBeInTheDocument()
    expect(screen.queryByTestId('productivity-investment-empty')).not.toBeInTheDocument()
  })
})