import '@testing-library/jest-dom'
import { render } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import type { DisplayAssistantPart, DisplayToolPart } from '../model/session-transcript-display'
import { AssistantParts } from './AssistantParts'

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

describe('AssistantParts — no rendered tool row exposes "unknown" as title', () => {
  function visibleUnknownLabel(container: HTMLElement): string | null {
    const spans = container.querySelectorAll('[data-testid="tool-row"] span')
    for (const span of Array.from(spans)) {
      const text = span.textContent?.trim()
      if (text === 'unknown') return 'span:' + span.outerHTML
    }
    return null
  }

  it('renders a readable label when tool name is missing with empty input', () => {
    const part = makeToolPart({
      id: 'gen-empty',
      normalizedName: 'unknown',
      toolName: 'unknown',
    })

    const { container } = renderAssistantParts([part])

    expect(visibleUnknownLabel(container)).toBeNull()
    const row = container.querySelector('[data-testid="tool-row"]')
    expect(row?.textContent).not.toContain('>unknown<')
    const labelSpan = row?.querySelector('span.text-xs.font-medium')
    expect(labelSpan?.textContent?.trim().length).toBeGreaterThan(0)
    expect(labelSpan?.textContent?.trim()).not.toBe('unknown')
  })

  it('surfaces a command via the bash registry when the unknown name is semantically inferred to "bash"', () => {
    const part = makeToolPart({
      id: 'gen-cmd',
      normalizedName: 'bash',
      toolName: 'bash',
      input: JSON.stringify({ command: 'npm test --watch=false' }),
    })

    const { container } = renderAssistantParts([part])
    expect(visibleUnknownLabel(container)).toBeNull()
    expect(container.querySelector('[data-testid="tool-row"]')?.textContent).toContain('npm test')
  })

  it('surfaces a file path via the read registry when the unknown name is semantically inferred to "read"', () => {
    const part = makeToolPart({
      id: 'gen-path',
      normalizedName: 'read',
      toolName: 'read',
      input: JSON.stringify({ filePath: '/repo/src/widgets/transcript/foo.ts' }),
    })

    const { container } = renderAssistantParts([part])
    expect(visibleUnknownLabel(container)).toBeNull()
    expect(container.querySelector('[data-testid="tool-row"]')?.textContent).toContain('foo.ts')
  })

  it('surfaces a query via the grep registry when the unknown name is semantically inferred to "grep"', () => {
    const part = makeToolPart({
      id: 'gen-q',
      normalizedName: 'grep',
      toolName: 'grep',
      input: JSON.stringify({ query: 'transcript gating' }),
    })

    const { container } = renderAssistantParts([part])
    expect(visibleUnknownLabel(container)).toBeNull()
    expect(container.querySelector('[data-testid="tool-row"]')?.textContent).toContain('transcript gating')
  })

  it('surfaces a url via FallbackEntry even when the unknown name is not semantically inferred', () => {
    const part = makeToolPart({
      id: 'gen-url',
      normalizedName: 'unknown',
      toolName: 'unknown',
      input: JSON.stringify({ url: 'https://example.com/page' }),
    })

    const { container } = renderAssistantParts([part])
    expect(visibleUnknownLabel(container)).toBeNull()
    expect(container.querySelector('[data-testid="tool-row"]')?.textContent).toContain('example.com/page')
  })

  it('falls back to a generic descriptive label when no recognizable content exists', () => {
    const part = makeToolPart({
      id: 'gen-last',
      normalizedName: 'unknown',
      toolName: 'unknown',
      input: JSON.stringify({ unrelated: 'value' }),
    })

    const { container } = renderAssistantParts([part])
    expect(visibleUnknownLabel(container)).toBeNull()
    const labelText = container.querySelector('[data-testid="tool-row"] span.text-xs.font-medium')?.textContent?.trim() ?? ''
    expect(labelText.length).toBeGreaterThan(0)
    expect(labelText).not.toBe('unknown')
  })
})
