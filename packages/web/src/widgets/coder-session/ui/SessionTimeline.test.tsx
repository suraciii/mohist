import '@testing-library/jest-dom'
import { render, screen, within } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import type { Round, ContextHealthState } from '../model/useSessionTimeline'
import { SessionTimeline } from './SessionTimeline'
function makeRound(overrides: Partial<Round> = {}): Round {
  return {
    roundIndex: 0,
    label: 'proposal.md',
    startedAt: '2024-01-01T00:00:00Z',
    completedAt: '2024-01-01T00:00:30Z',
    userText: 'do the thing',
    agentText: 'doing the thing',
    thoughtText: '',
    toolCalls: [],
    recoveryEvents: [],
    compactions: [],
    ...overrides,
  }
}

function makeContextHealth(overrides: Partial<ContextHealthState> = {}): ContextHealthState {
  return {
    status: 'green',
    contextWindowUsed: 450_000,
    contextWindowSize: 1_000_000,
    contextUsagePercent: 45,
    recordedAt: '2024-01-01T00:00:00Z',
    ...overrides,
  }
}

describe('SessionTimeline', () => {
  it('renders a loading state when isLoading is true', () => {
    render(
      <SessionTimeline
        rounds={[]}
        isLoading
        isStreaming={false}
        currentStage="build"
        isLive={false}
        recoveryStatus={null}
        planProgress={null}
      />,
    )
    expect(screen.getByText('Loading session...')).toBeInTheDocument()
  })

  it('renders the empty state when no rounds exist and not streaming', () => {
    render(
      <SessionTimeline
        rounds={[]}
        isLoading={false}
        isStreaming={false}
        currentStage="build"
        isLive={false}
        recoveryStatus={null}
        planProgress={null}
      />,
    )
    expect(screen.getByText('No agent activity yet')).toBeInTheDocument()
  })

  it('hides the context health section when contextHealth is not provided', () => {
    render(
      <SessionTimeline
        rounds={[makeRound()]}
        isLoading={false}
        isStreaming={false}
        currentStage="build"
        isLive={false}
        recoveryStatus={null}
        planProgress={null}
      />,
    )
    expect(screen.queryByTestId('context-health-section')).toBeNull()
  })

  it('hides the context health section when no recovery callbacks are provided', () => {
    render(
      <SessionTimeline
        rounds={[makeRound()]}
        isLoading={false}
        isStreaming={false}
        currentStage="build"
        isLive={false}
        recoveryStatus={null}
        planProgress={null}
        contextHealth={makeContextHealth({ status: 'red', contextUsagePercent: 95 })}
      />,
    )
    // No Compact/Reset handlers wired up => no actionable health banner.
    expect(screen.queryByTestId('context-health-section')).toBeNull()
  })

  it('renders the context health bar above the rounds when contextHealth and callbacks are provided', () => {
    render(
      <SessionTimeline
        rounds={[makeRound()]}
        isLoading={false}
        isStreaming={false}
        currentStage="build"
        isLive={false}
        recoveryStatus={null}
        planProgress={null}
        contextHealth={makeContextHealth({ status: 'red', contextUsagePercent: 95 })}
        onCompact={() => {}}
        onReset={() => {}}
      />,
    )
    const section = screen.getByTestId('context-health-section')
    expect(section).toBeInTheDocument()
    const bar = within(section).getByTestId('context-health-bar')
    expect(bar).toHaveAttribute('data-status', 'red')
    expect(within(screen.getByTestId('workflow-status-stage-build')).getByText('Build').previousElementSibling).toHaveClass('bg-info')
  })

  it('renders only real workflow stages in the status timeline', () => {
    render(
      <SessionTimeline
        rounds={[makeRound()]}
        isLoading={false}
        isStreaming={false}
        currentStage="done"
        isLive={false}
        recoveryStatus={null}
        planProgress={null}
      />,
    )

    for (const label of ['Plan', 'Build', 'Check', 'Integrate']) {
      expect(screen.getByText(label)).toBeInTheDocument()
    }
    for (const stage of ['plan', 'build', 'check', 'integrate']) {
      expect(screen.getByTestId(`workflow-status-stage-${stage}`)).toHaveAttribute('data-state', 'completed')
    }
    expect(screen.queryByText(/^Done$/)).not.toBeInTheDocument()
  })

  it('invokes onCompact when the user clicks the Compact action in the warning banner', () => {
    const onCompact = vi.fn()
    render(
      <SessionTimeline
        rounds={[makeRound()]}
        isLoading={false}
        isStreaming={false}
        currentStage="build"
        isLive={false}
        recoveryStatus={null}
        planProgress={null}
        contextHealth={makeContextHealth({ status: 'red', contextUsagePercent: 92 })}
        onCompact={onCompact}
        onReset={() => {}}
      />,
    )
    const section = screen.getByTestId('context-health-section')
    within(section).getByRole('button', { name: 'Compact' }).click()
    expect(onCompact).toHaveBeenCalledTimes(1)
  })

  it('invokes onReset when the user clicks the Reset action in the warning banner', () => {
    const onReset = vi.fn()
    render(
      <SessionTimeline
        rounds={[makeRound()]}
        isLoading={false}
        isStreaming={false}
        currentStage="build"
        isLive={false}
        recoveryStatus={null}
        planProgress={null}
        contextHealth={makeContextHealth({ status: 'red', contextUsagePercent: 92 })}
        onCompact={() => {}}
        onReset={onReset}
      />,
    )
    const section = screen.getByTestId('context-health-section')
    within(section).getByRole('button', { name: 'Reset' }).click()
    expect(onReset).toHaveBeenCalledTimes(1)
  })

  it('renders compaction entries as info-style timeline items', () => {
    const round = makeRound({
      compactions: [
        {
          id: 'comp-1',
          strategy: 'summary',
          contextWindowUsedBefore: 950_000,
          contextWindowUsedAfter: 400_000,
          contextWindowSize: 1_000_000,
          summary: 'Kept the original task instructions.',
          timestamp: 1_704_067_200_000,
          recordedAt: '2024-01-01T00:00:00Z',
        },
      ],
    })
    render(
      <SessionTimeline
        rounds={[round]}
        isLoading={false}
        isStreaming={false}
        currentStage="build"
        isLive={false}
        recoveryStatus={null}
        planProgress={null}
      />,
    )
    const entry = screen.getByTestId('compaction-timeline-entry')
    expect(entry).toBeInTheDocument()
    expect(within(entry).getByTestId('compaction-timeline-title')).toHaveTextContent('Context compacted (summary)')
    expect(within(entry).getByTestId('compaction-timeline-counts')).toHaveTextContent('950.0k tokens')
  })

  it('renders the compact compaction summary atop the transcript when any round carries compaction events', () => {
    const round = makeRound({
      compactions: [
        {
          id: 'comp-1',
          strategy: 'summary',
          contextWindowUsedBefore: 950_000,
          contextWindowUsedAfter: 400_000,
          contextWindowSize: 1_000_000,
          summary: 'Kept the original task instructions.',
          timestamp: 1_704_067_200_000,
          recordedAt: '2024-01-01T00:00:00Z',
        },
      ],
    })
    render(
      <SessionTimeline
        rounds={[round]}
        isLoading={false}
        isStreaming={false}
        currentStage="build"
        isLive={false}
        recoveryStatus={null}
        planProgress={null}
      />,
    )
    // The compact summary is rendered in the page (not nested inside the round).
    const summary = screen.getByTestId('compaction-compact-summary')
    expect(summary).toBeInTheDocument()
    expect(summary).toHaveAttribute('data-compaction-count', '1')
    // The summary must come before the per-round entry in document order
    // (i.e. it is "atop the transcript", not buried inside the round).
    const root = screen.getByText('Agent Session').closest('[class*="rounded-lg"]') as HTMLElement | null
    expect(root).not.toBeNull()
    const all = Array.from(root!.querySelectorAll('[data-testid]'))
    const summaryIdx = all.findIndex((el) => el.getAttribute('data-testid') === 'compaction-compact-summary')
    const entryIdx = all.findIndex((el) => el.getAttribute('data-testid') === 'compaction-timeline-entry')
    expect(summaryIdx).toBeGreaterThanOrEqual(0)
    expect(entryIdx).toBeGreaterThanOrEqual(0)
    expect(summaryIdx).toBeLessThan(entryIdx)
  })

  it('aggregates compaction events from multiple rounds into one compact summary', () => {
    const round1 = makeRound({
      roundIndex: 0,
      compactions: [
        {
          id: 'comp-a',
          strategy: 'summary',
          contextWindowUsedBefore: 900_000,
          contextWindowUsedAfter: 400_000,
          contextWindowSize: 1_000_000,
          timestamp: 1_704_067_200_000,
          recordedAt: '2024-01-01T00:00:00Z',
        },
      ],
    })
    const round2 = makeRound({
      roundIndex: 1,
      label: 'Round 2',
      compactions: [
        {
          id: 'comp-b',
          strategy: 'sliding-window',
          contextWindowUsedBefore: 800_000,
          contextWindowUsedAfter: 300_000,
          contextWindowSize: 1_000_000,
          timestamp: 1_704_153_600_000,
          recordedAt: '2024-01-01T00:00:30Z',
        },
      ],
    })
    render(
      <SessionTimeline
        rounds={[round1, round2]}
        isLoading={false}
        isStreaming={false}
        currentStage="build"
        isLive={false}
        recoveryStatus={null}
        planProgress={null}
      />,
    )
    const summary = screen.getByTestId('compaction-compact-summary')
    expect(summary).toBeInTheDocument()
    expect(summary).toHaveAttribute('data-compaction-count', '2')
    expect(screen.getByTestId('compaction-compact-summary-count')).toHaveTextContent('2 compactions')
  })

  it('omits the compact compaction summary when no round carries compaction events', () => {
    render(
      <SessionTimeline
        rounds={[makeRound()]}
        isLoading={false}
        isStreaming={false}
        currentStage="build"
        isLive={false}
        recoveryStatus={null}
        planProgress={null}
      />,
    )
    expect(screen.queryByTestId('compaction-compact-summary')).toBeNull()
  })

  it('keeps the per-round CompactionTimelineEntry intact when the compact summary is rendered', () => {
    const round = makeRound({
      compactions: [
        {
          id: 'comp-1',
          strategy: 'summary',
          contextWindowUsedBefore: 950_000,
          contextWindowUsedAfter: 400_000,
          contextWindowSize: 1_000_000,
          summary: 'Kept the original task instructions.',
          timestamp: 1_704_067_200_000,
          recordedAt: '2024-01-01T00:00:00Z',
        },
      ],
    })
    render(
      <SessionTimeline
        rounds={[round]}
        isLoading={false}
        isStreaming={false}
        currentStage="build"
        isLive={false}
        recoveryStatus={null}
        planProgress={null}
      />,
    )
    // Per-round detail (before/after token counts) must remain visible,
    // sitting below the aggregate summary (which is at the transcript top).
    const entry = screen.getByTestId('compaction-timeline-entry')
    expect(entry).toBeInTheDocument()
    expect(within(entry).getByTestId('compaction-timeline-counts')).toHaveTextContent('950.0k tokens')
    expect(within(entry).getByTestId('compaction-timeline-counts')).toHaveTextContent('400.0k tokens')
  })

  it('does not render a health bar when contextWindowSize is zero', () => {
    render(
      <SessionTimeline
        rounds={[makeRound()]}
        isLoading={false}
        isStreaming={false}
        currentStage="build"
        isLive={false}
        recoveryStatus={null}
        planProgress={null}
        contextHealth={makeContextHealth({ contextWindowSize: 0, contextWindowUsed: 0, contextUsagePercent: null })}
        onCompact={() => {}}
        onReset={() => {}}
      />,
    )
    expect(screen.queryByTestId('context-health-section')).toBeNull()
  })

  it('does not render a health bar when server health status is absent', () => {
    render(
      <SessionTimeline
        rounds={[makeRound()]}
        isLoading={false}
        isStreaming={false}
        currentStage="build"
        isLive={false}
        recoveryStatus={null}
        planProgress={null}
        contextHealth={makeContextHealth({ status: null, contextUsagePercent: 72 })}
        onCompact={() => {}}
        onReset={() => {}}
      />,
    )
    expect(screen.queryByTestId('context-health-section')).toBeNull()
  })
})
