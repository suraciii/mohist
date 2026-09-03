import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { dirname, resolve } from 'node:path'
import { test } from 'node:test'
import { fileURLToPath } from 'node:url'

import { parseSuiteConfig, stripJsonc, validateConfig } from './config.js'

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), '../..')

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
        { "id": "unit", "percentile": 95, "percentileMs": 50 }
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
  assert.equal(config.tracks[0].rules[0].percentileMs, 50)
})

test('validateConfig accepts a well-formed enforce track with a default rule', () => {
  const config = parseSuiteConfig(SAMPLE)
  assert.deepEqual(validateConfig(config), [])
})

test('validateConfig rejects non-array apphostArgs', () => {
  const config = parseSuiteConfig(
    JSON.stringify({
      suiteDeadlineMs: 1000,
      tracks: [
        {
          id: 't',
          kind: 'dotnet-apphost',
          apphostArgs: '-parallel',
          report: 'r',
          reportFormat: 'trx',
          deadlineMs: 100,
          enforce: false,
        },
      ],
    }),
  )
  assert.ok(validateConfig(config).some((e) => e.includes('apphostArgs must be an array of strings')))
})

test('validateConfig rejects non-string apphostArgs items', () => {
  const config = parseSuiteConfig(
    JSON.stringify({
      suiteDeadlineMs: 1000,
      tracks: [
        {
          id: 't',
          kind: 'dotnet-apphost',
          apphostArgs: ['-parallel', 1],
          report: 'r',
          reportFormat: 'trx',
          deadlineMs: 100,
          enforce: false,
        },
      ],
    }),
  )
  assert.ok(validateConfig(config).some((e) => e.includes('apphostArgs must contain only strings')))
})

function dotnetBehaviorTrack(apphostArgs: readonly string[], level: 'L0' | 'L1' = 'L0') {
  return parseSuiteConfig(
    JSON.stringify({
      suiteDeadlineMs: 1000,
      tracks: [
        {
          id: `server-${level.toLowerCase()}`,
          kind: 'dotnet-apphost',
          trackType: 'behavior',
          level,
          csproj: `server-${level.toLowerCase()}.csproj`,
          apphostArgs,
          report: `reports/server-${level.toLowerCase()}.trx`,
          reportFormat: 'trx',
          deadlineMs: 100,
          enforce: true,
          rules: [{ id: 'tests' }],
        },
      ],
    }),
  )
}

test('validateConfig accepts one positive trait selector matching a dotnet behavior track level', () => {
  assert.deepEqual(validateConfig(dotnetBehaviorTrack(['-trait', 'level=L0'])), [])
  assert.deepEqual(validateConfig(dotnetBehaviorTrack(['-trait', 'level=L1'], 'L1')), [])
})

test('validateConfig rejects missing, mismatched, duplicate, and negative test level selectors', () => {
  for (const args of [
    [],
    ['-trait', 'level=L1'],
    ['-trait', 'level=L0', '-trait', 'level=L0'],
    ['-trait-', 'level=L1'],
  ]) {
    assert.ok(
      validateConfig(dotnetBehaviorTrack(args)).some((error) => error.includes('positive "-trait", "level=L0"')),
      `expected an invalid positive selector diagnosis for ${JSON.stringify(args)}`,
    )
  }
  assert.ok(
    validateConfig(dotnetBehaviorTrack(['-trait-', 'level=L0'])).some((error) => error.includes('negative -trait-')),
  )
})

test('repository Server behavior tracks use their matching positive level selectors', () => {
  const config = parseSuiteConfig(readFileSync(resolve(repositoryRoot, 'test-duration.config.jsonc'), 'utf8'))
  assert.deepEqual(validateConfig(config), [])

  const serverL0 = config.tracks.find((track) => track.id === 'server-l0')
  const serverL1 = config.tracks.find((track) => track.id === 'server-l1')
  assert.deepEqual(serverL0?.apphostArgs?.slice(0, 2), ['-trait', 'level=L0'])
  assert.deepEqual(serverL1?.apphostArgs?.slice(0, 2), ['-trait', 'level=L1'])
})

test('validateConfig keeps execution ledgers on dotnet apphost tracks', () => {
  const config = parseSuiteConfig(
    JSON.stringify({
      suiteDeadlineMs: 1000,
      tracks: [
        {
          id: 'cli',
          kind: 'dotnet-apphost',
          csproj: 'cli.csproj',
          executionLedger: 'reports/cli-ledger.json',
          executionProvenance: 'reports/cli-provenance.json',
          executionSourceRoots: ['packages/server'],
          report: 'reports/cli.trx',
          reportFormat: 'trx',
          deadlineMs: 100,
          enforce: false,
          status: 'baseline-pending',
          reason: 'fixture baseline',
        },
      ],
    }),
  )
  assert.deepEqual(validateConfig(config), [])
})

test('validateConfig requires source roots for execution-ledger freshness', () => {
  const config = parseSuiteConfig(
    JSON.stringify({
      suiteDeadlineMs: 1000,
      tracks: [
        {
          id: 'cli',
          kind: 'dotnet-apphost',
          csproj: 'cli.csproj',
          executionLedger: 'ledger.json',
          executionProvenance: 'provenance.json',
          report: 'reports/cli.trx',
          reportFormat: 'trx',
          deadlineMs: 100,
          enforce: false,
        },
      ],
    }),
  )
  assert.ok(validateConfig(config).some((e) => e.includes('requires non-empty executionSourceRoots')))
})

test('validateConfig rejects execution ledgers on non-apphost tracks', () => {
  const config = parseSuiteConfig(
    JSON.stringify({
      suiteDeadlineMs: 1000,
      tracks: [
        {
          id: 'unit',
          kind: 'report-only',
          executionLedger: 'ledger.json',
          report: 'reports/unit.json',
          reportFormat: 'vitest',
          deadlineMs: 100,
          enforce: false,
        },
      ],
    }),
  )
  assert.ok(validateConfig(config).some((e) => e.includes('executionLedger requires kind=dotnet-apphost')))
})

test('validateConfig requires paired execution ledger provenance', () => {
  const config = parseSuiteConfig(
    JSON.stringify({
      suiteDeadlineMs: 1000,
      tracks: [
        {
          id: 'cli',
          kind: 'dotnet-apphost',
          csproj: 'cli.csproj',
          executionLedger: 'ledger.json',
          report: 'reports/cli.trx',
          reportFormat: 'trx',
          deadlineMs: 100,
          enforce: false,
        },
      ],
    }),
  )
  assert.ok(validateConfig(config).some((e) => e.includes('executionLedger requires executionProvenance')))
})

test('validateConfig rejects execution ledgers without a TRX report', () => {
  const config = parseSuiteConfig(
    JSON.stringify({
      suiteDeadlineMs: 1000,
      tracks: [
        {
          id: 'cli',
          kind: 'dotnet-apphost',
          csproj: 'cli.csproj',
          executionLedger: 'ledger.json',
          report: 'reports/cli.json',
          reportFormat: 'vitest',
          deadlineMs: 100,
          enforce: false,
        },
      ],
    }),
  )
  assert.ok(validateConfig(config).some((e) => e.includes('executionLedger requires reportFormat=trx')))
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
          rules: [{ id: 'spec', namePattern: 'Specs\\.' }],
        },
      ],
    }),
  )
  const errors = validateConfig(config)
  assert.ok(errors.some((e) => e.includes('default catch-all')))
})

test('validateConfig requires expectedTotal to be a positive integer', () => {
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
          rules: [{ id: 'spec', namePattern: 'Specs\\.', expectedTotal: 0 }, { id: 'unit' }],
        },
      ],
    }),
  )

  assert.ok(validateConfig(config).some((e) => e.includes('expectedTotal must be a positive integer')))
})
