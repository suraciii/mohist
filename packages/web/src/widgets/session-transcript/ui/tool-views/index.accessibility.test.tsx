import '@testing-library/jest-dom'
import { fireEvent, render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import type { DisplayToolPart } from '../../model/session-transcript-display'
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
        title="Explored · 2 reads"
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
        title="Explored · 2 reads"
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
        title="Explored · 1 read"
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
        title="Explored · 2 reads"
        tools={[makeReadTool({ id: 'a' }), makeReadTool({ id: 'b' })]}
        hasError={false}
      />,
    )

    const button = screen.getByRole('button', { name: /Explored/ })
    expect(button).toBeInTheDocument()
    const name = button.textContent?.trim() ?? ''
    expect(name.length).toBeGreaterThan(0)
    expect(name).not.toBe('unknown')
  })

  it('does not expose "unknown" as the accessible name even when group title is absent', () => {
    render(
      <ContextGroupView
        title="Explored · details"
        tools={[makeReadTool()]}
        hasError={false}
      />,
    )

    const button = screen.getByRole('button')
    const name = button.textContent?.trim() ?? ''
    expect(name).not.toBe('unknown')
  })
})
