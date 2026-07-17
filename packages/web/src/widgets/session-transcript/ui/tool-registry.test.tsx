import { describe, expect, it } from 'vitest'
import {
  getToolRegistryEntry,
  getToolTitle,
} from './tool-registry'

function fallbackTitle(toolName: string, rawInput?: string): string {
  return getToolRegistryEntry(toolName).getTitle(toolName, rawInput)
}

describe('tool-registry: FallbackEntry.getTitle — readable floor for unknown', () => {
  it('returns a generic descriptive label when tool name is "unknown" and no recognizable input', () => {
    const label = fallbackTitle('unknown', undefined)
    expect(label).not.toBe('unknown')
    expect(label.length).toBeGreaterThan(0)
  })

  it('returns a generic descriptive label when tool name normalizes to "unknown" and input has nothing recognizable', () => {
    const label = fallbackTitle('totally-unknown-name', '{"unrelated":"value"}')
    expect(label).not.toBe('unknown')
    expect(label.length).toBeGreaterThan(0)
  })

  it('surfaces a url from raw input even when tool name is "unknown"', () => {
    const label = fallbackTitle('unknown', JSON.stringify({ url: 'https://example.com/page' }))
    expect(label).not.toBe('unknown')
    expect(label).toBe('https://example.com/page')
  })

  it('surfaces a file path from raw input even when tool name is "unknown"', () => {
    const label = fallbackTitle('unknown', JSON.stringify({ filePath: '/repo/src/foo.ts' }))
    expect(label).not.toBe('unknown')
    expect(label).toBe('/repo/src/foo.ts')
  })

  it('surfaces a query string from raw input even when tool name is "unknown"', () => {
    const label = fallbackTitle('unknown', JSON.stringify({ query: 'transcript gating' }))
    expect(label).not.toBe('unknown')
    expect(label).toBe('transcript gating')
  })

  it('keeps a known tool name when it does not normalize to "unknown"', () => {
    const label = fallbackTitle('custom_tool', undefined)
    expect(label).toBe('custom_tool')
  })
})

describe('tool-registry: getToolTitle route', () => {
  it('never returns "unknown" for an unknown tool name', () => {
    expect(getToolTitle('completely_bogus', undefined)).not.toBe('unknown')
  })

  it('surfaces content from input for unknown tools (url via FallbackEntry default branch)', () => {
    const input = JSON.stringify({ url: 'https://example.com/page' })
    expect(getToolTitle('unknown', input)).toBe('https://example.com/page')
  })

  it('surfaces a bash command from input via BashEntry when name is inferred as "bash"', () => {
    const input = JSON.stringify({ command: 'git status' })
    expect(getToolTitle('bash', input)).toBe('git status')
  })

  it('falls back via FallbackEntry for an unknown tool name', () => {
    const entry = getToolRegistryEntry('completely_bogus')
    expect(entry.category).toBe('fallback')
  })
})
