import assert from 'node:assert/strict'
import { test } from 'node:test'

import { parseSuiteConfig, stripJsonc, validateConfig } from './config.js'

const SAMPLE = `{
  // top-level suite deadline
  "suiteDeadlineMs": 300000,
  "killGraceMs": 5000,
  /* block comment */
  "tracks": [
    {
      "id": "unit",
      "kind": "report-only",
      "report": "reports/u.json",
      "reportFormat": "vitest",
      "deadlineMs": 60000,
      "enforce": true,
      "rules": [
        { "id": "unit", "absoluteMs": 50, "percentile": 95, "percentileMs": 50,
          "allowlist": [{ "id": "slow", "observedMs": 120, "reason": "governed", "owner": "team", "deadline": "2026-11-30" }] }
      ]
    }
  ]
}`

test('stripJsonc removes line and block comments without touching strings', () => {
  const stripped = stripJsonc('{"a": "// not a comment", "b": 1 // real comment\n /* x */}')
  const parsed = JSON.parse(stripped)
  assert.equal(parsed.a, '// not a comment')
  assert.equal(parsed.b, 1)
})

test('parseSuiteConfig parses commented JSONC', () => {
  const config = parseSuiteConfig(SAMPLE)
  assert.equal(config.suiteDeadlineMs, 300000)
  assert.equal(config.tracks[0].rules[0].allowlist[0].id, 'slow')
})

test('validateConfig accepts a well-formed enforce track with a default rule', () => {
  const config = parseSuiteConfig(SAMPLE)
  assert.deepEqual(validateConfig(config), [])
})

test('validateConfig rejects non-array apphostArgs', () => {
  const config = parseSuiteConfig(JSON.stringify({
    suiteDeadlineMs: 1000,
    tracks: [{
      id: 't', kind: 'dotnet-apphost', apphostArgs: '-parallel', report: 'r', reportFormat: 'trx',
      deadlineMs: 100, enforce: false,
    }],
  }))
  assert.ok(validateConfig(config).some((e) => e.includes('apphostArgs must be an array of strings')))
})

test('validateConfig rejects non-string apphostArgs items', () => {
  const config = parseSuiteConfig(JSON.stringify({
    suiteDeadlineMs: 1000,
    tracks: [{
      id: 't', kind: 'dotnet-apphost', apphostArgs: ['-parallel', 1], report: 'r', reportFormat: 'trx',
      deadlineMs: 100, enforce: false,
    }],
  }))
  assert.ok(validateConfig(config).some((e) => e.includes('apphostArgs must contain only strings')))
})

test('validateConfig rejects enforce=true without rules', () => {
  const config = parseSuiteConfig(
    JSON.stringify({
      suiteDeadlineMs: 1000,
      tracks: [{ id: 't', kind: 'report-only', report: 'r', reportFormat: 'trx', deadlineMs: 100, enforce: true }],
    }),
  )
  const errors = validateConfig(config)
  assert.ok(errors.some((e) => e.includes('requires at least one rule')))
})

test('validateConfig fails closed when an unenforced track is not explicitly baseline-pending', () => {
  const config = parseSuiteConfig(
    JSON.stringify({
      suiteDeadlineMs: 1000,
      tracks: [
        { id: 'silent', kind: 'report-only', report: 'r', reportFormat: 'trx', deadlineMs: 100, enforce: false },
        {
          id: 'rules-ignored', kind: 'report-only', report: 's', reportFormat: 'trx', deadlineMs: 100,
          enforce: false, status: 'baseline-pending', reason: 'capture a baseline',
          rules: [{ id: 'unit', absoluteMs: 500 }],
        },
      ],
    }),
  )

  const errors = validateConfig(config)

  assert.ok(errors.some((error) => error.includes('track "silent": enforce=false requires status baseline-pending')))
  assert.ok(errors.some((error) => error.includes('track "silent": enforce=false requires a non-empty baseline-pending reason')))
  assert.ok(errors.some((error) => error.includes('track "rules-ignored": enforce=false must not carry unenforced rules')))
})

test('validateConfig accepts canonical resource limits and partitioned apphost tracks', () => {
  const config = parseSuiteConfig(JSON.stringify({
    suiteDeadlineMs: 1000,
    canonical: { maxConcurrentLanes: 4, resourceLimits: { host: 4, dotnet: 3 } },
    tracks: [{
      id: 'spec', kind: 'dotnet-apphost', apphost: 'bin/spec', report: 'reports/spec-{partition}.trx',
      reportFormat: 'trx', partitions: 4, deadlineMs: 100, enforce: true,
      rules: [{ id: 'spec', absoluteMs: 5000 }],
    }],
  }))
  assert.deepEqual(validateConfig(config), [])
})

test('validateConfig requires a valid non-partitioned duration-measurement phase', () => {
  const config = parseSuiteConfig(JSON.stringify({
    suiteDeadlineMs: 1000,
    canonical: {
      maxConcurrentLanes: 4,
      resourceLimits: { host: 4 },
      durationMeasurementTracks: ['unit', 'unit', 'missing', 'spec'],
    },
    tracks: [
      { id: 'unit', kind: 'dotnet-apphost', apphost: 'bin/unit', report: 'reports/unit.trx', reportFormat: 'trx', deadlineMs: 100, enforce: false },
      { id: 'spec', kind: 'dotnet-apphost', apphost: 'bin/spec', report: 'reports/spec-{partition}.trx', reportFormat: 'trx', partitions: 2, deadlineMs: 100, enforce: false },
    ],
  }))
  const errors = validateConfig(config)
  assert.ok(errors.some((error) => error.includes('requires canonical.resourceLimits.duration-measurement')))
  assert.ok(errors.some((error) => error.includes('duplicate track id: unit')))
  assert.ok(errors.some((error) => error.includes('unknown track: missing')))
  assert.ok(errors.some((error) => error.includes('cannot include partitioned track: spec')))
})

test('validateConfig requires duration isolation to target a non-partitioned Vitest track', () => {
  const config = parseSuiteConfig(JSON.stringify({
    suiteDeadlineMs: 1000,
    canonical: {
      maxConcurrentLanes: 4,
      resourceLimits: { host: 4, 'duration-measurement': 1 },
      durationMeasurementTracks: ['unit'],
      durationIsolationTrack: 'spec',
    },
    tracks: [
      { id: 'unit', kind: 'dotnet-apphost', apphost: 'bin/unit', report: 'reports/unit.trx', reportFormat: 'trx', deadlineMs: 100, enforce: false },
      { id: 'spec', kind: 'dotnet-apphost', apphost: 'bin/spec', report: 'reports/spec-{partition}.trx', reportFormat: 'trx', partitions: 2, deadlineMs: 100, enforce: false },
    ],
  }))
  const errors = validateConfig(config)
  assert.ok(errors.some((error) => error.includes('cannot include partitioned track: spec')))
})

test('validateConfig rejects invalid canonical limits and non-apphost partitioning', () => {
  const config = parseSuiteConfig(JSON.stringify({
    suiteDeadlineMs: 1000,
    canonical: { maxConcurrentLanes: 0, resourceLimits: { host: 0 } },
    tracks: [{
      id: 'spec', kind: 'vitest', run: ['npm', 'test'], report: 'reports/spec.json', reportFormat: 'vitest',
      partitions: 1, deadlineMs: 100, enforce: false,
    }],
  }))
  const errors = validateConfig(config)
  assert.ok(errors.some((error) => error.includes('canonical.maxConcurrentLanes')))
  assert.ok(errors.some((error) => error.includes('canonical.resourceLimits.host')))
  assert.ok(errors.some((error) => error.includes('partitions must be an integer greater than one')))
  assert.ok(errors.some((error) => error.includes('partitions require kind dotnet-apphost')))
  assert.ok(errors.some((error) => error.includes('partitioned reports must include {partition}')))
})

test('validateConfig requires the last rule to be the default catch-all', () => {
  const config = parseSuiteConfig(
    JSON.stringify({
      suiteDeadlineMs: 1000,
      tracks: [
        {
          id: 't',
          kind: 'report-only',
          report: 'r',
          reportFormat: 'trx',
          deadlineMs: 100,
          enforce: true,
          rules: [{ id: 'spec', namePattern: 'Specs\\.', absoluteMs: 500 }],
        },
      ],
    }),
  )
  const errors = validateConfig(config)
  assert.ok(errors.some((e) => e.includes('default catch-all')))
})

test('validateConfig rejects allowlist entries missing governance fields', () => {
  const config = parseSuiteConfig(
    JSON.stringify({
      suiteDeadlineMs: 1000,
      tracks: [
        {
          id: 't',
          kind: 'report-only',
          report: 'r',
          reportFormat: 'trx',
          deadlineMs: 100,
          enforce: true,
          rules: [
            {
              id: 'unit',
              absoluteMs: 50,
              allowlist: [
                { id: 'both', pattern: 'x', observedMs: 80, reason: 'both', owner: 'o', deadline: '2026-11-30' },
                { id: 'noreason', observedMs: 80, owner: 'o', deadline: '2026-11-30' },
                { id: 'noowner', observedMs: 80, reason: 'r', deadline: '2026-11-30' },
                { id: 'nodeadline', observedMs: 80, reason: 'r', owner: 'o' },
                { id: 'baddate', observedMs: 80, reason: 'r', owner: 'o', deadline: 'not-a-date' },
              ],
            },
          ],
        },
      ],
    }),
  )
  const errors = validateConfig(config)
  assert.ok(errors.some((e) => e.includes('both id and pattern')))
  assert.ok(errors.some((e) => e.includes('"noreason" needs a reason')))
  assert.ok(errors.some((e) => e.includes('"noowner" needs an owner')))
  assert.ok(errors.some((e) => e.includes('"nodeadline" needs a valid ISO date deadline')))
  assert.ok(errors.some((e) => e.includes('"baddate" needs a valid ISO date deadline')))
})
