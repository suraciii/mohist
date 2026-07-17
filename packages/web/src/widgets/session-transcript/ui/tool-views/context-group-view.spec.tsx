import '@testing-library/jest-dom'
import { fireEvent, render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import type { DisplayToolPart } from '../../model/session-transcript-display'
import { ContextGroupView } from './index'

function makeToolPart(overrides: Partial<DisplayToolPart>): DisplayToolPart {
  return {
    id: 'tool-1',
    partType: 'tool',
    toolCallId: 'tc-1',
    normalizedName: 'read',
    toolName: 'read',
    status: 'completed',
    startedAt: '2026-01-01T00:00:00Z',
    completedAt: '2026-01-01T00:00:01.000Z',
    hasError: false,
    isContextTool: true,
    ...overrides,
  } as DisplayToolPart
}

describe('ContextGroupView — collapsed summary counts', () => {
  it('renders the "Explored" prefix and per-type counts on the collapsed summary line', () => {
    const tools: DisplayToolPart[] = [
      ...Array.from({ length: 5 }, (_, i) => makeToolPart({ id: `r${i}`, normalizedName: 'read' })),
      ...Array.from({ length: 3 }, (_, i) => makeToolPart({ id: `s${i}`, normalizedName: 'grep' })),
      ...Array.from({ length: 2 }, (_, i) => makeToolPart({ id: `g${i}`, normalizedName: 'glob' })),
    ]

    const { container } = render(
      <ContextGroupView title="Explored · 5 reads · 3 searches · 2 globs" tools={tools} hasError={false} />,
    )

    expect(container.querySelector('[data-testid="context-group-row"]')).toBeInTheDocument()
    expect(screen.getByTestId('context-group-summary-prefix').textContent).toBe('Explored')
    expect(screen.getByTestId('context-group-summary-detail').textContent).toBe('5 reads · 3 searches · 2 globs')
  })

  it('omits the detail element when the projection-built title is just "Explored"', () => {
    render(
      <ContextGroupView title="Explored" tools={[makeToolPart({ id: 'r1' }), makeToolPart({ id: 'r2' })]} hasError={false} />,
    )

    expect(screen.getByTestId('context-group-summary-prefix').textContent).toBe('Explored')
    expect(screen.queryByTestId('context-group-summary-detail')).toBeNull()
  })

  it('keeps children hidden until the user expands the group', () => {
    const tools: DisplayToolPart[] = [
      makeToolPart({ id: 'r1', normalizedName: 'read', input: JSON.stringify({ filePath: '/repo/a.ts' }), output: 'a' }),
      makeToolPart({ id: 'r2', normalizedName: 'read', input: JSON.stringify({ filePath: '/repo/b.ts' }), output: 'b' }),
    ]

    const { container } = render(
      <ContextGroupView title="Explored · 2 reads" tools={tools} hasError={false} />,
    )

    expect(container.querySelector('[data-testid="context-group-children"]')).toBeNull()
  })
})

describe('ContextGroupView — expand reveals individual tool rows', () => {
  it('renders each merged tool as a ToolRowView with status/title/expand-to-detail', () => {
    const tools: DisplayToolPart[] = [
      makeToolPart({
        id: 'r1',
        normalizedName: 'read',
        input: JSON.stringify({ filePath: '/repo/a.ts' }),
        output: 'a-content',
      }),
      makeToolPart({
        id: 'g1',
        normalizedName: 'grep',
        input: JSON.stringify({ query: 'TODO' }),
        output: '[]',
      }),
    ]

    render(
      <ContextGroupView title="Explored · 1 read · 1 search" tools={tools} hasError={false} />,
    )

    fireEvent.click(screen.getByRole('button'))

    const children = screen.getByTestId('context-group-children')
    const toolRows = children.querySelectorAll('[data-testid="tool-row"]')
    expect(toolRows.length).toBe(2)

    expect(toolRows[0].getAttribute('data-tool-call-id')).toBe('tc-1')
    expect(toolRows[1].getAttribute('data-tool-call-id')).toBe('tc-1')

    fireEvent.click(toolRows[0].querySelector('button')!)
    expect(screen.getByText('a-content')).toBeInTheDocument()
  })
})

describe('ContextGroupView — failure-on-summary signaling', () => {
  it('applies data-tone="danger" on the collapsed summary line when any tool failed', () => {
    const tools: DisplayToolPart[] = [
      makeToolPart({ id: 'r1', status: 'completed' }),
      makeToolPart({
        id: 'r2',
        status: 'failed',
        error: 'boom',
        hasError: true,
      }),
    ]

    const { container } = render(
      <ContextGroupView title="Explored · 2 reads" tools={tools} hasError={true} />,
    )

    const row = container.querySelector('[data-testid="context-group-row"]')
    expect(row?.getAttribute('data-tone')).toBe('danger')

    const button = row?.querySelector('button')
    expect(button?.className).toContain('bg-danger-subtle')
    expect(button?.className).toContain('text-danger')
  })

  it('renders the "failed" label on the collapsed summary line', () => {
    const tools: DisplayToolPart[] = [
      makeToolPart({ id: 'r1', status: 'failed', hasError: true, error: 'boom' }),
      makeToolPart({ id: 'r2', status: 'completed' }),
    ]

    render(
      <ContextGroupView title="Explored · 2 reads" tools={tools} hasError={true} />,
    )

    const failedLabel = screen.getByTestId('context-group-failed-label')
    expect(failedLabel.textContent).toBe('failed')
    expect(failedLabel.getAttribute('data-tone')).toBe('danger')
  })

  it('signals the failure without requiring expansion', () => {
    const tools: DisplayToolPart[] = [
      makeToolPart({ id: 'r1', status: 'completed' }),
      makeToolPart({ id: 'r2', status: 'failed', hasError: true, error: 'boom' }),
    ]

    const { container } = render(
      <ContextGroupView title="Explored · 2 reads" tools={tools} hasError={true} />,
    )

    expect(container.querySelector('[data-testid="context-group-children"]')).toBeNull()
    expect(screen.getByTestId('context-group-failed-label')).toBeInTheDocument()
  })

  it('does not apply danger treatment when no tool in the group failed', () => {
    const tools: DisplayToolPart[] = [
      makeToolPart({ id: 'r1', status: 'completed' }),
      makeToolPart({ id: 'r2', status: 'completed' }),
    ]

    const { container } = render(
      <ContextGroupView title="Explored · 2 reads" tools={tools} hasError={false} />,
    )

    const row = container.querySelector('[data-testid="context-group-row"]')
    expect(row?.getAttribute('data-tone')).toBe('neutral')
    expect(screen.queryByTestId('context-group-failed-label')).toBeNull()

    const button = row?.querySelector('button')
    expect(button?.className).not.toContain('bg-danger-subtle')
  })
})

describe('ContextGroupView — full-width invariant', () => {
  it('does not apply a centered-card max-width cap on the grouped row root', () => {
    const tools: DisplayToolPart[] = [
      makeToolPart({ id: 'r1' }),
      makeToolPart({ id: 'r2' }),
    ]

    const { container } = render(
      <ContextGroupView title="Explored · 2 reads" tools={tools} hasError={false} />,
    )

    const row = container.querySelector('[data-testid="context-group-row"]')
    expect(row?.className).toContain('w-full')
    expect(row?.className).not.toContain('max-w-')
    expect(row?.className).not.toContain('mx-auto')
  })
})

describe('ContextGroupView — group vs lone tool invariant', () => {
  it('renders every merged tool as a ToolRowView regardless of how many tools are in the group', () => {
    const tools: DisplayToolPart[] = [
      makeToolPart({ id: 'r1', input: JSON.stringify({ filePath: '/repo/a.ts' }), output: 'a' }),
      makeToolPart({ id: 'r2', input: JSON.stringify({ filePath: '/repo/b.ts' }), output: 'b' }),
    ]

    render(
      <ContextGroupView title="Explored · 2 reads" tools={tools} hasError={false} />,
    )

    fireEvent.click(screen.getByRole('button'))

    const toolRows = screen.getAllByTestId('tool-row')
    expect(toolRows).toHaveLength(2)
  })
})
