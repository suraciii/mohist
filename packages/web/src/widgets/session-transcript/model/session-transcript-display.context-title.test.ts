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

describe('context-group title never surfaces "unknown"', () => {
  it('skips a literal "unknown" displayTitle and surfaces a readable fallback from input', () => {
    const turn = makeTurn([
      makeContextTool('r1', {
        displayTitle: 'unknown',
        input: JSON.stringify({ filePath: '/repo/src/foo.ts' }),
      }),
    ])

    const display = projectTurn(turn)

    expect(display.assistantParts[0].partType).toBe('context-group')
    const group = display.assistantParts[0] as { title: string }
    expect(group.title).not.toContain('unknown')
    expect(group.title).toContain('foo.ts')
  })

  it('renders a bare "Gathering context" title when no readable summary exists', () => {
    const turn = makeTurn([
      makeContextTool('r1', {
        displayTitle: 'unknown',
        displaySubtitle: 'unknown',
        target: 'unknown',
      }),
    ])

    const display = projectTurn(turn)

    const group = display.assistantParts[0] as { title: string }
    expect(group.title).toBe('Gathering context')
    expect(group.title).not.toContain('unknown')
  })
})
