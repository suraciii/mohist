import { describe, expect, it } from 'vitest'
import { extractPrDeliveryMetadata } from './pr-delivery'

describe('extractPrDeliveryMetadata', () => {
  it('returns null for null/undefined', () => {
    expect(extractPrDeliveryMetadata(null)).toBeNull()
    expect(extractPrDeliveryMetadata(undefined)).toBeNull()
  })

  it('returns null for non-publish-via-pr output', () => {
    expect(extractPrDeliveryMetadata({ kind: 'publish', prNumber: 12, prUrl: 'https://x' })).toBeNull()
  })

  it('extracts metadata from a JSON-string output', () => {
    const output = JSON.stringify({
      kind: 'publish-via-pr',
      prNumber: 42,
      prUrl: 'https://github.com/acme/widgets/pull/42',
      mergeCommitSha: 'abc123',
      targetBranch: 'main',
      baseSha: 'def456',
      pushed: true,
    })
    expect(extractPrDeliveryMetadata(output)).toEqual({
      prNumber: 42,
      prUrl: 'https://github.com/acme/widgets/pull/42',
      mergeCommitSha: 'abc123',
      targetBranch: 'main',
    })
  })

  it('extracts metadata from an object output', () => {
    const output = {
      kind: 'publish-via-pr',
      prNumber: 7,
      prUrl: 'https://github.com/acme/widgets/pull/7',
      mergeCommitSha: null,
      targetBranch: 'master',
    }
    expect(extractPrDeliveryMetadata(output)).toEqual({
      prNumber: 7,
      prUrl: 'https://github.com/acme/widgets/pull/7',
      mergeCommitSha: null,
      targetBranch: 'master',
    })
  })

  it('returns null when prNumber is missing', () => {
    expect(extractPrDeliveryMetadata({ kind: 'publish-via-pr', prUrl: 'https://x' })).toBeNull()
  })

  it('returns null when prUrl is missing', () => {
    expect(extractPrDeliveryMetadata({ kind: 'publish-via-pr', prNumber: 12 })).toBeNull()
  })

  it('coerces a numeric-string prNumber', () => {
    expect(extractPrDeliveryMetadata({ kind: 'publish-via-pr', prNumber: '12', prUrl: 'https://x' })).toEqual({
      prNumber: 12,
      prUrl: 'https://x',
      mergeCommitSha: null,
      targetBranch: null,
    })
  })

  it('ignores malformed JSON and falls through to null', () => {
    expect(extractPrDeliveryMetadata('{"kind": "publish-via-pr"')).toBeNull()
  })

  it('returns null for non-object non-string inputs', () => {
    expect(extractPrDeliveryMetadata(42)).toBeNull()
    expect(extractPrDeliveryMetadata(true)).toBeNull()
    expect(extractPrDeliveryMetadata([])).toBeNull()
  })
})