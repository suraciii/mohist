import assert from 'node:assert/strict'
import { test } from 'node:test'

import {
  buildLedgerEnvironment,
  createExecutionRunId,
  discoverManifest,
  manifestFromDiscovery,
  parseExecutionLedger,
  parseExecutionProvenance,
  serializeExecutionProvenance,
  validateExecutionEvidence,
} from './execution-ledger.js'
import type { ExecutionLedgerExpectation, TestCase } from './types.js'

const manifest = discoverManifest({
  listTests: () => 'Ns.Cli.Fast\nNs.Cli.Theory(value: 1)',
})

const expectation: ExecutionLedgerExpectation = {
  runId: 'l0-run-1',
  manifest,
  assemblyPath: '/virtual/Mohist.Cli.Tests.dll',
  assemblySha256: 'a'.repeat(64),
  parallelism: 'xunit-default',
}

function ledger(overrides: Record<string, unknown> = {}): string {
  return JSON.stringify({
    schemaVersion: 1,
    runId: expectation.runId,
    manifestHash: manifest.hash,
    manifestCount: 2,
    assemblyPath: expectation.assemblyPath,
    assemblySha256: expectation.assemblySha256,
    xunitVersion: '3.2.2.0',
    mtpVersion: '1.9.1.0',
    parallelism: expectation.parallelism,
    durationSource: 'xunit.v3.ITestResultMessage.ExecutionTime',
    durationUnit: 'seconds',
    cases: [
      { uid: 'uid-fast', name: 'Ns.Cli.Fast', outcome: 'passed', executionTimeSeconds: 0.01, startTime: '2026-08-11T06:00:00Z', finishTime: '2026-08-11T06:00:00.010Z' },
      { uid: 'uid-theory', name: 'Ns.Cli.Theory(value: 1)', outcome: 'passed', executionTimeSeconds: 0.02, startTime: '2026-08-11T06:00:00Z', finishTime: '2026-08-11T06:00:00.020Z' },
    ],
    ...overrides,
  })
}

const trxCases: TestCase[] = [
  { name: 'Ns.Cli.Fast', durationMs: 900, outcome: 'passed' },
  { name: 'Ns.Cli.Theory(value: 1)', durationMs: 800, outcome: 'passed' },
]

test('discovery and run ids use fake process and fake clock seams', () => {
  assert.deepEqual(manifest.names, ['Ns.Cli.Fast', 'Ns.Cli.Theory(value: 1)'])
  assert.equal(createExecutionRunId({ now: () => 1234 }, () => 'fixed'), 'ya-fixed')
  assert.throws(() => manifestFromDiscovery('Ns.Cli.Fast\nNs.Cli.Fast'), /duplicate test cases/)
})

test('execution evidence uses xUnit ExecutionTime seconds, never TRX duration', () => {
  const parsed = parseExecutionLedger(ledger())
  const result = validateExecutionEvidence(trxCases, parsed, expectation)

  assert.deepEqual(result.errors, [])
  assert.deepEqual(result.cases.map((item) => item.durationMs), [10, 20])
})

test('execution evidence fails closed for run, manifest, identity, and outcome mismatches', () => {
  const parsed = parseExecutionLedger(ledger({
    runId: 'stale-run',
    manifestHash: 'b'.repeat(64),
    assemblyPath: '/stale/test.dll',
    assemblySha256: 'c'.repeat(64),
    parallelism: 'xunit-none',
  }))
  const result = validateExecutionEvidence(trxCases, parsed, expectation)

  assert.ok(result.errors.some((error) => error.includes('run ID')))
  assert.ok(result.errors.some((error) => error.includes('manifest hash')))
  assert.ok(result.errors.some((error) => error.includes('assembly path')))
  assert.ok(result.errors.some((error) => error.includes('assembly hash')))
  assert.ok(result.errors.some((error) => error.includes('parallelism')))
})

test('execution evidence rejects duplicate UIDs and non-executed cases', () => {
  const duplicateUid = JSON.parse(ledger()) as { cases: Array<Record<string, unknown>> }
  duplicateUid.cases[1].uid = duplicateUid.cases[0].uid
  const duplicateResult = parseExecutionLedger(JSON.stringify(duplicateUid))
  assert.ok(duplicateResult.errors.some((error) => error.includes('duplicate uid')))

  const skipped = JSON.parse(ledger()) as { cases: Array<Record<string, unknown>> }
  skipped.cases[1].outcome = 'not-run'
  const skippedParsed = parseExecutionLedger(JSON.stringify(skipped))
  const skippedResult = validateExecutionEvidence(trxCases, skippedParsed, expectation)
  assert.ok(skippedResult.errors.some((error) => error.includes('non-executed test')))
})

test('execution evidence rejects an unsupported timing contract before evaluation', () => {
  const unsupported = JSON.parse(ledger()) as Record<string, unknown>
  unsupported.durationSource = 'trx.UnitTestResult.duration'
  const parsed = parseExecutionLedger(JSON.stringify(unsupported))
  assert.ok(parsed.errors.some((error) => error.includes('durationSource')))
})

test('execution evidence rejects duplicate names and missing partial records', () => {
  const duplicateName = JSON.parse(ledger()) as { cases: Array<Record<string, unknown>> }
  duplicateName.cases[1].name = duplicateName.cases[0].name
  assert.ok(parseExecutionLedger(JSON.stringify(duplicateName)).errors.some((error) => error.includes('duplicate test name')))

  const partial = JSON.parse(ledger()) as { cases: Array<Record<string, unknown>> }
  partial.cases.pop()
  const parsed = parseExecutionLedger(JSON.stringify(partial))
  assert.ok(parsed.errors.some((error) => error.includes('case count')))
})

test('saved provenance is self-authenticating, non-empty, and round trips exactly', () => {
  const parsed = parseExecutionProvenance(serializeExecutionProvenance(expectation))
  assert.deepEqual(parsed, expectation)

  const stale = JSON.parse(serializeExecutionProvenance(expectation)) as Record<string, unknown>
  stale.manifestHash = 'b'.repeat(64)
  assert.throws(() => parseExecutionProvenance(JSON.stringify(stale)), /manifestHash does not match/)
  assert.throws(() => parseExecutionProvenance(JSON.stringify({ ...stale, manifestNames: [], manifestCount: 0 })), /positive integer|no test cases/)
})

test('ledger environment carries every provenance field to the reporter', () => {
  const environment = buildLedgerEnvironment({
    runId: expectation.runId,
    manifest,
    ledgerPath: '/virtual/ledger.json',
    assemblyPath: expectation.assemblyPath,
    assemblySha256: expectation.assemblySha256,
    parallelism: expectation.parallelism,
  })

  assert.deepEqual(environment, {
    MOHIST_EXECUTION_LEDGER_PATH: '/virtual/ledger.json',
    MOHIST_EXECUTION_LEDGER_RUN_ID: expectation.runId,
    MOHIST_EXECUTION_LEDGER_MANIFEST_HASH: manifest.hash,
    MOHIST_EXECUTION_LEDGER_MANIFEST_COUNT: '2',
    MOHIST_EXECUTION_LEDGER_ASSEMBLY_PATH: expectation.assemblyPath,
    MOHIST_EXECUTION_LEDGER_ASSEMBLY_SHA256: expectation.assemblySha256,
    MOHIST_EXECUTION_LEDGER_PARALLELISM: expectation.parallelism,
  })
})
