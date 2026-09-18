import { describe, expect, it } from 'vitest'
import { loadBuildInfo, manifestCandidatesForTesting } from '../src/runtime/build-info.js'
import { buildManifest } from '../src/runtime/build-manifest.js'

const CANONICAL = {
  schemaVersion: 1,
  component: 'runner',
  version: '0.0.0+deadbeef',
  sourceRevision: 'source-sha',
  buildGitHash: 'build-sha',
  treeHash: 'tree-sha',
  artifactDigest: 'artifact-digest',
  releaseId: 'mohist-runner-source-sha',
  generation: 7,
  runnerId: 'runner-1',
}

function load(raw: unknown) {
  return loadBuildInfo({
    exists: () => true,
    readText: () => (typeof raw === 'string' ? raw : JSON.stringify(raw)),
  })
}

describe('runner build manifest builder', () => {
  it('buildsManifestMatchingInjectedGitHead', () => {
    const readGitHead = () => 'deadbeefcafebabe0000000000000000deadbeef'
    const fixedNow = 1_700_000_000_000
    const manifest = buildManifest(readGitHead, () => fixedNow)

    expect(manifest.buildGitHash).toBe('deadbeefcafebabe0000000000000000deadbeef')
    expect(manifest.builtAt).toBe(fixedNow)
    expect(manifest).not.toHaveProperty('gitHash')
  })

  it('buildsNullBuildGitHashWhenGitRevParseFails', () => {
    // Mirrors the production path: readGitHeadForRepo returns null when git is
    // absent or the directory is not a repo. The builder must propagate null
    // rather than throw, so the postbuild step stays non-fatal.
    const readGitHead = () => null
    const manifest = buildManifest(readGitHead, () => 1_700_000_000_000)

    expect(manifest.buildGitHash).toBeNull()
    expect(typeof manifest.builtAt).toBe('number')
  })

  it('merges non-collapsed display metadata without reintroducing gitHash', () => {
    const manifest = buildManifest(
      () => 'build-sha',
      () => 1_700_000_000_000,
      {
        component: 'runner',
        sourceRevision: 'source-sha',
        generation: 3,
      },
    )

    expect(manifest.buildGitHash).toBe('build-sha')
    expect(manifest.sourceRevision).toBe('source-sha')
    expect(manifest).not.toHaveProperty('gitHash')
  })
})

describe('runner build-info loader', () => {
  it('exposesCandidatePathsRelativeToModule', () => {
    const managedIdentityPath = process.env.MOHIST_RUNTIME_IDENTITY_PATH
    delete process.env.MOHIST_RUNTIME_IDENTITY_PATH
    try {
      const candidates = manifestCandidatesForTesting()
      expect(candidates.length).toBeGreaterThanOrEqual(1)
      for (const path of candidates) {
        expect(path.endsWith('build-info.json')).toBe(true)
      }
    } finally {
      if (managedIdentityPath === undefined) delete process.env.MOHIST_RUNTIME_IDENTITY_PATH
      else process.env.MOHIST_RUNTIME_IDENTITY_PATH = managedIdentityPath
    }
  })

  it('readsCanonicalManagedPayloadWithDistinctIdentityFields', () => {
    const result = load(CANONICAL)

    expect(result.schemaVersion).toBe(1)
    expect(result.component).toBe('runner')
    expect(result.sourceRevision).toBe('source-sha')
    expect(result.buildGitHash).toBe('build-sha')
    expect(result.treeHash).toBe('tree-sha')
    expect(result.artifactDigest).toBe('artifact-digest')
    expect(result.releaseId).toBe('mohist-runner-source-sha')
    expect(result.generation).toBe(7)
    expect(result.runnerId).toBe('runner-1')
    expect(result.version).toBe('0.0.0+deadbeef')
    expect(result).not.toHaveProperty('gitHash')
  })

  it('rejectsManagedPayloadWithUnknownSchemaVersionWithoutGitHashFallback', () => {
    const result = load({ ...CANONICAL, schemaVersion: 2, gitHash: 'legacy-sha' })

    expect(result).toEqual({
      schemaVersion: null,
      component: null,
      sourceRevision: null,
      buildGitHash: null,
      treeHash: null,
      artifactDigest: null,
      releaseId: null,
      generation: null,
      runnerId: null,
      version: null,
      builtAt: null,
    })
  })

  it('rejectsManagedPayloadWithWrongFieldTypesWithoutGitHashFallback', () => {
    const result = load({
      ...CANONICAL,
      generation: 'seven',
      treeHash: 123,
      gitHash: 'legacy-sha',
    })

    expect(result.buildGitHash).toBeNull()
    expect(result.sourceRevision).toBeNull()
    expect(result.treeHash).toBeNull()
    expect(result.generation).toBeNull()
  })

  it('rejectsManagedPayloadMissingARequiredField', () => {
    const { buildGitHash: _buildGitHash, ...missingBuildGitHash } = CANONICAL
    const result = load({ ...missingBuildGitHash, gitHash: 'legacy-sha', sourceRevision: 'legacy-sha' })

    expect(result.schemaVersion).toBeNull()
    expect(result.buildGitHash).toBeNull()
  })

  it('readsLegacyGitHashOnlyPayloadAsV0', () => {
    const result = load({ gitHash: 'deadbeef', builtAt: 1_700_000_000_000 })

    expect(result.schemaVersion).toBeNull()
    expect(result.buildGitHash).toBe('deadbeef')
    expect(result.sourceRevision).toBe('deadbeef')
    expect(result.builtAt).toBe(1_700_000_000_000)
  })

  it('readsLegacyPayloadWithoutSchemaVersionUsingExplicitBuildGitHash', () => {
    const result = load({ buildGitHash: 'build-sha', sourceRevision: 'source-sha', component: 'runner' })

    expect(result.schemaVersion).toBeNull()
    expect(result.buildGitHash).toBe('build-sha')
    expect(result.sourceRevision).toBe('source-sha')
  })

  it('prefersLegacyBuildGitHashOverGitHashWhenSchemaVersionIsAbsent', () => {
    const result = load({ gitHash: 'old-sha', buildGitHash: 'new-sha' })

    expect(result.buildGitHash).toBe('new-sha')
    expect(result.sourceRevision).toBe('new-sha')
  })

  it('treatsNullSchemaVersionAsLegacyV0', () => {
    const result = load({ schemaVersion: null, gitHash: 'deadbeef' })

    expect(result.schemaVersion).toBeNull()
    expect(result.buildGitHash).toBe('deadbeef')
  })

  it('returnsEmptyIdentityWhenNoManagedPayloadExists', () => {
    const result = loadBuildInfo({ exists: () => false, readText: () => '' })

    expect(result.schemaVersion).toBeNull()
    expect(result.buildGitHash).toBeNull()
    expect(result.sourceRevision).toBeNull()
    expect(result.component).toBeNull()
  })

  it('returnsEmptyIdentityForUnparseablePayload', () => {
    const result = load('{ not json')

    expect(result.buildGitHash).toBeNull()
    expect(result.schemaVersion).toBeNull()
  })
})
