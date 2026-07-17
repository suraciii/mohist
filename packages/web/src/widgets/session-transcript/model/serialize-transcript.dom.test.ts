import { describe, expect, it } from 'vitest'
import { serializeTranscriptPlainText } from './serialize-transcript'
import type {
  DisplayTurn,
  DisplayPrompt,
  DisplayAssistantPart,
} from './session-transcript-display'

function makePrompt(overrides: Partial<DisplayPrompt> = {}): DisplayPrompt {
  return {
    role: 'mohist',
    text: 'prompt body',
    kind: 'followup',
    sentAt: overrides.sentAt ?? '2024-05-15T10:00:00.000Z',
    ...overrides,
  }
}

function makeTurn(overrides: {
  id?: string
  startedAt: string
  completedAt?: string | null
  prompt?: Partial<DisplayPrompt>
  assistantParts?: DisplayAssistantPart[]
}): DisplayTurn {
  return {
    id: overrides.id ?? 'turn-1',
    startedAt: overrides.startedAt,
    completedAt: overrides.completedAt ?? null,
    prompt: makePrompt({ sentAt: overrides.startedAt, ...overrides.prompt }),
    assistantParts: overrides.assistantParts ?? [],
    changedFiles: [],
    state: 'idle',
  }
}

describe('serializeTranscriptPlainText', () => {
  it('emits a header line, prompt content, and assistant parts in document order for a representative multi-turn fixture', () => {
    const turns: DisplayTurn[] = [
      makeTurn({
        id: 't1',
        startedAt: '2024-05-15T10:00:00.000Z',
        prompt: {
          kind: 'initial',
          title: 'Add header navigation',
          subtitle: 'Refactor header',
          text: 'Refactor the SessionPage header to always render on the main transcript branch.',
        },
        assistantParts: [
          { id: 'p1', partType: 'text', text: 'Reading current implementation.', startedAt: '2024-05-15T10:00:01.000Z', completedAt: '2024-05-15T10:00:05.000Z' },
          {
            id: 'p2',
            partType: 'reasoning',
            text: 'x'.repeat(2048),
            startedAt: '2024-05-15T10:00:01.000Z',
            completedAt: '2024-05-15T10:00:03.000Z',
          },
          {
            id: 'p3',
            partType: 'tool',
            toolCallId: 'tc-1',
            normalizedName: 'apply_patch',
            toolName: 'apply_patch',
            status: 'completed',
            displayTitle: 'src/SessionPage.tsx',
            startedAt: '2024-05-15T10:00:06.000Z',
            hasError: false,
            isContextTool: false,
          },
          {
            id: 'p4',
            partType: 'error',
            message: 'Tool execution failed',
            kind: 'failed',
            at: '2024-05-15T10:00:08.000Z',
          },
        ],
      }),
      makeTurn({
        id: 't2',
        startedAt: '2024-05-15T10:30:00.000Z',
        prompt: {
          kind: 'followup',
          title: 'Wire TOC',
          text: 'Add the toolbar TOC trigger.',
        },
        assistantParts: [
          { id: 'p5', partType: 'text', text: 'On it.', startedAt: '2024-05-15T10:30:01.000Z', completedAt: '2024-05-15T10:30:05.000Z' },
        ],
      }),
    ]

    const out = serializeTranscriptPlainText(turns)

    const expected = [
      `== Turn 1 · Initial Task · ${new Date('2024-05-15T10:00:00.000Z').toLocaleString()} ==`,
      'Add header navigation',
      'Refactor header',
      'Refactor the SessionPage header to always render on the main transcript branch.',
      'Reading current implementation.',
      `[reasoning omitted, ${(2048 / 1024).toFixed(1)} KB]`,
      '[tool apply_patch] src/SessionPage.tsx',
      '[error] Tool execution failed',
      '',
      `== Turn 2 · Follow-up · ${new Date('2024-05-15T10:30:00.000Z').toLocaleString()} ==`,
      'Wire TOC',
      'Add the toolbar TOC trigger.',
      'On it.',
      '',
    ].join('\n')

    expect(out).toBe(expected)
  })

  it('uses the same kind labels as PromptBlock and TOC for the header', () => {
    const turns: DisplayTurn[] = [
      makeTurn({ id: 't1', startedAt: '2024-05-15T10:00:00.000Z', prompt: { kind: 'task', title: 'T1' } }),
      makeTurn({ id: 't2', startedAt: '2024-05-15T10:01:00.000Z', prompt: { kind: 'retry', title: 'T2' } }),
      makeTurn({ id: 't3', startedAt: '2024-05-15T10:02:00.000Z', prompt: { kind: 'recovery', title: 'T3' } }),
      makeTurn({ id: 't4', startedAt: '2024-05-15T10:03:00.000Z', prompt: { kind: 'followup', title: 'T4' } }),
      makeTurn({ id: 't5', startedAt: '2024-05-15T10:04:00.000Z', prompt: { kind: 'initial', title: 'T5' } }),
      makeTurn({ id: 't6', startedAt: '2024-05-15T10:05:00.000Z', prompt: { kind: 'legacy-missing', title: 'T6' } }),
    ]
    const out = serializeTranscriptPlainText(turns)
    expect(out).toContain('== Turn 1 · Task ·')
    expect(out).toContain('== Turn 2 · Retry ·')
    expect(out).toContain('== Turn 3 · Recovery ·')
    expect(out).toContain('== Turn 4 · Follow-up ·')
    expect(out).toContain('== Turn 5 · Initial Task ·')
    expect(out).toContain('== Turn 6 · Missing Prompt ·')
  })

  it('numbers turns 1-based in document order', () => {
    const turns: DisplayTurn[] = [
      makeTurn({ id: 'a', startedAt: '2024-05-15T10:00:00.000Z', prompt: { title: 'A' } }),
      makeTurn({ id: 'b', startedAt: '2024-05-15T10:01:00.000Z', prompt: { title: 'B' } }),
      makeTurn({ id: 'c', startedAt: '2024-05-15T10:02:00.000Z', prompt: { title: 'C' } }),
    ]
    const out = serializeTranscriptPlainText(turns)
    expect(out.indexOf('== Turn 1 ·')).toBeLessThan(out.indexOf('== Turn 2 ·'))
    expect(out.indexOf('== Turn 2 ·')).toBeLessThan(out.indexOf('== Turn 3 ·'))
  })

  it('omits the title, subtitle, and outputPath lines when the prompt has none', () => {
    const turns: DisplayTurn[] = [
      makeTurn({
        id: 't1',
        startedAt: '2024-05-15T10:00:00.000Z',
        prompt: { kind: 'followup', text: 'body-only' },
        assistantParts: [],
      }),
    ]
    const out = serializeTranscriptPlainText(turns)
    expect(out).toContain('== Turn 1 · Follow-up ·')
    expect(out).toContain('body-only')
    expect(out).not.toContain('Output:')
    expect(out).not.toContain('Context:')
  })

  it('renders tool parts as [tool <name>] <title> falling back to target then bare name', () => {
    const turns: DisplayTurn[] = [
      makeTurn({
        id: 't1',
        startedAt: '2024-05-15T10:00:00.000Z',
        prompt: { kind: 'task', title: 'Tools' },
        assistantParts: [
          { id: 'p1', partType: 'tool', toolCallId: 'tc1', normalizedName: 'apply_patch', toolName: 'apply_patch', status: 'completed', displayTitle: 'src/x.ts', startedAt: '2024-05-15T10:00:01.000Z', hasError: false, isContextTool: false },
          { id: 'p2', partType: 'tool', toolCallId: 'tc2', normalizedName: 'read', toolName: 'read', status: 'completed', target: 'src/y.ts', startedAt: '2024-05-15T10:00:02.000Z', hasError: false, isContextTool: true },
          { id: 'p3', partType: 'tool', toolCallId: 'tc3', normalizedName: 'unknown_tool', toolName: 'unknown_tool', status: 'completed', startedAt: '2024-05-15T10:00:03.000Z', hasError: false, isContextTool: false },
        ],
      }),
    ]
    const out = serializeTranscriptPlainText(turns)
    expect(out).toContain('[tool apply_patch] src/x.ts')
    expect(out).toContain('[tool read] src/y.ts')
    expect(out).toContain('[tool unknown_tool]')
  })

  it('renders context-group parts as [context-group] <title>', () => {
    const turns: DisplayTurn[] = [
      makeTurn({
        id: 't1',
        startedAt: '2024-05-15T10:00:00.000Z',
        prompt: { kind: 'task', title: 'Group' },
        assistantParts: [
          {
            id: 'cg1',
            partType: 'context-group',
            title: 'Explored · 2 reads',
            tools: [],
            hasError: false,
          },
        ],
      }),
    ]
    const out = serializeTranscriptPlainText(turns)
    expect(out).toContain('[context-group] Explored · 2 reads')
  })

  it('copies visible tool details including input, output, error, changed files, and nested context-group tools', () => {
    const turns: DisplayTurn[] = [
      makeTurn({
        id: 't1',
        startedAt: '2024-05-15T10:00:00.000Z',
        prompt: { kind: 'task', title: 'Detailed tools' },
        assistantParts: [
          {
            id: 'tool-1',
            partType: 'tool',
            toolCallId: 'tc-1',
            normalizedName: 'bash',
            toolName: 'bash',
            status: 'failed',
            displayTitle: 'npm test',
            input: '{"command":"npm test"}',
            output: 'failing test output',
            rawInput: 'raw command input',
            rawOutput: 'raw command output',
            error: 'exit code 1',
            details: { shell: 'bash', durationMs: 1250 },
            changedFiles: [
              { path: 'src/a.ts', operation: 'modified', additions: 2, deletions: 1 },
              { path: 'src/new.ts', operation: 'created', additions: 12 },
              { path: 'src/renamed.ts', oldPath: 'src/old.ts', operation: 'moved' },
            ],
            startedAt: '2024-05-15T10:00:01.000Z',
            hasError: true,
            isContextTool: false,
          },
          {
            id: 'cg1',
            partType: 'context-group',
            title: 'Explored · 1 read',
            hasError: false,
            tools: [
              {
                id: 'nested-tool',
                partType: 'tool',
                toolCallId: 'tc-2',
                normalizedName: 'read',
                toolName: 'read',
                status: 'completed',
                target: 'src/context.ts',
                input: '{"filePath":"src/context.ts"}',
                output: 'export const context = true',
                startedAt: '2024-05-15T10:00:02.000Z',
                hasError: false,
                isContextTool: true,
              },
            ],
          },
        ],
      }),
    ]

    const out = serializeTranscriptPlainText(turns)

    expect(out).toContain('[tool bash] npm test')
    expect(out).toContain('  input:\n{"command":"npm test"}')
    expect(out).toContain('  raw input:\nraw command input')
    expect(out).toContain('  output:\nfailing test output')
    expect(out).toContain('  raw output:\nraw command output')
    expect(out).toContain('  details:\n{\n  "shell": "bash",\n  "durationMs": 1250\n}')
    expect(out).toContain('  [tool-error] exit code 1')
    expect(out).toContain('  [changed-file] modified src/a.ts (+2 -1)')
    expect(out).toContain('  [changed-file] created src/new.ts (+12)')
    expect(out).toContain('  [changed-file] moved src/renamed.ts from src/old.ts')
    expect(out).toContain('[context-group] Explored · 1 read')
    expect(out).toContain('  [tool read] src/context.ts')
    expect(out).toContain('    input:\n  {"filePath":"src/context.ts"}')
    expect(out).toContain('    output:\n  export const context = true')
  })

  it('renders error parts as [error] <message>', () => {
    const turns: DisplayTurn[] = [
      makeTurn({
        id: 't1',
        startedAt: '2024-05-15T10:00:00.000Z',
        prompt: { kind: 'task', title: 'Error' },
        assistantParts: [
          { id: 'e1', partType: 'error', message: 'Tool execution failed', kind: 'failed', at: '2024-05-15T10:00:08.000Z' },
        ],
      }),
    ]
    const out = serializeTranscriptPlainText(turns)
    expect(out).toContain('[error] Tool execution failed')
  })

  it('summarizes reasoning as [reasoning omitted, X KB] matching the AssistantParts helper format', () => {
    const turns: DisplayTurn[] = [
      makeTurn({
        id: 't1',
        startedAt: '2024-05-15T10:00:00.000Z',
        prompt: { kind: 'task', title: 'Reasoning' },
        assistantParts: [
          { id: 'r1', partType: 'reasoning', text: 'x'.repeat(4096), startedAt: '2024-05-15T10:00:01.000Z', completedAt: '2024-05-15T10:00:03.000Z' },
        ],
      }),
    ]
    const out = serializeTranscriptPlainText(turns)
    expect(out).toContain(`[reasoning omitted, ${(4096 / 1024).toFixed(1)} KB]`)
    expect(out).not.toContain('x'.repeat(64))
  })

  it('renders the prompt text in full', () => {
    const turns: DisplayTurn[] = [
      makeTurn({
        id: 't1',
        startedAt: '2024-05-15T10:00:00.000Z',
        prompt: { kind: 'task', title: 'Full text', text: 'long body\nwith newlines\nand details' },
        assistantParts: [],
      }),
    ]
    const out = serializeTranscriptPlainText(turns)
    expect(out).toContain('long body\nwith newlines\nand details')
  })

  it('returns an empty string for an empty transcript', () => {
    const out = serializeTranscriptPlainText([])
    expect(out).toBe('')
  })

  it('is a pure function: input is not mutated', () => {
    const turns: DisplayTurn[] = [
      makeTurn({
        id: 't1',
        startedAt: '2024-05-15T10:00:00.000Z',
        prompt: { kind: 'task', title: 'Pure', text: 'body' },
        assistantParts: [
          { id: 'p1', partType: 'text', text: 'answer', startedAt: '2024-05-15T10:00:01.000Z', completedAt: '2024-05-15T10:00:05.000Z' },
        ],
      }),
    ]
    const snapshot = JSON.stringify(turns)
    serializeTranscriptPlainText(turns)
    serializeTranscriptPlainText(turns)
    expect(JSON.stringify(turns)).toBe(snapshot)
  })

  it('separates sections by a single blank line', () => {
    const turns: DisplayTurn[] = [
      makeTurn({ id: 't1', startedAt: '2024-05-15T10:00:00.000Z', prompt: { title: 'A' } }),
      makeTurn({ id: 't2', startedAt: '2024-05-15T10:01:00.000Z', prompt: { title: 'B' } }),
    ]
    const out = serializeTranscriptPlainText(turns)
    const matches = out.match(/\n\n/g) ?? []
    expect(matches).toHaveLength(1)
  })
})
