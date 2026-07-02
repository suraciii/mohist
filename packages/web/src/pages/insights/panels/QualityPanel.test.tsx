// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import type { QualityMetricsResponse, StageReworkRateDto } from '../../../entities/issue'

const useQualityMetricsMock = vi.fn()
vi.mock('../../../entities/issue/api/quality-metrics', () => ({
  useQualityMetrics: (...args: unknown[]) => useQualityMetricsMock(...args),
}))

import { QualityPanel } from './QualityPanel'

function makeStage(
  stage: string,
  enteredCount: number,
  reworkRate: number | null,
): StageReworkRateDto {
  return { stage, enteredCount, reworkRate }
}

function makeWindow(
  sampleCount: number,
  firstTimeRightRate: number | null,
  stages: StageReworkRateDto[],
) {
  return {
    from: '2026-06-20T00:00:00+00:00',
    to: '2026-06-27T00:00:00+00:00',
    sampleCount,
    firstTimeRightRate,
    stages,
  }
}

function makeQualityResponse(
  overrides?: Partial<QualityMetricsResponse>,
): QualityMetricsResponse {
  return {
    window7d: makeWindow(10, 0.7, [
      makeStage('plan', 10, 0.2),
      makeStage('build', 8, 0.0),
      makeStage('check', 9, 0.3),
      makeStage('integrate', 5, 0.1),
    ]),
    window30d: makeWindow(25, 0.6, [
      makeStage('plan', 25, 0.25),
      makeStage('build', 20, 0.05),
      makeStage('check', 22, 0.35),
      makeStage('integrate', 12, 0.15),
    ]),
    ...overrides,
  }
}

function renderPanel() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider>
        <MemoryRouter initialEntries={['/']}>
          <QualityPanel />
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

describe('QualityPanel', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  afterEach(() => {
    cleanup()
  })

  it('renders first-time-right and per-stage rework rates sourced from the endpoint', () => {
    useQualityMetricsMock.mockReturnValue({ data: makeQualityResponse() })

    const { container } = renderPanel()

    const section = screen.getByTestId('productivity-quality')
    expect(section).toBeInTheDocument()
    expect(section).not.toHaveAttribute('data-state', 'empty')

    expect(screen.getByTestId('productivity-quality-ftr-7d')).toHaveTextContent('70%')
    expect(screen.getByTestId('productivity-quality-ftr-30d')).toHaveTextContent('60%')
    expect(screen.getByTestId('productivity-quality-ftr-7d-sample')).toHaveTextContent('n=10')
    expect(screen.getByTestId('productivity-quality-ftr-30d-sample')).toHaveTextContent('n=25')

    expect(screen.getByTestId('productivity-quality-stage-plan-7d-rate')).toHaveTextContent('20%')
    expect(screen.getByTestId('productivity-quality-stage-plan-7d-sample')).toHaveTextContent('n=10')
    expect(screen.getByTestId('productivity-quality-stage-build-7d-rate')).toHaveTextContent('0%')
    expect(screen.getByTestId('productivity-quality-stage-build-7d-sample')).toHaveTextContent('n=8')
    expect(screen.getByTestId('productivity-quality-stage-check-7d-rate')).toHaveTextContent('30%')
    expect(screen.getByTestId('productivity-quality-stage-check-7d-sample')).toHaveTextContent('n=9')
    expect(
      screen.getByTestId('productivity-quality-stage-integrate-7d-rate'),
    ).toHaveTextContent('10%')
    expect(screen.getByTestId('productivity-quality-stage-integrate-7d-sample')).toHaveTextContent('n=5')

    expect(screen.getByTestId('productivity-quality-stage-plan-30d-rate')).toHaveTextContent('25%')
    expect(screen.getByTestId('productivity-quality-stage-plan-30d-sample')).toHaveTextContent('n=25')
    expect(screen.getByTestId('productivity-quality-stage-build-30d-rate')).toHaveTextContent('5%')
    expect(screen.getByTestId('productivity-quality-stage-build-30d-sample')).toHaveTextContent('n=20')
    expect(
      screen.getByTestId('productivity-quality-stage-check-30d-rate'),
    ).toHaveTextContent('35%')
    expect(screen.getByTestId('productivity-quality-stage-check-30d-sample')).toHaveTextContent('n=22')
    expect(
      screen.getByTestId('productivity-quality-stage-integrate-30d-rate'),
    ).toHaveTextContent('15%')
    expect(screen.getByTestId('productivity-quality-stage-integrate-30d-sample')).toHaveTextContent('n=12')

    expect(screen.queryByTestId('productivity-quality-empty')).not.toBeInTheDocument()
    expect(container.querySelector('[data-state="empty"]')).toBeNull()
  })

  it('renders each window empty state independently', () => {
    useQualityMetricsMock.mockReturnValue({
      data: makeQualityResponse({
        window7d: makeWindow(0, null, []),
      }),
    })

    const { container } = renderPanel()

    const section = screen.getByTestId('productivity-quality')
    expect(section).toBeInTheDocument()
    expect(section).not.toHaveAttribute('data-state', 'empty')

    const empty = screen.getByTestId('productivity-quality-window-7d-empty')
    expect(empty).toBeInTheDocument()
    expect(empty.textContent ?? '').toMatch(/no shipped issues/i)

    expect(screen.queryByTestId('productivity-quality-ftr-7d')).not.toBeInTheDocument()
    expect(screen.getByTestId('productivity-quality-ftr-30d')).toHaveTextContent('60%')
    expect(screen.queryByTestId('productivity-quality-empty')).not.toBeInTheDocument()
    expect(container.querySelector('[data-testid="productivity-quality-window-30d-empty"]')).toBeNull()
  })

  it('renders the panel empty state when both windows have no samples', () => {
    useQualityMetricsMock.mockReturnValue({
      data: makeQualityResponse({
        window7d: makeWindow(0, null, []),
        window30d: makeWindow(0, null, []),
      }),
    })

    renderPanel()

    const section = screen.getByTestId('productivity-quality')
    expect(section).toHaveAttribute('data-state', 'empty')
    expect(screen.getByTestId('productivity-quality-empty')).toBeInTheDocument()
    expect(screen.queryByTestId('productivity-quality-ftr-30d')).not.toBeInTheDocument()
  })

  it('distinguishes a zero-sample window from a perfect first-time-right score', () => {
    useQualityMetricsMock.mockReturnValue({
      data: makeQualityResponse({
        window7d: makeWindow(5, 1.0, [
          makeStage('plan', 5, 0),
          makeStage('build', 4, 0),
          makeStage('check', 5, 0),
          makeStage('integrate', 3, 0),
        ]),
      }),
    })

    renderPanel()

    const section = screen.getByTestId('productivity-quality')
    expect(section).not.toHaveAttribute('data-state', 'empty')

    expect(screen.getByTestId('productivity-quality-ftr-7d')).toHaveTextContent('100%')
    expect(screen.queryByTestId('productivity-quality-empty')).not.toBeInTheDocument()
  })

  it('renders empty for a stage with enteredCount === 0 independently of other stages', () => {
    useQualityMetricsMock.mockReturnValue({
      data: makeQualityResponse({
        window7d: makeWindow(5, 0.8, [
          makeStage('plan', 5, 0.2),
          makeStage('build', 5, 0.0),
          makeStage('check', 0, null),
          makeStage('integrate', 5, 0.1),
        ]),
      }),
    })

    renderPanel()

    expect(screen.getByTestId('productivity-quality-stage-check-7d-empty')).toBeInTheDocument()
    expect(screen.getByTestId('productivity-quality-stage-check-7d-empty')).toHaveTextContent('—')
    expect(screen.getByTestId('productivity-quality-stage-check-7d-sample')).toHaveTextContent('n=0')

    expect(screen.getByTestId('productivity-quality-stage-plan-7d-rate')).toHaveTextContent('20%')
    expect(screen.getByTestId('productivity-quality-stage-build-7d-rate')).toHaveTextContent('0%')
    expect(
      screen.getByTestId('productivity-quality-stage-integrate-7d-rate'),
    ).toHaveTextContent('10%')

    expect(
      screen.queryByTestId('productivity-quality-stage-check-7d-rate'),
    ).not.toBeInTheDocument()
  })

  it('renders the empty state when the hook returns no data', () => {
    useQualityMetricsMock.mockReturnValue({ data: undefined })

    renderPanel()

    const section = screen.getByTestId('productivity-quality')
    expect(section).toHaveAttribute('data-state', 'empty')
    expect(screen.getByTestId('productivity-quality-empty')).toBeInTheDocument()
  })
})
