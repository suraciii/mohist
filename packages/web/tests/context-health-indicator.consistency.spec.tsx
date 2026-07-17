import '@testing-library/jest-dom'
import { render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { describe, expect, it } from 'vitest'
import type { SessionCard as SessionCardType } from '@/entities/agent-ops'
import { ActiveSessionCard } from '@/widgets/coder-session'
import { CompactSessionCard } from '@/widgets/dashboard-pulse'

function makeListRow(overrides: Partial<SessionCardType> = {}): SessionCardType {
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
    failureCategory: null,
    inputTokens: null,
    outputTokens: null,
    totalTokens: null,
    costAmount: null,
    costCurrency: null,
    contextWindowUsed: null,
    contextWindowSize: null,
    contextUsagePercent: null,
    toolCallCount: null,
    toolErrorCount: null,
    healthStatus: null,
    ...overrides,
  }
}

function renderListRow(card: SessionCardType) {
  return render(
    <MemoryRouter>
      <ActiveSessionCard card={card} now={new Date('2026-01-01T00:10:00Z').getTime()} />
    </MemoryRouter>,
  )
}

function renderPulse(card: SessionCardType) {
  return render(
    <MemoryRouter>
      <CompactSessionCard card={card} />
    </MemoryRouter>,
  )
}

interface IndicatorAttrs {
  dataStatus: string | null
  dataSeverity: string | null
  role: string | null
  ariaLive: string | null
  title: string | null
  ariaLabel: string | null
  hasGlyph: boolean
  textContent: string | null
}

function snapshotIndicator(): IndicatorAttrs {
  const indicator = screen.getByTestId('context-health-indicator')
  return {
    dataStatus: indicator.getAttribute('data-status'),
    dataSeverity: indicator.getAttribute('data-severity'),
    role: indicator.getAttribute('role'),
    ariaLive: indicator.getAttribute('aria-live'),
    title: indicator.getAttribute('title'),
    ariaLabel: indicator.getAttribute('aria-label'),
    hasGlyph: screen.queryByTestId('context-health-glyph') !== null,
    textContent: indicator.textContent,
  }
}

describe('ContextHealthIndicator — cross-surface consistency', () => {
  it('produces identical alert treatment at green (30%) on both surfaces', () => {
    const usage = {
      contextWindowUsed: 300_000,
      contextWindowSize: 1_000_000,
      contextUsagePercent: 30,
      healthStatus: 'green',
    }

    const { unmount: unmountList } = renderListRow(makeListRow(usage))
    const list = snapshotIndicator()
    unmountList()

    renderPulse(makeListRow(usage))
    const pulse = snapshotIndicator()

    expect(pulse).toEqual(list)
  })

  it('produces identical alert treatment at yellow (72%) on both surfaces', () => {
    const usage = {
      contextWindowUsed: 720_000,
      contextWindowSize: 1_000_000,
      contextUsagePercent: 72,
      healthStatus: 'yellow',
    }

    const { unmount: unmountList } = renderListRow(makeListRow(usage))
    const list = snapshotIndicator()
    unmountList()

    renderPulse(makeListRow(usage))
    const pulse = snapshotIndicator()

    expect(pulse).toEqual(list)
  })

  it('produces identical alert treatment at red (95%) on both surfaces', () => {
    const usage = {
      contextWindowUsed: 950_000,
      contextWindowSize: 1_000_000,
      contextUsagePercent: 95,
      healthStatus: 'red',
    }

    const { unmount: unmountList } = renderListRow(makeListRow(usage))
    const list = snapshotIndicator()
    unmountList()

    renderPulse(makeListRow(usage))
    const pulse = snapshotIndicator()

    expect(pulse).toEqual(list)
  })

  it('hides the indicator consistently when contextWindowSize is null on both surfaces', () => {
    const usage = {
      contextWindowUsed: null,
      contextWindowSize: null,
      contextUsagePercent: null,
    }

    const { unmount: unmountList } = renderListRow(makeListRow(usage))
    expect(screen.queryByTestId('context-health-indicator')).toBeNull()
    unmountList()

    renderPulse(makeListRow(usage))
    expect(screen.queryByTestId('context-health-indicator')).toBeNull()
  })

  it('at red threshold both surfaces carry role="alert", aria-live="polite", and identical descriptive tooltip', () => {
    const usage = {
      contextWindowUsed: 950_000,
      contextWindowSize: 1_000_000,
      contextUsagePercent: 95,
      healthStatus: 'red',
    }

    const { unmount: unmountList } = renderListRow(makeListRow(usage))
    const list = snapshotIndicator()
    unmountList()

    renderPulse(makeListRow(usage))
    const pulse = snapshotIndicator()

    expect(list.role).toBe('alert')
    expect(pulse.role).toBe('alert')
    expect(list.ariaLive).toBe('polite')
    expect(pulse.ariaLive).toBe('polite')
    expect(list.title).toBe('Context window 95% full — at limit, compact or reset recommended')
    expect(pulse.title).toBe('Context window 95% full — at limit, compact or reset recommended')
    expect(list.hasGlyph).toBe(true)
    expect(pulse.hasGlyph).toBe(true)
  })

  it('at yellow threshold both surfaces carry role="status" and identical descriptive tooltip', () => {
    const usage = {
      contextWindowUsed: 720_000,
      contextWindowSize: 1_000_000,
      contextUsagePercent: 72,
      healthStatus: 'yellow',
    }

    const { unmount: unmountList } = renderListRow(makeListRow(usage))
    const list = snapshotIndicator()
    unmountList()

    renderPulse(makeListRow(usage))
    const pulse = snapshotIndicator()

    expect(list.role).toBe('status')
    expect(pulse.role).toBe('status')
    expect(list.ariaLive).toBeNull()
    expect(pulse.ariaLive).toBeNull()
    expect(list.title).toBe('Context window 72% full — near limit')
    expect(pulse.title).toBe('Context window 72% full — near limit')
    expect(list.hasGlyph).toBe(true)
    expect(pulse.hasGlyph).toBe(true)
  })

  it('at green threshold both surfaces are quiet (no role, no glyph, simple tooltip)', () => {
    const usage = {
      contextWindowUsed: 300_000,
      contextWindowSize: 1_000_000,
      contextUsagePercent: 30,
      healthStatus: 'green',
    }

    const { unmount: unmountList } = renderListRow(makeListRow(usage))
    const list = snapshotIndicator()
    unmountList()

    renderPulse(makeListRow(usage))
    const pulse = snapshotIndicator()

    expect(list.role).toBeNull()
    expect(pulse.role).toBeNull()
    expect(list.ariaLive).toBeNull()
    expect(pulse.ariaLive).toBeNull()
    expect(list.hasGlyph).toBe(false)
    expect(pulse.hasGlyph).toBe(false)
    expect(list.title).toBe('Context usage 30%')
    expect(pulse.title).toBe('Context usage 30%')
  })
})
