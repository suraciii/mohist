import '@testing-library/jest-dom'
import { fireEvent, render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { MiniTimeline } from './MiniTimeline'
import type { DisplayToolPart, DisplayTurn } from '../model/session-transcript-display'
import type { TranscriptLocateTarget } from '../model/use-transcript-locate'

function tool(overrides: Partial<DisplayToolPart> & { id: string }): DisplayToolPart {
  const { id, toolCallId, normalizedName, status, changedFiles } = overrides
  return {
    id,
    partType: 'tool',
    toolCallId: toolCallId ?? id,
    normalizedName: normalizedName ?? 'bash',
    toolName: normalizedName ?? 'bash',
    status: status ?? 'completed',
    startedAt: '',
    completedAt: null,
    hasError: status === 'failed',
    isContextTool: false,
    changedFiles,
  } as DisplayToolPart
}

function turn(overrides: Partial<DisplayTurn> & { id: string }): DisplayTurn {
  return {
    id: overrides.id,
    startedAt: '',
    completedAt: null,
    prompt: { role: 'mohist', text: '', kind: 'initial', sentAt: '' },
    assistantParts: overrides.assistantParts ?? [],
    changedFiles: [],
    state: 'idle',
  } as DisplayTurn
}

describe('MiniTimeline', () => {
  it('renders one node per turn boundary and one node per qualifying event', () => {
    const turns: DisplayTurn[] = [
      turn({
        id: 't1',
        assistantParts: [
          tool({ id: 'r1', normalizedName: 'read' }),
          tool({ id: 'f1', normalizedName: 'bash', status: 'failed' }),
          tool({ id: 'e1', normalizedName: 'edit', changedFiles: [{ path: '/x.ts', operation: 'modified' }] }),
        ],
      }),
      turn({ id: 't2' }),
    ]

    render(<MiniTimeline turns={turns} locate={vi.fn()} />)

    const nodes = screen.getAllByTestId('transcript-mini-timeline-node')
    expect(nodes).toHaveLength(5)
    const kinds = nodes.map(n => n.getAttribute('data-mini-timeline-node-kind'))
    expect(kinds).toEqual(['turn', 'read-explore', 'failed', 'file-change', 'turn'])
  })

  it('calls locate with the turn anchor when a turn node is activated', () => {
    const turns: DisplayTurn[] = [
      turn({ id: 't1', assistantParts: [] }),
      turn({ id: 't2', assistantParts: [] }),
    ]
    const locate = vi.fn<(target: TranscriptLocateTarget) => void>()

    render(<MiniTimeline turns={turns} locate={locate} />)

    const turnNodes = screen.getAllByTestId('transcript-mini-timeline-node')
      .filter(n => n.getAttribute('data-mini-timeline-node-kind') === 'turn')

    fireEvent.click(turnNodes[1])
    expect(locate).toHaveBeenCalledWith({ turnId: 't2' })

    locate.mockClear()
    fireEvent.keyDown(turnNodes[0], { key: 'Enter' })
    expect(locate).toHaveBeenCalledWith({ turnId: 't1' })

    locate.mockClear()
    fireEvent.keyDown(turnNodes[0], { key: ' ' })
    expect(locate).toHaveBeenCalledWith({ turnId: 't1' })
  })

  it('calls locate with the tool anchor when an event node is activated', () => {
    const turns: DisplayTurn[] = [
      turn({
        id: 't1',
        assistantParts: [
          tool({ id: 'r1', normalizedName: 'read', toolCallId: 'tc-r1' }),
          tool({ id: 'f1', normalizedName: 'bash', status: 'failed', toolCallId: 'tc-f1' }),
        ],
      }),
    ]
    const locate = vi.fn<(target: TranscriptLocateTarget) => void>()

    render(<MiniTimeline turns={turns} locate={locate} />)

    const readNode = screen.getAllByTestId('transcript-mini-timeline-node')
      .find(n => n.getAttribute('data-mini-timeline-node-kind') === 'read-explore')!
    fireEvent.click(readNode)
    expect(locate).toHaveBeenCalledWith({ toolCallId: 'tc-r1', groupId: undefined })

    const failedNode = screen.getAllByTestId('transcript-mini-timeline-node')
      .find(n => n.getAttribute('data-mini-timeline-node-kind') === 'failed')!
    fireEvent.click(failedNode)
    expect(locate).toHaveBeenCalledWith({ toolCallId: 'tc-f1', groupId: undefined })
  })

  it('looks up the containing group id when an event node lives inside a context group', () => {
    const turns: DisplayTurn[] = [
      turn({
        id: 't1',
        assistantParts: [{
          id: 'g1',
          partType: 'context-group',
          title: 'Explored',
          tools: [tool({ id: 'r1', normalizedName: 'read', toolCallId: 'tc-r1' })],
          hasError: false,
        }],
      }),
    ]
    const locate = vi.fn<(target: TranscriptLocateTarget) => void>()
    const groupIds = new Map([['tc-r1', 'g1']])

    render(<MiniTimeline turns={turns} locate={locate} groupIdsByToolCallId={groupIds} />)

    const node = screen.getAllByTestId('transcript-mini-timeline-node')
      .find(n => n.getAttribute('data-mini-timeline-node-kind') === 'read-explore')!
    fireEvent.click(node)
    expect(locate).toHaveBeenCalledWith({ toolCallId: 'tc-r1', groupId: 'g1' })
  })

  it('renders nothing when there are no turns', () => {
    const { container } = render(<MiniTimeline turns={[]} locate={vi.fn()} />)
    expect(container.querySelector('[data-testid="transcript-mini-timeline"]')).toBeNull()
  })

  it('does not render a mini timeline node for non-qualifying completed tool calls', () => {
    const turns: DisplayTurn[] = [
      turn({
        id: 't1',
        assistantParts: [
          tool({ id: 'b1', normalizedName: 'bash' }),
          tool({ id: 't1', normalizedName: 'todowrite' }),
        ],
      }),
    ]
    render(<MiniTimeline turns={turns} locate={vi.fn()} />)

    const nodes = screen.queryAllByTestId('transcript-mini-timeline-node')
    expect(nodes).toHaveLength(1)
    expect(nodes[0].getAttribute('data-mini-timeline-node-kind')).toBe('turn')
  })

  it('emits turn nodes and event nodes separately for single-turn sessions with events', () => {
    const turns: DisplayTurn[] = [
      turn({
        id: 'only',
        assistantParts: [
          tool({ id: 'r1', normalizedName: 'read' }),
          tool({ id: 'r2', normalizedName: 'read' }),
        ],
      }),
    ]
    render(<MiniTimeline turns={turns} locate={vi.fn()} />)

    const nodes = screen.getAllByTestId('transcript-mini-timeline-node')
    expect(nodes).toHaveLength(3)
    expect(nodes.map(n => n.getAttribute('data-mini-timeline-node-kind')))
      .toEqual(['turn', 'read-explore', 'read-explore'])
  })

  it('hides the rail below the xl breakpoint via Tailwind hidden xl:flex', () => {
    const turns: DisplayTurn[] = [turn({ id: 't1' })]
    const { container } = render(<MiniTimeline turns={turns} locate={vi.fn()} />)
    const rail = container.querySelector('[data-testid="transcript-mini-timeline"]') as HTMLElement
    expect(rail.className).toContain('hidden')
    expect(rail.className).toContain('xl:flex')
  })
})