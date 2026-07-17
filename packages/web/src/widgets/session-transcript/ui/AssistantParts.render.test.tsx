import '@testing-library/jest-dom'
import { fireEvent, render, screen, within } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import type { DisplayAssistantPart, DisplayToolPart } from '../model/session-transcript-display'
import { AssistantParts, ErrorPartView } from './AssistantParts'

function makeToolPart(overrides: Partial<DisplayToolPart>): DisplayToolPart {
  return {
    id: 'tool-1',
    partType: 'tool',
    toolCallId: 'tc-1',
    normalizedName: 'bash',
    toolName: 'bash',
    status: 'completed',
    startedAt: '2026-01-01T00:00:00Z',
    hasError: false,
    isContextTool: false,
    ...overrides,
  } as DisplayToolPart
}

function renderAssistantParts(parts: DisplayAssistantPart[]) {
  return render(<AssistantParts parts={parts} />)
}

describe('AssistantParts tool views — baseline render', () => {
  it('renders bash command + output preview for a terminal tool part', () => {
    const part = makeToolPart({
      id: 'bash-1',
      normalizedName: 'bash',
      toolName: 'bash',
      status: 'completed',
      input: JSON.stringify({ command: 'echo hi' }),
      output: 'all green',
      details: { exitCode: 0, outputPreview: 'all green', cwd: '/repo' },
    })

    renderAssistantParts([part])

    const button = screen.getByRole('button')
    fireEvent.click(button)

    expect(screen.getByText('Command')).toBeInTheDocument()
    expect(screen.getByText('Output')).toBeInTheDocument()
    expect(screen.getByText('all green')).toBeInTheDocument()
    expect(screen.getByText('/repo')).toBeInTheDocument()
    expect(screen.getByText('success')).toBeInTheDocument()
  })

  it('renders bash exit code badge when exit code is non-zero', () => {
    const part = makeToolPart({
      id: 'bash-1',
      normalizedName: 'bash',
      toolName: 'bash',
      input: JSON.stringify({ command: 'false' }),
      output: 'something failed',
      details: { exitCode: 1, outputPreview: 'something failed' },
    })

    renderAssistantParts([part])

    fireEvent.click(screen.getByRole('button'))

    expect(screen.getByText('exit 1')).toBeInTheDocument()
  })

  it('renders read tool with file basename and truncated output', () => {
    const part = makeToolPart({
      id: 'read-1',
      normalizedName: 'read',
      toolName: 'read',
      input: JSON.stringify({ filePath: '/repo/src/foo.ts' }),
      output: Array.from({ length: 12 }, (_, i) => `line${i + 1}`).join('\n'),
    })

    const { container } = renderAssistantParts([part])

    fireEvent.click(screen.getByRole('button'))

    expect(screen.getByText('Reading')).toBeInTheDocument()
    expect(screen.getByText('foo.ts')).toBeInTheDocument()
    const outputPre = container.querySelector('pre')
    expect(outputPre?.textContent).toContain('line1')
    expect(outputPre?.textContent).toContain('line8')
    expect(outputPre?.textContent).not.toContain('line9')
    expect(outputPre?.textContent).toContain('...')
  })

  it('renders search tool with pattern and results (truncated to 5)', () => {
    const results = ['result-one', 'result-two', 'result-three', 'result-four', 'result-five', 'result-six', 'result-seven']
    const part = makeToolPart({
      id: 'grep-1',
      normalizedName: 'grep',
      toolName: 'grep',
      input: JSON.stringify({ pattern: 'uniquepat', type: 'js' }),
      output: JSON.stringify(results),
    })

    const { container } = renderAssistantParts([part])

    fireEvent.click(screen.getByRole('button'))

    expect(screen.getByText('Searching')).toBeInTheDocument()
    expect(screen.getAllByText('uniquepat').length).toBeGreaterThan(0)
    expect(screen.getByText('(js)')).toBeInTheDocument()
    const allPres = container.querySelectorAll('pre')
    const allText = Array.from(allPres).map(p => p.textContent).join('\n')
    expect(allText).toContain('result-one')
    expect(allText).toContain('result-five')
    expect(allText).not.toContain('result-six')
  })

  it('renders todo list with completed/in-progress/pending counts', () => {
    const part = makeToolPart({
      id: 'todo-1',
      normalizedName: 'todowrite',
      toolName: 'todowrite',
      status: 'completed',
      input: JSON.stringify({
        todos: [
          { status: 'completed', content: 'Step 1' },
          { status: 'completed', content: 'Step 2' },
          { status: 'in_progress', content: 'Step 3' },
          { status: 'pending', content: 'Step 4' },
        ],
      }),
    })

    renderAssistantParts([part])

    fireEvent.click(screen.getByRole('button'))

    expect(screen.getByText('2/4 completed')).toBeInTheDocument()
    expect(screen.getByText('1 in progress')).toBeInTheDocument()
    expect(screen.getByText('1 pending')).toBeInTheDocument()
    expect(screen.getByText('Step 1')).toBeInTheDocument()
    expect(screen.getByText('Step 3')).toBeInTheDocument()
  })

  it('renders delegation view with description, subagentType and childSessionId', () => {
    const part = makeToolPart({
      id: 'task-1',
      normalizedName: 'task',
      toolName: 'task',
      input: JSON.stringify({ description: 'different-input' }),
      details: { description: 'Explore the codebase', subagentType: 'explorer', childSessionId: 'child-42' },
    })

    renderAssistantParts([part])

    fireEvent.click(screen.getByRole('button'))

    expect(screen.getByText('Delegation')).toBeInTheDocument()
    expect(screen.getByText('explorer')).toBeInTheDocument()
    expect(screen.getByText('child-42')).toBeInTheDocument()
    expect(screen.getByText('Explore the codebase')).toBeInTheDocument()
  })

  it('renders diff view with changed files and an expand button', () => {
    const part = makeToolPart({
      id: 'edit-1',
      normalizedName: 'edit',
      toolName: 'edit',
      changedFiles: [
        { path: 'src/foo.ts', operation: 'modified', additions: 2, deletions: 1 },
      ],
      details: { family: 'mutation', files: [{ path: 'src/foo.ts', diff: '--- a/src/foo.ts\n+++ b/src/foo.ts\n@@ -1,1 +1,2 @@\n-line1\n+line1\n+line2' }] },
    })

    renderAssistantParts([part])

    fireEvent.click(screen.getByRole('button'))

    expect(screen.getByText(/Changed files/)).toBeInTheDocument()
    expect(screen.getByText('src/foo.ts')).toBeInTheDocument()
    expect(screen.getByText('+2')).toBeInTheDocument()
    expect(screen.getByText('-1')).toBeInTheDocument()
  })

  it('renders context group header with title detail', () => {
    const group = {
      id: 'ctx-1',
      partType: 'context-group',
      title: 'Explored · 1 read',
      tools: [
        makeToolPart({
          id: 'tool-r1',
          normalizedName: 'read',
          toolName: 'read',
          status: 'completed',
          input: JSON.stringify({ filePath: '/repo/src/foo.ts' }),
          output: 'contents',
          isContextTool: true,
        }),
      ],
      hasError: false,
    } as DisplayAssistantPart

    renderAssistantParts([group])

    expect(screen.getByText('Explored')).toBeInTheDocument()
    expect(screen.getByText('1 read')).toBeInTheDocument()
  })

  it('renders ToolStatusDot variants for each status', () => {
    const statuses: Array<DisplayToolPart['status']> = ['running', 'completed', 'failed', 'cancelled', 'pending']
    const parts: DisplayAssistantPart[] = statuses.map((status, i) => makeToolPart({
      id: `dot-${i}`,
      normalizedName: 'bash',
      toolName: 'bash',
      status,
    }))

    const { container } = renderAssistantParts(parts)

    expect(container.querySelector('[data-tone="success"].bg-success')).toBeInTheDocument()
    expect(container.querySelector('[data-tone="danger"].bg-danger')).toBeInTheDocument()
    expect(container.querySelector('[data-tone="neutral"].bg-muted-foreground\\/60')).toBeInTheDocument()
    expect(container.querySelector('[data-tone="neutral"].bg-muted-foreground\\/40')).toBeInTheDocument()
    expect(container.querySelector('.animate-ping')).toBeInTheDocument()
  })

  it('renders generic fallback input/output when no specialized view matches', () => {
    const part = makeToolPart({
      id: 'generic-1',
      normalizedName: 'unknown',
      toolName: 'unknown',
      input: 'raw-input-text',
      output: 'raw-output-text',
    })

    renderAssistantParts([part])

    fireEvent.click(screen.getByRole('button'))

    expect(screen.getByText('Input')).toBeInTheDocument()
    expect(screen.getByText('Output')).toBeInTheDocument()
    expect(within(screen.getAllByText('raw-input-text')[0].closest('pre')!).getByText('raw-input-text')).toBeInTheDocument()
    expect(within(screen.getAllByText('raw-output-text')[0].closest('pre')!).getByText('raw-output-text')).toBeInTheDocument()
  })
})

describe('ErrorPartView accessibility', () => {
  it('marks the decorative warning svg aria-hidden so screen readers ignore it', () => {
    const { container } = render(<ErrorPartView message="boom" kind="failed" at="2026-01-01T00:00:00Z" />)

    const svg = container.querySelector('svg')
    expect(svg).not.toBeNull()
    expect(svg?.getAttribute('aria-hidden')).toBe('true')
  })
})
