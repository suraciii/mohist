import '@testing-library/jest-dom'
import { afterEach, describe, expect, it } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import { BarSeries } from './BarSeries'
import { LineSeries } from './LineSeries'
import { ChartAxes } from './ChartAxes'
import { ChartLegend } from './ChartLegend'
import type { LegendEntry } from './ChartLegend'
import { setPrefersReducedMotion } from '../../../../tests/setup'

// --- BarSeries ---

describe('BarSeries', () => {
  const barData = [
    { value: 10, label: 'Day 1' },
    { value: 25, label: 'Day 2' },
    { value: 5, label: 'Day 3' },
    { value: 30, label: 'Day 4' },
  ]

  afterEach(() => {
    cleanup()
  })

  it('renders one rect per data point', () => {
    render(
      <svg>
        <BarSeries
          data={barData}
          plotX={0}
          plotY={0}
          plotWidth={400}
          plotHeight={200}
        />
      </svg>,
    )

    const bars = screen.getByTestId('bar-series')
    expect(bars).toBeInTheDocument()

    for (let i = 0; i < barData.length; i++) {
      expect(screen.getByTestId(`bar-${i}`)).toBeInTheDocument()
    }
  })

  it('bars use transform scaleY for height encoding (not width/height CSS)', () => {
    render(
      <svg>
        <BarSeries
          data={barData}
          plotX={0}
          plotY={0}
          plotWidth={400}
          plotHeight={200}
        />
      </svg>,
    )

    const bar = screen.getByTestId('bar-0')
    expect(bar).toHaveAttribute('height', '200')
    expect(bar.style.transform).toContain('scaleY')
  })

  it('bars use fill-chart-* class for color (no hex/rgb literals)', () => {
    render(
      <svg>
        <BarSeries
          data={barData}
          plotX={0}
          plotY={0}
          plotWidth={400}
          plotHeight={200}
        />
      </svg>,
    )

    const bar = screen.getByTestId('bar-0')
    const classes = bar.getAttribute('class') ?? ''
    expect(classes).toMatch(/fill-chart-/)
    expect(bar.getAttribute('fill')).toBeNull()
  })

  it('uses a max of 1 when all values are zero', () => {
    const zeroData = [
      { value: 0, label: 'A' },
      { value: 0, label: 'B' },
    ]

    render(
      <svg>
        <BarSeries
          data={zeroData}
          plotX={0}
          plotY={0}
          plotWidth={200}
          plotHeight={200}
        />
      </svg>,
    )

    const barSeries = screen.getByTestId('bar-series')
    expect(barSeries.children).toHaveLength(2)
    expect((barSeries.children[0] as HTMLElement).style.transform).toContain('scaleY(0)')
  })

  it('supports custom className for theme-token color override', () => {
    render(
      <svg>
        <BarSeries
          data={[{ value: 10, label: 'A' }]}
          plotX={0}
          plotY={0}
          plotWidth={100}
          plotHeight={100}
          className="fill-chart-5"
        />
      </svg>,
    )

    const bar = screen.getByTestId('bar-0')
    expect(bar.getAttribute('class')).toContain('fill-chart-5')
  })
})

// --- BarSeries motion ---

describe('BarSeries motion', () => {
  const barData = [{ value: 50, label: 'Day' }]

  afterEach(() => {
    cleanup()
  })

  it('uses transform transition when animated (not width/height)', () => {
    render(
      <svg>
        <BarSeries
          data={barData}
          plotX={0}
          plotY={0}
          plotWidth={100}
          plotHeight={200}
          animated={true}
        />
      </svg>,
    )

    const bar = screen.getByTestId('bar-0')
    expect(bar.style.transition).toContain('transform')
    expect(bar.style.transition).not.toContain('width')
    expect(bar.style.transition).not.toContain('height')
  })

  it('disables animation when prefers-reduced-motion: reduce is set', () => {
    setPrefersReducedMotion(true)

    render(
      <svg>
        <BarSeries
          data={barData}
          plotX={0}
          plotY={0}
          plotWidth={100}
          plotHeight={200}
          animated={true}
        />
      </svg>,
    )

    const bar = screen.getByTestId('bar-0')
    expect(bar.style.transition).toBe('none')

    setPrefersReducedMotion(false)
  })

  it('still renders correct final values when reduced motion is active', () => {
    setPrefersReducedMotion(true)

    render(
      <svg>
        <BarSeries
          data={barData}
          plotX={0}
          plotY={0}
          plotWidth={100}
          plotHeight={200}
        />
      </svg>,
    )

    const bar = screen.getByTestId('bar-0')
    expect(bar).toHaveAttribute('height', '200')
    expect(bar.style.transform).toContain('scaleY')

    setPrefersReducedMotion(false)
  })
})

// --- LineSeries ---

describe('LineSeries', () => {
  afterEach(() => {
    cleanup()
  })

  it('renders a path when points are provided', () => {
    render(
      <svg>
        <LineSeries
          points={[
            { x: 0, y: 100 },
            { x: 50, y: 60 },
            { x: 100, y: 80 },
          ]}
        />
      </svg>,
    )

    const series = screen.getByTestId('line-series')
    expect(series).toBeInTheDocument()
    const path = series.querySelector('path')
    expect(path).toBeInTheDocument()
    expect(path).toHaveAttribute('d')
  })

  it('renders markers for each valid point', () => {
    render(
      <svg>
        <LineSeries
          points={[
            { x: 0, y: 100 },
            { x: 50, y: 60 },
            { x: 100, y: 80 },
          ]}
        />
      </svg>,
    )

    expect(screen.getByTestId('line-marker-0')).toBeInTheDocument()
    expect(screen.getByTestId('line-marker-1')).toBeInTheDocument()
    expect(screen.getByTestId('line-marker-2')).toBeInTheDocument()
  })

  it('splits path segments at null points', () => {
    render(
      <svg>
        <LineSeries
          points={[
            { x: 0, y: 100 },
            null,
            { x: 100, y: 80 },
          ]}
        />
      </svg>,
    )

    const series = screen.getByTestId('line-series')
    const paths = series.querySelectorAll('path')
    const markers = series.querySelectorAll('circle')
    expect(paths).toHaveLength(2)
    expect(paths[0]).toHaveAttribute('d', 'M0,100')
    expect(paths[1]).toHaveAttribute('d', 'M100,80')
    expect(markers).toHaveLength(2)
  })

  it('returns null when all points are null', () => {
    const { container } = render(
      <svg>
        <LineSeries points={[null, null]} />
      </svg>,
    )

    expect(container.querySelector('[data-testid="line-series"]')).toBeNull()
  })

  it('uses stroke-chart-* class for color (no hex/rgb literals)', () => {
    render(
      <svg>
        <LineSeries
          points={[{ x: 0, y: 100 }, { x: 50, y: 50 }]}
        />
      </svg>,
    )

    const path = screen.getByTestId('line-series').querySelector('path')
    const classes = path?.getAttribute('class') ?? ''
    expect(classes).toMatch(/stroke-chart-/)
  })

  it('supports custom className for theme-token color override', () => {
    render(
      <svg>
        <LineSeries
          points={[{ x: 0, y: 100 }, { x: 50, y: 50 }]}
          className="stroke-chart-2"
          markerClassName="fill-chart-2"
        />
      </svg>,
    )

    const path = screen.getByTestId('line-series').querySelector('path')
    expect(path?.getAttribute('class')).toContain('stroke-chart-2')

    const marker = screen.getByTestId('line-marker-0')
    expect(marker.getAttribute('class')).toContain('fill-chart-2')
  })

  it('uses opacity transition when animated (not width/height)', () => {
    render(
      <svg>
        <LineSeries
          points={[{ x: 0, y: 100 }, { x: 50, y: 50 }]}
          animated={true}
        />
      </svg>,
    )

    const path = screen.getByTestId('line-series').querySelector('path')
    expect(path?.style.transition).toContain('opacity')
  })
})

// --- ChartAxes ---

describe('ChartAxes', () => {
  const ticks = [
    { value: 0, y: 200 },
    { value: 10, y: 150 },
    { value: 20, y: 100 },
    { value: 30, y: 50 },
  ]

  afterEach(() => {
    cleanup()
  })

  it('renders axis line and tick labels', () => {
    render(
      <svg>
        <ChartAxes
          side="left"
          ticks={ticks}
          plotX={40}
          plotY={0}
          plotWidth={360}
          plotHeight={200}
        />
      </svg>,
    )

    const axis = screen.getByTestId('chart-axis-left')
    expect(axis).toBeInTheDocument()

    const axisLine = axis.querySelector('line')
    expect(axisLine).toBeInTheDocument()

    for (const tick of ticks) {
      expect(axis.textContent).toContain(String(tick.value))
    }
  })

  it('renders right-side axis', () => {
    render(
      <svg>
        <ChartAxes
          side="right"
          ticks={ticks}
          plotX={40}
          plotY={0}
          plotWidth={360}
          plotHeight={200}
        />
      </svg>,
    )

    expect(screen.getByTestId('chart-axis-right')).toBeInTheDocument()
  })

  it('axis labels use stroke-border class for chrome (no color literals)', () => {
    render(
      <svg>
        <ChartAxes
          side="left"
          ticks={ticks}
          plotX={40}
          plotY={0}
          plotWidth={360}
          plotHeight={200}
        />
      </svg>,
    )

    const axisLine = screen.getByTestId('chart-axis-left').querySelector('line')
    const classes = axisLine?.getAttribute('class') ?? ''
    expect(classes).toContain('stroke-border')
  })

  it('label text uses fill-muted-foreground with tabular-nums', () => {
    render(
      <svg>
        <ChartAxes
          side="left"
          ticks={ticks}
          plotX={40}
          plotY={0}
          plotWidth={360}
          plotHeight={200}
        />
      </svg>,
    )

    const text = screen.getByTestId('chart-axis-left').querySelector('text')
    const classes = text?.getAttribute('class') ?? ''
    expect(classes).toContain('tabular-nums')
    expect(classes).toContain('fill-muted-foreground')
  })
})

// --- ChartLegend ---

describe('ChartLegend', () => {
  afterEach(() => {
    cleanup()
  })

  it('renders legend entries with shape-based swatches', () => {
    const entries: LegendEntry[] = [
      { label: 'Daily cost', shape: 'bar', className: 'fill-chart-2' },
      { label: 'Cost per ship', shape: 'line', className: 'stroke-chart-5' },
    ]

    render(<ChartLegend entries={entries} />)

    const legend = screen.getByTestId('chart-legend')
    expect(legend).toBeInTheDocument()
    expect(legend.textContent).toContain('Daily cost')
    expect(legend.textContent).toContain('Cost per ship')

    const svgs = legend.querySelectorAll('svg')
    expect(svgs).toHaveLength(2)

    const lineMarkers = svgs[1].querySelectorAll('circle')
    expect(lineMarkers).toHaveLength(3)
    for (const marker of lineMarkers) {
      expect(marker).toHaveAttribute('fill', 'none')
      expect(marker.getAttribute('class')).toContain('stroke-chart-5')
    }
  })

  it('does not render when there is a single entry', () => {
    const { container } = render(
      <ChartLegend entries={[{ label: 'Only series', shape: 'bar', className: 'fill-chart-2' }]} />,
    )

    expect(container.querySelector('[data-testid="chart-legend"]')).toBeNull()
  })

  it('does not render when entries array is empty', () => {
    const { container } = render(<ChartLegend entries={[]} />)

    expect(container.querySelector('[data-testid="chart-legend"]')).toBeNull()
  })

  it('uses tabular-nums for numeric labels in legend', () => {
    const entries: LegendEntry[] = [
      { label: '1,234', shape: 'bar', className: 'fill-chart-2' },
      { label: '56', shape: 'line', className: 'stroke-chart-5' },
    ]

    render(<ChartLegend entries={entries} />)

    const legend = screen.getByTestId('chart-legend')
    expect(legend.querySelector('.tabular-nums')).toBeInTheDocument()
  })
})
