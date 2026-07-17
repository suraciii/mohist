import { describe, it, expect } from 'vitest'
import { projectTurn } from './session-transcript-display'
import type { SessionTurn, FileChangeSummary, ToolPart, ErrorPart, SessionPart } from '../../../entities/coder-session'

function makeToolPart(
  id: string,
  toolCallId: string,
  toolName: string,
  normalizedName: string,
  status: 'pending' | 'running' | 'completed' | 'failed' | 'cancelled',
  opts?: {
    title?: string
    target?: string
    changedFiles?: FileChangeSummary[]
    error?: string
    input?: string
    output?: string
  },
): ToolPart {
  return {
    id,
    type: 'tool',
    tool: {
      toolCallId,
      normalizedName,
      toolName,
      status,
      title: opts?.title,
      target: opts?.target,
      input: opts?.input,
      output: opts?.output,
      startedAt: '2024-01-01T00:00:02Z',
      completedAt: status === 'completed' || status === 'failed' ? '2024-01-01T00:00:03Z' : null,
      changedFiles: opts?.changedFiles,
      error: opts?.error,
    },
  }
}

function makeErrorPart(id: string, kind: 'timeout' | 'failed' | 'cancelled' | 'recovery', message: string): ErrorPart {
  return { id, type: 'error', message, kind, at: '2024-01-01T00:00:04Z' }
}

function makeTurn(
  id: string,
  promptText: string,
  assistant: SessionPart[],
): SessionTurn {
  return {
    id,
    startedAt: '2024-01-01T00:00:00Z',
    completedAt: null,
    user: {
      role: 'mohist',
      text: promptText,
      kind: 'task',
      sentAt: '2024-01-01T00:00:00Z',
      summary: { kind: 'task', rawText: promptText },
    },
    assistant,
  }
}

describe('context-group projection', () => {
  it('groups contiguous context tools into context-group', () => {
    const turn = makeTurn('turn-1', 'Understand codebase', [
      makeToolPart('r1', 'c1', 'read', 'read', 'completed', { title: 'Read utils.ts' }),
      makeToolPart('r2', 'c2', 'read', 'read', 'completed', { title: 'Read auth.ts' }),
      makeToolPart('r3', 'c3', 'grep', 'grep', 'completed', { title: 'Search for token' }),
    ])
    const display = projectTurn(turn)
    expect(display.assistantParts).toHaveLength(1)
    expect(display.assistantParts[0].partType).toBe('context-group')
    const group = display.assistantParts[0] as any
    expect(group.title).toContain('Explored')
    expect(group.title).toContain('2 reads')
    expect(group.title).toContain('1 search')
    expect(group.tools).toHaveLength(3)
    expect(group.hasError).toBe(false)
  })

  it('context-group hasError is true if any grouped tool has error', () => {
    const turn = makeTurn('turn-1', 'Search files', [
      makeToolPart('r1', 'c1', 'read', 'read', 'completed'),
      makeToolPart('r2', 'c2', 'grep', 'grep', 'failed', { error: 'Pattern not found' }),
    ])
    const display = projectTurn(turn)
    const group = display.assistantParts[0] as any
    expect(group.hasError).toBe(true)
  })

  it('groups glob and search together in context group', () => {
    const turn = makeTurn('turn-1', 'Find files', [
      makeToolPart('g1', 'c1', 'glob', 'glob', 'completed'),
      makeToolPart('s1', 'c2', 'search', 'search', 'completed'),
    ])
    const display = projectTurn(turn)
    expect(display.assistantParts).toHaveLength(1)
    expect(display.assistantParts[0].partType).toBe('context-group')
  })

  it('a single exploratory call is pushed as a plain tool part, not a context-group', () => {
    const turn = makeTurn('turn-1', 'Read one file', [
      makeToolPart('r1', 'c1', 'read', 'read', 'completed'),
    ])
    const display = projectTurn(turn)
    expect(display.assistantParts).toHaveLength(1)
    expect(display.assistantParts[0].partType).toBe('tool')
  })

  it('a single exploratory call followed by a non-exploratory call yields tool + tool, not a context-group', () => {
    const turn = makeTurn('turn-1', 'Make changes', [
      makeToolPart('r1', 'c1', 'read', 'read', 'completed'),
      makeToolPart('p1', 'c2', 'apply_patch', 'apply_patch', 'completed'),
    ])
    const display = projectTurn(turn)
    expect(display.assistantParts).toHaveLength(2)
    expect(display.assistantParts[0].partType).toBe('tool')
    expect((display.assistantParts[0] as any).normalizedName).toBe('read')
    expect(display.assistantParts[1].partType).toBe('tool')
    const toolPart = display.assistantParts[1] as any
    expect(toolPart.normalizedName).toBe('apply_patch')
  })

  it('a non-exploratory call interrupts a run and forms two separate context-groups around it', () => {
    const turn = makeTurn('turn-1', 'Mixed workflow', [
      makeToolPart('r1', 'c1', 'read', 'read', 'completed'),
      makeToolPart('r2', 'c2', 'read', 'read', 'completed'),
      makeToolPart('p1', 'c3', 'apply_patch', 'apply_patch', 'completed'),
      makeToolPart('g1', 'c4', 'grep', 'grep', 'completed'),
      makeToolPart('g2', 'c5', 'grep', 'grep', 'completed'),
    ])
    const display = projectTurn(turn)
    expect(display.assistantParts).toHaveLength(3)
    expect(display.assistantParts[0].partType).toBe('context-group')
    expect((display.assistantParts[0] as any).tools).toHaveLength(2)
    expect(display.assistantParts[1].partType).toBe('tool')
    expect((display.assistantParts[1] as any).normalizedName).toBe('apply_patch')
    expect(display.assistantParts[2].partType).toBe('context-group')
    expect((display.assistantParts[2] as any).tools).toHaveLength(2)
  })

  it('exploratory runs split by non-exploratory tools form separate context-groups', () => {
    const turn = makeTurn('turn-1', 'Two phases', [
      makeToolPart('r1', 'c1', 'read', 'read', 'completed'),
      makeToolPart('r2', 'c2', 'read', 'read', 'completed'),
      makeToolPart('b1', 'c3', 'bash', 'bash', 'completed'),
      makeToolPart('g1', 'c4', 'grep', 'grep', 'completed'),
      makeToolPart('g2', 'c5', 'grep', 'grep', 'completed'),
    ])
    const display = projectTurn(turn)
    expect(display.assistantParts).toHaveLength(3)
    expect(display.assistantParts[0].partType).toBe('context-group')
    expect(display.assistantParts[1].partType).toBe('tool')
    expect((display.assistantParts[1] as any).normalizedName).toBe('bash')
    expect(display.assistantParts[2].partType).toBe('context-group')
  })

  it('flushes context group before error part', () => {
    const turn = makeTurn('turn-1', 'Work', [
      makeToolPart('r1', 'c1', 'read', 'read', 'completed'),
      makeToolPart('r2', 'c2', 'read', 'read', 'completed'),
      makeErrorPart('err-1', 'failed', 'Oops'),
    ])
    const display = projectTurn(turn)
    expect(display.assistantParts).toHaveLength(2)
    expect(display.assistantParts[0].partType).toBe('context-group')
    expect(display.assistantParts[1].partType).toBe('error')
  })

  it('a lone context call flushes as a tool part before an error part', () => {
    const turn = makeTurn('turn-1', 'Lone', [
      makeToolPart('r1', 'c1', 'read', 'read', 'completed'),
      makeErrorPart('err-1', 'failed', 'Oops'),
    ])
    const display = projectTurn(turn)
    expect(display.assistantParts).toHaveLength(2)
    expect(display.assistantParts[0].partType).toBe('tool')
    expect((display.assistantParts[0] as any).normalizedName).toBe('read')
    expect(display.assistantParts[1].partType).toBe('error')
  })

  it('infers read_file as context tool', () => {
    const turn = makeTurn('turn-1', 'Read files', [
      makeToolPart('r1', 'c1', 'read_file', 'read_file', 'completed'),
      makeToolPart('r2', 'c2', 'read_file', 'read_file', 'completed'),
    ])
    const display = projectTurn(turn)
    expect(display.assistantParts).toHaveLength(1)
    expect(display.assistantParts[0].partType).toBe('context-group')
  })
})
