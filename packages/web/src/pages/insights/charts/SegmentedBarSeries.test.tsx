import '@testing-library/jest-dom'
import { afterEach, describe, expect, it } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import { SegmentedBarSeries } from './SegmentedBarSeries'
import type { SegmentedBarDatum } from './SegmentedBarSeries'
import { setPrefersReducedMotion } from '../../../../tests/setup'

describe('SegmentedBarSeries', () => {
  const twoSegmentData: SegmentedBarDatum[] = [
    {
      label: 'Day 1',
      segments: [
        { value: 10, fill: 'fill-chart-2' },
        { value: 3, fill: 'fill-chart-4' },
      ],
    },
    {
      label: 'Day 2',
      segments: [
        { value: 25, fill: 'fill-chart-2' },
        { value: 5, fill: 'fill-chart-4' },
      ],
    },
    {
      label: 'Day 3',
      segments: [
        { value: 5, fill: 'fill-chart-2' },
        { value: 0, fill: 'fill-chart-4' },
      ],
    },
  ]

  afterEach(() => {
    cleanup()
  })

  it('renders one group per datum', () => {
    render(
      <svg>
        <SegmentedBarSeries
          data={twoSegmentData}
          max={30}
          plotX={0}
          plotY={0}
          plotWidth={400}
          plotHeight={200}
        />
      </svg>,
    )

    const bars = screen.getByTestId('segmented-bar-series')
    expect(bars).toBeInTheDocument()

    for (let i = 0; i < twoSegmentData.length; i++) {
      expect(screen.getByTestId(`segmented-bar-${i}`)).toBeInTheDocument()
    }
  })

  it('renders all segments within each bar', () => {
    render(
      <svg>
        <SegmentedBarSeries
          data={twoSegmentData}
          max={30}
          plotX={0}
          plotY={0}
          plotWidth={400}
          plotHeight={200}
        />
      </svg>,
    )

    for (let i = 0; i < twoSegmentData.length; i++) {
      for (let s = 0; s < twoSegmentData[i].segments.length; s++) {
        expect(screen.getByTestId(`segment-${i}-${s}`)).toBeInTheDocument()
      }
    }
  })

  it('scales segments to the caller-supplied max on a shared axis', () => {
    render(
      <svg>
        <SegmentedBarSeries
          data={twoSegmentData}
          max={30}
          plotX={0}
          plotY={0}
          plotWidth={400}
          plotHeight={200}
        />
      </svg>,
    )

    const seg00 = screen.getByTestId('segment-0-0')
    const seg01 = screen.getByTestId('segment-0-1')

    expect(seg00.style.transform).toContain('scaleY(0.33333')
    expect(seg01.style.transform).toContain('scaleY(0.1)')

    const seg10 = screen.getByTestId('segment-1-0')
    expect(seg10.style.transform).toContain('scaleY(0.83333')
  })

  it('can cap a segment visual height while preserving the shared axis max', () => {
    render(
      <svg>
        <SegmentedBarSeries
          data={[{
            label: 'Day 1',
            segments: [
              { value: 2, fill: 'fill-chart-2' },
              { value: 5, maxValue: 2, widthRatio: 0.6, fill: 'fill-chart-4' },
            ],
          }]}
          max={5}
          plotX={0}
          plotY={0}
          plotWidth={100}
          plotHeight={200}
        />
      </svg>,
    )

    expect(screen.getByTestId('segment-0-0').style.transform).toContain('scaleY(0.4)')
    const capped = screen.getByTestId('segment-0-1')
    expect(capped.style.transform).toContain('scaleY(0.4)')
    expect(Number(capped.getAttribute('width'))).toBeLessThan(Number(screen.getByTestId('segment-0-0').getAttribute('width')))
  })

  it('segments use class-based fill (no fill attribute)', () => {
    render(
      <svg>
        <SegmentedBarSeries
          data={twoSegmentData}
          max={30}
          plotX={0}
          plotY={0}
          plotWidth={400}
          plotHeight={200}
        />
      </svg>,
    )

    const seg00 = screen.getByTestId('segment-0-0')
    expect(seg00.getAttribute('class')).toContain('fill-chart-2')
    expect(seg00.getAttribute('fill')).toBeNull()

    const seg01 = screen.getByTestId('segment-0-1')
    expect(seg01.getAttribute('class')).toContain('fill-chart-4')
    expect(seg01.getAttribute('fill')).toBeNull()
  })

  it('segments use transform scaleY for height encoding', () => {
    render(
      <svg>
        <SegmentedBarSeries
          data={twoSegmentData}
          max={30}
          plotX={0}
          plotY={0}
          plotWidth={400}
          plotHeight={200}
        />
      </svg>,
    )

    const seg = screen.getByTestId('segment-0-0')
    expect(seg).toHaveAttribute('height', '200')
    expect(seg.style.transform).toContain('scaleY')
  })

  it('zero-value segment renders a zero-height rect (not omitted)', () => {
    render(
      <svg>
        <SegmentedBarSeries
          data={twoSegmentData}
          max={30}
          plotX={0}
          plotY={0}
          plotWidth={400}
          plotHeight={200}
        />
      </svg>,
    )

    const segments = screen.getByTestId('segmented-bar-2')
    expect(segments.children).toHaveLength(2)

    const zeroSeg = screen.getByTestId('segment-2-1')
    expect(zeroSeg.style.transform).toContain('scaleY(0)')
  })

  it('uses a max of 1 when caller-supplied max is 0', () => {
    const data: SegmentedBarDatum[] = [
      { label: 'A', segments: [{ value: 0, fill: 'fill-chart-2' }] },
    ]

    render(
      <svg>
        <SegmentedBarSeries
          data={data}
          max={0}
          plotX={0}
          plotY={0}
          plotWidth={200}
          plotHeight={200}
        />
      </svg>,
    )

    const seg = screen.getByTestId('segment-0-0')
    expect(seg.style.transform).toContain('scaleY(0)')
  })

  it('renders correct number of total segments across all bars', () => {
    render(
      <svg>
        <SegmentedBarSeries
          data={twoSegmentData}
          max={30}
          plotX={0}
          plotY={0}
          plotWidth={400}
          plotHeight={200}
        />
      </svg>,
    )

    const bar0 = screen.getByTestId('segmented-bar-0')
    const bar1 = screen.getByTestId('segmented-bar-1')
    const bar2 = screen.getByTestId('segmented-bar-2')

    expect(bar0.children).toHaveLength(2)
    expect(bar1.children).toHaveLength(2)
    expect(bar2.children).toHaveLength(2)
  })

  it('supports custom barGap', () => {
    render(
      <svg>
        <SegmentedBarSeries
          data={twoSegmentData}
          max={30}
          plotX={0}
          plotY={0}
          plotWidth={400}
          plotHeight={200}
          barGap={10}
        />
      </svg>,
    )

    const series = screen.getByTestId('segmented-bar-series')
    expect(series.children).toHaveLength(3)
  })
})

describe('SegmentedBarSeries motion', () => {
  const singleBar: SegmentedBarDatum[] = [
    {
      label: 'Day',
      segments: [
        { value: 20, fill: 'fill-chart-2' },
        { value: 5, fill: 'fill-chart-4' },
      ],
    },
  ]

  afterEach(() => {
    cleanup()
  })

  it('uses transform transition when animated', () => {
    render(
      <svg>
        <SegmentedBarSeries
          data={singleBar}
          max={30}
          plotX={0}
          plotY={0}
          plotWidth={100}
          plotHeight={200}
          animated={true}
        />
      </svg>,
    )

    const seg = screen.getByTestId('segment-0-0')
    expect(seg.style.transition).toContain('transform')
    expect(seg.style.transition).not.toContain('width')
    expect(seg.style.transition).not.toContain('height')
  })

  it('disables animation when prefers-reduced-motion: reduce is set', () => {
    setPrefersReducedMotion(true)

    render(
      <svg>
        <SegmentedBarSeries
          data={singleBar}
          max={30}
          plotX={0}
          plotY={0}
          plotWidth={100}
          plotHeight={200}
          animated={true}
        />
      </svg>,
    )

    const seg = screen.getByTestId('segment-0-0')
    expect(seg.style.transition).toBe('none')

    setPrefersReducedMotion(false)
  })

  it('still renders correct transform values when reduced motion is active', () => {
    setPrefersReducedMotion(true)

    render(
      <svg>
        <SegmentedBarSeries
          data={singleBar}
          max={30}
          plotX={0}
          plotY={0}
          plotWidth={100}
          plotHeight={200}
        />
      </svg>,
    )

    const seg = screen.getByTestId('segment-0-0')
    expect(seg).toHaveAttribute('height', '200')
    expect(seg.style.transform).toContain('scaleY(0.66666')

    setPrefersReducedMotion(false)
  })

  it('animated=false disables transition', () => {
    render(
      <svg>
        <SegmentedBarSeries
          data={singleBar}
          max={30}
          plotX={0}
          plotY={0}
          plotWidth={100}
          plotHeight={200}
          animated={false}
        />
      </svg>,
    )

    const seg = screen.getByTestId('segment-0-0')
    expect(seg.style.transition).toBe('none')
  })
})
