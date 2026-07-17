import { describe, expect, it } from 'vitest'
import {
  GENERIC_TOOL_LABEL,
  inferDisplayTitle,
  inferToolName,
  normalizeToolName,
} from './transcript-tool-utils'

describe('inferDisplayTitle — readable floor for unknown name', () => {
  it('returns the call title verbatim when provided', () => {
    expect(inferDisplayTitle('unknown', 'Loaded skill: software-design')).toEqual({
      displayTitle: 'Loaded skill: software-design',
    })
  })

  it('returns a semantic family display title for a recognized tool name', () => {
    expect(inferDisplayTitle('read')).toEqual({ displayTitle: 'Read' })
    expect(inferDisplayTitle('apply_patch')).toEqual({ displayTitle: 'Patch' })
    expect(inferDisplayTitle('todowrite')).toEqual({ displayTitle: 'Update todo list' })
  })

  it('returns the raw tool name when no recognized mapping exists for a real tool', () => {
    expect(inferDisplayTitle('custom_tool')).toEqual({ displayTitle: 'custom_tool' })
  })

  it('falls back to the generic descriptive label when tool name is "unknown" and no title is given', () => {
    const result = inferDisplayTitle('unknown')
    expect(result.displayTitle).toBe(GENERIC_TOOL_LABEL)
    expect(result.displayTitle).not.toBe('unknown')
  })

  it('falls back to the generic descriptive label when tool name is undefined', () => {
    const result = inferDisplayTitle('')
    expect(result.displayTitle).toBe(GENERIC_TOOL_LABEL)
    expect(result.displayTitle).not.toBe('unknown')
  })
})

describe('inferToolName + normalizeToolName — preserved as internal registry keys', () => {
  it('still emits "unknown" as a registry key when no semantic inference succeeds', () => {
    expect(inferToolName(undefined)).toBe('unknown')
    expect(normalizeToolName(undefined)).toBe('unknown')
  })

  it('preserves the existing semantic inference for command input', () => {
    expect(inferToolName('unknown', undefined, { command: 'npm run build' })).toBe('bash')
  })

  it('preserves the existing semantic inference for file path input', () => {
    expect(inferToolName('unknown', undefined, { filePath: '/repo/src/foo.ts' })).toBe('read')
  })

  it('preserves the existing semantic inference for search input', () => {
    expect(inferToolName('unknown', undefined, { query: 'transcript gating' })).toBe('search')
  })

  it('preserves the existing semantic inference for url input', () => {
    expect(inferToolName('unknown', undefined, { url: 'https://example.com' })).toBe('webfetch')
  })

  it('preserves the existing semantic inference for delegation payload', () => {
    expect(inferToolName('unknown', undefined, { description: 'Inspect routes', subagent_type: 'explore' })).toBe('task')
  })

  it('preserves the existing semantic inference from a "skill" title', () => {
    expect(inferToolName('unknown', 'Loaded skill: software-design')).toBe('skill')
  })

  it('preserves the existing semantic inference from a patch marker in raw input string', () => {
    expect(inferToolName('unknown', undefined, '*** Begin Patch\n*** Update File: src/foo.ts')).toBe('apply_patch')
  })
})
