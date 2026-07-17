import '@testing-library/jest-dom'
import { fireEvent, render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import type { DisplayAssistantPart, DisplayToolPart } from '../../model/session-transcript-display'
import { ToolRowView, ContextGroupView } from './index'

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

describe('ToolRowView dispatcher', () => {
  it('routes bash input through BashContentView', () => {
    const part = makeToolPart({
      id: 'bash-1',
      normalizedName: 'bash',
      input: JSON.stringify({ command: 'pwd' }),
      output: '/home',
    })

    render(<ToolRowView part={part} />)

    fireEvent.click(screen.getByRole('button'))

    expect(screen.getByText('Command')).toBeInTheDocument()
    expect(screen.getByText('Output')).toBeInTheDocument()
  })

  it('routes read input through ReadContentView', () => {
    const part = makeToolPart({
      id: 'read-1',
      normalizedName: 'read',
      input: JSON.stringify({ filePath: '/repo/foo.ts' }),
      output: 'x',
    })

    render(<ToolRowView part={part} />)

    fireEvent.click(screen.getByRole('button'))

    expect(screen.getByText('Reading')).toBeInTheDocument()
    expect(screen.getByText('foo.ts')).toBeInTheDocument()
  })

  it('routes grep input through SearchContentView', () => {
    const part = makeToolPart({
      id: 'grep-1',
      normalizedName: 'grep',
      input: JSON.stringify({ pattern: 'foo' }),
      output: JSON.stringify(['hit1', 'hit2']),
    })

    render(<ToolRowView part={part} />)

    fireEvent.click(screen.getByRole('button'))

    expect(screen.getByText('Searching')).toBeInTheDocument()
  })

  it('routes todowrite input through TodoContentView', () => {
    const part = makeToolPart({
      id: 'todo-1',
      normalizedName: 'todowrite',
      input: JSON.stringify({ todos: [{ status: 'completed', content: 'done' }] }),
    })

    render(<ToolRowView part={part} />)

    fireEvent.click(screen.getByRole('button'))

    expect(screen.getByText('1/1 completed')).toBeInTheDocument()
  })

  it('routes task input through DelegationContentView', () => {
    const part = makeToolPart({
      id: 'task-1',
      normalizedName: 'task',
      input: JSON.stringify({ description: 'do work' }),
      details: { description: 'do work', subagentType: 'explorer' },
    })

    render(<ToolRowView part={part} />)

    fireEvent.click(screen.getByRole('button'))

    expect(screen.getByText('Delegation')).toBeInTheDocument()
    expect(screen.getByText('explorer')).toBeInTheDocument()
  })

  it('routes edit input through DiffContentView', () => {
    const part = makeToolPart({
      id: 'edit-1',
      normalizedName: 'edit',
      changedFiles: [
        { path: '/repo/x.ts', operation: 'modified' as const, additions: 1, deletions: 1 },
      ],
      input: JSON.stringify({ filePath: '/repo/x.ts', oldString: 'a', newString: 'b' }),
    })

    render(<ToolRowView part={part} />)

    fireEvent.click(screen.getByRole('button'))

    expect(screen.getByText(/Changed files/)).toBeInTheDocument()
    expect(screen.getAllByText('x.ts').length).toBeGreaterThan(0)
  })

  it('renders error message above all content views', () => {
    const part = makeToolPart({
      id: 'err-1',
      normalizedName: 'bash',
      error: 'something broke',
      input: JSON.stringify({ command: 'x' }),
      output: 'y',
    })

    const { container } = render(<ToolRowView part={part} />)

    fireEvent.click(screen.getByRole('button'))

    expect(container.textContent).toContain('something broke')
  })

  it('does not expand when nothing to show', () => {
    const part = makeToolPart({
      id: 'empty-1',
      normalizedName: 'bash',
    })

    const { container } = render(<ToolRowView part={part} />)

    fireEvent.click(screen.getByRole('button'))

    expect(container.querySelector('.cursor-default')).toBeInTheDocument()
  })
})

describe('ToolRowView accessibility', () => {
  it('exposes aria-expanded=false on the disclosure button when expandable and collapsed', () => {
    const part = makeToolPart({
      id: 'ax-1',
      normalizedName: 'bash',
      input: JSON.stringify({ command: 'echo hi' }),
      output: 'hi',
    })

    render(<ToolRowView part={part} />)

    const button = screen.getByRole('button')
    expect(button.getAttribute('aria-expanded')).toBe('false')
  })

  it('flips aria-expanded to true after the user expands the row', () => {
    const part = makeToolPart({
      id: 'ax-2',
      normalizedName: 'bash',
      input: JSON.stringify({ command: 'echo hi' }),
      output: 'hi',
    })

    render(<ToolRowView part={part} />)

    const button = screen.getByRole('button')
    fireEvent.click(button)

    expect(button.getAttribute('aria-expanded')).toBe('true')
  })

  it('does not expose aria-expanded when the row has nothing to disclose', () => {
    const part = makeToolPart({
      id: 'ax-3',
      normalizedName: 'bash',
    })

    render(<ToolRowView part={part} />)

    const button = screen.getByRole('button')
    expect(button.hasAttribute('aria-expanded')).toBe(false)
  })

  it('does not expose aria-expanded on a running row even if input is present', () => {
    const part = makeToolPart({
      id: 'ax-4',
      normalizedName: 'bash',
      status: 'running',
      input: JSON.stringify({ command: 'long-running' }),
    })

    render(<ToolRowView part={part} />)

    const button = screen.getByRole('button')
    expect(button.hasAttribute('aria-expanded')).toBe(false)
  })

  it('marks the chevron svg aria-hidden', () => {
    const part = makeToolPart({
      id: 'ax-5',
      normalizedName: 'bash',
      input: JSON.stringify({ command: 'echo hi' }),
      output: 'hi',
    })

    const { container } = render(<ToolRowView part={part} />)

    const chevron = container.querySelector('svg[viewBox="0 0 20 20"]')
    expect(chevron).not.toBeNull()
    expect(chevron?.getAttribute('aria-hidden')).toBe('true')
  })

  it('marks the tool icon svg aria-hidden', () => {
    const part = makeToolPart({
      id: 'ax-6',
      normalizedName: 'bash',
      input: JSON.stringify({ command: 'echo hi' }),
      output: 'hi',
    })

    const { container } = render(<ToolRowView part={part} />)

    const allSvgs = container.querySelectorAll('svg')
    for (const svg of Array.from(allSvgs)) {
      expect(svg.getAttribute('aria-hidden')).toBe('true')
    }
  })

  it('provides a readable accessible name sourced from the tool label', () => {
    const part = makeToolPart({
      id: 'ax-7',
      normalizedName: 'bash',
      displayTitle: 'echo hi',
      input: JSON.stringify({ command: 'echo hi' }),
      output: 'hi',
    })

    render(<ToolRowView part={part} />)

    const button = screen.getByRole('button', { name: 'echo hi' })
    expect(button).toBeInTheDocument()
    const name = button.textContent?.trim() ?? ''
    expect(name.length).toBeGreaterThan(0)
    expect(name).not.toBe('unknown')
  })

  it('does not surface "unknown" as the accessible name when the tool name is missing', () => {
    const part = makeToolPart({
      id: 'ax-8',
      normalizedName: 'unknown',
      toolName: 'unknown',
      input: JSON.stringify({ command: 'echo readable' }),
      output: 'readable',
    })

    render(<ToolRowView part={part} />)

    const buttons = screen.getAllByRole('button')
    const main = buttons.find(b => b.hasAttribute('aria-expanded'))!
    const name = main.textContent?.trim() ?? ''
    expect(name.length).toBeGreaterThan(0)
    expect(name).not.toBe('unknown')
  })
})

describe('ContextGroupView', () => {
  it('renders the title prefix and detail split by " · "', () => {
    const group = {
      id: 'ctx-1',
      partType: 'context-group',
      title: 'Gathering context · 2 reads',
      tools: [
        makeToolPart({ id: 't1', normalizedName: 'read', isContextTool: true, status: 'completed' }),
        makeToolPart({ id: 't2', normalizedName: 'read', isContextTool: true, status: 'completed' }),
      ],
      hasError: false,
    } as Extract<DisplayAssistantPart, { partType: 'context-group' }>

    render(<ContextGroupView title={group.title} tools={group.tools} hasError={group.hasError} />)

    expect(screen.getByText('Gathering context')).toBeInTheDocument()
    expect(screen.getByText('2 reads')).toBeInTheDocument()
  })

  it('shows a single expanded read summary when the group has one completed context tool', () => {
    const tool = makeToolPart({
      id: 't1',
      normalizedName: 'read',
      isContextTool: true,
      status: 'completed',
      input: JSON.stringify({ filePath: '/repo/foo.ts' }),
      output: 'file contents here',
    })

    const { container } = render(
      <ContextGroupView title="Gathering context · /repo/foo.ts" tools={[tool]} hasError={false} />,
    )

    fireEvent.click(screen.getByRole('button'))

    expect(container.textContent).toContain('Reading')
    expect(container.textContent).toContain('file contents here')
  })

  it('lists each tool via ToolRowView when the group has more than one tool', () => {
    const tools = [
      makeToolPart({ id: 't1', normalizedName: 'read', isContextTool: true, status: 'completed' }),
      makeToolPart({ id: 't2', normalizedName: 'grep', isContextTool: true, status: 'completed' }),
    ]

    render(<ContextGroupView title="Gathering context · 2 ops" tools={tools} hasError={false} />)

    fireEvent.click(screen.getByRole('button'))

    expect(screen.getAllByRole('button').length).toBeGreaterThanOrEqual(3)
  })

  it('renders the failed indicator when hasError is true', () => {
    render(
      <ContextGroupView
        title="Gathering context · 1 op"
        tools={[]}
        hasError
      />,
    )

    expect(screen.getByText('failed')).toBeInTheDocument()
  })
})

describe('ContextGroupView accessibility', () => {
  function makeReadTool(overrides: Partial<DisplayToolPart> = {}): DisplayToolPart {
    return makeToolPart({
      id: 't1',
      normalizedName: 'read',
      toolName: 'read',
      isContextTool: true,
      status: 'completed',
      input: JSON.stringify({ filePath: '/repo/foo.ts' }),
      output: 'contents',
      ...overrides,
    })
  }

  it('exposes aria-expanded=false on the disclosure button initially', () => {
    render(
      <ContextGroupView
        title="Gathering context · 2 reads"
        tools={[makeReadTool({ id: 'a' }), makeReadTool({ id: 'b' })]}
        hasError={false}
      />,
    )

    const button = screen.getByRole('button')
    expect(button.getAttribute('aria-expanded')).toBe('false')
  })

  it('flips aria-expanded to true after the user expands the group', () => {
    render(
      <ContextGroupView
        title="Gathering context · 2 reads"
        tools={[makeReadTool({ id: 'a' }), makeReadTool({ id: 'b' })]}
        hasError={false}
      />,
    )

    const button = screen.getByRole('button')
    fireEvent.click(button)

    expect(button.getAttribute('aria-expanded')).toBe('true')
  })

  it('marks all decorative svgs aria-hidden (icon + chevron)', () => {
    const { container } = render(
      <ContextGroupView
        title="Gathering context · 1 read"
        tools={[makeReadTool()]}
        hasError={false}
      />,
    )

    const svgs = container.querySelectorAll('svg')
    expect(svgs.length).toBeGreaterThan(0)
    for (const svg of Array.from(svgs)) {
      expect(svg.getAttribute('aria-hidden')).toBe('true')
    }
  })

  it('provides a readable accessible name from the title prefix', () => {
    render(
      <ContextGroupView
        title="Gathering context · 2 reads"
        tools={[makeReadTool({ id: 'a' }), makeReadTool({ id: 'b' })]}
        hasError={false}
      />,
    )

    const button = screen.getByRole('button', { name: /Gathering context/ })
    expect(button).toBeInTheDocument()
    const name = button.textContent?.trim() ?? ''
    expect(name.length).toBeGreaterThan(0)
    expect(name).not.toBe('unknown')
  })

  it('does not expose "unknown" as the accessible name even when group title is absent', () => {
    render(
      <ContextGroupView
        title="Gathering context · details"
        tools={[makeReadTool()]}
        hasError={false}
      />,
    )

    const button = screen.getByRole('button')
    const name = button.textContent?.trim() ?? ''
    expect(name).not.toBe('unknown')
  })
})
