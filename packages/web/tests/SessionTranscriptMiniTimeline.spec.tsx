import '@testing-library/jest-dom'
import { act, fireEvent, render } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { SessionTranscriptLayout } from '../src/widgets/session-transcript/ui/SessionTranscriptLayout'
import type { DisplayToolPart, DisplayTurn } from '../src/widgets/session-transcript/model/session-transcript-display'

const STARTED_AT = '2026-01-01T00:00:00.000Z'

function makeToolPart(overrides: Partial<DisplayToolPart> & { id?: string }): DisplayToolPart {
  const { id, toolCallId, normalizedName, status, changedFiles } = overrides
  return {
    id: id ?? 'tool-1',
    partType: 'tool',
    toolCallId: toolCallId ?? id ?? 'tool-1',
    normalizedName: normalizedName ?? 'bash',
    toolName: normalizedName ?? 'bash',
    status: status ?? 'completed',
    startedAt: STARTED_AT,
    completedAt: '2026-01-01T00:00:01.000Z',
    hasError: status === 'failed',
    isContextTool: false,
    changedFiles,
  } as DisplayToolPart
}

function makeTurn(overrides: {
  id?: string
  startedAt?: string
  completedAt?: string | null
  assistantParts?: DisplayTurn['assistantParts']
}): DisplayTurn {
  const startedAt = overrides.startedAt ?? STARTED_AT
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

function setupScrollContainerWithRows(rows: { turnId?: string; toolCallId?: string }[]) {
  const container = document.createElement('div')
  document.body.appendChild(container)
  for (const row of rows) {
    const el = document.createElement('div')
    if (row.turnId) el.setAttribute('data-turn-id', row.turnId)
    if (row.toolCallId) el.setAttribute('data-tool-call-id', row.toolCallId)
    container.appendChild(el)
  }
  return { container, ref: { current: container } }
}

describe('SessionTranscriptLayout mini-timeline rail', () => {
  let scrollIntoViewSpy: ReturnType<typeof vi.spyOn>

  beforeEach(() => {
    vi.useFakeTimers()
    scrollIntoViewSpy = vi.spyOn(Element.prototype, 'scrollIntoView').mockImplementation(() => {})
  })

  afterEach(() => {
    scrollIntoViewSpy.mockRestore()
    vi.useRealTimers()
  })

  it('renders the mini-timeline rail as a sibling of the transcript column', () => {
    const turns = [makeTurn({ id: 't1' }), makeTurn({ id: 't2' })]
    render(<SessionTranscriptLayout turns={turns} isRunning={false} />)

    const rail = document.querySelector('[data-mini-timeline]') as HTMLElement
    expect(rail).not.toBeNull()
    expect(rail.className).toContain('hidden')
    expect(rail.className).toContain('xl:flex')

    const root = document.querySelector('[data-scrollable]') as HTMLElement
    expect(root.className).toContain('xl:flex-row')

    const transcriptColumn = root.querySelector(':scope > [data-mini-timeline] + div') as HTMLElement
    expect(transcriptColumn).not.toBeNull()
    expect(transcriptColumn.className).toContain('flex-1')
    expect(transcriptColumn.querySelector('[role="log"]')).not.toBeNull()
  })

  it('renders one node per turn boundary plus one node per qualifying event', () => {
    const turns: DisplayTurn[] = [
      makeTurn({
        id: 't1',
        assistantParts: [
          makeToolPart({ id: 'r1', normalizedName: 'read' }),
          makeToolPart({ id: 'f1', normalizedName: 'bash', status: 'failed' }),
          makeToolPart({ id: 'e1', normalizedName: 'edit', changedFiles: [{ path: '/x.ts', operation: 'modified' }] }),
        ],
      }),
      makeTurn({ id: 't2' }),
    ]
    render(<SessionTranscriptLayout turns={turns} isRunning={false} />)

    const nodes = document.querySelectorAll('[data-testid="transcript-mini-timeline-node"]')
    expect(nodes.length).toBe(5)
    const kinds = Array.from(nodes).map(n => n.getAttribute('data-mini-timeline-node-kind'))
    expect(kinds).toEqual(['turn', 'read-explore', 'failed', 'file-change', 'turn'])
  })

  it('uses distinct colors for failed / file-change / read-explore node kinds', () => {
    const turns: DisplayTurn[] = [
      makeTurn({
        id: 't1',
        assistantParts: [
          makeToolPart({ id: 'r1', normalizedName: 'read' }),
          makeToolPart({ id: 'f1', normalizedName: 'bash', status: 'failed' }),
          makeToolPart({ id: 'e1', normalizedName: 'edit', changedFiles: [{ path: '/x.ts', operation: 'modified' }] }),
        ],
      }),
    ]
    render(<SessionTranscriptLayout turns={turns} isRunning={false} />)

    const failed = document.querySelector('[data-mini-timeline-node-kind="failed"] [aria-hidden="true"]') as HTMLElement
    const fileChange = document.querySelector('[data-mini-timeline-node-kind="file-change"] [aria-hidden="true"]') as HTMLElement
    const readExplore = document.querySelector('[data-mini-timeline-node-kind="read-explore"] [aria-hidden="true"]') as HTMLElement
    expect(failed.className).toContain('bg-danger')
    expect(fileChange.className).toContain('bg-success')
    expect(readExplore.className).toContain('bg-muted-foreground/40')
  })

  it('does not render event nodes for non-edit non-read non-failed completed tool calls', () => {
    const turns: DisplayTurn[] = [
      makeTurn({
        id: 't1',
        assistantParts: [
          makeToolPart({ id: 'b1', normalizedName: 'bash' }),
          makeToolPart({ id: 't1', normalizedName: 'todowrite' }),
        ],
      }),
    ]
    render(<SessionTranscriptLayout turns={turns} isRunning={false} />)

    const nodes = document.querySelectorAll('[data-testid="transcript-mini-timeline-node"]')
    expect(nodes.length).toBe(1)
    expect(nodes[0].getAttribute('data-mini-timeline-node-kind')).toBe('turn')
  })

  it('single-turn session with events renders one event node per event, not just one turn node', () => {
    const turns: DisplayTurn[] = [
      makeTurn({
        id: 'only',
        assistantParts: [
          makeToolPart({ id: 'r1', normalizedName: 'read' }),
          makeToolPart({ id: 'r2', normalizedName: 'read' }),
          makeToolPart({ id: 'f1', normalizedName: 'bash', status: 'failed' }),
        ],
      }),
    ]
    render(<SessionTranscriptLayout turns={turns} isRunning={false} />)

    const nodes = document.querySelectorAll('[data-testid="transcript-mini-timeline-node"]')
    expect(nodes.length).toBe(4)
    const kinds = Array.from(nodes).map(n => n.getAttribute('data-mini-timeline-node-kind'))
    expect(kinds).toEqual(['turn', 'read-explore', 'read-explore', 'failed'])
  })

  it('activating a turn node scrolls the corresponding row into view via data-turn-id', () => {
    const turns: DisplayTurn[] = [makeTurn({ id: 't1' }), makeTurn({ id: 't2' })]
    const { ref } = setupScrollContainerWithRows([
      { turnId: 't1' },
      { turnId: 't2' },
    ])

    render(<SessionTranscriptLayout turns={turns} isRunning={false} scrollContainerRef={ref} />)

    const turnNodes = Array.from(document.querySelectorAll('[data-testid="transcript-mini-timeline-node"]'))
      .filter(n => n.getAttribute('data-mini-timeline-node-kind') === 'turn')

    fireEvent.click(turnNodes[1])
    act(() => {
      vi.runOnlyPendingTimers()
    })

    expect(scrollIntoViewSpy).toHaveBeenCalled()
    const lastInstance = scrollIntoViewSpy.mock.instances[scrollIntoViewSpy.mock.calls.length - 1] as HTMLElement
    expect(lastInstance.getAttribute('data-turn-id')).toBe('t2')
    expect(scrollIntoViewSpy).toHaveBeenLastCalledWith({ block: 'center' })

    document.body.removeChild(ref.current)
  })

  it('activating an event node scrolls the corresponding row into view via data-tool-call-id', () => {
    const turns: DisplayTurn[] = [
      makeTurn({
        id: 't1',
        assistantParts: [makeToolPart({ id: 'r1', normalizedName: 'read', toolCallId: 'tc-r1' })],
      }),
    ]
    const { ref } = setupScrollContainerWithRows([
      { turnId: 't1' },
      { toolCallId: 'tc-r1' },
    ])

    render(<SessionTranscriptLayout turns={turns} isRunning={false} scrollContainerRef={ref} />)

    const readNode = Array.from(document.querySelectorAll('[data-testid="transcript-mini-timeline-node"]'))
      .find(n => n.getAttribute('data-mini-timeline-node-kind') === 'read-explore')!
    fireEvent.click(readNode)
    act(() => {
      vi.runOnlyPendingTimers()
    })

    expect(scrollIntoViewSpy).toHaveBeenCalled()
    const lastInstance = scrollIntoViewSpy.mock.instances[scrollIntoViewSpy.mock.calls.length - 1] as HTMLElement
    expect(lastInstance.getAttribute('data-tool-call-id')).toBe('tc-r1')

    document.body.removeChild(ref.current)
  })

  it('keyboard Enter and Space activation scrolls the corresponding row', () => {
    const turns: DisplayTurn[] = [makeTurn({ id: 't1' })]
    const { ref } = setupScrollContainerWithRows([{ turnId: 't1' }])

    render(<SessionTranscriptLayout turns={turns} isRunning={false} scrollContainerRef={ref} />)

    const turnNode = document.querySelector('[data-testid="transcript-mini-timeline-node"]') as HTMLElement

    fireEvent.keyDown(turnNode, { key: 'Enter' })
    act(() => {
      vi.runOnlyPendingTimers()
    })
    expect(scrollIntoViewSpy).toHaveBeenCalledTimes(1)
    expect((scrollIntoViewSpy.mock.instances[0] as HTMLElement).getAttribute('data-turn-id')).toBe('t1')

    fireEvent.keyDown(turnNode, { key: ' ' })
    act(() => {
      vi.runOnlyPendingTimers()
    })
    expect(scrollIntoViewSpy).toHaveBeenCalledTimes(2)
    expect((scrollIntoViewSpy.mock.instances[1] as HTMLElement).getAttribute('data-turn-id')).toBe('t1')

    document.body.removeChild(ref.current)
  })

  it('failed tool calls inside context groups produce reachable failed-event nodes', () => {
    const turns: DisplayTurn[] = [
      makeTurn({
        id: 't1',
        assistantParts: [{
          id: 'group-1',
          partType: 'context-group',
          title: 'Explored',
          tools: [
            makeToolPart({ id: 'r1', normalizedName: 'read', toolCallId: 'tc-r1' }),
            makeToolPart({ id: 'f1', normalizedName: 'bash', status: 'failed', toolCallId: 'tc-f1' }),
          ],
          hasError: true,
        }],
      }),
    ]
    const { ref } = setupScrollContainerWithRows([
      { turnId: 't1' },
      { toolCallId: 'tc-r1' },
      { toolCallId: 'tc-f1' },
    ])

    render(<SessionTranscriptLayout turns={turns} isRunning={false} scrollContainerRef={ref} />)

    const failedNode = document.querySelector(
      '[data-mini-timeline-node-kind="failed"]',
    ) as HTMLElement
    expect(failedNode.getAttribute('data-mini-timeline-tool-call-id')).toBe('tc-f1')

    fireEvent.click(failedNode)
    act(() => {
      vi.runOnlyPendingTimers()
    })

    expect(scrollIntoViewSpy).toHaveBeenCalled()
    const lastInstance = scrollIntoViewSpy.mock.instances[scrollIntoViewSpy.mock.calls.length - 1] as HTMLElement
    expect(lastInstance.getAttribute('data-tool-call-id')).toBe('tc-f1')

    document.body.removeChild(ref.current)
  })

  it('does not render the mini-timeline when there are no turns', () => {
    render(<SessionTranscriptLayout turns={[]} isRunning={false} />)
    expect(document.querySelector('[data-mini-timeline]')).toBeNull()
  })
})