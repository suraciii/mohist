import '@testing-library/jest-dom'
import { act, render } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { useState } from 'react'
import { SessionTranscriptLayout } from '../SessionTranscriptLayout'
import { ToolRowView } from './index'
import type { DisplayToolPart, DisplayTurn } from '../../model/session-transcript-display'

const STARTED_AT = '2026-01-01T00:00:00.000Z'

function makeToolPart(overrides: Partial<DisplayToolPart>): DisplayToolPart {
  return {
    id: 'tool-1',
    partType: 'tool',
    toolCallId: 'tc-1',
    normalizedName: 'bash',
    toolName: 'bash',
    status: 'completed',
    startedAt: STARTED_AT,
    completedAt: '2026-01-01T00:00:01.000Z',
    hasError: false,
    isContextTool: false,
    ...overrides,
  } as DisplayToolPart
}

function makeTurn(overrides: {
  id?: string
  startedAt?: string
  completedAt?: string | null
  assistantParts?: DisplayTurn['assistantParts']
}): DisplayTurn {
  const startedAt = overrides.startedAt ?? '2024-05-15T10:00:00.000Z'
  return {
    id: overrides.id ?? 'turn-1',
    startedAt,
    completedAt: overrides.completedAt ?? null,
    prompt: {
      role: 'mohist',
      text: 'prompt body',
      kind: 'followup',
      sentAt: startedAt,
    },
    assistantParts: overrides.assistantParts ?? [],
    changedFiles: [],
    state: 'idle',
  }
}

function findDuration(container: HTMLElement) {
  return container.querySelector('[data-testid="tool-row-duration"]')
}

describe('ToolRowView — live-ticking duration on in-progress rows', () => {
  it('renders no duration for a running row when now is not provided', () => {
    const part = makeToolPart({ id: 'no-now', status: 'running' })

    const { container } = render(<ToolRowView part={part} />)

    expect(findDuration(container)).toBeNull()
  })

  it('renders a live duration for a running row when now is provided, tagged data-duration-mode="live"', () => {
    const startedMs = new Date(STARTED_AT).getTime()
    const part = makeToolPart({ id: 'running-now', status: 'running' })

    const { container } = render(
      <ToolRowView part={part} now={startedMs + 4700} />,
    )

    const duration = findDuration(container)
    expect(duration?.getAttribute('data-duration-mode')).toBe('live')
    expect(duration?.textContent).toBe('4.7s')
  })

  it('renders a live duration for a pending row when now is provided', () => {
    const startedMs = new Date(STARTED_AT).getTime()
    const part = makeToolPart({ id: 'pending-now', status: 'pending' })

    const { container } = render(
      <ToolRowView part={part} now={startedMs + 60_500} />,
    )

    const duration = findDuration(container)
    expect(duration?.getAttribute('data-duration-mode')).toBe('live')
    expect(duration?.textContent).toBe('1m 00s')
  })

  it('renders the frozen delta for a completed row, ignoring now', () => {
    const part = makeToolPart({
      id: 'completed-now',
      status: 'completed',
      completedAt: '2026-01-01T00:00:04.700Z',
    })

    const { container } = render(
      <ToolRowView part={part} now={new Date(STARTED_AT).getTime() + 100_000} />,
    )

    const duration = findDuration(container)
    expect(duration?.getAttribute('data-duration-mode')).toBe('frozen')
    expect(duration?.textContent).toBe('4.7s')
  })

  it('renders the frozen delta for a failed row, ignoring now', () => {
    const part = makeToolPart({
      id: 'failed-now',
      status: 'failed',
      completedAt: '2026-01-01T00:00:02.500Z',
    })

    const { container } = render(
      <ToolRowView part={part} now={new Date(STARTED_AT).getTime() + 100_000} />,
    )

    const duration = findDuration(container)
    expect(duration?.getAttribute('data-duration-mode')).toBe('frozen')
    expect(duration?.textContent).toBe('2.5s')
  })

  it('renders the frozen delta for a cancelled row, ignoring now', () => {
    const part = makeToolPart({
      id: 'cancelled-now',
      status: 'cancelled',
      completedAt: '2026-01-01T00:00:01.000Z',
    })

    const { container } = render(
      <ToolRowView part={part} now={new Date(STARTED_AT).getTime() + 100_000} />,
    )

    const duration = findDuration(container)
    expect(duration?.getAttribute('data-duration-mode')).toBe('frozen')
    expect(duration?.textContent).toBe('1.0s')
  })

  it('freezes on transition from running to completed (now prop cleared)', () => {
    const startedMs = new Date(STARTED_AT).getTime()
    const running = makeToolPart({ id: 'freeze-1', status: 'running' })
    const completed = makeToolPart({
      id: 'freeze-1',
      status: 'completed',
      completedAt: '2026-01-01T00:00:04.700Z',
    })

    const { container, rerender } = render(
      <ToolRowView part={running} now={startedMs + 4700} />,
    )

    expect(findDuration(container)?.textContent).toBe('4.7s')
    expect(findDuration(container)?.getAttribute('data-duration-mode')).toBe('live')

    rerender(<ToolRowView part={completed} />)

    expect(findDuration(container)?.textContent).toBe('4.7s')
    expect(findDuration(container)?.getAttribute('data-duration-mode')).toBe('frozen')
  })
})

describe('SessionTranscriptLayout — ticking duration wires useNow to in-progress rows only', () => {
  beforeEach(() => {
    vi.useFakeTimers()
    vi.setSystemTime(new Date('2026-01-01T00:00:00.000Z'))
  })

  afterEach(() => {
    vi.useRealTimers()
  })

  function makeRunningTurn() {
    const tool = makeToolPart({
      id: 'live-tool',
      toolCallId: 'tc-live',
      status: 'running',
      startedAt: STARTED_AT,
    })
    return makeTurn({ id: 'turn-live', assistantParts: [tool] })
  }

  function makeTerminalTurn() {
    const tool = makeToolPart({
      id: 'done-tool',
      toolCallId: 'tc-done',
      status: 'completed',
      startedAt: '2026-01-01T00:00:00.000Z',
      completedAt: '2026-01-01T00:00:02.000Z',
    })
    return makeTurn({ id: 'turn-done', assistantParts: [tool] })
  }

  it('renders a live duration on a running tool row in a running session, ticking once per second', () => {
    const turns: DisplayTurn[] = [makeRunningTurn()]

    const { container } = render(
      <SessionTranscriptLayout turns={turns} isRunning />,
    )

    const duration = findDuration(container)
    expect(duration?.getAttribute('data-duration-mode')).toBe('live')

    act(() => {
      vi.advanceTimersByTime(1000)
    })

    expect(findDuration(container)?.textContent).toBe('1.0s')

    act(() => {
      vi.advanceTimersByTime(4000)
    })

    expect(findDuration(container)?.textContent).toBe('5.0s')
  })

  it('stops ticking when the session transitions to not running mid-tool', () => {
    function Host() {
      const [running, setRunning] = useState(true)
      return (
        <div>
          <button data-testid="stop" onClick={() => setRunning(false)}>stop</button>
          <SessionTranscriptLayout turns={[makeRunningTurn()]} isRunning={running} />
        </div>
      )
    }

    const { container, getByTestId } = render(<Host />)

    act(() => {
      vi.advanceTimersByTime(2000)
    })
    expect(findDuration(container)?.textContent).toBe('2.0s')

    act(() => {
      getByTestId('stop').click()
    })

    expect(findDuration(container)).toBeNull()

    act(() => {
      vi.advanceTimersByTime(60_000)
    })

    expect(findDuration(container)).toBeNull()
  })

  it('does not render a live duration on a terminal row even when the session is running', () => {
    const turns: DisplayTurn[] = [makeTerminalTurn()]

    const { container } = render(
      <SessionTranscriptLayout turns={turns} isRunning />,
    )

    const duration = findDuration(container)
    expect(duration).not.toBeNull()
    expect(duration?.getAttribute('data-duration-mode')).toBe('frozen')
    expect(duration?.textContent).toBe('2.0s')

    act(() => {
      vi.advanceTimersByTime(60_000)
    })

    expect(findDuration(container)?.textContent).toBe('2.0s')
  })

  it('renders no duration on a running tool row when the session is not running', () => {
    const turns: DisplayTurn[] = [makeRunningTurn()]

    const { container } = render(
      <SessionTranscriptLayout turns={turns} isRunning={false} />,
    )

    expect(findDuration(container)).toBeNull()

    act(() => {
      vi.advanceTimersByTime(60_000)
    })

    expect(findDuration(container)).toBeNull()
  })

  it('only passes now to in-progress rows; terminal rows remain unaffected by per-second ticks', () => {
    const turns: DisplayTurn[] = [makeTerminalTurn(), makeRunningTurn()]

    const { container } = render(
      <SessionTranscriptLayout turns={turns} isRunning />,
    )

    const durations = container.querySelectorAll('[data-testid="tool-row-duration"]')
    expect(durations).toHaveLength(2)

    const frozen = container.querySelector('[data-duration-mode="frozen"]')
    const live = container.querySelector('[data-duration-mode="live"]')
    expect(frozen?.textContent).toBe('2.0s')
    expect(live).not.toBeNull()

    act(() => {
      vi.advanceTimersByTime(3000)
    })

    const frozenAfter = container.querySelector('[data-duration-mode="frozen"]')
    const liveAfter = container.querySelector('[data-duration-mode="live"]')
    expect(frozenAfter?.textContent).toBe('2.0s')
    expect(liveAfter?.textContent).toBe('3.0s')
  })

  it('uses the provided now value verbatim when injected', () => {
    const fixedNow = new Date('2026-01-01T00:00:42.500Z').getTime()
    const turns: DisplayTurn[] = [makeRunningTurn()]

    const { container } = render(
      <SessionTranscriptLayout turns={turns} isRunning now={fixedNow} />,
    )

    expect(findDuration(container)?.textContent).toBe('42.5s')

    act(() => {
      vi.advanceTimersByTime(60_000)
    })

    expect(findDuration(container)?.textContent).toBe('42.5s')
  })

  it('freezes a tool row at the finalized delta when the tool transitions to completed mid-session', () => {
    const initial = makeRunningTurn()
    const updatedTool: DisplayToolPart = {
      ...(initial.assistantParts[0] as DisplayToolPart),
      status: 'completed',
      completedAt: '2026-01-01T00:00:03.500Z',
    }
    const updated: DisplayTurn = {
      ...initial,
      assistantParts: [updatedTool],
    }

    const { container, rerender } = render(
      <SessionTranscriptLayout turns={[initial]} isRunning />,
    )

    act(() => {
      vi.advanceTimersByTime(1000)
    })
    expect(findDuration(container)?.textContent).toBe('1.0s')

    rerender(<SessionTranscriptLayout turns={[updated]} isRunning />)

    const duration = findDuration(container)
    expect(duration?.getAttribute('data-duration-mode')).toBe('frozen')
    expect(duration?.textContent).toBe('3.5s')

    act(() => {
      vi.advanceTimersByTime(60_000)
    })

    expect(findDuration(container)?.textContent).toBe('3.5s')
  })

  it('tears down the interval when the layout unmounts', () => {
    const setIntervalSpy = vi.spyOn(globalThis, 'setInterval')
    const clearIntervalSpy = vi.spyOn(globalThis, 'clearInterval')

    const turns: DisplayTurn[] = [makeRunningTurn()]
    const { unmount } = render(
      <SessionTranscriptLayout turns={turns} isRunning />,
    )

    expect(setIntervalSpy).toHaveBeenCalled()

    unmount()

    expect(clearIntervalSpy).toHaveBeenCalled()

    setIntervalSpy.mockRestore()
    clearIntervalSpy.mockRestore()
  })
})
