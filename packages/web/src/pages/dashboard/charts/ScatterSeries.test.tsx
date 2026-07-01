// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, describe, expect, it } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import { ScatterSeries } from './ScatterSeries'
import type { ScatterPoint } from './ScatterSeries'
import { setPrefersReducedMotion } from '../../../../tests/setup'

describe('ScatterSeries', () => {
  const points: ScatterPoint[] = [
    { x: 10, y: 20, id: 'a' },
    { x: 30, y: 40, id: 'b' },
    { x: 50, y: 60, id: 'c' },
  ]

  afterEach(() => {
    cleanup()
  })

  it('renders one circle per point', () => {
    render(
      <svg>
        <ScatterSeries points={points} />
      </svg>,
    )

    const series = screen.getByTestId('scatter-series')
    expect(series).toBeInTheDocument()
    expect(series.children).toHaveLength(points.length)

    for (const point of points) {
      expect(screen.getByTestId(`scatter-point-${point.id}`)).toBeInTheDocument()
    }
  })

  it('encodes the point as cx/cy attributes (no transform positioning)', () => {
    render(
      <svg>
        <ScatterSeries points={points} />
      </svg>,
    )

    const first = screen.getByTestId(`scatter-point-${points[0].id}`)
    expect(first).toHaveAttribute('cx', '10')
    expect(first).toHaveAttribute('cy', '20')

    const third = screen.getByTestId(`scatter-point-${points[2].id}`)
    expect(third).toHaveAttribute('cx', '50')
    expect(third).toHaveAttribute('cy', '60')
  })

  it('uses class-based fill (theme token) instead of an attribute', () => {
    render(
      <svg>
        <ScatterSeries points={points} className="fill-chart-2" />
      </svg>,
    )

    const first = screen.getByTestId(`scatter-point-${points[0].id}`)
    expect(first.getAttribute('class')).toContain('fill-chart-2')
    expect(first.getAttribute('fill')).toBeNull()
  })

  it('renders nothing when the points array is empty', () => {
    render(
      <svg>
        <ScatterSeries points={[]} />
      </svg>,
    )

    expect(screen.queryByTestId('scatter-series')).not.toBeInTheDocument()
  })

  it('respects the caller-supplied radius', () => {
    render(
      <svg>
        <ScatterSeries points={points} radius={6} />
      </svg>,
    )

    const first = screen.getByTestId(`scatter-point-${points[0].id}`)
    expect(first).toHaveAttribute('r', '6')
  })

  it('falls back to index keys when point id is omitted', () => {
    const indexPoints: ScatterPoint[] = [
      { x: 1, y: 1 },
      { x: 2, y: 2 },
    ]

    render(
      <svg>
        <ScatterSeries points={indexPoints} />
      </svg>,
    )

    expect(screen.getByTestId('scatter-point-0')).toBeInTheDocument()
    expect(screen.getByTestId('scatter-point-1')).toBeInTheDocument()
  })

  it('exposes transform/opacity motion when animated and not reduced', () => {
    render(
      <svg>
        <ScatterSeries points={points} />
      </svg>,
    )

    const first = screen.getByTestId(`scatter-point-${points[0].id}`)
    expect(first.style.transition).toContain('opacity')
    expect(first.style.transition).toContain('transform')
    expect(first.style.transition).not.toContain('width')
    expect(first.style.transition).not.toContain('height')
  })
})

describe('ScatterSeries motion', () => {
  const points: ScatterPoint[] = [
    { x: 5, y: 5, id: 'a' },
    { x: 15, y: 15, id: 'b' },
  ]

  afterEach(() => {
    cleanup()
  })

  it('disables animation transitions when prefers-reduced-motion: reduce is set', () => {
    setPrefersReducedMotion(true)

    render(
      <svg>
        <ScatterSeries points={points} animated={true} />
      </svg>,
    )

    const first = screen.getByTestId(`scatter-point-${points[0].id}`)
    expect(first.style.transition).toBe('none')

    setPrefersReducedMotion(false)
  })

  it('still renders cx/cy when reduced motion is active (visual state is correct)', () => {
    setPrefersReducedMotion(true)

    render(
      <svg>
        <ScatterSeries points={points} />
      </svg>,
    )

    const first = screen.getByTestId(`scatter-point-${points[0].id}`)
    expect(first).toHaveAttribute('cx', '5')
    expect(first).toHaveAttribute('cy', '5')

    setPrefersReducedMotion(false)
  })

  it('animated=false disables transitions', () => {
    render(
      <svg>
        <ScatterSeries points={points} animated={false} />
      </svg>,
    )

    const first = screen.getByTestId(`scatter-point-${points[0].id}`)
    expect(first.style.transition).toBe('none')
  })
})
