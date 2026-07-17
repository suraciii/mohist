import { describe, expect, it } from 'vitest'
import { projectTurn } from './session-transcript-display'
import type { SessionTurn, ToolPart } from '../../../entities/coder-session'

function makeContextTool(
  id: string,
  overrides: Partial<ToolPart['tool']> = {},
): ToolPart {
  return {
    id,
    type: 'tool',
    tool: {
      toolCallId: id,
      normalizedName: 'read',
      toolName: 'read',
      status: 'completed',
      startedAt: '2024-01-01T00:00:02Z',
      completedAt: '2024-01-01T00:00:03Z',
      ...overrides,
    },
  }
}

function makeTurn(assistant: ToolPart[]): SessionTurn {
  return {
    id: 'turn-1',
    startedAt: '2024-01-01T00:00:00Z',
    completedAt: null,
    incomplete: true,
    user: {
      role: 'mohist',
      text: 'read the file',
      kind: 'task',
      sentAt: '2024-01-01T00:00:00Z',
      summary: { kind: 'task', rawText: 'read the file' },
    },
    assistant,
  }
}

describe('context-group title', () => {
  it('a single exploratory call becomes a plain tool part, not a context-group', () => {
    const turn = makeTurn([
      makeContextTool('r1', {
        displayTitle: 'unknown',
        input: JSON.stringify({ filePath: '/repo/src/foo.ts' }),
      }),
    ])

    const display = projectTurn(turn)

    expect(display.assistantParts).toHaveLength(1)
    expect(display.assistantParts[0].partType).toBe('tool')
  })

  it('a run of ≥2 exploratory calls forms a context-group with per-type counts', () => {
    const turn = makeTurn([
      makeContextTool('r1'),
      makeContextTool('r2'),
      makeContextTool('g1', { toolName: 'grep', normalizedName: 'grep' }),
    ])

    const display = projectTurn(turn)

    expect(display.assistantParts).toHaveLength(1)
    expect(display.assistantParts[0].partType).toBe('context-group')
    const group = display.assistantParts[0] as { title: string }
    expect(group.title).toBe('Explored · 2 reads · 1 search')
  })

  it('summary never surfaces a literal "unknown" title on the grouped row', () => {
    const turn = makeTurn([
      makeContextTool('r1', { displayTitle: 'unknown', displaySubtitle: 'unknown', target: 'unknown' }),
      makeContextTool('r2', { displayTitle: 'unknown', displaySubtitle: 'unknown', target: 'unknown' }),
    ])

    const display = projectTurn(turn)

    const group = display.assistantParts[0] as { title: string }
    expect(group.title).toContain('Explored')
    expect(group.title).toContain('2 reads')
    expect(group.title).not.toContain('unknown')
  })
})
