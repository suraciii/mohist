import '@testing-library/jest-dom'
import { act, fireEvent, render } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { useState } from 'react'
import { SessionTranscriptLayout } from './SessionTranscriptLayout'
import { CurrentActivityBar } from './CurrentActivityBar'
import { selectActiveToolCall } from '../model/select-active-tool-call'
import type { DisplayToolPart, DisplayTurn } from '../model/session-transcript-display'

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

function findBar(container: HTMLElement) {
  return container.querySelector('[data-testid="transcript-current-activity-bar"]')
}

function findJump(container: HTMLElement) {
  return container.querySelector('[data-testid="transcript-current-activity-bar-jump"]')
}

describe('CurrentActivityBar — standalone render gating and click-to-jump', () => {
  let scrollIntoViewSpy: ReturnType<typeof vi.spyOn>

  beforeEach(() => {
    scrollIntoViewSpy = vi.spyOn(Element.prototype, 'scrollIntoView').mockImplementation(() => {})
  })

  afterEach(() => {
    scrollIntoViewSpy.mockRestore()
  })

  function setupScrollContainerWithRow(toolCallId: string) {
    const container = document.createElement('div')
    document.body.appendChild(container)
    const row = document.createElement('div')
    row.setAttribute('data-tool-call-id', toolCallId)
    container.appendChild(row)
    return { container, row, ref: { current: container } }
  }

  it('renders the verb-led title and a live duration for the active tool', () => {
    const startedMs = new Date(STARTED_AT).getTime()
    const activeTool = makeToolPart({
      id: 'live-tool',
      toolCallId: 'tc-live',
      normalizedName: 'read',
      status: 'running',
      startedAt: STARTED_AT,
      input: JSON.stringify({ filePath: 'src/foo.ts' }),
    })
    const { ref } = setupScrollContainerWithRow('tc-live')

    const { container } = render(
      <CurrentActivityBar activeTool={activeTool} now={startedMs + 4700} scrollContainerRef={ref} />,
    )

    const bar = findBar(container)
    expect(bar).not.toBeNull()
    expect(bar?.getAttribute('data-active-tool-call-id')).toBe('tc-live')
    expect(bar?.className).toContain('sticky')
    expect(bar?.className).toContain('bottom-0')

    const verbTitle = container.querySelector('[data-testid="transcript-current-activity-bar-verb-title"]')
    expect(verbTitle?.textContent).toBe('Reading foo.ts')

    const duration = container.querySelector('[data-testid="transcript-current-activity-bar-duration"]')
    expect(duration?.getAttribute('data-duration-mode')).toBe('live')
    expect(duration?.textContent).toBe('4.7s')
  })

  it('renders the verb-led target after the verb when present', () => {
    const startedMs = new Date(STARTED_AT).getTime()
    const activeTool = makeToolPart({
      id: 'bash-tool',
      toolCallId: 'tc-bash',
      normalizedName: 'bash',
      status: 'running',
      startedAt: STARTED_AT,
      input: JSON.stringify({ command: 'ls -la' }),
    })
    const { ref } = setupScrollContainerWithRow('tc-bash')

    const { container } = render(
      <CurrentActivityBar activeTool={activeTool} now={startedMs + 1000} scrollContainerRef={ref} />,
    )

    const verbTitle = container.querySelector('[data-testid="transcript-current-activity-bar-verb-title"]')
    expect(verbTitle?.textContent).toBe('$ ls -la')
  })

  it('clicks scroll the corresponding tool row into view via scrollIntoView({ block: "center" })', () => {
    const startedMs = new Date(STARTED_AT).getTime()
    const activeTool = makeToolPart({
      id: 'click-tool',
      toolCallId: 'tc-click',
      normalizedName: 'read',
      status: 'running',
      startedAt: STARTED_AT,
    })
    const { ref, row } = setupScrollContainerWithRow('tc-click')

    const { container } = render(
      <CurrentActivityBar activeTool={activeTool} now={startedMs + 1500} scrollContainerRef={ref} />,
    )

    fireEvent.click(findJump(container)!)

    expect(scrollIntoViewSpy).toHaveBeenCalledTimes(1)
    expect(scrollIntoViewSpy.mock.calls[0][0]).toEqual({ block: 'center' })
    expect(scrollIntoViewSpy.mock.instances[0]).toBe(row)
  })

  it('keyboard activation (Enter) also scrolls the row into view', () => {
    const startedMs = new Date(STARTED_AT).getTime()
    const activeTool = makeToolPart({
      id: 'kbd-tool',
      toolCallId: 'tc-kbd',
      normalizedName: 'read',
      status: 'running',
      startedAt: STARTED_AT,
    })
    const { ref, row } = setupScrollContainerWithRow('tc-kbd')

    const { container } = render(
      <CurrentActivityBar activeTool={activeTool} now={startedMs + 1500} scrollContainerRef={ref} />,
    )

    fireEvent.keyDown(findJump(container)!, { key: 'Enter' })

    expect(scrollIntoViewSpy).toHaveBeenCalledTimes(1)
    expect(scrollIntoViewSpy.mock.instances[0]).toBe(row)
  })

  it('keyboard activation (Space) also scrolls the row into view', () => {
    const startedMs = new Date(STARTED_AT).getTime()
    const activeTool = makeToolPart({
      id: 'kbd-space-tool',
      toolCallId: 'tc-kbd-space',
      normalizedName: 'read',
      status: 'running',
      startedAt: STARTED_AT,
    })
    const { ref, row } = setupScrollContainerWithRow('tc-kbd-space')

    const { container } = render(
      <CurrentActivityBar activeTool={activeTool} now={startedMs + 1500} scrollContainerRef={ref} />,
    )

    fireEvent.keyDown(findJump(container)!, { key: ' ' })

    expect(scrollIntoViewSpy).toHaveBeenCalledTimes(1)
    expect(scrollIntoViewSpy.mock.instances[0]).toBe(row)
  })

  it('does not throw when the row is missing; queries the scroll container by data-tool-call-id', () => {
    const startedMs = new Date(STARTED_AT).getTime()
    const activeTool = makeToolPart({
      id: 'no-row-tool',
      toolCallId: 'tc-missing',
      normalizedName: 'read',
      status: 'running',
      startedAt: STARTED_AT,
    })
    const { ref } = setupScrollContainerWithRow('different-id')

    const { container } = render(
      <CurrentActivityBar activeTool={activeTool} now={startedMs + 1500} scrollContainerRef={ref} />,
    )

    expect(() => fireEvent.click(findJump(container)!)).not.toThrow()
    expect(scrollIntoViewSpy).not.toHaveBeenCalled()
  })

  it('escapes tool-call-ids containing selector-special characters', () => {
    const startedMs = new Date(STARTED_AT).getTime()
    const trickyId = 'tc.with:weird[chars]'
    const activeTool = makeToolPart({
      id: 'tricky-tool',
      toolCallId: trickyId,
      normalizedName: 'read',
      status: 'running',
      startedAt: STARTED_AT,
    })
    const { ref, row } = setupScrollContainerWithRow(trickyId)

    const { container } = render(
      <CurrentActivityBar activeTool={activeTool} now={startedMs + 1500} scrollContainerRef={ref} />,
    )

    fireEvent.click(findJump(container)!)

    expect(scrollIntoViewSpy).toHaveBeenCalledTimes(1)
    expect(scrollIntoViewSpy.mock.instances[0]).toBe(row)
  })
})

describe('CurrentActivityBar — gating inside SessionTranscriptLayout', () => {
  beforeEach(() => {
    vi.useFakeTimers()
    vi.setSystemTime(new Date('2026-01-01T00:00:00.000Z'))
  })

  afterEach(() => {
    vi.useRealTimers()
  })

  function makeRunningTurn(overrides: Partial<DisplayToolPart> = {}) {
    const tool = makeToolPart({
      id: 'live-tool',
      toolCallId: 'tc-live',
      status: 'running',
      startedAt: STARTED_AT,
      ...overrides,
    })
    return makeTurn({ id: 'turn-live', assistantParts: [tool] })
  }

  it('renders the bar when the session is running and a tool call is in progress', () => {
    const turns: DisplayTurn[] = [makeRunningTurn()]
    const container = document.createElement('div')
    document.body.appendChild(container)
    const row = document.createElement('div')
    row.setAttribute('data-tool-call-id', 'tc-live')
    container.appendChild(row)
    const scrollContainerRef = { current: container }

    const view = render(
      <SessionTranscriptLayout
        turns={turns}
        isRunning
        scrollContainerRef={scrollContainerRef}
      />,
    )

    const bar = findBar(view.container)
    expect(bar).not.toBeNull()
    expect(bar?.getAttribute('data-active-tool-call-id')).toBe('tc-live')
  })

  it('does not render the bar when the session is not running', () => {
    const turns: DisplayTurn[] = [makeRunningTurn()]
    const container = document.createElement('div')
    document.body.appendChild(container)
    const scrollContainerRef = { current: container }

    const view = render(
      <SessionTranscriptLayout
        turns={turns}
        isRunning={false}
        scrollContainerRef={scrollContainerRef}
      />,
    )

    expect(findBar(view.container)).toBeNull()
  })

  it('does not render the bar when no tool call is in progress', () => {
    const terminal = makeToolPart({
      id: 'done',
      toolCallId: 'tc-done',
      status: 'completed',
      startedAt: STARTED_AT,
      completedAt: '2026-01-01T00:00:02.000Z',
    })
    const turns: DisplayTurn[] = [makeTurn({ id: 'turn-done', assistantParts: [terminal] })]
    const container = document.createElement('div')
    document.body.appendChild(container)
    const scrollContainerRef = { current: container }

    const view = render(
      <SessionTranscriptLayout
        turns={turns}
        isRunning
        scrollContainerRef={scrollContainerRef}
      />,
    )

    expect(findBar(view.container)).toBeNull()
  })

  it('removes the bar when the session ends mid-tool', () => {
    function Host() {
      const [running, setRunning] = useState(true)
      return (
        <div>
          <button data-testid="stop" onClick={() => setRunning(false)}>stop</button>
          <SessionTranscriptLayout
            turns={[makeRunningTurn()]}
            isRunning={running}
            scrollContainerRef={scrollContainerRef}
          />
        </div>
      )
    }

    const container = document.createElement('div')
    document.body.appendChild(container)
    const row = document.createElement('div')
    row.setAttribute('data-tool-call-id', 'tc-live')
    container.appendChild(row)
    const scrollContainerRef = { current: container }

    const { container: viewContainer, getByTestId } = render(<Host />)

    expect(findBar(viewContainer)).not.toBeNull()

    act(() => {
      getByTestId('stop').click()
    })

    expect(findBar(viewContainer)).toBeNull()
  })

  it('the bar duration ticks once per second using the layout-provided now', () => {
    const turns: DisplayTurn[] = [makeRunningTurn()]
    const container = document.createElement('div')
    document.body.appendChild(container)
    const scrollContainerRef = { current: container }

    const view = render(
      <SessionTranscriptLayout
        turns={turns}
        isRunning
        scrollContainerRef={scrollContainerRef}
      />,
    )

    const duration = view.container.querySelector('[data-testid="transcript-current-activity-bar-duration"]')
    expect(duration?.getAttribute('data-duration-mode')).toBe('live')
    expect(duration?.textContent).toBe('0ms')

    act(() => {
      vi.advanceTimersByTime(1000)
    })

    const durationAfter = view.container.querySelector('[data-testid="transcript-current-activity-bar-duration"]')
    expect(durationAfter?.textContent).toBe('1.0s')

    act(() => {
      vi.advanceTimersByTime(4000)
    })

    const durationLater = view.container.querySelector('[data-testid="transcript-current-activity-bar-duration"]')
    expect(durationLater?.textContent).toBe('5.0s')
  })

  it('uses the provided now prop verbatim without starting its own interval for the bar', () => {
    const fixedNow = new Date('2026-01-01T00:00:42.500Z').getTime()
    const turns: DisplayTurn[] = [makeRunningTurn()]
    const container = document.createElement('div')
    document.body.appendChild(container)
    const scrollContainerRef = { current: container }

    const view = render(
      <SessionTranscriptLayout
        turns={turns}
        isRunning
        scrollContainerRef={scrollContainerRef}
        now={fixedNow}
      />,
    )

    const duration = view.container.querySelector('[data-testid="transcript-current-activity-bar-duration"]')
    expect(duration?.textContent).toBe('42.5s')

    act(() => {
      vi.advanceTimersByTime(60_000)
    })

    const durationAfter = view.container.querySelector('[data-testid="transcript-current-activity-bar-duration"]')
    expect(durationAfter?.textContent).toBe('42.5s')
  })

  it('re-targets the new tool row when the active tool transitions to a new call', () => {
    const scrollIntoViewSpy = vi.spyOn(Element.prototype, 'scrollIntoView').mockImplementation(() => {})

    try {
      const container = document.createElement('div')
      document.body.appendChild(container)
      const firstRow = document.createElement('div')
      firstRow.setAttribute('data-tool-call-id', 'tc-first')
      container.appendChild(firstRow)
      const secondRow = document.createElement('div')
      secondRow.setAttribute('data-tool-call-id', 'tc-second')
      container.appendChild(secondRow)
      const scrollContainerRef = { current: container }

      const firstTurn = makeTurn({
        id: 'turn-1',
        assistantParts: [makeToolPart({
          id: 'first-tool',
          toolCallId: 'tc-first',
          status: 'running',
          startedAt: STARTED_AT,
        })],
      })
      const secondTurn = makeTurn({
        id: 'turn-2',
        assistantParts: [makeToolPart({
          id: 'second-tool',
          toolCallId: 'tc-second',
          status: 'running',
          startedAt: STARTED_AT,
        })],
      })

      const view = render(
        <SessionTranscriptLayout
          turns={[firstTurn]}
          isRunning
          scrollContainerRef={scrollContainerRef}
        />,
      )

      expect(findBar(view.container)?.getAttribute('data-active-tool-call-id')).toBe('tc-first')

      act(() => {
        view.rerender(
          <SessionTranscriptLayout
            turns={[firstTurn, secondTurn]}
            isRunning
            scrollContainerRef={scrollContainerRef}
          />,
        )
      })

      expect(findBar(view.container)?.getAttribute('data-active-tool-call-id')).toBe('tc-second')

      fireEvent.click(findJump(view.container)!)

      expect(scrollIntoViewSpy).toHaveBeenCalledTimes(1)
      expect(scrollIntoViewSpy.mock.instances[0]).toBe(secondRow)
    } finally {
      scrollIntoViewSpy.mockRestore()
    }
  })

  it('consumes the active-tool selector so it tracks turns on re-render', () => {
    const turns: DisplayTurn[] = [makeTurn({ id: 'turn-1', assistantParts: [] })]
    expect(selectActiveToolCall(turns)).toBeNull()

    const withLive = [
      makeTurn({
        id: 'turn-1',
        assistantParts: [makeToolPart({ id: 'live', toolCallId: 'tc-live', status: 'running', startedAt: STARTED_AT })],
      }),
    ]
    const active = selectActiveToolCall(withLive)
    expect(active).not.toBeNull()
    expect(active?.toolCallId).toBe('tc-live')
  })
})