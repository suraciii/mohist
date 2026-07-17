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
    expect(screen.getAllByText('/repo/x.ts').length).toBeGreaterThan(0)
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

describe('ContextGroupView', () => {
  it('renders the title prefix and detail split by " · "', () => {
    const group = {
      id: 'ctx-1',
      partType: 'context-group',
      title: 'Explored · 2 reads',
      tools: [
        makeToolPart({ id: 't1', normalizedName: 'read', isContextTool: true, status: 'completed' }),
        makeToolPart({ id: 't2', normalizedName: 'read', isContextTool: true, status: 'completed' }),
      ],
      hasError: false,
    } as Extract<DisplayAssistantPart, { partType: 'context-group' }>

    render(<ContextGroupView title={group.title} tools={group.tools} hasError={group.hasError} />)

    expect(screen.getByText('Explored')).toBeInTheDocument()
    expect(screen.getByText('2 reads')).toBeInTheDocument()
  })

  it('lists each tool via ToolRowView when the group has more than one tool', () => {
    const tools = [
      makeToolPart({ id: 't1', normalizedName: 'read', isContextTool: true, status: 'completed' }),
      makeToolPart({ id: 't2', normalizedName: 'grep', isContextTool: true, status: 'completed' }),
    ]

    render(<ContextGroupView title="Explored · 2 ops" tools={tools} hasError={false} />)

    fireEvent.click(screen.getByRole('button'))

    expect(screen.getAllByRole('button').length).toBeGreaterThanOrEqual(3)
  })

  it('renders the failed indicator when hasError is true', () => {
    render(
      <ContextGroupView
        title="Explored · 1 op"
        tools={[]}
        hasError
      />,
    )

    expect(screen.getByText('failed')).toBeInTheDocument()
  })
})
