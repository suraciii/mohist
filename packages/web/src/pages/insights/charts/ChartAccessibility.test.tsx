// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, describe, expect, it } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import { ChartAccessibility } from './ChartAccessibility'

describe('ChartAccessibility', () => {
  afterEach(() => {
    cleanup()
  })

  const baseLegend = [
    { label: 'Daily cost', shape: 'bar' as const, className: 'fill-chart-2' },
    { label: 'Cost per ship', shape: 'line' as const, className: 'stroke-chart-5' },
  ]

  function renderAccessibility(overrides?: Partial<React.ComponentProps<typeof ChartAccessibility>>) {
    return render(
      <ChartAccessibility
        ariaLabel="Daily cost and cost-per-ship for trailing 7 days"
        summary="Series: daily cost and cost-per-ship. Window: trailing 7 days. Total cost: $12.50. Peak day: Jun 15 at $3.20."
        legend={baseLegend}
        {...overrides}
      >
        <rect x="0" y="0" width="100" height="100" data-testid="chart-body" />
      </ChartAccessibility>,
    )
  }

  it('renders SVG with role=img and concise aria-label', () => {
    renderAccessibility()

    const svg = screen.getByRole('img')
    expect(svg).toBeInTheDocument()
    expect(svg).toHaveAttribute('aria-label', 'Daily cost and cost-per-ship for trailing 7 days')
  })

  it('renders an sr-only figcaption with the textual data summary', () => {
    renderAccessibility()

    const srSummary = screen.getByTestId('chart-sr-summary')
    expect(srSummary).toBeInTheDocument()
    expect(srSummary.className).toContain('sr-only')
    expect(srSummary.textContent).toContain('Series: daily cost')
    expect(srSummary.textContent).toContain('Total cost: $12.50')
  })

  it('renders a visible legend that disambiguates series by shape, not color alone', () => {
    renderAccessibility()

    const legend = screen.getByTestId('chart-legend')
    expect(legend).toBeInTheDocument()
    expect(legend).toBeVisible()

    expect(legend.textContent).toContain('Daily cost')
    expect(legend.textContent).toContain('Cost per ship')
  })

  it('legend entries have different shape swatches (bar vs line)', () => {
    renderAccessibility()

    const legend = screen.getByTestId('chart-legend')
    const svgs = legend.querySelectorAll('svg')
    expect(svgs.length).toBeGreaterThanOrEqual(2)

    const firstSvg = svgs[0].innerHTML
    const secondSvg = svgs[1].innerHTML
    expect(firstSvg).toContain('rect')
    expect(secondSvg).toContain('polyline')
  })

  it('renders legend with shape-based visual disambiguation (not just color)', () => {
    const sameClassLegend = [
      { label: 'Series A', shape: 'bar' as const, className: 'fill-chart-2' },
      { label: 'Series B', shape: 'line' as const, className: 'fill-chart-2' },
    ]

    render(
      <ChartAccessibility
        ariaLabel="Test"
        summary="Test summary"
        legend={sameClassLegend}
      >
        <rect data-testid="chart-body" />
      </ChartAccessibility>,
    )

    const legend = screen.getByTestId('chart-legend')
    expect(legend).toBeInTheDocument()
    const svgs = legend.querySelectorAll('svg')
    expect(svgs).toHaveLength(2)
    expect(svgs[0].innerHTML).toContain('rect')
    expect(svgs[1].innerHTML).toContain('polyline')
  })

  it('renders chart content (children) inside the SVG', () => {
    renderAccessibility()

    const chartBody = screen.getByTestId('chart-body')
    expect(chartBody).toBeInTheDocument()
    expect(chartBody.closest('svg')).toBeInTheDocument()
  })

  it('renders legend when there is more than one series', () => {
    renderAccessibility()

    expect(screen.getByTestId('chart-legend')).toBeInTheDocument()
  })

  it('does not render legend when there is a single series', () => {
    render(
      <ChartAccessibility
        ariaLabel="Single series"
        summary="Single series chart"
        legend={[{ label: 'Only series', shape: 'bar' as const, className: 'fill-chart-2' }]}
      >
        <rect data-testid="chart-body" />
      </ChartAccessibility>,
    )

    expect(screen.queryByTestId('chart-legend')).not.toBeInTheDocument()
  })

  it('applies viewBox to the SVG', () => {
    renderAccessibility({ viewBox: '0 0 200 150' })

    const svg = screen.getByRole('img')
    expect(svg).toHaveAttribute('viewBox', '0 0 200 150')
  })

  it('uses default viewBox when none provided', () => {
    renderAccessibility()

    const svg = screen.getByRole('img')
    expect(svg).toHaveAttribute('viewBox', '0 0 500 300')
  })
})
