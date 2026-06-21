// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { describe, expect, it, vi, beforeEach } from 'vitest'
import type { SessionCard } from '@/widgets/coder-session/model/activity-cards'
import { CompactSessionCard } from './CompactSessionCard'

function makeCard(overrides: Partial<SessionCard> = {}): SessionCard {
  return {
    issueId: 'issue-1',
    issueNumber: '12',
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
    toolCallCount: 4,
    toolErrorCount: 1,
    ...overrides,
  }
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
      }),
    )

    const indicator = screen.getByTestId('context-health-indicator')
    expect(indicator).toHaveAttribute('data-status', 'red')
    expect(indicator).toHaveTextContent('95%')
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
})
