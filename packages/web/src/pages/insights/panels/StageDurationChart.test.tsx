import '@testing-library/jest-dom'
import { afterEach, describe, expect, it } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import { TEST_PROJECT } from '../../../../tests/test-utils'
import { setPrefersReducedMotion } from '../../../../tests/setup'
import type { StageDurationMetricsResponse } from '../../../entities/issue'

import { StageDurationChart, type StageDurationHook } from './StageDurationChart'

function buildData(overrides?: Partial<StageDurationMetricsResponse>): StageDurationMetricsResponse {
  return {
    window: { from: '2026-06-01T00:00:00+00:00', to: '2026-07-01T00:00:00+00:00' },
    stages: [
      { stage: 'plan', sampleCount: 4, averageSeconds: 1800, medianSeconds: 1500 },
      { stage: 'build', sampleCount: 4, averageSeconds: 5400, medianSeconds: 4800 },
      { stage: 'check', sampleCount: 4, averageSeconds: 3600, medianSeconds: 3000 },
      { stage: 'integrate', sampleCount: 3, averageSeconds: 1200, medianSeconds: 900 },
    ],
    flowEfficiencyRatio: 0.62,
    waitBreakout: {
      averageApprovalGateWaitSeconds: 600,
      averageInactiveGapSeconds: 1200,
    },
    ...overrides,
  }
}

function buildEmpty(): StageDurationMetricsResponse {
  return {
    window: { from: '2026-06-01T00:00:00+00:00', to: '2026-07-01T00:00:00+00:00' },
    stages: [],
    flowEfficiencyRatio: null,
    waitBreakout: {
      averageApprovalGateWaitSeconds: null,
      averageInactiveGapSeconds: null,
    },
  }
}

let stageDurationResult: ReturnType<StageDurationHook>

const stageDurationHook: StageDurationHook = () => stageDurationResult

function mockStageDurationResponse(data: StageDurationMetricsResponse) {
  stageDurationResult = { data, isLoading: false, isError: false }
}

function mockStageDurationPending() {
  stageDurationResult = { data: undefined, isLoading: true, isError: false }
}

function mockStageDurationError() {
  stageDurationResult = { data: undefined, isLoading: false, isError: true }
}

function renderChart() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId={TEST_PROJECT.id} initialProjects={[TEST_PROJECT]}>
        <MemoryRouter initialEntries={['/']}>
          <StageDurationChart range="30d" stageDurationHook={stageDurationHook} />
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

describe('StageDurationChart', () => {
  afterEach(() => {
    cleanup()
  })

  // --- Three-state routing ---

  it('renders loading state via ChartContainer', () => {
    mockStageDurationPending()

    renderChart()

    expect(screen.getByTestId('chart-container-loading')).toBeInTheDocument()
    expect(screen.queryByTestId('stage-duration-chart')).toBeInTheDocument()
    expect(screen.queryByTestId('chart-accessibility')).not.toBeInTheDocument()
  })

  it('renders error state via ChartContainer', async () => {
    mockStageDurationError()

    renderChart()

    await waitFor(() => {
      expect(screen.getByTestId('chart-container-error')).toBeInTheDocument()
    })
    expect(screen.queryByTestId('chart-accessibility')).not.toBeInTheDocument()
  })

  it('renders empty state with concrete next action when no stages', async () => {
    mockStageDurationResponse(buildEmpty())

    renderChart()

    await waitFor(() => {
      const empty = screen.getByTestId('chart-container-empty')
      expect(empty).toBeInTheDocument()
      expect(empty.textContent).toContain('Stage durations appear once an issue completes on the project')
    })
    expect(screen.queryByTestId('chart-accessibility')).not.toBeInTheDocument()
  })

  it('renders empty state when no stages are reached by any delivered issue', async () => {
    mockStageDurationResponse(buildData({
      stages: [
        { stage: 'plan', sampleCount: 0, averageSeconds: null, medianSeconds: null },
        { stage: 'build', sampleCount: 0, averageSeconds: null, medianSeconds: null },
      ],
    }))

    renderChart()

    await waitFor(() => {
      expect(screen.getByTestId('chart-container-empty')).toBeInTheDocument()
    })
    expect(screen.queryByTestId('chart-accessibility')).not.toBeInTheDocument()
  })

  it('renders resolved chart content with accessibility wrapper', async () => {
    mockStageDurationResponse(buildData())

    renderChart()

    await waitFor(() => {
      expect(screen.getByTestId('chart-accessibility')).toBeInTheDocument()
    })
    expect(screen.queryByTestId('chart-container-loading')).not.toBeInTheDocument()
    expect(screen.queryByTestId('chart-container-error')).not.toBeInTheDocument()
    expect(screen.queryByTestId('chart-container-empty')).not.toBeInTheDocument()
  })

  // --- Bars / order / values ---

  it('renders one bar per stage in workflow stage order', async () => {
    mockStageDurationResponse(buildData())

    renderChart()

    await waitFor(() => {
      expect(screen.getByTestId('stage-bar-plan')).toBeInTheDocument()
      expect(screen.getByTestId('stage-bar-build')).toBeInTheDocument()
      expect(screen.getByTestId('stage-bar-check')).toBeInTheDocument()
      expect(screen.getByTestId('stage-bar-integrate')).toBeInTheDocument()

      const bars = screen.getByTestId('stage-bars')
      const allBars = Array.from(bars.querySelectorAll('[data-testid^="stage-bar-fill-"]'))
      const stageNames = allBars.map((b) => b.getAttribute('data-testid')!.replace('stage-bar-fill-', ''))
      expect(stageNames).toEqual(['plan', 'build', 'check', 'integrate'])
    })
  })

  it('omits stages that have no reached samples', async () => {
    mockStageDurationResponse(buildData({
      stages: [
        { stage: 'plan', sampleCount: 2, averageSeconds: 1800, medianSeconds: 1500 },
        { stage: 'build', sampleCount: 2, averageSeconds: 5400, medianSeconds: 4800 },
      ],
    }))

    renderChart()

    await waitFor(() => {
      expect(screen.getByTestId('stage-bar-plan')).toBeInTheDocument()
      expect(screen.getByTestId('stage-bar-build')).toBeInTheDocument()
    })
    expect(screen.queryByTestId('stage-bar-check')).not.toBeInTheDocument()
    expect(screen.queryByTestId('stage-bar-integrate')).not.toBeInTheDocument()
  })

  it('encodes bar length from the stage-duration surface (average lens default)', async () => {
    mockStageDurationResponse(buildData())

    renderChart()

    await waitFor(() => {
      const plan = screen.getByTestId('stage-bar-fill-plan')
      const build = screen.getByTestId('stage-bar-fill-build')
      const integrate = screen.getByTestId('stage-bar-fill-integrate')

      const planTransform = plan.getAttribute('style') || ''
      const buildTransform = build.getAttribute('style') || ''
      const integrateTransform = integrate.getAttribute('style') || ''

      expect(planTransform).toContain('scaleX(')
      expect(buildTransform).toContain('scaleX(')
      expect(integrateTransform).toContain('scaleX(')

      expect(planTransform).toMatch(/scaleX\(0\.[0-9]+\)/)
      expect(buildTransform).toMatch(/scaleX\(1\)/)
      expect(integrateTransform).toMatch(/scaleX\(0\.[0-9]+\)/)
    })
  })

  it('uses theme-token fill-chart-2 class on bar fills (no fill attribute)', async () => {
    mockStageDurationResponse(buildData())

    renderChart()

    await waitFor(() => {
      const plan = screen.getByTestId('stage-bar-fill-plan')
      expect(plan.getAttribute('class')).toContain('fill-chart-2')
      expect(plan.getAttribute('fill')).toBeNull()
    })
  })

  // --- Lens toggle ---

  it('lens toggle renders Average and Median buttons with aria-pressed', async () => {
    mockStageDurationResponse(buildData())

    renderChart()

    await waitFor(() => {
      const avg = screen.getByTestId('stage-duration-lens-average')
      const med = screen.getByTestId('stage-duration-lens-median')

      expect(avg).toHaveAttribute('aria-pressed', 'true')
      expect(med).toHaveAttribute('aria-pressed', 'false')

      expect(avg.closest('[role="group"]')).toBe(screen.getByTestId('stage-duration-lens'))
    })
  })

  it('switching to median re-renders bar lengths from per-stage median (no second fetch)', async () => {
    mockStageDurationResponse(buildData())

    renderChart()

    await waitFor(() => {
      expect(screen.getByTestId('stage-bar-fill-plan')).toBeInTheDocument()
    })

    const planAvg = screen.getByTestId('stage-bar-fill-plan').getAttribute('style') || ''
    expect(planAvg).toMatch(/scaleX\(0\.[0-9]+\)/)

    fireEvent.click(screen.getByTestId('stage-duration-lens-median'))

    const planMedian = screen.getByTestId('stage-bar-fill-plan').getAttribute('style') || ''
    expect(planMedian).toMatch(/scaleX\(/)
    expect(planMedian).not.toEqual(planAvg)

    expect(screen.getByTestId('stage-duration-lens-median')).toHaveAttribute('aria-pressed', 'true')
    expect(screen.getByTestId('stage-duration-lens-average')).toHaveAttribute('aria-pressed', 'false')
  })

  it('renders empty state when all stages have null values for the active lens', async () => {
    mockStageDurationResponse(buildData({
      stages: [
        { stage: 'plan', sampleCount: 0, averageSeconds: 1800, medianSeconds: null },
        { stage: 'build', sampleCount: 0, averageSeconds: 3600, medianSeconds: null },
      ],
    }))

    renderChart()

    await waitFor(() => {
      expect(screen.getByTestId('chart-accessibility')).toBeInTheDocument()
    })

    fireEvent.click(screen.getByTestId('stage-duration-lens-median'))

    expect(screen.getByTestId('chart-container-empty')).toBeInTheDocument()
    expect(screen.queryByTestId('stage-bar-plan')).not.toBeInTheDocument()
  })

  // --- Ratio and wait breakout ---

  it('renders the flow-efficiency ratio sourced from the surface next to the bars', async () => {
    mockStageDurationResponse(buildData({ flowEfficiencyRatio: 0.42 }))

    renderChart()

    await waitFor(() => {
      const annotation = screen.getByTestId('flow-efficiency-annotation')
      expect(annotation).toBeInTheDocument()
      expect(annotation.textContent).toContain('42%')
    })
  })

  it('does not fabricate a ratio when the surface returns null', async () => {
    mockStageDurationResponse(buildData({ flowEfficiencyRatio: null }))

    renderChart()

    await waitFor(() => {
      expect(screen.queryByTestId('flow-efficiency-annotation')).not.toBeInTheDocument()
    })
  })

  it('renders the wait breakout with approval-wait and inactive-gap values from the surface', async () => {
    mockStageDurationResponse(buildData({
      waitBreakout: {
        averageApprovalGateWaitSeconds: 1800,
        averageInactiveGapSeconds: 3600,
      },
    }))

    renderChart()

    await waitFor(() => {
      const annotation = screen.getByTestId('wait-breakout-annotation')
      expect(annotation).toBeInTheDocument()
      expect(annotation.textContent).toContain('30m')
      expect(annotation.textContent).toContain('1.0h')
    })
  })

  it('does not fabricate wait-breakout values when the surface returns null fields', async () => {
    mockStageDurationResponse(buildData({
      waitBreakout: {
        averageApprovalGateWaitSeconds: null,
        averageInactiveGapSeconds: null,
      },
    }))

    renderChart()

    await waitFor(() => {
      expect(screen.queryByTestId('wait-breakout-annotation')).not.toBeInTheDocument()
    })
  })

  // --- Accessibility / legend ---

  it('legend distinguishes stage bars, ratio, and wait breakout by shape (bar/line/dashed)', async () => {
    mockStageDurationResponse(buildData())

    renderChart()

    await waitFor(() => {
      const legend = screen.getByTestId('chart-legend')
      expect(legend).toBeInTheDocument()
      expect(legend.textContent).toContain('Stage bar')
      expect(legend.textContent).toContain('Flow efficiency')
      expect(legend.textContent).toContain('Wait')

      const dashedPolyline = legend.querySelector('polyline[stroke-dasharray]')
      expect(dashedPolyline).toBeInTheDocument()
    })
  })

  it('chart svg has role=img and aria-label naming the stages', async () => {
    mockStageDurationResponse(buildData())

    renderChart()

    await waitFor(() => {
      const svg = document.querySelector('svg[role="img"]')
      expect(svg).toBeInTheDocument()
      expect(svg!.getAttribute('aria-label')).toContain('Stage duration chart')
      expect(svg!.getAttribute('aria-label')).toContain('plan')
      expect(svg!.getAttribute('aria-label')).toContain('integrate')
    })
  })

  it('sr-only summary names the lens, ratio, and wait-breakout values', async () => {
    mockStageDurationResponse(buildData())

    renderChart()

    await waitFor(() => {
      const summary = screen.getByTestId('chart-sr-summary')
      expect(summary).toBeInTheDocument()
      expect(summary.textContent).toContain('average')
      expect(summary.textContent).toContain('Flow efficiency')
      expect(summary.textContent).toContain('approval-gate wait')
      expect(summary.textContent).toContain('inactive gap')
    })
  })

  // --- tabular-nums / axes / labels ---

  it('numeric labels use tabular-nums', async () => {
    mockStageDurationResponse(buildData())

    renderChart()

    await waitFor(() => {
      expect(document.querySelectorAll('.tabular-nums').length).toBeGreaterThan(0)
    })
  })

  it('renders a horizontal axis at the bottom of the plot', async () => {
    mockStageDurationResponse(buildData())

    renderChart()

    await waitFor(() => {
      expect(screen.getByTestId('chart-axis-bottom')).toBeInTheDocument()
    })
  })

  it('renders per-stage value labels with stage label', async () => {
    mockStageDurationResponse(buildData())

    renderChart()

    await waitFor(() => {
      expect(screen.getByTestId('stage-bar-value-plan')).toBeInTheDocument()
      expect(screen.getByTestId('stage-bar-value-integrate')).toBeInTheDocument()
      expect(screen.getByTestId('stage-bar-value-plan').textContent).toMatch(/(m|h|s)/)
    })
  })

  it('keeps the max-value label inside the plot instead of clipping past the viewBox', async () => {
    mockStageDurationResponse(buildData())

    renderChart()

    await waitFor(() => {
      const maxLabel = screen.getByTestId('stage-bar-value-build')
      expect(maxLabel).toHaveAttribute('text-anchor', 'end')
      expect(Number(maxLabel.getAttribute('x'))).toBeLessThanOrEqual(464)
      expect(maxLabel.getAttribute('class')).toContain('fill-background')
    })
  })

  // --- Motion ---

  it('bar motion uses transform: scaleX with a left transformOrigin and transitions', async () => {
    mockStageDurationResponse(buildData())

    renderChart()

    await waitFor(() => {
      const plan = screen.getByTestId('stage-bar-fill-plan')
      const style = plan.getAttribute('style') || ''
      expect(style).toMatch(/transform:\s*scaleX\(/)
      expect(style).toMatch(/transform-origin:\s*70px/)
      expect(style).toContain('transition:')
    })
  })

  it('honors prefers-reduced-motion by removing transition', async () => {
    setPrefersReducedMotion(true)
    mockStageDurationResponse(buildData())

    renderChart()

    await waitFor(() => {
      const plan = screen.getByTestId('stage-bar-fill-plan')
      const style = plan.getAttribute('style') || ''
      expect(style).toContain('transition: none')
    })

    setPrefersReducedMotion(false)
  })

  // --- Section heading ---

  it('renders within a section with testid and aria-label', async () => {
    mockStageDurationResponse(buildData())

    renderChart()

    await waitFor(() => {
      const section = screen.getByTestId('stage-duration-chart')
      expect(section).toBeInTheDocument()
      expect(section).toHaveAttribute('aria-label', 'Stage Duration')
    })
  })

  it('shows section heading', async () => {
    mockStageDurationResponse(buildData())

    renderChart()

    await waitFor(() => {
      expect(screen.getByText('Stage Duration')).toBeInTheDocument()
    })
  })

  // --- Window annotation ---

  it('renders a window badge derived from the endpoint window when stages are populated', async () => {
    mockStageDurationResponse(buildData({
      window: { from: '2026-06-01T00:00:00+00:00', to: '2026-07-01T00:00:00+00:00' },
    }))

    renderChart()

    await waitFor(() => {
      const badge = screen.getByTestId('stage-duration-chart-window')
      expect(badge).toBeInTheDocument()
      expect(badge.textContent).toContain('Jun 1')
      expect(badge.textContent).toContain('Jul 1')
    })
  })

  it('hides the window badge in the empty state', async () => {
    mockStageDurationResponse(buildData({
      stages: [
        { stage: 'plan', sampleCount: 0, averageSeconds: null, medianSeconds: null },
        { stage: 'build', sampleCount: 0, averageSeconds: null, medianSeconds: null },
      ],
    }))

    renderChart()

    await waitFor(() => {
      expect(screen.getByTestId('chart-container-empty')).toBeInTheDocument()
    })
    expect(screen.queryByTestId('stage-duration-chart-window')).not.toBeInTheDocument()
  })
})
