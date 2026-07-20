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

  it('extracts metadata from structured output', () => {
    const output = {
      kind: 'publish-via-pr',
      prNumber: 42,
      prUrl: 'https://github.com/acme/widgets/pull/42',
      mergeCommitSha: 'abc123',
      targetBranch: 'main',
      baseSha: 'def456',
      pushed: true,
    }
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

  it('returns null for string output', () => {
    expect(extractPrDeliveryMetadata('{"kind":"publish-via-pr","prNumber":7,"prUrl":"https://x"}')).toBeNull()
  })

  it('returns null for non-object inputs', () => {
    expect(extractPrDeliveryMetadata(42)).toBeNull()
    expect(extractPrDeliveryMetadata(true)).toBeNull()
    expect(extractPrDeliveryMetadata([])).toBeNull()
  })

  it('extracts metadata from a PR-first create-pull-request output', () => {
    const output = {
      kind: 'create-pull-request',
      prNumber: 17,
      prUrl: 'https://github.com/acme/widgets/pull/17',
      targetBranch: 'main',
    }
    expect(extractPrDeliveryMetadata(output)).toEqual({
      prNumber: 17,
      prUrl: 'https://github.com/acme/widgets/pull/17',
      mergeCommitSha: null,
      targetBranch: 'main',
    })
  })

  it('extracts metadata from a PR-first merge-pull-request output', () => {
    const output = {
      kind: 'merge-pull-request',
      prNumber: 17,
      prUrl: 'https://github.com/acme/widgets/pull/17',
      mergeCommitSha: 'final-sha',
      targetBranch: 'main',
    }
    expect(extractPrDeliveryMetadata(output)).toEqual({
      prNumber: 17,
      prUrl: 'https://github.com/acme/widgets/pull/17',
      mergeCommitSha: 'final-sha',
      targetBranch: 'main',
    })
  })

  it('returns the same PR identity across create and merge output kinds (stable identity)', () => {
    const createOutput = {
      kind: 'create-pull-request',
      prNumber: 21,
      prUrl: 'https://github.com/acme/widgets/pull/21',
      targetBranch: 'main',
    }
    const mergeOutput = {
      kind: 'merge-pull-request',
      prNumber: 21,
      prUrl: 'https://github.com/acme/widgets/pull/21',
      mergeCommitSha: 'final-sha',
      targetBranch: 'main',
    }
    const create = extractPrDeliveryMetadata(createOutput)
    const merge = extractPrDeliveryMetadata(mergeOutput)
    expect(create).not.toBeNull()
    expect(merge).not.toBeNull()
    expect(create!.prNumber).toBe(merge!.prNumber)
    expect(create!.prUrl).toBe(merge!.prUrl)
  })
})
