import assert from 'node:assert/strict'
import { mkdirSync, mkdtempSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { test } from 'node:test'

import { parseArgs, validateEvidence } from './gate.js'
import { planIdentity } from './plan.js'
import type { SuiteConfig } from './types.js'

function config(): SuiteConfig {
  const track = (id: string, application: string) => ({
    id,
    kind: 'report-only' as const,
    trackType: 'behavior' as const,
    application,
    specKind: 'Design' as const,
    level: 'L0' as const,
    resources: [],
    report: `reports/${id}.json`,
    reportFormat: 'vitest' as const,
    deadlineMs: 1000,
    enforce: true,
  })
  return {
    suiteDeadlineMs: 300_000,
    tracks: [track('server-unit', 'server'), track('web-unit', 'web')],
    plan: {
      applications: ['server', 'web'],
      repositoryScope: 'repository',
      applicationBuilds: {
        server: [{ command: 'dotnet', args: ['build'] }],
        web: [{ command: 'npm', args: ['run', 'build'] }],
      },
      repositoryChecks: [{ command: 'npm', args: ['run', 'archtest'] }],
      resourceLanes: [{ id: 'default', resources: [], capacity: 1 }],
    },
  }
}

function writeScope(root: string, scope: string, trackId: string): void {
  const scopeRoot = join(root, scope)
  mkdirSync(scopeRoot, { recursive: true })
  if (scope === 'repository') {
    writeFileSync(
      join(scopeRoot, 'repository.json'),
      JSON.stringify({ scope, sourceRevision: 'revision-1', planIdentity: planIdentity(config()) }),
    )
    writeFileSync(
      join(scopeRoot, 'checks.json'),
      JSON.stringify([
        { command: 'npm', args: ['run', 'archtest'], exitCode: 0, timedOut: false, cleanupComplete: true },
      ]),
    )
    writeFileSync(join(scopeRoot, 'summary.json'), JSON.stringify({ passed: true }))
    return
  }
  writeFileSync(
    join(scopeRoot, 'application.json'),
    JSON.stringify({ application: scope, sourceRevision: 'revision-1', planIdentity: planIdentity(config()) }),
  )
  writeFileSync(
    join(scopeRoot, 'summary.json'),
    JSON.stringify({
      passed: true,
      evaluations: [{ trackId, total: 1, passed: true }],
      runs: [{ trackId, exitCode: 0, reportReady: true, cleanupComplete: true, timedOut: false }],
    }),
  )
}

test('Gate parses only its evidence root and help', () => {
  assert.deepEqual(parseArgs(['--evidence-root', '/tmp/evidence']), {
    evidenceRoot: '/tmp/evidence',
    help: false,
  })
  assert.deepEqual(parseArgs(['--help']), { evidenceRoot: undefined, help: true })
  assert.throws(() => parseArgs(['--track', 'server-unit']), /unknown gate argument/)
})

test('Gate accepts complete application and Repository evidence', () => {
  const root = mkdtempSync(join(tmpdir(), 'mohist-gate-'))
  writeScope(root, 'server', 'server-unit')
  writeScope(root, 'web', 'web-unit')
  writeScope(root, 'repository', '')
  assert.deepEqual(validateEvidence(config(), root), [])
})

test('Gate rejects missing, unexpected, and non-passing evidence', () => {
  const root = mkdtempSync(join(tmpdir(), 'mohist-gate-'))
  writeScope(root, 'server', 'server-unit')
  writeScope(root, 'web', 'web-unit')
  writeScope(root, 'repository', '')
  mkdirSync(join(root, 'extra'), { recursive: true })
  writeFileSync(join(root, 'web', 'summary.json'), JSON.stringify({ passed: false, evaluations: [], runs: [] }))
  const errors = validateEvidence(config(), root)
  assert.ok(errors.some((error) => error.includes('unexpected evidence scope: extra')))
  assert.ok(errors.some((error) => error.includes('web: summary.passed is not true')))
  assert.ok(errors.some((error) => error.includes('missing evaluation for web-unit')))
})
