import '@testing-library/jest-dom'
import { fireEvent, render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import type { DisplayToolPart } from '../../model/session-transcript-display'
import { ToolRowView } from './index'

function makeToolPart(overrides: Partial<DisplayToolPart>): DisplayToolPart {
  return {
    id: 'tool-1',
    partType: 'tool',
    toolCallId: 'tc-1',
    normalizedName: 'bash',
    toolName: 'bash',
    status: 'completed',
    startedAt: '2026-01-01T00:00:00Z',
    completedAt: '2026-01-01T00:00:01.000Z',
    hasError: false,
    isContextTool: false,
    ...overrides,
  } as DisplayToolPart
}

describe('ToolRowView — verb-led title builder', () => {
  it('shows "$ {command}" for a completed bash call', () => {
    const part = makeToolPart({
      id: 'verb-bash',
      normalizedName: 'bash',
      input: JSON.stringify({ command: 'dotnet test' }),
      output: 'ok',
    })

    const { container } = render(<ToolRowView part={part} />)

    const verb = container.querySelector('[data-testid="tool-row-verb-title"]')
    expect(verb?.textContent?.trim()).toBe('$ dotnet test')
  })

  it('shows "Edited {file}" for a completed edit call', () => {
    const part = makeToolPart({
      id: 'verb-edit',
      normalizedName: 'edit',
      changedFiles: [{ path: '/repo/TaskRun.cs', operation: 'modified', additions: 12, deletions: 3 }],
      input: JSON.stringify({ filePath: '/repo/TaskRun.cs', oldString: 'x', newString: 'y' }),
    })

    const { container } = render(<ToolRowView part={part} />)

    const verb = container.querySelector('[data-testid="tool-row-verb-title"]')
    expect(verb?.textContent?.trim()).toBe('Edited TaskRun.cs')
  })

  it('shows "Read {basename}" for a completed read call', () => {
    const part = makeToolPart({
      id: 'verb-read',
      normalizedName: 'read',
      input: JSON.stringify({ filePath: '/repo/src/foo.ts' }),
      output: 'x',
    })

    const { container } = render(<ToolRowView part={part} />)

    const verb = container.querySelector('[data-testid="tool-row-verb-title"]')
    expect(verb?.textContent?.trim()).toBe('Read foo.ts')
  })

  it('shows "Read {basename}" for a completed glob call (read family)', () => {
    const part = makeToolPart({
      id: 'verb-glob',
      normalizedName: 'glob',
      input: JSON.stringify({ filePath: '/repo/src/foo.ts' }),
      output: 'x',
    })

    const { container } = render(<ToolRowView part={part} />)

    const verb = container.querySelector('[data-testid="tool-row-verb-title"]')
    expect(verb?.textContent?.trim()).toBe('Read foo.ts')
  })

  it('shows "Searched {query}" for a completed grep call', () => {
    const part = makeToolPart({
      id: 'verb-grep',
      normalizedName: 'grep',
      input: JSON.stringify({ query: 'TODO' }),
      output: '[]',
    })

    const { container } = render(<ToolRowView part={part} />)

    const verb = container.querySelector('[data-testid="tool-row-verb-title"]')
    expect(verb?.textContent?.trim()).toBe('Searched TODO')
  })

  it('shows "Editing {file}" for a running edit call', () => {
    const part = makeToolPart({
      id: 'verb-running-edit',
      normalizedName: 'edit',
      status: 'running',
      startedAt: '2026-01-01T00:00:00Z',
      input: JSON.stringify({ filePath: '/repo/WorkflowDefinition.cs' }),
    })

    const { container } = render(<ToolRowView part={part} />)

    const verb = container.querySelector('[data-testid="tool-row-verb-title"]')
    expect(verb?.textContent?.trim()).toBe('Editing WorkflowDefinition.cs')
  })

  it('shows "Failed to edit {file}" for a failed edit call', () => {
    const part = makeToolPart({
      id: 'verb-failed-edit',
      normalizedName: 'edit',
      status: 'failed',
      input: JSON.stringify({ filePath: '/repo/src/foo.ts' }),
      error: 'boom',
      hasError: true,
    })

    const { container } = render(<ToolRowView part={part} />)

    const verb = container.querySelector('[data-testid="tool-row-verb-title"]')
    expect(verb?.textContent?.trim()).toBe('Failed to edit foo.ts')
  })

  it('falls back to toolName for non-verb-family tools', () => {
    const part = makeToolPart({
      id: 'verb-other',
      normalizedName: 'todowrite',
      input: JSON.stringify({ todos: [] }),
    })

    const { container } = render(<ToolRowView part={part} />)

    const verb = container.querySelector('[data-testid="tool-row-verb-title"]')
    expect(verb?.textContent?.trim()).toBe('todowrite')
  })

  it('uses a single trailing ellipsis for running rows without a recognizable target', () => {
    const part = makeToolPart({
      id: 'verb-running-other',
      normalizedName: 'task',
      status: 'running',
    })

    const { container } = render(<ToolRowView part={part} />)

    const verb = container.querySelector('[data-testid="tool-row-verb-title"]')
    expect(verb?.textContent?.trim()).toBe('task…')
  })
})

describe('ToolRowView — inline edit stats', () => {
  it('shows file path + +N / −M inline on a single-file edit collapsed row', () => {
    const part = makeToolPart({
      id: 'edit-stats-1',
      normalizedName: 'edit',
      changedFiles: [{ path: '/repo/TaskRun.cs', operation: 'modified', additions: 12, deletions: 3 }],
      input: JSON.stringify({ filePath: '/repo/TaskRun.cs', oldString: 'x', newString: 'y' }),
    })

    const { container } = render(<ToolRowView part={part} />)

    expect(container.querySelector('[data-testid="tool-row-edit-file"]')?.textContent).toBe('/repo/TaskRun.cs')
    const stats = container.querySelector('[data-testid="tool-row-edit-stats"]')
    expect(stats?.textContent).toContain('+12')
    expect(stats?.textContent).toContain('−3')
  })

  it('summarizes N files changed for multi-file edit collapsed row, list only on expand', () => {
    const part = makeToolPart({
      id: 'edit-stats-2',
      normalizedName: 'edit',
      changedFiles: [
        { path: '/repo/a.ts', operation: 'modified', additions: 3, deletions: 0 },
        { path: '/repo/b.ts', operation: 'modified', additions: 2, deletions: 1 },
        { path: '/repo/c.ts', operation: 'modified', additions: 1, deletions: 4 },
      ],
      input: JSON.stringify({ filePath: '/repo/a.ts', oldString: 'x', newString: 'y' }),
    })

    const { container } = render(<ToolRowView part={part} />)

    expect(container.querySelector('[data-testid="tool-row-edit-file"]')).toBeNull()
    expect(container.querySelector('[data-testid="tool-row-edit-file-count"]')?.textContent).toBe('3 files')
    expect(container.querySelector('[data-testid="tool-row-edit-stats"]')?.textContent).toBeFalsy()

    fireEvent.click(screen.getByRole('button'))
    expect(screen.getByText('/repo/a.ts')).toBeInTheDocument()
    expect(screen.getByText('/repo/b.ts')).toBeInTheDocument()
    expect(screen.getByText('/repo/c.ts')).toBeInTheDocument()
  })

  it('produces inline stats when only raw input is available (parseEditWriteChanges fallback)', () => {
    const part = makeToolPart({
      id: 'edit-stats-3',
      normalizedName: 'edit',
      input: JSON.stringify({ filePath: '/repo/x.ts', oldString: 'a\nb', newString: 'a\nb\nc' }),
    })

    const { container } = render(<ToolRowView part={part} />)

    expect(container.querySelector('[data-testid="tool-row-edit-file"]')?.textContent).toBe('x.ts')
    const stats = container.querySelector('[data-testid="tool-row-edit-stats"]')
    expect(stats?.textContent).toContain('+3')
    expect(stats?.textContent).toContain('−2')
  })
})

describe('ToolRowView — whole-row failure styling', () => {
  it('marks a failed tool row with data-tone="danger" and bg-danger subtle', () => {
    const part = makeToolPart({
      id: 'fail-row-1',
      normalizedName: 'bash',
      status: 'failed',
      input: JSON.stringify({ command: 'false' }),
      error: 'exit 1',
      hasError: true,
    })

    const { container } = render(<ToolRowView part={part} />)

    const row = container.querySelector('[data-testid="tool-row"]')
    expect(row?.getAttribute('data-tone')).toBe('danger')
    const button = row?.querySelector('button')
    expect(button?.className).toContain('bg-danger-subtle')
    expect(button?.className).toContain('text-danger')
  })

  it('does not apply danger styling to a completed tool row', () => {
    const part = makeToolPart({
      id: 'fail-row-2',
      normalizedName: 'bash',
      status: 'completed',
      input: JSON.stringify({ command: 'true' }),
      output: 'ok',
    })

    const { container } = render(<ToolRowView part={part} />)

    const row = container.querySelector('[data-testid="tool-row"]')
    expect(row?.getAttribute('data-tone')).not.toBe('danger')
    const button = row?.querySelector('button')
    expect(button?.className).not.toContain('bg-danger-subtle')
  })
})

describe('ToolRowView — running/pending in-progress state', () => {
  it('renders a single-line in-progress state without a duration timer', () => {
    const part = makeToolPart({
      id: 'running-1',
      normalizedName: 'bash',
      status: 'running',
      startedAt: '2026-01-01T00:00:00Z',
      input: JSON.stringify({ command: 'long-running' }),
    })

    const { container } = render(<ToolRowView part={part} />)

    const row = container.querySelector('[data-testid="tool-row"]')
    expect(row?.getAttribute('data-tool-state')).toBe('running')
    expect(container.querySelector('[data-testid="tool-row-duration"]')).toBeNull()
    expect(container.querySelector('[data-testid="tool-row-verb-title"]')?.textContent).toContain('long-running')
  })

  it('omits aria-expanded so a running row is not announced as expandable', () => {
    const part = makeToolPart({
      id: 'running-2',
      normalizedName: 'bash',
      status: 'running',
      startedAt: '2026-01-01T00:00:00Z',
      input: JSON.stringify({ command: 'long-running' }),
    })

    render(<ToolRowView part={part} />)

    const button = screen.getByRole('button')
    expect(button.hasAttribute('aria-expanded')).toBe(false)
  })
})

describe('ToolRowView — stable identity anchors', () => {
  it('exposes data-tool-call-id tied to the part toolCallId', () => {
    const part = makeToolPart({
      id: 'identity-1',
      toolCallId: 'tc-stable-42',
      normalizedName: 'bash',
      input: JSON.stringify({ command: 'echo a' }),
      output: 'a',
    })

    const { container } = render(<ToolRowView part={part} />)
    const row = container.querySelector('[data-testid="tool-row"]')
    expect(row?.getAttribute('data-tool-call-id')).toBe('tc-stable-42')
  })

  it('exposes data-tool-state holding the current status', () => {
    const part = makeToolPart({
      id: 'identity-2',
      toolCallId: 'tc-stable-43',
      normalizedName: 'bash',
      status: 'completed',
      input: JSON.stringify({ command: 'echo a' }),
      output: 'a',
    })

    const { container } = render(<ToolRowView part={part} />)
    const row = container.querySelector('[data-testid="tool-row"]')
    expect(row?.getAttribute('data-tool-state')).toBe('completed')
  })

  it('keeps data-tool-call-id stable across a running→completed transition', () => {
    const runningPart = makeToolPart({
      id: 'identity-3',
      toolCallId: 'tc-stable-44',
      normalizedName: 'bash',
      status: 'running',
      startedAt: '2026-01-01T00:00:00Z',
      input: JSON.stringify({ command: 'echo a' }),
    })

    const { container, rerender } = render(<ToolRowView part={runningPart} />)

    const runningRow = container.querySelector('[data-testid="tool-row"]')
    expect(runningRow?.getAttribute('data-tool-call-id')).toBe('tc-stable-44')
    expect(runningRow?.getAttribute('data-tool-state')).toBe('running')

    const completedPart: DisplayToolPart = {
      ...runningPart,
      status: 'completed',
      output: 'a',
      completedAt: '2026-01-01T00:00:01.000Z',
    }

    rerender(<ToolRowView part={completedPart} />)

    const completedRow = container.querySelector('[data-testid="tool-row"]')
    expect(completedRow?.getAttribute('data-tool-call-id')).toBe('tc-stable-44')
    expect(completedRow?.getAttribute('data-tool-state')).toBe('completed')
  })
})

describe('ToolRowView — full-width invariant', () => {
  it('does not apply a centered-card max-width cap on the row root', () => {
    const part = makeToolPart({
      id: 'full-width-1',
      normalizedName: 'bash',
      input: JSON.stringify({ command: 'pwd' }),
      output: '/home',
    })

    const { container } = render(<ToolRowView part={part} />)

    const row = container.querySelector('[data-testid="tool-row"]')
    expect(row?.className).toContain('w-full')
    expect(row?.className).not.toContain('max-w-')
    expect(row?.className).not.toContain('mx-auto')
  })
})

describe('ToolRowView — collapse/expand', () => {
  it('keeps typed detail hidden when the row is collapsed', () => {
    const part = makeToolPart({
      id: 'collapse-1',
      normalizedName: 'bash',
      input: JSON.stringify({ command: 'echo secret' }),
      output: 'very long output that fills the screen'.repeat(20),
    })

    const { container } = render(<ToolRowView part={part} />)

    expect(container.querySelector('[data-testid="tool-row-detail"]')).toBeNull()
  })

  it('reveals typed detail only after the row is expanded', () => {
    const part = makeToolPart({
      id: 'collapse-2',
      normalizedName: 'bash',
      input: JSON.stringify({ command: 'echo expand-me' }),
      output: 'expand-me',
    })

    render(<ToolRowView part={part} />)

    fireEvent.click(screen.getByRole('button'))

    expect(screen.getByText('expand-me')).toBeInTheDocument()
  })
})
