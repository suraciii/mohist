// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, describe, expect, it } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import { AreaSeries } from './AreaSeries'
import type { AreaBand } from './AreaSeries'
import { setPrefersReducedMotion } from '../../../../tests/setup'

describe('AreaSeries', () => {
  afterEach(() => {
    cleanup()
    setPrefersReducedMotion(false)
  })

  function buildBands(): AreaBand[] {
    return [
      {
        label: 'backlog',
        upper: [
          { x: 0, y: 200 },
          { x: 50, y: 180 },
          { x: 100, y: 150 },
        ],
      },
      {
        label: 'plan',
        upper: [
          { x: 0, y: 150 },
          { x: 50, y: 140 },
          { x: 100, y: 110 },
        ],
        lower: [
          { x: 0, y: 200 },
          { x: 50, y: 180 },
          { x: 100, y: 150 },
        ],
      },
      {
        label: 'build',
        upper: [
          { x: 0, y: 110 },
          { x: 50, y: 90 },
          { x: 100, y: 60 },
        ],
        lower: [
          { x: 0, y: 150 },
          { x: 50, y: 140 },
          { x: 100, y: 110 },
        ],
      },
    ]
  }

  it('renders one path per band', () => {
    render(
      <svg>
        <AreaSeries bands={buildBands()} baselineY={250} />
      </svg>,
    )

    const series = screen.getByTestId('area-series')
    expect(series).toBeInTheDocument()
    expect(series.querySelectorAll('path')).toHaveLength(3)
  })

  it('labels each path by its band label', () => {
    render(
      <svg>
        <AreaSeries bands={buildBands()} baselineY={250} />
      </svg>,
    )

    expect(screen.getByTestId('area-band-backlog')).toBeInTheDocument()
    expect(screen.getByTestId('area-band-plan')).toBeInTheDocument()
    expect(screen.getByTestId('area-band-build')).toBeInTheDocument()
  })

  it('encodes a closed area path between the upper edge, the baseline, and the close', () => {
    render(
      <svg>
        <AreaSeries
          bands={[
            {
              label: 'only',
              upper: [
                { x: 10, y: 50 },
                { x: 60, y: 40 },
              ],
            },
          ]}
          baselineY={100}
        />
      </svg>,
    )

    const path = screen.getByTestId('area-band-only')
    const d = path.getAttribute('d') || ''
    expect(d.startsWith('M10,50')).toBe(true)
    expect(d).toContain('L60,40')
    expect(d).toContain('L60,100')
    expect(d).toContain('L10,100')
    expect(d.endsWith('Z')).toBe(true)
  })

  it('closes non-bottom bands against their lower edge', () => {
    render(
      <svg>
        <AreaSeries
          bands={[
            {
              label: 'plan',
              upper: [
                { x: 10, y: 50 },
                { x: 60, y: 40 },
              ],
              lower: [
                { x: 10, y: 80 },
                { x: 60, y: 70 },
              ],
            },
          ]}
          baselineY={100}
        />
      </svg>,
    )

    const path = screen.getByTestId('area-band-plan')
    const d = path.getAttribute('d') || ''
    expect(d).toContain('L60,70')
    expect(d).toContain('L10,80')
    expect(d).not.toContain('L60,100')
    expect(d).not.toContain('L10,100')
  })

  it('omits bands with empty `upper` arrays', () => {
    render(
      <svg>
        <AreaSeries
          bands={[
            { label: 'a', upper: [{ x: 0, y: 100 }] },
            { label: 'b', upper: [] },
            { label: 'c', upper: [{ x: 0, y: 80 }] },
          ]}
          baselineY={120}
        />
      </svg>,
    )

    const series = screen.getByTestId('area-series')
    expect(series.querySelectorAll('path')).toHaveLength(2)
    expect(screen.getByTestId('area-band-a')).toBeInTheDocument()
    expect(screen.getByTestId('area-band-c')).toBeInTheDocument()
  })

  it('returns null when every band is empty', () => {
    const { container } = render(
      <svg>
        <AreaSeries
          bands={[
            { label: 'a', upper: [] },
            { label: 'b', upper: [] },
          ]}
          baselineY={120}
        />
      </svg>,
    )

    expect(container.querySelector('[data-testid="area-series"]')).toBeNull()
  })

  it('uses the theme-token fill class for color (no fill attribute / no hex literals)', () => {
    render(
      <svg>
        <AreaSeries bands={buildBands()} baselineY={250} />
      </svg>,
    )

    const path = screen.getByTestId('area-band-backlog')
    const classes = path.getAttribute('class') ?? ''
    expect(classes).toMatch(/fill-chart-/)
    expect(path.getAttribute('fill')).toBeNull()
  })

  it('supports a custom className for theme-token override', () => {
    render(
      <svg>
        <AreaSeries
          bands={[{ label: 'x', upper: [{ x: 0, y: 100 }] }]}
          baselineY={120}
          className="fill-chart-4"
        />
      </svg>,
    )

    const path = screen.getByTestId('area-band-x')
    expect(path.getAttribute('class')).toContain('fill-chart-4')
  })

  it('uses the per-band className function when supplied', () => {
    render(
      <svg>
        <AreaSeries
          bands={[
            { label: 'a', upper: [{ x: 0, y: 100 }] },
            { label: 'b', upper: [{ x: 0, y: 80 }] },
          ]}
          baselineY={120}
          bandClassName={(_band, i) => `fill-chart-${i + 1}`}
        />
      </svg>,
    )

    expect(screen.getByTestId('area-band-a').getAttribute('class')).toContain('fill-chart-1')
    expect(screen.getByTestId('area-band-b').getAttribute('class')).toContain('fill-chart-2')
  })

  it('does not render a stroke on the filled band', () => {
    render(
      <svg>
        <AreaSeries bands={buildBands()} baselineY={250} />
      </svg>,
    )

    const path = screen.getByTestId('area-band-backlog')
    expect(path.getAttribute('stroke')).toBe('none')
  })
})

describe('AreaSeries motion', () => {
  afterEach(() => {
    cleanup()
    setPrefersReducedMotion(false)
  })

  it('uses an opacity transition when animated (not width/height)', () => {
    render(
      <svg>
        <AreaSeries
          bands={[{ label: 'a', upper: [{ x: 0, y: 100 }] }]}
          baselineY={120}
          animated={true}
        />
      </svg>,
    )

    const path = screen.getByTestId('area-band-a')
    const style = path.getAttribute('style') || ''
    expect(style).toContain('opacity')
    expect(style).not.toContain('width')
    expect(style).not.toContain('height')
  })

  it('disables transition when prefers-reduced-motion: reduce is set', () => {
    setPrefersReducedMotion(true)

    render(
      <svg>
        <AreaSeries
          bands={[{ label: 'a', upper: [{ x: 0, y: 100 }] }]}
          baselineY={120}
          animated={true}
        />
      </svg>,
    )

    const path = screen.getByTestId('area-band-a')
    const style = path.getAttribute('style') || ''
    expect(style).toContain('transition: none')
  })

  it('uses transition: none when animated={false}', () => {
    render(
      <svg>
        <AreaSeries
          bands={[{ label: 'a', upper: [{ x: 0, y: 100 }] }]}
          baselineY={120}
          animated={false}
        />
      </svg>,
    )

    const path = screen.getByTestId('area-band-a')
    const style = path.getAttribute('style') || ''
    expect(style).toContain('transition: none')
  })
})
