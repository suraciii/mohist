import '@testing-library/jest-dom'
import { render } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import {
  ToolStatusDot,
  ToolIcon,
  getToolDisplayLabel,
  getToolDisplayArgs,
  getRegistrySubtitle,
  truncateOutput,
} from './shared'

describe('tool-views shared', () => {
  describe('ToolStatusDot', () => {
    it('renders a success-tone dot for completed', () => {
      const { container } = render(<ToolStatusDot status="completed" />)
      const dot = container.querySelector('span[data-tone="success"]')
      expect(dot).not.toBeNull()
      expect(dot?.className).toContain('bg-success')
      expect(dot?.className).not.toContain('bg-green-')
    })

    it('renders a danger-tone dot for failed', () => {
      const { container } = render(<ToolStatusDot status="failed" />)
      const dot = container.querySelector('span[data-tone="danger"]')
      expect(dot).not.toBeNull()
      expect(dot?.className).toContain('bg-danger')
      expect(dot?.className).not.toContain('bg-red-')
    })

    it('renders a neutral dot for cancelled', () => {
      const { container } = render(<ToolStatusDot status="cancelled" />)
      const dot = container.querySelector('span[data-tone="neutral"]')
      expect(dot).not.toBeNull()
      expect(dot?.className).toContain('bg-muted-foreground/60')
      expect(dot?.className).not.toContain('bg-gray-')
    })

    it('renders a default neutral dot for pending', () => {
      const { container } = render(<ToolStatusDot status="pending" />)
      const dot = container.querySelector('span[data-tone="neutral"]')
      expect(dot).not.toBeNull()
      expect(dot?.className).not.toContain('bg-gray-')
    })

    it('renders the animated running indicator with info-tone', () => {
      const { container } = render(<ToolStatusDot status="running" />)
      const indicator = container.querySelector('.animate-ping')
      expect(indicator).not.toBeNull()
      const wrapper = container.querySelector('span[data-tone="info"]')
      expect(wrapper).not.toBeNull()
      expect(indicator?.className).toContain('bg-info')
    })
  })

  describe('ToolIcon', () => {
    it('renders an svg for a known tool', () => {
      const { container } = render(<ToolIcon normalizedName="bash" />)
      expect(container.querySelector('svg')).toBeInTheDocument()
    })

    it('renders an svg for unknown tools via fallback', () => {
      const { container } = render(<ToolIcon normalizedName="totally-unknown-tool" />)
      expect(container.querySelector('svg')).toBeInTheDocument()
    })
  })

  describe('getToolDisplayLabel', () => {
    it('prefers displayTitle when provided', () => {
      expect(getToolDisplayLabel('bash', 'Custom Title', 'Sub', '{"command":"x"}')).toBe('Custom Title')
    })

    it('falls back to displaySubtitle when no displayTitle', () => {
      expect(getToolDisplayLabel('bash', undefined, 'Sub', '{"command":"x"}')).toBe('Sub')
    })

    it('uses the tool registry title when neither display title nor subtitle are set', () => {
      const input = JSON.stringify({ command: 'echo hi' })
      expect(getToolDisplayLabel('bash', undefined, undefined, input)).toBe('echo hi')
    })

    it('never returns the literal "unknown" when the name is missing', () => {
      const label = getToolDisplayLabel('unknown')
      expect(label).not.toBe('unknown')
      expect(label.length).toBeGreaterThan(0)
    })

    it('never returns "unknown" when name is missing and input is empty', () => {
      const label = getToolDisplayLabel('unknown', undefined, undefined, undefined)
      expect(label).not.toBe('unknown')
      expect(label.length).toBeGreaterThan(0)
    })

    it('surfaces a command from raw input when name resolves via semantic inference to "bash"', () => {
      const input = JSON.stringify({ command: 'npm install --save-dev typescript' })
      const label = getToolDisplayLabel('bash', undefined, undefined, input)
      expect(label).not.toBe('unknown')
      expect(label).toContain('npm install')
    })

    it('surfaces a file path from raw input when name resolves via semantic inference to "read"', () => {
      const input = JSON.stringify({ filePath: '/repo/src/widgets/transcript/foo.ts' })
      const label = getToolDisplayLabel('read', undefined, undefined, input)
      expect(label).not.toBe('unknown')
      expect(label).toContain('foo.ts')
    })

    it('surfaces a query string from raw input when name resolves via semantic inference to "grep"', () => {
      const input = JSON.stringify({ query: 'transcript gating' })
      const label = getToolDisplayLabel('grep', undefined, undefined, input)
      expect(label).not.toBe('unknown')
      expect(label).toContain('transcript gating')
    })

    it('surfaces a url from raw input even when name is "unknown" (FallBackEntry extracts url)', () => {
      const input = JSON.stringify({ url: 'https://example.com/page' })
      const label = getToolDisplayLabel('unknown', undefined, undefined, input)
      expect(label).not.toBe('unknown')
      expect(label).toBe('https://example.com/page')
    })

    it('surfaces a patch marker from raw input when name resolves via inference to "apply_patch"', () => {
      const input = JSON.stringify({
        patchText: '*** Begin Patch\n*** Update File: src/widgets/foo.ts\n-old\n+new\n*** End Patch',
      })
      const label = getToolDisplayLabel('apply_patch', undefined, undefined, input)
      expect(label).not.toBe('unknown')
      expect(label).toContain('foo.ts')
    })

    it('ignores displayTitle "unknown" and falls through to a generic label', () => {
      const label = getToolDisplayLabel('unknown', 'unknown', undefined, undefined)
      expect(label).not.toBe('unknown')
      expect(label.length).toBeGreaterThan(0)
    })

    it('ignores displaySubtitle "unknown" and falls through to a generic label', () => {
      const label = getToolDisplayLabel('unknown', undefined, 'unknown', undefined)
      expect(label).not.toBe('unknown')
      expect(label.length).toBeGreaterThan(0)
    })

    it('returns a generic descriptive label as last resort for unknown name with no recognizable input', () => {
      const label = getToolDisplayLabel('unknown', undefined, undefined, '{"unrelated":"value"}')
      expect(label).not.toBe('unknown')
      expect(label.length).toBeGreaterThan(0)
    })

    it('returns a generic descriptive label for unknown name and unparseable input', () => {
      const label = getToolDisplayLabel('unknown', undefined, undefined, 'not-json')
      expect(label).not.toBe('unknown')
      expect(label.length).toBeGreaterThan(0)
    })
  })

  describe('getToolDisplayArgs', () => {
    it('returns tool-arg badges from the registry', () => {
      const input = JSON.stringify({ method: 'POST', format: 'json' })
      const args = getToolDisplayArgs('webfetch', input)
      expect(args).toContain('POST')
      expect(args).toContain('json')
    })

    it('returns an empty array for empty/unparseable input', () => {
      expect(getToolDisplayArgs('webfetch')).toEqual([])
    })
  })

  describe('getRegistrySubtitle', () => {
    it('delegates to the registry entry', () => {
      const input = JSON.stringify({ patchText: '*** Update File: src/foo.ts\n-old\n+new' })
      expect(getRegistrySubtitle('apply_patch', input)).toBe('1 file changed')
    })

    it('returns undefined when no subtitle applies', () => {
      expect(getRegistrySubtitle('read', JSON.stringify({ filePath: '/a.ts' }))).toBeUndefined()
    })
  })

  describe('truncateOutput', () => {
    it('returns the original output when shorter than maxLines', () => {
      expect(truncateOutput('a\nb', 5)).toBe('a\nb')
    })

    it('appends an ellipsis when truncated', () => {
      const out = truncateOutput('a\nb\nc\nd\ne\nf', 3)
      expect(out).toBe('a\nb\nc\n...')
    })
  })
})
