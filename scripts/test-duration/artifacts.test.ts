import assert from 'node:assert/strict'
import { test } from 'node:test'

import { buildStampMatchesRun, createArtifactRoot, isInsideDirectory } from './artifacts.js'

test('artifact roots reject repository parents and use a unique child below an external parent', () => {
  const prefixes: string[] = []
  const ops = {
    tempDirectory: () => '/tmp',
    makeDirectory: (prefix: string) => {
      prefixes.push(prefix)
      return `${prefix}random`
    },
  }

  assert.throws(
    () => createArtifactRoot('run-1', '/repo', '/repo/artifacts', ops),
    /outside the repository/,
  )
  assert.equal(
    createArtifactRoot('run-2', '/repo', '/diagnostics', ops),
    '/diagnostics/mohist-canonical-gate-run-2-random',
  )
  assert.deepEqual(prefixes, ['/diagnostics/mohist-canonical-gate-run-2-'])
  assert.equal(isInsideDirectory('/repo/artifacts', '/repo'), true)
  assert.equal(isInsideDirectory('/diagnostics', '/repo'), false)
})

test('build stamps are accepted only when they carry the owning run identity', () => {
  const run = JSON.stringify({ runId: 'run-1', startedAt: 1000, suiteDeadlineMs: 300000 })
  assert.equal(buildStampMatchesRun(run, JSON.stringify({ runId: 'run-1', builtAt: 2000 })), true)
  assert.equal(buildStampMatchesRun(run, JSON.stringify({ runId: 'run-2', builtAt: 2000 })), false)
  assert.equal(buildStampMatchesRun(run, '{}'), false)
})

test('source revision is part of the build provenance when the run records one', () => {
  const run = JSON.stringify({ runId: 'run-1', startedAt: 1000, suiteDeadlineMs: 300000, sourceRevision: 'c093' })
  assert.equal(buildStampMatchesRun(run, JSON.stringify({ runId: 'run-1', builtAt: 2000, sourceRevision: 'c093' })), true)
  assert.equal(buildStampMatchesRun(run, JSON.stringify({ runId: 'run-1', builtAt: 2000, sourceRevision: 'other' })), false)
})
