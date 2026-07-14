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
