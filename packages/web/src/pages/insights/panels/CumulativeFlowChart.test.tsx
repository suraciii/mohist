// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import type { CumulativeFlowResponse } from '../../../entities/issue'

const mockUseCumulativeFlow = vi.fn()
vi.mock('../../../entities/issue', () => ({
  useCumulativeFlow: () => mockUseCumulativeFlow(),
}))

import { CumulativeFlowChart } from './CumulativeFlowChart'

function buildPopulatedData(
  overrides?: Partial<CumulativeFlowResponse>,
): CumulativeFlowResponse {
  return {
    snapshots: [
      {
        day: '2026-06-28',
        backlog: 5,
        plan: 1,
        build: 2,
        check: 0,
        integrate: 0,
        done: 3,
      },
      {
        day: '2026-06-29',
        backlog: 4,
        plan: 2,
        build: 1,
        check: 1,
        integrate: 0,
        done: 4,
      },
      {
        day: '2026-06-30',
        backlog: 6,
        plan: 1,
        build: 3,
        check: 2,
        integrate: 1,
        done: 5,
      },
    ],
    rangeFrom: '2026-04-02',
    rangeTo: '2026-06-30',
    ...overrides,
  }
}

function buildEmptySeries(): CumulativeFlowResponse {
  return {
    snapshots: [],
    rangeFrom: '2026-04-02',
    rangeTo: '2026-06-30',
  }
}

function mockMatchMedia(reduced: boolean) {
  const mql = {
    matches: reduced,
    media: '(prefers-reduced-motion: reduce)',
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
    addListener: vi.fn(),
    removeListener: vi.fn(),
    dispatchEvent: vi.fn(),
    onchange: null,
  }
  vi.stubGlobal('matchMedia', vi.fn(() => mql))
}

describe('CumulativeFlowChart', () => {
  afterEach(() => {
    cleanup()
    mockUseCumulativeFlow.mockReset()
  })

  // --- Three-state routing ---

  it('renders loading state via ChartContainer', () => {
    mockMatchMedia(false)
    mockUseCumulativeFlow.mockReturnValue({
      data: undefined,
      isLoading: true,
      isError: false,
    })

    render(<CumulativeFlowChart />)

    expect(screen.getByTestId('chart-container-loading')).toBeInTheDocument()
    expect(screen.queryByTestId('cumulative-flow-chart')).toBeInTheDocument()
    expect(screen.queryByTestId('chart-accessibility')).not.toBeInTheDocument()
  })

  it('renders error state via ChartContainer', () => {
    mockMatchMedia(false)
    mockUseCumulativeFlow.mockReturnValue({
      data: undefined,
      isLoading: false,
      isError: true,
      error: new Error('fail'),
    })

    render(<CumulativeFlowChart />)

    expect(screen.getByTestId('chart-container-error')).toBeInTheDocument()
    expect(screen.queryByTestId('chart-accessibility')).not.toBeInTheDocument()
  })

  it('renders empty state when the series is empty (no snapshots have landed yet)', () => {
    mockMatchMedia(false)
    mockUseCumulativeFlow.mockReturnValue({
      data: buildEmptySeries(),
      isLoading: false,
      isError: false,
    })

    render(<CumulativeFlowChart />)

    const empty = screen.getByTestId('chart-container-empty')
    expect(empty).toBeInTheDocument()
    expect(empty.textContent).toContain('gains history once the first daily stage-population snapshot lands')
    expect(screen.queryByTestId('chart-accessibility')).not.toBeInTheDocument()
  })

  it('renders empty state when data is undefined', () => {
    mockMatchMedia(false)
    mockUseCumulativeFlow.mockReturnValue({
      data: undefined,
      isLoading: false,
      isError: false,
    })

    render(<CumulativeFlowChart />)

    expect(screen.getByTestId('chart-container-empty')).toBeInTheDocument()
    expect(screen.queryByTestId('chart-accessibility')).not.toBeInTheDocument()
  })

  it('renders a resolved zero chart when landed snapshots have zero population', () => {
    mockMatchMedia(false)
    mockUseCumulativeFlow.mockReturnValue({
      data: buildPopulatedData({
        snapshots: [
          { day: '2026-06-28', backlog: 0, plan: 0, build: 0, check: 0, integrate: 0, done: 0 },
          { day: '2026-06-29', backlog: 0, plan: 0, build: 0, check: 0, integrate: 0, done: 0 },
        ],
      }),
      isLoading: false,
      isError: false,
    })

    render(<CumulativeFlowChart />)

    expect(screen.getByTestId('chart-accessibility')).toBeInTheDocument()
    expect(screen.queryByTestId('chart-container-empty')).not.toBeInTheDocument()
    expect(screen.getByTestId('chart-sr-summary').textContent).toContain('Total WIP on latest day: 0')
  })

  it('renders resolved chart content via the accessibility wrapper', () => {
    mockMatchMedia(false)
    mockUseCumulativeFlow.mockReturnValue({
      data: buildPopulatedData(),
      isLoading: false,
      isError: false,
    })

    render(<CumulativeFlowChart />)

    expect(screen.getByTestId('chart-accessibility')).toBeInTheDocument()
    expect(screen.queryByTestId('chart-container-loading')).not.toBeInTheDocument()
    expect(screen.queryByTestId('chart-container-error')).not.toBeInTheDocument()
    expect(screen.queryByTestId('chart-container-empty')).not.toBeInTheDocument()
  })

  // --- Bands in workflow order ---

  it('renders one band per workflow stage in workflow order', () => {
    mockMatchMedia(false)
    mockUseCumulativeFlow.mockReturnValue({
      data: buildPopulatedData(),
      isLoading: false,
      isError: false,
    })

    render(<CumulativeFlowChart />)

    for (const stage of ['backlog', 'plan', 'build', 'check', 'integrate', 'done']) {
      expect(screen.getByTestId(`area-band-${stage}`)).toBeInTheDocument()
    }

    const series = screen.getByTestId('area-series')
    const paths = Array.from(series.querySelectorAll('path'))
    const stageLabels = paths.map((p) =>
      p.getAttribute('data-testid')!.replace('area-band-', ''),
    )
    expect(stageLabels).toEqual([
      'backlog', 'plan', 'build', 'check', 'integrate', 'done',
    ])
  })

  // --- Per-stage per-day band values come from the snapshot series ---

  it('encodes each band\'s upper edge at the cumulative-count y for that stage on that day', () => {
    mockMatchMedia(false)
    mockUseCumulativeFlow.mockReturnValue({
      data: buildPopulatedData(),
      isLoading: false,
      isError: false,
    })

    render(<CumulativeFlowChart />)

    // The viewport is 500×300, with MARGIN.top = 26 and plotHeight =
    // 300 - 26 - 35 = 239; baselineY = 265.
    const SVG_WIDTH = 500
    const MARGIN = { left: 50, right: 24 }
    const plotWidth = SVG_WIDTH - MARGIN.left - MARGIN.right

    // rangeFrom..rangeTo spans 90 inclusive days. The three snapshots
    // are days 87, 88, and 89 in that fixed window, so their x values
    // sit near the right edge rather than being compressed into three
    // adjacent index slots.
    const center0 = 50 + (87 / 89) * plotWidth
    const center1 = 50 + (88 / 89) * plotWidth
    const center2 = 50 + plotWidth

    const baselineY = 26 + 239
    const plotHeight = 239

    // Peak stacked total across the three days:
    //   day0: 5+1+2+0+0+3 = 11
    //   day1: 4+2+1+1+0+4 = 12
    //   day2: 6+1+3+2+1+5 = 18  ← max
    const maxValue = 18

    // The "backlog" band's upper edge y on day 0 is the cumulative
    // count up to and including backlog = 5 → top edge at
    // baselineY - (5 / maxValue) * plotHeight.
    const expectedBacklogDay0 = baselineY - (5 / maxValue) * plotHeight
    const backlogPath = screen.getByTestId('area-band-backlog')
    expect(backlogPath.getAttribute('d')).toContain(`${center0},${expectedBacklogDay0}`)

    // The "done" band's upper edge y on day 2 is the full stacked
    // total = 18 → top edge at baselineY - plotHeight (the chart
    // ceiling).
    const expectedDoneDay2 = baselineY - (18 / maxValue) * plotHeight
    const donePath = screen.getByTestId('area-band-done')
    expect(donePath.getAttribute('d')).toContain(`${center2},${expectedDoneDay2}`)

    // The "check" band on day 1 = backlog+plan+build+check = 4+2+1+1 = 8.
    const expectedCheckDay1 = baselineY - (8 / maxValue) * plotHeight
    const checkPath = screen.getByTestId('area-band-check')
    expect(checkPath.getAttribute('d')).toContain(`${center1},${expectedCheckDay1}`)
  })

  it('non-bottom band paths close to the previous cumulative edge', () => {
    mockMatchMedia(false)
    mockUseCumulativeFlow.mockReturnValue({
      data: buildPopulatedData(),
      isLoading: false,
      isError: false,
    })

    render(<CumulativeFlowChart />)

    const planPath = screen.getByTestId('area-band-plan')
    const d = planPath.getAttribute('d') || ''
    // Max total is 18. The plan band's lower edge is the backlog
    // cumulative edge, not the chart baseline.
    const baselineY = 265
    const plotHeight = 239
    const backlogDay2 = baselineY - (6 / 18) * plotHeight
    expect(d).toContain(`L${50 + 426},${backlogDay2}`)
    expect(d).not.toMatch(/L[\d.]+,265 L[\d.]+,265/)
    expect(d.endsWith('Z')).toBe(true)
  })

  it('positions sparse snapshots by date offset across the fixed range', () => {
    mockMatchMedia(false)
    mockUseCumulativeFlow.mockReturnValue({
      data: buildPopulatedData({
        rangeFrom: '2026-04-02',
        rangeTo: '2026-06-30',
        snapshots: [
          { day: '2026-04-02', backlog: 1, plan: 0, build: 0, check: 0, integrate: 0, done: 0 },
          { day: '2026-06-21', backlog: 2, plan: 0, build: 0, check: 0, integrate: 0, done: 0 },
        ],
      }),
      isLoading: false,
      isError: false,
    })

    render(<CumulativeFlowChart />)

    const plotWidth = 500 - 50 - 24
    const x0 = 50
    const x1 = 50 + (80 / 89) * plotWidth
    const path = screen.getByTestId('area-band-backlog')
    const d = path.getAttribute('d') || ''
    expect(d).toContain(`M${x0},`)
    expect(d).toContain(`L${x1},`)
  })

  it('renders a one-snapshot series as a visible day slice', () => {
    mockMatchMedia(false)
    mockUseCumulativeFlow.mockReturnValue({
      data: buildPopulatedData({
        snapshots: [
          { day: '2026-06-30', backlog: 2, plan: 1, build: 0, check: 0, integrate: 0, done: 0 },
        ],
      }),
      isLoading: false,
      isError: false,
    })

    render(<CumulativeFlowChart />)

    const path = screen.getByTestId('area-band-backlog')
    const d = path.getAttribute('d') || ''
    expect(d).toMatch(/^M[\d.]+,[\d.]+ L[\d.]+,[\d.]+/)
    const numbers = [...d.matchAll(/[ML]([\d.]+),/g)].map((match) => Number(match[1]))
    expect(new Set(numbers).size).toBeGreaterThan(1)
  })

  // --- Theme tokens / palette ---

  it('band fills reference theme tokens (no inline fill attribute)', () => {
    mockMatchMedia(false)
    mockUseCumulativeFlow.mockReturnValue({
      data: buildPopulatedData(),
      isLoading: false,
      isError: false,
    })

    render(<CumulativeFlowChart />)

    const series = screen.getByTestId('area-series')
    const paths = Array.from(series.querySelectorAll('path'))
    for (const path of paths) {
      const className = path.getAttribute('class') ?? ''
      expect(className).toMatch(/fill-chart-\d/)
      expect(path.getAttribute('fill')).toBeNull()
    }
  })

  // --- Accessibility / legend ---

  it('legend lists one entry per workflow stage with a non-color swatch (bar shape)', () => {
    mockMatchMedia(false)
    mockUseCumulativeFlow.mockReturnValue({
      data: buildPopulatedData(),
      isLoading: false,
      isError: false,
    })

    render(<CumulativeFlowChart />)

    const legend = screen.getByTestId('chart-legend')
    expect(legend).toBeInTheDocument()
    expect(legend.textContent).toContain('Backlog')
    expect(legend.textContent).toContain('Plan')
    expect(legend.textContent).toContain('Build')
    expect(legend.textContent).toContain('Check')
    expect(legend.textContent).toContain('Integrate')
    expect(legend.textContent).toContain('Done')

    // Six rect swatches (one per legend entry).
    const swatches = legend.querySelectorAll('rect')
    expect(swatches).toHaveLength(6)
  })

  it('chart svg has role=img and an aria-label naming the stages', () => {
    mockMatchMedia(false)
    mockUseCumulativeFlow.mockReturnValue({
      data: buildPopulatedData(),
      isLoading: false,
      isError: false,
    })

    render(<CumulativeFlowChart />)

    const svg = document.querySelector('svg[role="img"]')
    expect(svg).toBeInTheDocument()
    const aria = svg!.getAttribute('aria-label') || ''
    expect(aria).toContain('Cumulative flow diagram')
    for (const stage of ['backlog', 'plan', 'build', 'check', 'integrate', 'done']) {
      expect(aria).toContain(stage)
    }
  })

  it('sr-only summary names the trailing window and the salient per-stage populations', () => {
    mockMatchMedia(false)
    mockUseCumulativeFlow.mockReturnValue({
      data: buildPopulatedData(),
      isLoading: false,
      isError: false,
    })

    render(<CumulativeFlowChart />)

    const summary = screen.getByTestId('chart-sr-summary')
    expect(summary).toBeInTheDocument()
    expect(summary.textContent).toContain('Cumulative flow diagram')
    expect(summary.textContent).toContain('Backlog 6')
    expect(summary.textContent).toContain('Done 5')
    expect(summary.textContent).toContain('Total WIP on latest day: 18')
  })

  // --- tabular-nums / axes ---

  it('numeric labels use tabular-nums', () => {
    mockMatchMedia(false)
    mockUseCumulativeFlow.mockReturnValue({
      data: buildPopulatedData(),
      isLoading: false,
      isError: false,
    })

    render(<CumulativeFlowChart />)

    expect(document.querySelectorAll('.tabular-nums').length).toBeGreaterThan(0)
  })

  it('renders a left y-axis carrying the stacked issue-count ticks', () => {
    mockMatchMedia(false)
    mockUseCumulativeFlow.mockReturnValue({
      data: buildPopulatedData(),
      isLoading: false,
      isError: false,
    })

    render(<CumulativeFlowChart />)

    expect(screen.getByTestId('chart-axis-left')).toBeInTheDocument()
  })

  // --- Motion ---

  it('band motion uses opacity transition honoring prefers-reduced-motion', () => {
    mockMatchMedia(false)
    mockUseCumulativeFlow.mockReturnValue({
      data: buildPopulatedData(),
      isLoading: false,
      isError: false,
    })

    render(<CumulativeFlowChart />)

    const path = screen.getByTestId('area-band-backlog')
    const style = path.getAttribute('style') || ''
    expect(style).toContain('opacity')
    expect(style).toContain('transition')
  })

  it('removes the transition when prefers-reduced-motion: reduce is set', () => {
    mockMatchMedia(true)
    mockUseCumulativeFlow.mockReturnValue({
      data: buildPopulatedData(),
      isLoading: false,
      isError: false,
    })

    render(<CumulativeFlowChart />)

    const path = screen.getByTestId('area-band-backlog')
    const style = path.getAttribute('style') || ''
    expect(style).toContain('transition: none')
  })

  // --- Section heading ---

  it('renders within a section with testid and aria-label', () => {
    mockMatchMedia(false)
    mockUseCumulativeFlow.mockReturnValue({
      data: buildPopulatedData(),
      isLoading: false,
      isError: false,
    })

    render(<CumulativeFlowChart />)

    const section = screen.getByTestId('cumulative-flow-chart')
    expect(section).toBeInTheDocument()
    expect(section).toHaveAttribute('aria-label', 'Cumulative Flow')
  })

  it('shows the section heading', () => {
    mockMatchMedia(false)
    mockUseCumulativeFlow.mockReturnValue({
      data: buildPopulatedData(),
      isLoading: false,
      isError: false,
    })

    render(<CumulativeFlowChart />)

    expect(screen.getByText('Cumulative Flow')).toBeInTheDocument()
  })

  // --- Window annotation ---

  it('renders a window badge derived from the endpoint range when snapshots exist', () => {
    mockMatchMedia(false)
    mockUseCumulativeFlow.mockReturnValue({
      data: buildPopulatedData({
        rangeFrom: '2026-04-02',
        rangeTo: '2026-06-30',
      }),
      isLoading: false,
      isError: false,
    })

    render(<CumulativeFlowChart />)

    const badge = screen.getByTestId('cumulative-flow-chart-window')
    expect(badge).toBeInTheDocument()
    expect(badge.textContent).toContain('Apr 2')
    expect(badge.textContent).toContain('Jun 30')
  })

  it('hides the window badge in the empty state (no range to show)', () => {
    mockMatchMedia(false)
    mockUseCumulativeFlow.mockReturnValue({
      data: buildEmptySeries(),
      isLoading: false,
      isError: false,
    })

    render(<CumulativeFlowChart />)

    expect(screen.getByTestId('chart-container-empty')).toBeInTheDocument()
    expect(screen.queryByTestId('cumulative-flow-chart-window')).not.toBeInTheDocument()
  })
})
