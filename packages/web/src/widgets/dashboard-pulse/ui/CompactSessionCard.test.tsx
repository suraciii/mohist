import '@testing-library/jest-dom'
import { render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { describe, expect, it, vi, beforeEach } from 'vitest'
import type { SessionCard } from '@/entities/agent-ops'
import { CompactSessionCard } from './CompactSessionCard'

function makeCard(overrides: Partial<SessionCard> = {}): SessionCard {
  return {
    issueNumber: 12,
    issueTitle: 'Fix project selector',
    issueStage: 'Build',
    sessionId: 'session-1',
    status: 'active',
    model: 'claude-opus-4-7',
    resolvedModel: null,
    taskDescription: 'Implement CLI active project state',
    title: 'Implement CLI active project state',
    createdAt: '2026-01-01T00:00:00Z',
    completedAt: null,
    lastActivityAt: '2026-01-01T00:00:30Z',
    activityPreviews: [{ kind: 'text', text: 'preview text' }],
    taskProgress: null,
    currentWorkTitle: 'Implement CLI active project state',
    failureReason: null,
    failureCategory: 'context_exhausted',
    inputTokens: 12_400,
    outputTokens: 3_200,
    totalTokens: 15_600,
    costAmount: 0.18,
    costCurrency: 'USD',
    contextWindowUsed: 300_000,
    contextWindowSize: 1_000_000,
    contextUsagePercent: 30,
    healthStatus: 'green',
    contextUsageHistory: null,
    toolCallCount: 4,
    toolErrorCount: 1,
    ...overrides,
  }
}

function makeHistory(count: number, opts: { firstPercent?: number; lastPercent?: number } = {}) {
  const { firstPercent = 10, lastPercent = 80 } = opts
  return Array.from({ length: count }, (_, i) => {
    const ratio = count === 1 ? 0 : i / (count - 1)
    const percent = Math.round(firstPercent + (lastPercent - firstPercent) * ratio)
    return { at: `2026-01-01T00:0${i}:00Z`, percent }
  })
}

function renderCard(card: SessionCard) {
  return render(
    <MemoryRouter>
      <CompactSessionCard card={card} />
    </MemoryRouter>,
  )
}

describe('CompactSessionCard', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('shows issue number, stage, and current task title', () => {
    renderCard(makeCard({ issueStage: 'Plan', title: 'Draft implementation plan' }))

    expect(screen.getByText('#12')).toBeInTheDocument()
    expect(screen.getByTestId('pulse-compact-stage')).toHaveTextContent('Plan')
    expect(screen.getByTestId('pulse-compact-title')).toHaveTextContent('Draft implementation plan')
  })

  it('falls back to task description when title is missing', () => {
    renderCard(makeCard({ title: null, taskDescription: 'Implement the foobar handler' }))

    expect(screen.getByTestId('pulse-compact-title')).toHaveTextContent('Implement the foobar handler')
  })

  it('falls back to issue title when both task title and description are missing', () => {
    renderCard(makeCard({ title: null, taskDescription: null }))

    expect(screen.getByTestId('pulse-compact-title')).toHaveTextContent('Fix project selector')
  })

  it('renders a compact task progress bar when present', () => {
    renderCard(makeCard({ taskProgress: { completed: 3, total: 8 } }))

    const progress = screen.getByTestId('pulse-compact-progress')
    expect(progress).toHaveTextContent('3/8 tasks')
    const bar = progress.querySelector('div.h-full')
    expect(bar).toHaveStyle({ width: '37.5%' })
  })

  it('renders zero-width progress when task progress total is zero', () => {
    renderCard(makeCard({ taskProgress: { completed: 0, total: 0 } }))

    const progress = screen.getByTestId('pulse-compact-progress')
    expect(progress).toHaveTextContent('0/0 tasks')
    const bar = progress.querySelector('div.h-full')
    expect(bar).toHaveStyle({ width: '0%' })
  })

  it('clamps task progress above total to full width', () => {
    renderCard(makeCard({ taskProgress: { completed: 12, total: 8 } }))

    const progress = screen.getByTestId('pulse-compact-progress')
    expect(progress).toHaveTextContent('12/8 tasks')
    const bar = progress.querySelector('div.h-full')
    expect(bar).toHaveStyle({ width: '100%' })
  })

  it('omits the task progress bar when taskProgress is null', () => {
    renderCard(makeCard({ taskProgress: null }))

    expect(screen.queryByTestId('pulse-compact-progress')).not.toBeInTheDocument()
  })

  it('renders token usage and cost on a single compact line when both are present', () => {
    renderCard(
      makeCard({ totalTokens: 15_600, costAmount: 0.18, costCurrency: 'USD' }),
    )

    const usage = screen.getByTestId('pulse-compact-usage')
    expect(usage).toHaveTextContent('15.6k tok')
    expect(usage).toHaveTextContent('$0.18')
  })

  it('renders only cost when tokens are absent', () => {
    renderCard(
      makeCard({ totalTokens: null, costAmount: 0.42, costCurrency: 'USD' }),
    )

    const usage = screen.getByTestId('pulse-compact-usage')
    expect(usage).toHaveTextContent('$0.42')
    expect(usage).not.toHaveTextContent('tok')
  })

  it('renders only tokens when cost is absent', () => {
    renderCard(
      makeCard({ totalTokens: 9_500, costAmount: null, costCurrency: null }),
    )

    const usage = screen.getByTestId('pulse-compact-usage')
    expect(usage).toHaveTextContent('9.5k tok')
    expect(usage).not.toHaveTextContent('$')
  })

  it('omits the usage line when neither tokens nor cost are present', () => {
    renderCard(
      makeCard({ totalTokens: null, costAmount: null, costCurrency: null }),
    )

    expect(screen.queryByTestId('pulse-compact-usage')).not.toBeInTheDocument()
  })

  it('renders context-health color via ContextHealthIndicator', () => {
    renderCard(
      makeCard({
        contextWindowUsed: 950_000,
        contextWindowSize: 1_000_000,
        contextUsagePercent: 95,
        healthStatus: 'red',
      }),
    )

    const indicator = screen.getByTestId('context-health-indicator')
    expect(indicator).toHaveAttribute('data-status', 'red')
    expect(indicator).toHaveTextContent('95%')
  })

  it('renders a quiet green indicator at low usage (no glyph, no role)', () => {
    renderCard(
      makeCard({
        contextWindowUsed: 300_000,
        contextWindowSize: 1_000_000,
        contextUsagePercent: 30,
      }),
    )

    const indicator = screen.getByTestId('context-health-indicator')
    expect(indicator).toHaveAttribute('data-status', 'green')
    expect(indicator).toHaveAttribute('data-severity', 'ok')
    expect(indicator).toHaveTextContent('30%')
    expect(indicator).toHaveAttribute('title', 'Context usage 30%')
    expect(indicator).not.toHaveAttribute('role')
    expect(indicator).not.toHaveAttribute('aria-live')
    expect(screen.queryByTestId('context-health-glyph')).toBeNull()
  })

  it('renders yellow alert treatment at moderate usage with role="status" and warning glyph', () => {
    renderCard(
      makeCard({
        contextWindowUsed: 720_000,
        contextWindowSize: 1_000_000,
        contextUsagePercent: 72,
        healthStatus: 'yellow',
      }),
    )

    const indicator = screen.getByTestId('context-health-indicator')
    expect(indicator).toHaveAttribute('data-status', 'yellow')
    expect(indicator).toHaveAttribute('data-severity', 'warning')
    expect(indicator).toHaveTextContent('72%')
    expect(indicator).toHaveAttribute('role', 'status')
    expect(indicator).toHaveAttribute('title', 'Context window 72% full — near limit')
    expect(indicator).toHaveAttribute('aria-label', 'Context window 72% full — near limit')
    expect(screen.getByTestId('context-health-glyph')).toBeInTheDocument()
  })

  it('renders red critical alert treatment at high usage with role="alert" and aria-live="polite"', () => {
    renderCard(
      makeCard({
        contextWindowUsed: 950_000,
        contextWindowSize: 1_000_000,
        contextUsagePercent: 95,
        healthStatus: 'red',
      }),
    )

    const indicator = screen.getByTestId('context-health-indicator')
    expect(indicator).toHaveAttribute('data-status', 'red')
    expect(indicator).toHaveAttribute('data-severity', 'critical')
    expect(indicator).toHaveTextContent('95%')
    expect(indicator).toHaveAttribute('role', 'alert')
    expect(indicator).toHaveAttribute('aria-live', 'polite')
    expect(indicator).toHaveAttribute('title', 'Context window 95% full — at limit, compact or reset recommended')
    expect(indicator).toHaveAttribute('aria-label', 'Context window 95% full — at limit, compact or reset recommended')
    expect(screen.getByTestId('context-health-glyph')).toBeInTheDocument()
  })

  it('hides context-health indicator when contextWindowSize is null', () => {
    renderCard(
      makeCard({
        contextWindowUsed: null,
        contextWindowSize: null,
        contextUsagePercent: null,
      }),
    )

    expect(screen.queryByTestId('context-health-indicator')).not.toBeInTheDocument()
  })

  it('does NOT render elapsed time, model, activity previews, tool counts, failure category, or anomaly text', () => {
    const card = makeCard({
      model: 'claude-opus-4-7',
      failureCategory: 'context_exhausted',
      toolCallCount: 4,
      toolErrorCount: 1,
      activityPreviews: [{ kind: 'text', text: 'this should not appear' }],
    })

    renderCard(card)

    const html = document.body.textContent ?? ''
    expect(html).not.toContain('claude-opus-4-7')
    expect(html).not.toContain('context_exhausted')
    expect(html).not.toContain('4 tools')
    expect(html).not.toContain('this should not appear')
    expect(html).not.toMatch(/\d+h \d+m/)
    expect(html).not.toMatch(/\d+m ago/)
    expect(screen.queryByTestId('active-session-anomalies')).not.toBeInTheDocument()
  })

  it('stages use the STAGE_COLORS map for plan stage', () => {
    renderCard(makeCard({ issueStage: 'Plan' }))
    const stage = screen.getByTestId('pulse-compact-stage')
    expect(stage.className).toContain('bg-blue-100')
  })

  it('renders a context-usage trend mini-chart when the activity source carries a usage history', () => {
    renderCard(
      makeCard({
        contextWindowUsed: 720_000,
        contextWindowSize: 1_000_000,
        contextUsagePercent: 72,
        contextUsageHistory: makeHistory(6, { firstPercent: 10, lastPercent: 72 }),
      }),
    )

    const trend = screen.getByTestId('pulse-compact-trend')
    expect(trend).toBeInTheDocument()
    const chart = trend.querySelector('[data-testid="context-usage-trend-mini-chart"]') as SVGSVGElement | null
    expect(chart).not.toBeNull()
    expect(chart!.getAttribute('data-history-length')).toBe('6')
    expect(chart!.getAttribute('data-latest-percent')).toBe('72')
    expect(chart!.getAttribute('data-status')).toBeNull()
  })

  it('does not render the trend chart when usage history is absent', () => {
    renderCard(
      makeCard({
        contextWindowUsed: 300_000,
        contextWindowSize: 1_000_000,
        contextUsagePercent: 30,
        contextUsageHistory: null,
      }),
    )

    expect(screen.queryByTestId('pulse-compact-trend')).not.toBeInTheDocument()
    expect(screen.queryByTestId('context-usage-trend-mini-chart')).not.toBeInTheDocument()
  })

  it('does not render the trend chart when usage history is an empty array (wire-omitted case)', () => {
    renderCard(
      makeCard({
        contextWindowUsed: 300_000,
        contextWindowSize: 1_000_000,
        contextUsagePercent: 30,
        contextUsageHistory: [],
      }),
    )

    expect(screen.queryByTestId('pulse-compact-trend')).not.toBeInTheDocument()
    expect(screen.queryByTestId('context-usage-trend-mini-chart')).not.toBeInTheDocument()
  })

  it('does not render the trend chart when fewer than two samples are available (insufficient to plot)', () => {
    renderCard(
      makeCard({
        contextWindowUsed: 300_000,
        contextWindowSize: 1_000_000,
        contextUsagePercent: 30,
        contextUsageHistory: [{ at: '2026-01-01T00:00:00Z', percent: 30 }],
      }),
    )

    expect(screen.queryByTestId('pulse-compact-trend')).not.toBeInTheDocument()
    expect(screen.queryByTestId('context-usage-trend-mini-chart')).not.toBeInTheDocument()
  })

  it('renders the trend chart with neutral stroke when the latest sample crosses 80%', () => {
    renderCard(
      makeCard({
        contextWindowUsed: 950_000,
        contextWindowSize: 1_000_000,
        contextUsagePercent: 95,
        contextUsageHistory: makeHistory(4, { firstPercent: 40, lastPercent: 95 }),
      }),
    )

    const chart = screen.getByTestId('context-usage-trend-mini-chart')
    expect(chart.getAttribute('data-status')).toBeNull()
    expect(chart.getAttribute('data-latest-percent')).toBe('95')
    const linePath = chart.querySelectorAll('path')[1]!
    expect(linePath.getAttribute('class')).toContain('stroke-gray-400')
  })

  it('renders the trend chart even when the snapshot contextWindowSize is unknown (history is the source for the chart)', () => {
    renderCard(
      makeCard({
        contextWindowUsed: null,
        contextWindowSize: null,
        contextUsagePercent: null,
        contextUsageHistory: makeHistory(3, { firstPercent: 20, lastPercent: 65 }),
      }),
    )

    const chart = screen.getByTestId('context-usage-trend-mini-chart')
    expect(chart.getAttribute('data-latest-percent')).toBe('65')
    expect(chart.getAttribute('data-status')).toBeNull()
    expect(screen.queryByTestId('context-health-indicator')).not.toBeInTheDocument()
  })

  it('does not write or mutate any domain state when reading history (Pulse zone stays read-only)', () => {
    const card = makeCard({
      contextWindowUsed: 720_000,
      contextWindowSize: 1_000_000,
      contextUsagePercent: 72,
      contextUsageHistory: makeHistory(4, { firstPercent: 30, lastPercent: 72 }),
    })
    const snapshot = JSON.parse(JSON.stringify(card))

    renderCard(card)

    expect(card).toEqual(snapshot)
    // Sanity check — the card stays the same object after render (no inner mutation either).
    expect(card.contextUsageHistory).toEqual(snapshot.contextUsageHistory)
  })
})
