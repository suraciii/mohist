import { describe, expect, it } from 'vitest'
import type { DisplayContextGroupPart, DisplayToolPart, DisplayTurn } from './session-transcript-display'
import { projectSessionToTimelineNodes } from './timeline-nodes'

function tool(overrides: Partial<DisplayToolPart> & { id: string }): DisplayToolPart {
  const { id, toolCallId, normalizedName, toolName, status, startedAt, completedAt, hasError, isContextTool, ...rest } = overrides
  return {
    id,
    partType: 'tool',
    toolCallId: toolCallId ?? id,
    normalizedName: normalizedName ?? 'bash',
    toolName: toolName ?? normalizedName ?? 'bash',
    status: status ?? 'completed',
    startedAt: startedAt ?? '',
    completedAt: completedAt ?? null,
    hasError: hasError ?? false,
    isContextTool: isContextTool ?? false,
    ...rest,
  } as DisplayToolPart
}

function turn(overrides: Partial<DisplayTurn> & { id: string }): DisplayTurn {
  return {
    id: overrides.id,
    startedAt: overrides.startedAt ?? '',
    completedAt: overrides.completedAt ?? null,
    prompt: overrides.prompt ?? { role: 'mohist', text: '', kind: 'initial', sentAt: '' },
    assistantParts: overrides.assistantParts ?? [],
    changedFiles: overrides.changedFiles ?? [],
    state: overrides.state ?? 'idle',
  } as DisplayTurn
}

function contextGroup(id: string, tools: DisplayToolPart[], hasError = false): DisplayContextGroupPart {
  return {
    id,
    partType: 'context-group',
    title: 'Explored',
    tools,
    hasError,
  }
}

describe('projectSessionToTimelineNodes', () => {
  it('emits one turn node per turn boundary, in order', () => {
    const turns = [
      turn({ id: 't1', assistantParts: [] }),
      turn({ id: 't2', assistantParts: [] }),
      turn({ id: 't3', assistantParts: [] }),
    ]
    const nodes = projectSessionToTimelineNodes(turns)
    expect(nodes.filter(n => n.kind === 'turn').map(n => n.turnId)).toEqual(['t1', 't2', 't3'])
    expect(nodes.filter(n => n.kind === 'turn').map(n => n.turnIndex)).toEqual([1, 2, 3])
  })

  it('emits a failed node for a standalone failed tool call', () => {
    const turns = [
      turn({
        id: 't1',
        assistantParts: [tool({ id: 'a', status: 'failed', normalizedName: 'bash', toolCallId: 'tc-a' })],
      }),
    ]
    const nodes = projectSessionToTimelineNodes(turns)
    const failed = nodes.filter(n => n.kind === 'failed')
    expect(failed).toHaveLength(1)
    expect(failed[0]).toMatchObject({
      kind: 'failed',
      turnId: 't1',
      turnIndex: 1,
      toolCallId: 'tc-a',
    })
  })

  it('emits a failed node for a failed call inside a context group', () => {
    const groupTools = [
      tool({ id: 'r1', normalizedName: 'read' }),
      tool({ id: 'r2', normalizedName: 'read', status: 'failed', toolCallId: 'tc-failed-in-group' }),
    ]
    const turns = [
      turn({ id: 't1', assistantParts: [contextGroup('g1', groupTools, true)] }),
    ]
    const nodes = projectSessionToTimelineNodes(turns)
    const failed = nodes.filter(n => n.kind === 'failed')
    expect(failed).toHaveLength(1)
    expect(failed[0]).toMatchObject({
      kind: 'failed',
      turnId: 't1',
      turnIndex: 1,
      toolCallId: 'tc-failed-in-group',
    })
  })

  it('emits a file-change node when changedFiles is non-empty', () => {
    const turns = [
      turn({
        id: 't1',
        assistantParts: [
          tool({
            id: 'e1',
            normalizedName: 'edit',
            toolCallId: 'tc-edit',
            changedFiles: [{ path: '/a.ts', operation: 'modified' }],
          }),
        ],
      }),
    ]
    const nodes = projectSessionToTimelineNodes(turns)
    const fileChange = nodes.filter(n => n.kind === 'file-change')
    expect(fileChange).toHaveLength(1)
    expect(fileChange[0].toolCallId).toBe('tc-edit')
  })

  it('emits a file-change node for an edit-family tool without changedFiles', () => {
    const turns = [
      turn({
        id: 't1',
        assistantParts: [
          tool({ id: 'e1', normalizedName: 'edit', toolCallId: 'tc-edit' }),
        ],
      }),
    ]
    const nodes = projectSessionToTimelineNodes(turns)
    expect(nodes.filter(n => n.kind === 'file-change').map(n => n.toolCallId)).toEqual(['tc-edit'])
  })

  it('does not emit a file-change node for a failed tool even when it has changedFiles', () => {
    const turns = [
      turn({
        id: 't1',
        assistantParts: [
          tool({
            id: 'e1',
            normalizedName: 'edit',
            toolCallId: 'tc-failed-edit',
            status: 'failed',
            changedFiles: [{ path: '/a.ts', operation: 'modified' }],
          }),
        ],
      }),
    ]
    const nodes = projectSessionToTimelineNodes(turns)
    expect(nodes.filter(n => n.kind === 'file-change')).toHaveLength(0)
    expect(nodes.filter(n => n.kind === 'failed').map(n => n.toolCallId)).toEqual(['tc-failed-edit'])
  })

  it('emits a read-explore node for completed context-tool names', () => {
    const turns = [
      turn({
        id: 't1',
        assistantParts: [
          tool({ id: 'r1', normalizedName: 'read' }),
          tool({ id: 's1', normalizedName: 'grep' }),
          tool({ id: 'g1', normalizedName: 'glob' }),
        ],
      }),
    ]
    const nodes = projectSessionToTimelineNodes(turns)
    expect(nodes.filter(n => n.kind === 'read-explore').map(n => n.toolCallId)).toEqual([
      'r1', 's1', 'g1',
    ])
  })

  it('emits read-explore nodes for tools inside a context group', () => {
    const turns = [
      turn({
        id: 't1',
        assistantParts: [
          contextGroup('g1', [
            tool({ id: 'r1', normalizedName: 'read' }),
            tool({ id: 'r2', normalizedName: 'read' }),
          ]),
        ],
      }),
    ]
    const nodes = projectSessionToTimelineNodes(turns)
    expect(nodes.filter(n => n.kind === 'read-explore').map(n => n.toolCallId)).toEqual(['r1', 'r2'])
  })

  it('does not emit event nodes for non-edit non-read non-failed completed tools', () => {
    const turns = [
      turn({
        id: 't1',
        assistantParts: [
          tool({ id: 'b1', normalizedName: 'bash' }),
          tool({ id: 't1', normalizedName: 'todowrite' }),
        ],
      }),
    ]
    const nodes = projectSessionToTimelineNodes(turns)
    const eventKinds = nodes.filter(n => n.kind !== 'turn')
    expect(eventKinds).toHaveLength(0)
  })

  it('does not emit a read-explore node for a non-completed context-tool call', () => {
    const turns = [
      turn({
        id: 't1',
        assistantParts: [
          tool({ id: 'r1', normalizedName: 'read', status: 'failed' }),
        ],
      }),
    ]
    const nodes = projectSessionToTimelineNodes(turns)
    expect(nodes.filter(n => n.kind === 'read-explore')).toHaveLength(0)
    expect(nodes.filter(n => n.kind === 'failed').map(n => n.toolCallId)).toEqual(['r1'])
  })

  it('preserves document order across turns and within context groups', () => {
    const turns = [
      turn({
        id: 't1',
        assistantParts: [
          tool({ id: 'r1', normalizedName: 'read' }),
          contextGroup('g1', [
            tool({ id: 'r2', normalizedName: 'read' }),
            tool({ id: 's1', normalizedName: 'grep' }),
          ]),
        ],
      }),
      turn({
        id: 't2',
        assistantParts: [
          tool({ id: 'e1', normalizedName: 'edit', changedFiles: [{ path: '/x.ts', operation: 'modified' }] }),
        ],
      }),
    ]
    const nodes = projectSessionToTimelineNodes(turns)
    const events = nodes.filter(n => n.kind !== 'turn')
    expect(events.map(n => `${n.kind}:${n.toolCallId}`)).toEqual([
      'read-explore:r1',
      'read-explore:r2',
      'read-explore:s1',
      'file-change:e1',
    ])
  })

  it('single-turn session with qualifying events renders one event node per event', () => {
    const turns = [
      turn({
        id: 'only-turn',
        assistantParts: [
          tool({ id: 'r1', normalizedName: 'read' }),
          tool({ id: 'r2', normalizedName: 'read' }),
          tool({ id: 'f1', normalizedName: 'bash', status: 'failed' }),
          tool({ id: 'e1', normalizedName: 'edit', changedFiles: [{ path: '/x.ts', operation: 'modified' }] }),
        ],
      }),
    ]
    const nodes = projectSessionToTimelineNodes(turns)
    const events = nodes.filter(n => n.kind !== 'turn')
    expect(events).toHaveLength(4)
    expect(events.map(n => n.kind)).toEqual([
      'read-explore',
      'read-explore',
      'failed',
      'file-change',
    ])
  })

  it('emits a turn node for an empty turn but no event nodes', () => {
    const turns = [turn({ id: 't1', assistantParts: [] })]
    const nodes = projectSessionToTimelineNodes(turns)
    expect(nodes).toEqual([{ kind: 'turn', turnId: 't1', turnIndex: 1 }])
  })

  it('emits no nodes for an empty session', () => {
    expect(projectSessionToTimelineNodes([])).toEqual([])
  })
})