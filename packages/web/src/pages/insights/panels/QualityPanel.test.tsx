import '@testing-library/jest-dom'
import { describe, expect, it } from 'vitest'
import { render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { TEST_PROJECT } from '../../../../tests/test-utils'
import { ProjectProvider } from '../../../entities/project'
import type { QualityMetricsResponse, QualityMetricsWindowDto, StageReworkRateDto } from '../../../entities/issue'

import { QualityPanel, formatWindowTitle, type QualityMetricsDataHook } from './QualityPanel'

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
  overrides?: Partial<QualityMetricsWindowDto>,
): QualityMetricsWindowDto {
  return {
    from: '2026-06-01T00:00:00+00:00',
    to: '2026-06-30T23:59:59+00:00',
    sampleCount,
    firstTimeRightRate,
    stages,
    ...overrides,
  }
}

function makeQualityResponse(
  overrides?: Partial<QualityMetricsResponse>,
): QualityMetricsResponse {
  return {
    window: makeWindow(25, 0.6, [
      makeStage('plan', 25, 0.25),
      makeStage('build', 20, 0.05),
      makeStage('check', 22, 0.35),
      makeStage('integrate', 12, 0.15),
    ]),
    ...overrides,
  }
}

let qualityMetricsResult: ReturnType<QualityMetricsDataHook>

const qualityMetricsHook: QualityMetricsDataHook = () => qualityMetricsResult

function mockQualityResponse(data: QualityMetricsResponse) {
  qualityMetricsResult = { data }
}

function mockQualityPending() {
  qualityMetricsResult = { data: undefined }
}

function renderPanel(range: '7d' | '30d' | '90d' = '30d') {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId={TEST_PROJECT.id} initialProjects={[TEST_PROJECT]}>
        <MemoryRouter initialEntries={['/']}>
          <QualityPanel range={range} qualityMetricsHook={qualityMetricsHook} />
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

describe('QualityPanel', () => {
  it('renders exactly one QualityWindow sourced from data.window with formatWindowTitle', async () => {
    mockQualityResponse(makeQualityResponse())

    const { container } = renderPanel()

    await waitFor(() => {
      const section = screen.getByTestId('productivity-quality')
      expect(section).toBeInTheDocument()
      expect(section).not.toHaveAttribute('data-state', 'empty')

      const windowBlocks = container.querySelectorAll('[data-testid="productivity-quality-window"]')
      expect(windowBlocks).toHaveLength(1)

      expect(screen.getByTestId('productivity-quality-ftr')).toHaveTextContent('60%')
      expect(screen.getByTestId('productivity-quality-ftr-sample')).toHaveTextContent('n=25')

      expect(screen.getByTestId('productivity-quality-stage-plan-rate')).toHaveTextContent('25%')
      expect(screen.getByTestId('productivity-quality-stage-plan-sample')).toHaveTextContent('n=25')
      expect(screen.getByTestId('productivity-quality-stage-build-rate')).toHaveTextContent('5%')
      expect(screen.getByTestId('productivity-quality-stage-build-sample')).toHaveTextContent('n=20')
      expect(screen.getByTestId('productivity-quality-stage-check-rate')).toHaveTextContent('35%')
      expect(screen.getByTestId('productivity-quality-stage-check-sample')).toHaveTextContent('n=22')
      expect(screen.getByTestId('productivity-quality-stage-integrate-rate')).toHaveTextContent('15%')
      expect(screen.getByTestId('productivity-quality-stage-integrate-sample')).toHaveTextContent('n=12')

      const titleEl = windowBlocks[0].querySelector('h4')!
      expect(titleEl.textContent).toBe('Jun 1 – Jun 30')
      expect(titleEl.textContent).not.toMatch(/Last 7 days/i)

      expect(screen.queryByTestId('productivity-quality-empty')).not.toBeInTheDocument()
      expect(container.querySelector('[data-state="empty"]')).toBeNull()
    })
  })

  it('uses the range-driven window span for the title across 7d / 30d / 90d', async () => {
    mockQualityResponse({
      window: makeWindow(
        10,
        0.7,
        [makeStage('plan', 10, 0.1)],
        { from: '2026-04-01T00:00:00+00:00', to: '2026-06-30T23:59:59+00:00' },
      ),
    })

    renderPanel('90d')

    await waitFor(() => {
      const titleEl = screen.getByTestId('productivity-quality-window').querySelector('h4')!
      expect(titleEl.textContent).toBe('Apr 1 – Jun 30')
      expect(titleEl.textContent).not.toMatch(/Last 7 days/i)
    })
  })

  it('renders the panel empty state when window.sampleCount is zero', async () => {
    mockQualityResponse({
      window: makeWindow(0, null, []),
    })

    const { container } = renderPanel()

    await waitFor(() => {
      const section = screen.getByTestId('productivity-quality')
      expect(section).toHaveAttribute('data-state', 'empty')

      const empty = screen.getByTestId('productivity-quality-empty')
      expect(empty).toBeInTheDocument()
      expect(empty.textContent ?? '').toMatch(/no quality data yet/i)

      expect(screen.queryByTestId('productivity-quality-ftr')).not.toBeInTheDocument()
      expect(screen.queryByTestId('productivity-quality-window')).not.toBeInTheDocument()
      expect(container.querySelectorAll('[data-testid^="productivity-quality-stage-"]')).toHaveLength(0)
    })
  })

  it('does not fabricate a precise FTR percentage when window.sampleCount is zero', async () => {
    mockQualityResponse({
      window: makeWindow(0, 0.42, [
        makeStage('plan', 0, 0.18),
        makeStage('build', 0, 0.05),
      ]),
    })

    const { container } = renderPanel()

    await waitFor(() => {
      expect(screen.getByTestId('productivity-quality-empty')).toBeInTheDocument()
    })
    expect(screen.queryByTestId('productivity-quality-ftr')).not.toBeInTheDocument()
    expect(container.textContent).not.toContain('42%')
    expect(container.textContent).not.toMatch(/1[78]%|2[0-9]%/)
  })

  it('renders the panel empty state when the hook returns no data', async () => {
    mockQualityPending()

    renderPanel()

    await waitFor(() => {
      const section = screen.getByTestId('productivity-quality')
      expect(section).toHaveAttribute('data-state', 'empty')
      expect(screen.getByTestId('productivity-quality-empty')).toBeInTheDocument()
    })
  })

  it('distinguishes a zero-sample window from a perfect first-time-right score', async () => {
    mockQualityResponse({
      window: makeWindow(5, 1.0, [
        makeStage('plan', 5, 0),
        makeStage('build', 4, 0),
        makeStage('check', 5, 0),
        makeStage('integrate', 3, 0),
      ]),
    })

    renderPanel()

    await waitFor(() => {
      const section = screen.getByTestId('productivity-quality')
      expect(section).not.toHaveAttribute('data-state', 'empty')

      expect(screen.getByTestId('productivity-quality-ftr')).toHaveTextContent('100%')
      expect(screen.queryByTestId('productivity-quality-empty')).not.toBeInTheDocument()
    })
  })

  it('renders empty for a stage with enteredCount === 0 independently of other stages', async () => {
    mockQualityResponse({
      window: makeWindow(5, 0.8, [
        makeStage('plan', 5, 0.2),
        makeStage('build', 5, 0.0),
        makeStage('check', 0, null),
        makeStage('integrate', 5, 0.1),
      ]),
    })

    renderPanel()

    await waitFor(() => {
      expect(screen.getByTestId('productivity-quality-stage-check-empty')).toBeInTheDocument()
      expect(screen.getByTestId('productivity-quality-stage-check-empty')).toHaveTextContent('—')
      expect(screen.getByTestId('productivity-quality-stage-check-sample')).toHaveTextContent('n=0')

      expect(screen.getByTestId('productivity-quality-stage-plan-rate')).toHaveTextContent('20%')
      expect(screen.getByTestId('productivity-quality-stage-build-rate')).toHaveTextContent('0%')
      expect(screen.getByTestId('productivity-quality-stage-integrate-rate')).toHaveTextContent('10%')

      expect(screen.queryByTestId('productivity-quality-stage-check-rate')).not.toBeInTheDocument()
    })
  })
})

describe('formatWindowTitle', () => {
  it('formats a window from-to as "Mon D – Mon D"', () => {
    const result = formatWindowTitle({
      from: '2026-06-01T00:00:00+00:00',
      to: '2026-06-30T23:59:59+00:00',
      sampleCount: 1,
      firstTimeRightRate: 0.5,
      stages: [],
    })

    expect(result).toBe('Jun 1 – Jun 30')
  })
})
