import { describe, it, expect } from 'vitest'
import {
  projectTurn,
  projectSessionToDisplayTurns,
  extractTurnChangedFiles,
} from './session-transcript-display'
import type { CoderSessionDetail, SessionTurn, TextPart, ReasoningPart, FileChangeSummary, ToolPart, ErrorPart, SessionPart } from '../../../entities/coder-session'

function makeTextPart(id: string, text: string, startedAt = '2024-01-01T00:00:00Z'): TextPart {
  return { id, type: 'text', text, startedAt, completedAt: null }
}

function makeReasoningPart(id: string, text: string, startedAt = '2024-01-01T00:00:01Z'): ReasoningPart {
  return { id, type: 'reasoning', text, startedAt, completedAt: null }
}

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
    rawInput?: string
    rawOutput?: string
    metadata?: Record<string, unknown>
    details?: Record<string, unknown>
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
      rawInput: opts?.rawInput,
      rawOutput: opts?.rawOutput,
      metadata: opts?.metadata,
      details: opts?.details,
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
  completedAt: string | null = null,
  incomplete?: boolean,
): SessionTurn {
  return {
    id,
    startedAt: '2024-01-01T00:00:00Z',
    completedAt,
    incomplete,
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

function makeSessionDetail(turns: SessionTurn[]): CoderSessionDetail {
  return {
    id: 'session-1',
    runtimeSessionId: 'acp-1',
    executionId: 'exec-1',
    taskDescription: 'Test task',
    status: 'completed',
    createdAt: '2024-01-01T00:00:00Z',
    completedAt: '2024-01-01T00:01:00Z',
    model: 'claude',
    runtime: 'coder',
    stage: 'build',
    title: 'Test Session',
    metadata: {
      sessionId: 'session-1',
      issueId: 'issue-1',
      runtimeSessionId: 'acp-1',
      executionId: 'exec-1',
      title: 'Test Session',
      status: 'completed',
      model: 'claude',
      stage: 'build',
      createdAt: '2024-01-01T00:00:00Z',
      completedAt: '2024-01-01T00:01:00Z',
      turnCount: turns.length,
    },
    turns,
    incomplete: false,
  }
}

describe('projectTurn', () => {
  it('projects a simple text turn correctly', () => {
    const turn = makeTurn('turn-1', 'Fix the login bug', [makeTextPart('t1', 'I will fix it.')])
    const display = projectTurn(turn)
    expect(display.id).toBe('turn-1')
    expect(display.prompt.text).toBe('Fix the login bug')
    expect(display.prompt.role).toBe('mohist')
    expect(display.assistantParts).toHaveLength(1)
    expect(display.assistantParts[0].partType).toBe('text')
    expect((display.assistantParts[0] as any).text).toBe('I will fix it.')
  })

  it('projects reasoning part as reasoning display part', () => {
    const turn = makeTurn('turn-1', 'Refactor auth', [
      makeTextPart('t1', 'Let me think...'),
      makeReasoningPart('r1', 'Need to update the token validation.'),
    ])
    const display = projectTurn(turn)
    expect(display.assistantParts).toHaveLength(2)
    expect(display.assistantParts[0].partType).toBe('text')
    expect(display.assistantParts[1].partType).toBe('reasoning')
  })

  it('projects tool part as tool display part', () => {
    const turn = makeTurn('turn-1', 'Update config', [
      makeToolPart('tool-1', 'call-1', 'apply_patch', 'apply_patch', 'completed', { title: 'Patch design.md' }),
    ])
    const display = projectTurn(turn)
    expect(display.assistantParts).toHaveLength(1)
    expect(display.assistantParts[0].partType).toBe('tool')
    const toolPart = display.assistantParts[0] as any
    expect(toolPart.normalizedName).toBe('apply_patch')
    expect(toolPart.status).toBe('completed')
    expect(toolPart.hasError).toBe(false)
  })

  it('marks failed tool as hasError=true', () => {
    const turn = makeTurn('turn-1', 'Try patch', [
      makeToolPart('tool-1', 'call-1', 'apply_patch', 'apply_patch', 'failed', { error: 'Patch rejected' }),
    ])
    const display = projectTurn(turn)
    const toolPart = display.assistantParts[0] as any
    expect(toolPart.hasError).toBe(true)
    expect(toolPart.error).toBe('Patch rejected')
  })

  it('projects error part as error display part', () => {
    const turn = makeTurn('turn-1', 'Do something', [makeErrorPart('err-1', 'failed', 'Session failed')])
    const display = projectTurn(turn)
    expect(display.assistantParts).toHaveLength(1)
    expect(display.assistantParts[0].partType).toBe('error')
    const errPart = display.assistantParts[0] as any
    expect(errPart.message).toBe('Session failed')
    expect(errPart.kind).toBe('failed')
  })

  it('renders todowrite in transcript when present', () => {
    const turn = makeTurn('turn-1', 'Plan tasks', [
      makeToolPart('tool-todo', 'call-todo', 'todowrite', 'todowrite', 'completed'),
    ])
    const display = projectTurn(turn)
    expect(display.assistantParts).toHaveLength(1)
    expect(display.assistantParts[0].partType).toBe('tool')
    expect((display.assistantParts[0] as any).normalizedName).toBe('todowrite')
  })

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
    expect(group.title).toContain('Gathering context')
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

  it('does not group file-change tools with context tools', () => {
    const turn = makeTurn('turn-1', 'Make changes', [
      makeToolPart('r1', 'c1', 'read', 'read', 'completed'),
      makeToolPart('p1', 'c2', 'apply_patch', 'apply_patch', 'completed'),
    ])
    const display = projectTurn(turn)
    expect(display.assistantParts).toHaveLength(2)
    expect(display.assistantParts[0].partType).toBe('context-group')
    expect(display.assistantParts[1].partType).toBe('tool')
    const toolPart = display.assistantParts[1] as any
    expect(toolPart.normalizedName).toBe('apply_patch')
  })

  it('does not duplicate apply_patch changed-files in turn summary', () => {
    const turn = makeTurn('turn-1', 'Patch files', [
      makeToolPart('p1', 'c1', 'apply_patch', 'apply_patch', 'completed', {
        title: 'Apply patch',
        changedFiles: [
          { path: 'src/utils.ts', operation: 'modified', additions: 10, deletions: 2 },
          { path: 'src/auth.ts', operation: 'created', additions: 5 },
        ],
      }),
    ])
    const display = projectTurn(turn)
    expect(display.changedFiles).toHaveLength(0)
  })

  it('extracts changed-files from edit tool into turn', () => {
    const turn = makeTurn('turn-1', 'Edit file', [
      makeToolPart('e1', 'c1', 'edit', 'edit', 'completed', {
        changedFiles: [{ path: 'config.json', operation: 'modified', additions: 3, deletions: 1 }],
      }),
    ])
    const display = projectTurn(turn)
    expect(display.changedFiles).toHaveLength(1)
    expect(display.changedFiles[0].path).toBe('config.json')
  })

  it('projects mutation details into changed files with raw diff detail', () => {
    const turn = makeTurn('turn-1', 'Edit file', [
      makeToolPart('e1', 'c1', 'write', 'write', 'completed', {
        title: 'Write config',
      }),
    ])
    const tool = turn.assistant[0]
    if (tool.type === 'tool') {
      tool.tool.details = {
        family: 'mutation',
        files: [{
          path: 'config.json',
          operation: 'modified',
          additions: 1,
          deletions: 1,
          diff: '--- a/config.json\n+++ b/config.json\n@@ -1 +1 @@\n-old\n+new',
        }],
      }
    }
    const display = projectTurn(turn)
    const files = extractTurnChangedFiles(display)
    expect(files).toHaveLength(1)
    expect(files[0].path).toBe('config.json')
    expect(files[0].rawDetail).toContain('-old')
  })

  it('turn state is idle when completedAt is set', () => {
    const turn = makeTurn('turn-1', 'Done', [makeTextPart('t1', 'Done.')], '2024-01-01T00:01:00Z')
    const display = projectTurn(turn)
    expect(display.state).toBe('idle')
  })

  it('turn state is streaming when not completed and not incomplete', () => {
    const turn = makeTurn('turn-1', 'Working...', [makeTextPart('t1', 'Working...')])
    const display = projectTurn(turn)
    expect(display.state).toBe('streaming')
  })

  it('turn state is finalizing when incomplete is true', () => {
    const turn = makeTurn('turn-1', 'Almost done', [makeTextPart('t1', 'Almost...')], null, true)
    const display = projectTurn(turn)
    expect(display.state).toBe('finalizing')
  })

  it('skips hidden tool parts', () => {
    const turn: SessionTurn = {
      ...makeTurn('turn-1', 'Do things', []),
      assistant: [{ id: 'hidden-tool', type: 'tool', hidden: true, tool: { toolCallId: 'x', toolName: 'internal', status: 'completed', startedAt: '2024-01-01T00:00:01Z' } } as any],
    }
    const display = projectTurn(turn)
    expect(display.assistantParts).toHaveLength(0)
  })

  it('flushes context group before error part', () => {
    const turn = makeTurn('turn-1', 'Work', [
      makeToolPart('r1', 'c1', 'read', 'read', 'completed'),
      makeErrorPart('err-1', 'failed', 'Oops'),
    ])
    const display = projectTurn(turn)
    expect(display.assistantParts).toHaveLength(2)
    expect(display.assistantParts[0].partType).toBe('context-group')
    expect(display.assistantParts[1].partType).toBe('error')
  })

  it('infers read_file as context tool', () => {
    const turn = makeTurn('turn-1', 'Read files', [
      makeToolPart('r1', 'c1', 'read_file', 'read_file', 'completed'),
    ])
    const display = projectTurn(turn)
    expect(display.assistantParts).toHaveLength(1)
    expect(display.assistantParts[0].partType).toBe('context-group')
  })

  it('leaves non-context tools as individual parts when not contiguous', () => {
    const turn = makeTurn('turn-1', 'Do things', [
      makeToolPart('b1', 'c1', 'bash', 'bash', 'completed'),
      makeToolPart('p1', 'c2', 'apply_patch', 'apply_patch', 'completed'),
    ])
    const display = projectTurn(turn)
    expect(display.assistantParts).toHaveLength(2)
    expect(display.assistantParts[0].partType).toBe('tool')
    expect(display.assistantParts[1].partType).toBe('tool')
  })

  it('projects unknown tool when normalizedName is unknown and was not suppressed', () => {
    const turn = makeTurn('turn-1', 'Do unknown thing', [
      makeToolPart('u1', 'c1', 'unknown', 'unknown', 'completed'),
    ])
    const display = projectTurn(turn)
    expect(display.assistantParts).toHaveLength(1)
    expect(display.assistantParts[0].partType).toBe('tool')
    const toolPart = display.assistantParts[0] as any
    expect(toolPart.normalizedName).toBe('unknown')
  })

  it('handles prompt summary fields', () => {
    const turn: SessionTurn = {
      ...makeTurn('turn-1', 'Fix login', []),
      user: {
        role: 'mohist',
        text: 'Fix login bug',
        kind: 'task',
        sentAt: '2024-01-01T00:00:00Z',
        summary: {
          kind: 'task',
          rawText: 'Fix login bug',
          title: 'Fix login',
          subtitle: 'Output: src/auth.ts',
          outputPath: 'src/auth.ts',
          contextFiles: ['src/auth.ts', 'src/utils.ts'],
        },
      },
    }
    const display = projectTurn(turn)
    expect(display.prompt.title).toBe('Fix login')
    expect(display.prompt.subtitle).toBeUndefined()
    expect(display.prompt.outputPath).toBe('src/auth.ts')
    expect(display.prompt.contextFiles).toEqual(['src/auth.ts', 'src/utils.ts'])
  })

  it('preserves raw input, output, metadata, and details on tool parts', () => {
    const rawInput = '{"command":"npm test","cwd":"/project"}'
    const rawOutput = '{"stdout":"ok","exitCode":1}'
    const metadata = { childSessionId: 'child-session-xyz' }
    const details = { family: 'execution', cwd: '/project', exitCode: 1, outputPreview: 'ok' }
    const turn = makeTurn('turn-1', 'Run tests', [
      makeToolPart('tool-1', 'call-1', 'bash', 'bash', 'completed', {
        rawInput,
        rawOutput,
        metadata,
        details,
      }),
    ])
    const display = projectTurn(turn)
    const toolPart = display.assistantParts[0] as any
    expect(toolPart.partType).toBe('tool')
    expect(toolPart.rawInput).toBe(rawInput)
    expect(toolPart.rawOutput).toBe(rawOutput)
    expect(toolPart.metadata).toEqual(metadata)
    expect(toolPart.details).toEqual(details)
  })

  it('exposes raw tool fields on diff tool parts for reviewable disclosure', () => {
    const rawInput = '{"file_path":"src/app.ts","old_string":"old","new_string":"new"}'
    const rawOutput = 'old content\nnew content'
    const metadata = { toolName: 'edit' }
    const details = { family: 'mutation', files: [] }
    const turn = makeTurn('turn-1', 'Edit file', [
      makeToolPart('tool-1', 'call-1', 'edit', 'edit', 'completed', {
        rawInput,
        rawOutput,
        metadata,
        details,
      }),
    ])
    const display = projectTurn(turn)
    const toolPart = display.assistantParts[0] as any
    expect(toolPart.partType).toBe('tool')
    expect(toolPart.normalizedName).toBe('edit')
    expect(toolPart.rawInput).toBe(rawInput)
    expect(toolPart.rawOutput).toBe(rawOutput)
    expect(toolPart.metadata).toEqual(metadata)
    expect(toolPart.details).toEqual(details)
  })
})

describe('projectSessionToDisplayTurns', () => {
  it('projects all turns in session detail', () => {
    const turn1 = makeTurn('turn-1', 'First task', [makeTextPart('t1', 'First response')])
    const turn2 = makeTurn('turn-2', 'Second task', [makeTextPart('t2', 'Second response')])
    const session = makeSessionDetail([turn1, turn2])
    const display = projectSessionToDisplayTurns(session)
    expect(display).toHaveLength(2)
    expect(display[0].id).toBe('turn-1')
    expect(display[1].id).toBe('turn-2')
  })
})

describe('extractTurnChangedFiles', () => {
  it('extracts changed files from display turn for non-apply_patch tools', () => {
    const turn = makeTurn('turn-1', 'Patch', [
      makeToolPart('p1', 'c1', 'edit', 'edit', 'completed', {
        changedFiles: [{ path: 'a.txt', operation: 'modified', additions: 5 }],
      }),
    ])
    const display = projectTurn(turn)
    const files = extractTurnChangedFiles(display)
    expect(files).toHaveLength(1)
    expect(files[0].path).toBe('a.txt')
  })
})

describe('reasoning reorder', () => {
  it('moves reasoning after text when they share same second timestamp', () => {
    const turn = makeTurn('turn-1', 'Test task', [
      makeReasoningPart('r1', 'Thinking...', '2024-01-01T00:00:00Z'),
      makeTextPart('t1', 'Hello world', '2024-01-01T00:00:00Z'),
    ])
    const display = projectTurn(turn)
    expect(display.assistantParts).toHaveLength(2)
    expect(display.assistantParts[0].partType).toBe('text')
    expect(display.assistantParts[1].partType).toBe('reasoning')
  })

  it('keeps original order when reasoning and text have different timestamps', () => {
    const turn = makeTurn('turn-1', 'Test task', [
      makeReasoningPart('r1', 'Thinking...', '2024-01-01T00:00:01Z'),
      makeTextPart('t1', 'Hello world', '2024-01-01T00:00:02Z'),
    ])
    const display = projectTurn(turn)
    expect(display.assistantParts).toHaveLength(2)
    expect(display.assistantParts[0].partType).toBe('reasoning')
    expect(display.assistantParts[1].partType).toBe('text')
  })

  it('moves multiple reasoning blocks after text when same second', () => {
    const turn = makeTurn('turn-1', 'Test task', [
      makeReasoningPart('r1', 'Thinking step 1...', '2024-01-01T00:00:00Z'),
      makeReasoningPart('r2', 'Thinking step 2...', '2024-01-01T00:00:00Z'),
      makeTextPart('t1', 'Hello world', '2024-01-01T00:00:00Z'),
    ])
    const display = projectTurn(turn)
    expect(display.assistantParts).toHaveLength(3)
    expect(display.assistantParts[0].partType).toBe('text')
    expect((display.assistantParts[0] as any).text).toBe('Hello world')
    expect(display.assistantParts[1].partType).toBe('reasoning')
    expect((display.assistantParts[1] as any).text).toBe('Thinking step 1...')
    expect(display.assistantParts[2].partType).toBe('reasoning')
    expect((display.assistantParts[2] as any).text).toBe('Thinking step 2...')
  })

  it('does not reorder when reasoning is followed by tool (not text)', () => {
    const turn = makeTurn('turn-1', 'Test task', [
      makeReasoningPart('r1', 'Thinking...', '2024-01-01T00:00:00Z'),
      makeToolPart('tool-1', 'call-1', 'bash', 'bash', 'completed'),
    ])
    const display = projectTurn(turn)
    expect(display.assistantParts).toHaveLength(2)
    expect(display.assistantParts[0].partType).toBe('reasoning')
    expect(display.assistantParts[1].partType).toBe('tool')
  })

  it('does not reorder when reasoning is followed by error', () => {
    const turn = makeTurn('turn-1', 'Test task', [
      makeReasoningPart('r1', 'Thinking...', '2024-01-01T00:00:00Z'),
      makeErrorPart('err-1', 'failed', 'Something went wrong'),
    ])
    const display = projectTurn(turn)
    expect(display.assistantParts).toHaveLength(2)
    expect(display.assistantParts[0].partType).toBe('reasoning')
    expect(display.assistantParts[1].partType).toBe('error')
  })

  it('does not reorder when reasoning block is at end of parts', () => {
    const turn = makeTurn('turn-1', 'Test task', [
      makeTextPart('t1', 'Hello', '2024-01-01T00:00:01Z'),
      makeReasoningPart('r1', 'Thinking...', '2024-01-01T00:00:00Z'),
    ])
    const display = projectTurn(turn)
    expect(display.assistantParts).toHaveLength(2)
    expect(display.assistantParts[0].partType).toBe('text')
    expect(display.assistantParts[1].partType).toBe('reasoning')
  })

  it('does not reorder reasoning across context group boundary', () => {
    const turn = makeTurn('turn-1', 'Test task', [
      makeReasoningPart('r1', 'Thinking...', '2024-01-01T00:00:00Z'),
      makeTextPart('t1', 'Hello', '2024-01-01T00:00:00Z'),
      makeToolPart('r2', 'c1', 'read', 'read', 'completed'),
      makeToolPart('r3', 'c2', 'read', 'read', 'completed'),
      makeReasoningPart('r4', 'More thinking...', '2024-01-01T00:00:00Z'),
      makeTextPart('t2', 'World', '2024-01-01T00:00:00Z'),
    ])
    const display = projectTurn(turn)
    expect(display.assistantParts).toHaveLength(5)
    expect(display.assistantParts[0].partType).toBe('text')
    expect(display.assistantParts[1].partType).toBe('reasoning')
    expect(display.assistantParts[2].partType).toBe('context-group')
    expect(display.assistantParts[3].partType).toBe('text')
    expect(display.assistantParts[4].partType).toBe('reasoning')
  })

  it('does not reorder when last reasoning and text share second but earlier text exists', () => {
    const turn = makeTurn('turn-1', 'Test task', [
      makeTextPart('t1', 'First text', '2024-01-01T00:00:01Z'),
      makeReasoningPart('r1', 'Thinking...', '2024-01-01T00:00:00Z'),
      makeTextPart('t2', 'Second text', '2024-01-01T00:00:00Z'),
    ])
    const display = projectTurn(turn)
    expect(display.assistantParts).toHaveLength(3)
    expect(display.assistantParts[0].partType).toBe('text')
    expect((display.assistantParts[0] as any).text).toBe('First text')
    expect(display.assistantParts[1].partType).toBe('text')
    expect((display.assistantParts[1] as any).text).toBe('Second text')
    expect(display.assistantParts[2].partType).toBe('reasoning')
  })
})
