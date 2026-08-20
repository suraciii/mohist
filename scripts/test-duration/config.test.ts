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
          executionSourceRoots: ['packages/cli'],
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
