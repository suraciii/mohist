import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'
import { loadBuildInfo } from '../src/runtime/build-info.js'

// The nine canonical RuntimeIdentity v1 keys pinned by
// fixtures/runtime-identity.v1.json. The list is duplicated in the Go and C#
// conformance suites on purpose: a drift in any language breaks its own tests.
const CANONICAL_KEYS = [
  'schemaVersion',
  'component',
  'sourceRevision',
  'buildGitHash',
  'treeHash',
  'artifactDigest',
  'releaseId',
  'generation',
  'runnerId',
] as const

const FIXTURE_URL = new URL('../../../fixtures/runtime-identity.v1.json', import.meta.url)
const fixture = JSON.parse(readFileSync(FIXTURE_URL, 'utf8')) as Record<string, unknown>

const EMPTY_IDENTITY = {
  schemaVersion: null,
  component: null,
  sourceRevision: null,
  buildGitHash: null,
  treeHash: null,
  artifactDigest: null,
  releaseId: null,
  generation: null,
  runnerId: null,
}

function load(raw: unknown) {
  return loadBuildInfo({
    exists: () => true,
    readText: () => (typeof raw === 'string' ? raw : JSON.stringify(raw)),
  })
}

describe('RuntimeIdentity v1 conformance fixture', () => {
  it('fixtureHasExactlyTheCanonicalNineFieldKeySet', () => {
    expect(Object.keys(fixture).sort()).toEqual([...CANONICAL_KEYS].sort())
  })

  it('fixtureDeclaresCanonicalFieldTypesAndMeanings', () => {
    expect(fixture.schemaVersion).toBe(1)
    expect(Number.isInteger(fixture.generation)).toBe(true)
    expect(fixture.generation as number).toBeGreaterThan(0)
    for (const field of [
      'component',
      'sourceRevision',
      'buildGitHash',
      'treeHash',
      'artifactDigest',
      'releaseId',
      'runnerId',
    ]) {
      expect(typeof fixture[field]).toBe('string')
      expect((fixture[field] as string).length).toBeGreaterThan(0)
    }
    expect(fixture.buildGitHash).toBe(fixture.sourceRevision)
  })

  it('loaderParsesTheFixtureIntoTheCanonicalIdentity', () => {
    const result = load(fixture)
    expect(result).toEqual({ ...fixture, version: null, builtAt: null })
    expect(Object.keys(result).sort()).toEqual([...CANONICAL_KEYS, 'version', 'builtAt'].sort())
  })

  it('unknownSchemaVersionYieldsAnEmptyIdentityWithoutGitHashFallback', () => {
    const result = load({ ...fixture, schemaVersion: 2, gitHash: 'legacy-sha' })
    expect(result).toEqual({ ...EMPTY_IDENTITY, version: null, builtAt: null })
  })

  it('wrongFieldTypeYieldsAnEmptyIdentityWithoutGitHashFallback', () => {
    const result = load({ ...fixture, generation: '42', gitHash: 'legacy-sha' })
    expect(result).toEqual({ ...EMPTY_IDENTITY, version: null, builtAt: null })
  })

  it('missingRequiredFieldYieldsAnEmptyIdentity', () => {
    const { buildGitHash: _removed, ...missing } = fixture
    const result = load({ ...missing, gitHash: 'legacy-sha', sourceRevision: 'legacy-sha' })
    expect(result).toEqual({ ...EMPTY_IDENTITY, version: null, builtAt: null })
  })

  it('legacyGitHashOnlyPayloadReadsAsV0', () => {
    const result = load({ gitHash: 'legacy-sha' })
    expect(result.schemaVersion).toBeNull()
    expect(result.buildGitHash).toBe('legacy-sha')
    expect(result.sourceRevision).toBe('legacy-sha')
  })
})
