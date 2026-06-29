import { describe, expect, it } from 'vitest'
import { EPIC_DESCRIPTION_TEMPLATE, hasEpicDescriptionStructure } from './epic-description-template'

describe('EPIC_DESCRIPTION_TEMPLATE', () => {
  it('is a single markdown string', () => {
    expect(typeof EPIC_DESCRIPTION_TEMPLATE).toBe('string')
    expect(EPIC_DESCRIPTION_TEMPLATE.length).toBeGreaterThan(0)
  })

  it('contains the Goal / Background / Non-goals / Scope section headers', () => {
    expect(EPIC_DESCRIPTION_TEMPLATE).toContain('## Goal')
    expect(EPIC_DESCRIPTION_TEMPLATE).toContain('## Background')
    expect(EPIC_DESCRIPTION_TEMPLATE).toContain('## Non-goals')
    expect(EPIC_DESCRIPTION_TEMPLATE).toContain('## Scope')
  })

  it('lists the four required headers in the expected order', () => {
    const goal = EPIC_DESCRIPTION_TEMPLATE.indexOf('## Goal')
    const background = EPIC_DESCRIPTION_TEMPLATE.indexOf('## Background')
    const nonGoals = EPIC_DESCRIPTION_TEMPLATE.indexOf('## Non-goals')
    const scope = EPIC_DESCRIPTION_TEMPLATE.indexOf('## Scope')

    expect(goal).toBeGreaterThanOrEqual(0)
    expect(background).toBeGreaterThan(goal)
    expect(nonGoals).toBeGreaterThan(background)
    expect(scope).toBeGreaterThan(nonGoals)
  })

  it('uses `<…>` placeholders for the section bodies (not empty stubs)', () => {
    expect(EPIC_DESCRIPTION_TEMPLATE).toMatch(/<[^>]+>/)
    const bodyLines = EPIC_DESCRIPTION_TEMPLATE.split('\n').filter((line) => !line.startsWith('## ') && line.trim() !== '')
    expect(bodyLines.length).toBeGreaterThanOrEqual(4)
  })
})

describe('hasEpicDescriptionStructure', () => {
  it('returns true when the template itself is passed in', () => {
    expect(hasEpicDescriptionStructure(EPIC_DESCRIPTION_TEMPLATE)).toBe(true)
  })

  it('returns true when all four headers are present regardless of body content', () => {
    const filled = [
      '## Goal',
      'Ship the planning entry point.',
      '',
      '## Background',
      'Today the dialog is bare.',
      '',
      '## Non-goals',
      'No automatic breakdown.',
      '',
      '## Scope',
      'Web UI only.',
    ].join('\n')

    expect(hasEpicDescriptionStructure(filled)).toBe(true)
  })

  it('returns true when the headers appear alongside unrelated content', () => {
    const wrapped = `Some intro\n\n${EPIC_DESCRIPTION_TEMPLATE}\n\nSome outro`
    expect(hasEpicDescriptionStructure(wrapped)).toBe(true)
  })

  it('returns true when standalone headers have trailing whitespace', () => {
    const withTrailingWhitespace = [
      '## Goal   ',
      'ship it',
      '',
      '## Background\t',
      'context',
      '',
      '## Non-goals ',
      'not this',
      '',
      '## Scope',
      'web only',
    ].join('\n')

    expect(hasEpicDescriptionStructure(withTrailingWhitespace)).toBe(true)
  })

  it('returns false for an empty string', () => {
    expect(hasEpicDescriptionStructure('')).toBe(false)
  })

  it('returns false for null and undefined', () => {
    expect(hasEpicDescriptionStructure(null)).toBe(false)
    expect(hasEpicDescriptionStructure(undefined)).toBe(false)
  })

  it('returns false for a simple one-line description', () => {
    expect(hasEpicDescriptionStructure('Just a plain epic description.')).toBe(false)
  })

  it('returns false when only one header is present', () => {
    expect(hasEpicDescriptionStructure('## Goal\nShip the entry point.')).toBe(false)
  })

  it('returns false when only some of the headers are present', () => {
    const partial = [
      '## Goal',
      'ship it',
      '',
      '## Background',
      'context',
    ].join('\n')

    expect(hasEpicDescriptionStructure(partial)).toBe(false)
  })

  it('returns false when required header text appears only inside prose', () => {
    const prose = 'Mention ## Goal, ## Background, ## Non-goals, and ## Scope without standalone headers.'
    expect(hasEpicDescriptionStructure(prose)).toBe(false)
  })

  it('returns false when required header text appears at a different markdown heading level', () => {
    const wrongLevel = [
      '### Goal',
      'ship it',
      '',
      '### Background',
      'context',
      '',
      '### Non-goals',
      'not this',
      '',
      '### Scope',
      'web only',
    ].join('\n')

    expect(hasEpicDescriptionStructure(wrongLevel)).toBe(false)
  })

  it('is case-sensitive and does not match lower-case headers', () => {
    const lower = EPIC_DESCRIPTION_TEMPLATE.toLowerCase()
    expect(hasEpicDescriptionStructure(lower)).toBe(false)
  })
})
