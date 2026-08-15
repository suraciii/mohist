import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
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

test('validateConfig rejects partitioned execution-ledger tracks before scheduling', () => {
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
          report: 'reports/cli-{partition}.trx',
          reportFormat: 'trx',
          partitions: 2,
          deadlineMs: 100,
          enforce: false,
          status: 'baseline-pending',
          reason: 'fixture baseline',
        },
      ],
    }),
  )
  assert.ok(validateConfig(config).some((error) => error.includes('executionLedger tracks cannot be partitioned')))
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

test('validateConfig fails closed when an unenforced track is not explicitly baseline-pending', () => {
  const config = parseSuiteConfig(
    JSON.stringify({
      suiteDeadlineMs: 1000,
      tracks: [
        { id: 'silent', kind: 'report-only', report: 'r', reportFormat: 'trx', deadlineMs: 100, enforce: false },
        {
          id: 'rules-ignored',
          kind: 'report-only',
          report: 's',
          reportFormat: 'trx',
          deadlineMs: 100,
          enforce: false,
          status: 'baseline-pending',
          reason: 'capture a baseline',
          rules: [{ id: 'unit', absoluteMs: 500 }],
        },
      ],
    }),
  )

  const errors = validateConfig(config)

  assert.ok(errors.some((error) => error.includes('track "silent": enforce=false requires status baseline-pending')))
  assert.ok(
    errors.some((error) =>
      error.includes('track "silent": enforce=false requires a non-empty baseline-pending reason'),
    ),
  )
  assert.ok(
    errors.some((error) => error.includes('track "rules-ignored": enforce=false must not carry unenforced rules')),
  )
})

test('validateConfig accepts bounded Server Spec partitions and rejects other partitioned tracks', () => {
  const config = parseSuiteConfig(
    JSON.stringify({
      suiteDeadlineMs: 1000,
      canonical: {
        maxConcurrentLanes: 4,
        resourceLimits: { host: 4, dotnet: 4, 'server-spec': 4 },
        partitionExecutionCapacity: 4,
      },
      tracks: [
        {
          id: 'server-spec',
          kind: 'dotnet-apphost',
          apphost: 'bin/spec',
          report: 'reports/spec-{partition}.trx',
          reportFormat: 'trx',
          partitions: 4,
          partitionMaxThreads: 1,
          deadlineMs: 100,
          enforce: true,
          rules: [{ id: 'spec', absoluteMs: 5000 }],
        },
      ],
    }),
  )
  assert.deepEqual(validateConfig(config), [])

  const unsupported = parseSuiteConfig(
    JSON.stringify({
      suiteDeadlineMs: 1000,
      canonical: {
        maxConcurrentLanes: 4,
        resourceLimits: { host: 4, dotnet: 4, spec: 4, 'server-spec': 4 },
        partitionExecutionCapacity: 4,
      },
      tracks: [
        {
          id: 'spec',
          kind: 'dotnet-apphost',
          apphost: 'bin/spec',
          report: 'reports/spec-{partition}.trx',
          reportFormat: 'trx',
          partitions: 4,
          partitionMaxThreads: 1,
          deadlineMs: 100,
          enforce: true,
          rules: [{ id: 'spec', absoluteMs: 5000 }],
        },
      ],
    }),
  )
  assert.ok(
    validateConfig(unsupported).some((error) => error.includes('only server-spec supports partitioned execution')),
  )

  const unsupportedWithoutCanonical = parseSuiteConfig(
    JSON.stringify({
      suiteDeadlineMs: 1000,
      tracks: [
        {
          id: 'spec',
          kind: 'dotnet-apphost',
          apphost: 'bin/spec',
          report: 'reports/spec-{partition}.trx',
          reportFormat: 'trx',
          partitions: 4,
          partitionMaxThreads: 1,
          deadlineMs: 100,
          enforce: true,
          rules: [{ id: 'spec', absoluteMs: 5000 }],
        },
      ],
    }),
  )
  const unsupportedErrors = validateConfig(unsupportedWithoutCanonical)
  assert.ok(unsupportedErrors.some((error) => error.includes('only server-spec supports partitioned execution')))
  assert.ok(unsupportedErrors.some((error) => error.includes('canonical configuration is required')))

  const serverSpecWithoutCanonical = parseSuiteConfig(
    JSON.stringify({
      suiteDeadlineMs: 1000,
      tracks: [
        {
          id: 'server-spec',
          kind: 'dotnet-apphost',
          apphost: 'bin/spec',
          report: 'reports/spec-{partition}.trx',
          reportFormat: 'trx',
          partitions: 4,
          partitionMaxThreads: 1,
          deadlineMs: 100,
          enforce: true,
          rules: [{ id: 'spec', absoluteMs: 5000 }],
        },
      ],
    }),
  )
  assert.ok(
    validateConfig(serverSpecWithoutCanonical).some((error) => error.includes('canonical configuration is required')),
  )
})

test('checked-in Server Spec topology stays within the four-unit execution capacity', () => {
  const config = parseSuiteConfig(readFileSync(new URL('../../test-duration.config.jsonc', import.meta.url), 'utf8'))
  const serverSpec = config.tracks.find((track) => track.id === 'server-spec')
  assert.ok(serverSpec)
  assert.equal(serverSpec.partitions, 4)
  assert.equal(serverSpec.partitionMaxThreads, 1)
  assert.equal(config.canonical?.partitionExecutionCapacity, 4)
  assert.equal(
    Math.min(serverSpec.partitions!, config.canonical!.resourceLimits['server-spec']) * serverSpec.partitionMaxThreads!,
    4,
  )
  assert.deepEqual(validateConfig(config), [])
})

test('validateConfig requires bounded partition concurrency and permits a partitioned duration phase', () => {
  const config = parseSuiteConfig(
    JSON.stringify({
      suiteDeadlineMs: 1000,
      canonical: {
        maxConcurrentLanes: 4,
        resourceLimits: { host: 4, 'server-spec': 2 },
        partitionExecutionCapacity: 4,
        durationMeasurementTracks: ['unit', 'unit', 'missing', 'server-spec'],
      },
      tracks: [
        {
          id: 'unit',
          kind: 'dotnet-apphost',
          apphost: 'bin/unit',
          report: 'reports/unit.trx',
          reportFormat: 'trx',
          deadlineMs: 100,
          enforce: false,
        },
        {
          id: 'server-spec',
          kind: 'dotnet-apphost',
          apphost: 'bin/spec',
          report: 'reports/spec-{partition}.trx',
          reportFormat: 'trx',
          partitions: 2,
          partitionMaxThreads: 2,
          deadlineMs: 100,
          enforce: false,
        },
      ],
    }),
  )
  const errors = validateConfig(config)
  assert.ok(errors.some((error) => error.includes('requires canonical.resourceLimits.duration-measurement')))
  assert.ok(errors.some((error) => error.includes('duplicate track id: unit')))
  assert.ok(errors.some((error) => error.includes('unknown track: missing')))
  assert.ok(!errors.some((error) => error.includes('cannot include partitioned track: server-spec')))
})

test('validateConfig fails closed when partition execution capacity is missing or exceeded', () => {
  const track = {
    id: 'server-spec',
    kind: 'dotnet-apphost',
    apphost: 'bin/spec',
    report: 'reports/spec-{partition}.trx',
    reportFormat: 'trx',
    partitions: 4,
    partitionMaxThreads: 2,
    deadlineMs: 100,
    enforce: true,
    rules: [{ id: 'spec', absoluteMs: 5000 }],
  }
  const missing = parseSuiteConfig(
    JSON.stringify({
      suiteDeadlineMs: 1000,
      canonical: { maxConcurrentLanes: 4, resourceLimits: { host: 4, 'server-spec': 4 } },
      tracks: [track],
    }),
  )
  const exceeded = parseSuiteConfig(
    JSON.stringify({
      suiteDeadlineMs: 1000,
      canonical: {
        maxConcurrentLanes: 4,
        resourceLimits: { host: 4, 'server-spec': 3 },
        partitionExecutionCapacity: 4,
      },
      tracks: [track],
    }),
  )

  assert.ok(
    validateConfig(missing).some((error) => error.includes('partitionExecutionCapacity must be a positive integer')),
  )
  assert.ok(validateConfig(exceeded).some((error) => error.includes('partition concurrency exceeds')))
})

test('validateConfig requires duration isolation to target a non-partitioned Vitest track', () => {
  const config = parseSuiteConfig(
    JSON.stringify({
      suiteDeadlineMs: 1000,
      canonical: {
        maxConcurrentLanes: 4,
        resourceLimits: { host: 4, 'duration-measurement': 1 },
        durationMeasurementTracks: ['unit'],
        durationIsolationTrack: 'server-spec',
      },
      tracks: [
        {
          id: 'unit',
          kind: 'dotnet-apphost',
          apphost: 'bin/unit',
          report: 'reports/unit.trx',
          reportFormat: 'trx',
          deadlineMs: 100,
          enforce: false,
        },
        {
          id: 'server-spec',
          kind: 'dotnet-apphost',
          apphost: 'bin/spec',
          report: 'reports/spec-{partition}.trx',
          reportFormat: 'trx',
          partitions: 2,
          partitionMaxThreads: 1,
          deadlineMs: 100,
          enforce: false,
        },
      ],
    }),
  )
  const errors = validateConfig(config)
  assert.ok(errors.some((error) => error.includes('cannot include partitioned track: server-spec')))
})

test('validateConfig rejects invalid canonical limits and non-apphost partitioning', () => {
  const config = parseSuiteConfig(
    JSON.stringify({
      suiteDeadlineMs: 1000,
      canonical: { maxConcurrentLanes: 0, resourceLimits: { host: 0 } },
      tracks: [
        {
          id: 'spec',
          kind: 'vitest',
          run: ['npm', 'test'],
          report: 'reports/spec.json',
          reportFormat: 'vitest',
          partitions: 1,
          partitionMaxThreads: 0,
          deadlineMs: 100,
          enforce: false,
        },
      ],
    }),
  )
  const errors = validateConfig(config)
  assert.ok(errors.some((error) => error.includes('canonical.maxConcurrentLanes')))
  assert.ok(errors.some((error) => error.includes('canonical.resourceLimits.host')))
  assert.ok(errors.some((error) => error.includes('partitions must be an integer greater than one')))
  assert.ok(errors.some((error) => error.includes('positive integer partitionMaxThreads')))
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
