import { describe, expect, it } from 'vitest'
import {
  LABEL_KEY_PATTERN,
  deriveLabelPairsFromIssues,
  deriveSelectableLabelPairs,
  formatLabelToken,
  labelMapEquals,
  normalizeLabelMap,
  parseLabelSearchParams,
  parseLabelToken,
  parseLabelTokensCsv,
  serializeLabelSearchParams,
  serializeLabelTokens,
  tokensToLabelMap,
  validateLabelEntry,
  validateLabelKey,
  validateLabelValue,
} from './labels'

describe('label key validation', () => {
  it('accepts valid keys', () => {
    for (const key of ['stream', 'module-auth', 'stream--auth', 'a1', 'a-b-c', 'x']) {
      expect(validateLabelKey(key), key).toBeNull()
    }
  })

  it('rejects uppercase letters', () => {
    expect(validateLabelKey('Stream')?.message).toMatch(/lowercase/i)
    expect(validateLabelKey('STREAM')?.message).toMatch(/lowercase/i)
  })

  it('rejects whitespace', () => {
    expect(validateLabelKey('stream frontend')?.message).toMatch(/lowercase|whitespace|dashes/)
    expect(validateLabelKey(' stream')?.message).toMatch(/whitespace/)
    expect(validateLabelKey('stream ')?.message).toMatch(/whitespace/)
  })

  it('rejects empty key', () => {
    expect(validateLabelKey('')?.message).toMatch(/required/i)
  })

  it('rejects leading or trailing dash', () => {
    expect(validateLabelKey('-stream')?.message).toMatch(/lowercase|dashes/)
    expect(validateLabelKey('stream-')?.message).toMatch(/lowercase|dashes/)
  })

  it('rejects characters outside [a-z0-9-] like underscore or unicode', () => {
    expect(validateLabelKey('stream_auth')?.message).toMatch(/lowercase|dashes/)
    expect(validateLabelKey('数据')?.message).toMatch(/lowercase|dashes/)
  })

  it('exports the canonical pattern', () => {
    expect(LABEL_KEY_PATTERN.test('stream')).toBe(true)
    expect(LABEL_KEY_PATTERN.test('-stream')).toBe(false)
  })
})

describe('label value validation', () => {
  it('accepts a normal value', () => {
    expect(validateLabelValue('frontend')).toBeNull()
  })

  it('rejects empty value', () => {
    expect(validateLabelValue('')?.message).toMatch(/required/i)
  })

  it('rejects whitespace-only value', () => {
    expect(validateLabelValue('   ')?.message).toMatch(/whitespace/i)
  })
})

describe('label entry validation', () => {
  it('rejects invalid key with clear message', () => {
    const result = validateLabelEntry({ key: 'Bad-Key', value: 'frontend' })
    expect(result.ok).toBe(false)
    if (!result.ok) expect(result.error).toMatch(/lowercase/)
  })

  it('rejects empty value with clear message', () => {
    const result = validateLabelEntry({ key: 'stream', value: '' })
    expect(result.ok).toBe(false)
    if (!result.ok) expect(result.error).toMatch(/required|empty/i)
  })

  it('returns trimmed entry on success', () => {
    const result = validateLabelEntry({ key: 'stream', value: 'frontend' })
    expect(result.ok).toBe(true)
    if (result.ok) expect(result.entry).toEqual({ key: 'stream', value: 'frontend' })
  })
})

describe('parseLabelToken / formatLabelToken', () => {
  it('splits on first = only', () => {
    const parsed = parseLabelToken('stream=key=value')
    expect(parsed).toEqual({ key: 'stream', value: 'key=value' })
  })

  it('rejects tokens without = or with leading =', () => {
    expect(parseLabelToken('stream')).toBeNull()
    expect(parseLabelToken('=frontend')).toBeNull()
    expect(parseLabelToken('')).toBeNull()
  })

  it('formats a key=value token', () => {
    expect(formatLabelToken('stream', 'frontend')).toBe('stream=frontend')
  })
})

describe('parseLabelTokensCsv / serializeLabelTokens / tokensToLabelMap', () => {
  it('round-trips label tokens through csv', () => {
    const tokens = serializeLabelTokens([
      { key: 'stream', value: 'frontend' },
      { key: 'module', value: 'auth' },
    ])
    expect(tokens).toBe('stream=frontend,module=auth')
    expect(parseLabelTokensCsv(tokens)).toEqual([
      { key: 'stream', value: 'frontend' },
      { key: 'module', value: 'auth' },
    ])
  })

  it('returns empty array for null / empty csv', () => {
    expect(parseLabelTokensCsv(null)).toEqual([])
    expect(parseLabelTokensCsv('')).toEqual([])
    expect(parseLabelTokensCsv('not-valid,also bad')).toEqual([])
  })

  it('converts tokens to label map preserving last value per key', () => {
    const tokens = ['stream=frontend', 'stream=backend']
    expect(tokensToLabelMap(tokens)).toEqual({ stream: 'backend' })
  })
})

describe('label URL search params', () => {
  it('serializes each label as a repeated labels parameter', () => {
    const params = new URLSearchParams()
    serializeLabelSearchParams(params, ['stream=frontend', 'module=auth'])
    expect(params.toString()).toBe('labelMode=repeated&labels=stream%3Dfrontend&labels=module%3Dauth')
  })

  it('round-trips a single label value containing a comma from serialized params', () => {
    const params = new URLSearchParams()
    serializeLabelSearchParams(params, ['stream=front,end'])
    expect(parseLabelSearchParams(new URLSearchParams(params.toString()))).toEqual(['stream=front,end'])
  })

  it('parses repeated labels parameters without splitting commas inside values', () => {
    const raw = 'labels=stream%3Dfront%2Cend&labels=module%3Dauth'
    const params = new URLSearchParams(raw)
    expect(parseLabelSearchParams(params, raw)).toEqual(['stream=front,end', 'module=auth'])
  })

  it('parses legacy comma-separated labels parameter', () => {
    const params = new URLSearchParams('labels=stream%3Dfrontend%2Cmodule%3Dauth')
    expect(parseLabelSearchParams(params)).toEqual(['stream=frontend', 'module=auth'])
  })
})

describe('normalizeLabelMap', () => {
  it('drops legacy array input', () => {
    expect(normalizeLabelMap(['frontend', 'bug'])).toEqual({})
  })

  it('drops invalid keys and empty values', () => {
    expect(normalizeLabelMap({
      Stream: 'frontend',
      'stream ': 'backend',
      module: 'auth',
      bad: '',
    })).toEqual({ module: 'auth' })
  })

  it('keeps valid key/value pairs', () => {
    expect(normalizeLabelMap({ stream: 'frontend', module: 'auth' }))
      .toEqual({ stream: 'frontend', module: 'auth' })
  })
})

describe('labelMapEquals', () => {
  it('returns true for equal maps (different insertion order)', () => {
    expect(labelMapEquals({ a: '1', b: '2' }, { b: '2', a: '1' })).toBe(true)
  })

  it('returns false when values differ', () => {
    expect(labelMapEquals({ a: '1' }, { a: '2' })).toBe(false)
  })

  it('returns false when keys differ', () => {
    expect(labelMapEquals({ a: '1' }, { b: '1' })).toBe(false)
  })
})

describe('deriveSelectableLabelPairs / deriveLabelPairsFromIssues', () => {
  it('sorts pairs by key then value', () => {
    const input: Record<string, string> = { module: 'auth', stream: 'frontend' }
    input.stream = 'frontend'
    input.stream = 'backend'
    expect(deriveSelectableLabelPairs(input))
      .toEqual([
        { key: 'module', value: 'auth' },
        { key: 'stream', value: 'backend' },
      ])
  })

  it('derives distinct key=value pairs from loaded issues', () => {
    const issues = [
      { labels: { stream: 'frontend', module: 'auth' } },
      { labels: { stream: 'frontend' } },
      { labels: { stream: 'backend' } },
      { labels: {} },
    ]
    expect(deriveLabelPairsFromIssues(issues)).toEqual([
      { key: 'module', value: 'auth' },
      { key: 'stream', value: 'backend' },
      { key: 'stream', value: 'frontend' },
    ])
  })

  it('ignores issues with non-object labels (legacy rows)', () => {
    const issues = [
      { labels: ['legacy', 'tokens'] },
      { labels: null },
      { labels: { stream: 'frontend' } },
    ]
    expect(deriveLabelPairsFromIssues(issues)).toEqual([
      { key: 'stream', value: 'frontend' },
    ])
  })
})
