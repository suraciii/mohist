import assert from 'node:assert/strict'
import { mock, test } from 'node:test'

import {
  commandFor,
  createTimeout,
  DEFAULT_XUNIT_PARALLELISM,
  evaluateTrackArtifacts,
  main,
  parseArgs,
  parallelismFor,
  runProcessWithDeadline,
  writeExecutionProvenance,
} from './guard.js'
import { formatEvaluation, formatSummary, summarize } from './diagnostics.js'
import { manifestFromDiscovery, serializeExecutionProvenance } from './execution-ledger.js'
import type { CurrentExecutionIdentity, ExecutionLedgerExpectation, TrackConfig, TrackEvaluation, TrackRun } from './types.js'

const fastCaseUid = '1'.repeat(64)

function fastManifest() {
  return manifestFromDiscovery(JSON.stringify([
    { ID: fastCaseUid, DisplayName: 'Ns.Cli.Fast', Class: 'Ns.Cli', Method: 'Fast' },
  ]))
}

function captureStderr(): { calls: () => string; restore: () => void } {
  const stderrMock = mock.method(process.stderr, 'write', () => true)
  return {
    calls: () => stderrMock.mock.calls.map((c) => String(c.arguments[0])).join(''),
    restore: () => stderrMock.mock.restore(),
  }
}

test('parseArgs: focused with both arguments resolves the request', () => {
  const args = parseArgs(['focused', 'packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj', 'Mohist.Cli.Tests.Skills.SkillsContentTests'])
  assert.equal(args.mode, 'focused')
  assert.equal(args.focused?.csproj, 'packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj')
  assert.equal(args.focused?.className, 'Mohist.Cli.Tests.Skills.SkillsContentTests')
})

test('commandFor appends dotnet apphost arguments after the default report arguments', () => {
  const track: TrackConfig = {
    id: 'unit',
    kind: 'dotnet-apphost',
    apphost: 'bin/tests',
    apphostArgs: ['-parallel', 'none'],
    report: 'reports/unit.trx',
    reportFormat: 'trx',
    deadlineMs: 1000,
    enforce: false,
  }
  const command = commandFor(track)
  assert.deepEqual(command.args.slice(-6), ['-noColor', '-noLogo', '-trx', `${process.cwd()}/reports/unit.trx`, '-parallel', 'none'])
})

test('parallelism provenance records every effective xUnit default and explicit override', () => {
  const track: TrackConfig = {
    id: 'cli', kind: 'dotnet-apphost', report: 'reports/cli.trx', reportFormat: 'trx', deadlineMs: 1000, enforce: false,
  }
  assert.equal(parallelismFor(track), DEFAULT_XUNIT_PARALLELISM)
  assert.equal(
    parallelismFor({ ...track, apphostArgs: ['-parallel', 'none', '-parallelAlgorithm', 'aggressive', '-maxThreads', '2'] }),
    'xunit-v3:parallel=none;parallelAlgorithm=aggressive;maxThreads=2',
  )
  assert.throws(() => parallelismFor({ ...track, apphostArgs: ['-parallel', 'collections', '-parallel', 'none'] }), /duplicate xUnit option/)
  assert.throws(() => parallelismFor({ ...track, apphostArgs: ['-maxThreads'] }), /requires a value/)
})

test('execution provenance writer creates its parent and writes through a fake artifact store', () => {
  const manifest = fastManifest()
  const calls: string[] = []
  writeExecutionProvenance('/virtual/reports/cli.execution-provenance.json', {
    runId: 'run-1',
    manifest,
    assemblyPath: '/virtual/Mohist.Cli.Tests.dll',
    assemblySha256: 'a'.repeat(64),
    sourceSha256: 'b'.repeat(64),
    parallelism: DEFAULT_XUNIT_PARALLELISM,
  }, {
    ensureDirectory: (path) => calls.push(`mkdir:${path}`),
    writeText: (path, content) => calls.push(`write:${path}:${JSON.parse(content).runId}`),
  })
  assert.deepEqual(calls, [
    'mkdir:/virtual/reports',
    'write:/virtual/reports/cli.execution-provenance.json:run-1',
  ])
})

test('createTimeout can be cancelled so completed tracks do not retain deadline timers', () => {
  let scheduledCallback: (() => void) | undefined
  let clearedTimer: unknown
  const timeout = createTimeout(60_000, {
    set: (callback) => {
      scheduledCallback = callback
      return 'timer'
    },
    clear: (timer) => {
      clearedTimer = timer
    },
  })

  timeout.cancel()

  assert.equal(typeof scheduledCallback, 'function')
  assert.equal(clearedTimer, 'timer')
})

test('compiled discovery timeout uses the existing deadline path without reporting cleanup proof', async () => {
  let expire!: (reason: 'track') => void
  const timeout = new Promise<'track'>((resolvePromise) => { expire = resolvePromise })
  let killed = false
  const running = runProcessWithDeadline({
    child: { pid: 42, done: new Promise<{ exitCode: number | null; stdout: string }>(() => undefined) },
    timeout,
    kill: async () => { killed = true },
    now: () => 100,
  })

  expire('track')
  const result = await running

  assert.equal(result.status, 'timeout')
  assert.equal(result.timeoutReason, 'track')
  assert.equal(killed, true)
  assert.equal('cleanupFailed' in result, false)
  assert.equal('cleanupError' in result, false)
})

test('--check evaluates authoritative saved provenance and ledger without an in-memory run', () => {
  const manifest = fastManifest()
  const expected: ExecutionLedgerExpectation = {
    runId: 'saved-run',
    manifest,
    assemblyPath: '/virtual/Mohist.Cli.Tests.dll',
    assemblySha256: 'a'.repeat(64),
    sourceSha256: 'b'.repeat(64),
    parallelism: DEFAULT_XUNIT_PARALLELISM,
  }
  const track: TrackConfig = {
    id: 'cli',
    kind: 'dotnet-apphost',
    csproj: 'virtual.csproj',
    report: 'reports/cli.trx',
    executionLedger: 'reports/cli.execution-ledger.json',
    executionProvenance: 'reports/cli.execution-provenance.json',
    executionSourceRoots: ['packages/cli'],
    reportFormat: 'trx',
    deadlineMs: 60_000,
    enforce: true,
    rules: [{ id: 'unit', absoluteMs: 50 }],
  }
  const artifacts = new Map<string, string>([
    [track.report, '<TestRun><Results><UnitTestResult testName="Ns.Cli.Fast" outcome="Passed" duration="00:00:00.9000000"/></Results></TestRun>'],
    [track.executionProvenance!, serializeExecutionProvenance(expected)],
    [track.executionLedger!, JSON.stringify({
      schemaVersion: 2,
      runId: expected.runId,
      manifestHash: manifest.hash,
      manifestCount: 1,
      assemblyPath: expected.assemblyPath,
      assemblySha256: expected.assemblySha256,
      sourceSha256: expected.sourceSha256,
      xunitVersion: '3.2.2.0',
      mtpVersion: '1.9.1.0',
      parallelism: expected.parallelism,
      durationSource: 'xunit.v3.ITestResultMessage.ExecutionTime',
      durationUnit: 'seconds',
      cases: [{ uid: 'fast', testCaseUid: fastCaseUid, name: 'Ns.Cli.Fast', className: 'Ns.Cli', collectionName: 'Ns.Cli collection', outcome: 'passed', executionTimeSeconds: 0.01, startTime: '2026-08-11T00:00:00Z', finishTime: '2026-08-11T00:00:01Z' }],
    })],
  ])

  const result = evaluateTrackArtifacts(track, {
    readText: (path) => {
      const value = artifacts.get(path)
      if (value === undefined) throw new Error(`missing ${path}`)
      return value
    },
  }, undefined, new Date('2026-08-11T00:00:00Z'), {
    manifest,
    assemblyPath: expected.assemblyPath,
    assemblySha256: expected.assemblySha256,
    sourceSha256: expected.sourceSha256,
    parallelism: expected.parallelism,
  })

  assert.equal(result.total, 1)
  assert.equal(result.passed, true)
  assert.equal(result.reportError, undefined)
})

function savedExecutionFixture(): {
  readonly track: TrackConfig
  readonly expected: ExecutionLedgerExpectation
  readonly current: CurrentExecutionIdentity
  readonly artifacts: Map<string, string>
} {
  const manifest = fastManifest()
  const expected: ExecutionLedgerExpectation = {
    runId: 'saved-run',
    manifest,
    assemblyPath: '/virtual/Mohist.Cli.Tests.dll',
    assemblySha256: 'a'.repeat(64),
    sourceSha256: 'b'.repeat(64),
    parallelism: DEFAULT_XUNIT_PARALLELISM,
  }
  const track: TrackConfig = {
    id: 'cli',
    kind: 'dotnet-apphost',
    csproj: 'virtual.csproj',
    report: 'reports/cli.trx',
    executionLedger: 'reports/cli.execution-ledger.json',
    executionProvenance: 'reports/cli.execution-provenance.json',
    executionSourceRoots: ['packages/cli'],
    reportFormat: 'trx',
    deadlineMs: 60_000,
    enforce: true,
    rules: [{ id: 'unit', absoluteMs: 50 }],
  }
  const current: CurrentExecutionIdentity = {
    manifest,
    assemblyPath: expected.assemblyPath,
    assemblySha256: expected.assemblySha256,
    sourceSha256: expected.sourceSha256,
    parallelism: expected.parallelism,
  }
  return {
    track,
    expected,
    current,
    artifacts: new Map([
      [track.report, '<TestRun><Results><UnitTestResult testName="Ns.Cli.Fast" outcome="Passed" duration="00:00:00.9000000"/></Results></TestRun>'],
      [track.executionProvenance!, serializeExecutionProvenance(expected)],
      [track.executionLedger!, JSON.stringify({
        schemaVersion: 2,
        runId: expected.runId,
        manifestHash: manifest.hash,
        manifestCount: 1,
        assemblyPath: expected.assemblyPath,
        assemblySha256: expected.assemblySha256,
        sourceSha256: expected.sourceSha256,
        xunitVersion: '3.2.2.0',
        mtpVersion: '1.9.1.0',
        parallelism: expected.parallelism,
        durationSource: 'xunit.v3.ITestResultMessage.ExecutionTime',
        durationUnit: 'seconds',
        cases: [{ uid: 'fast', testCaseUid: fastCaseUid, name: 'Ns.Cli.Fast', className: 'Ns.Cli', collectionName: 'Ns.Cli collection', outcome: 'passed', executionTimeSeconds: 0.01, startTime: '2026-08-11T00:00:00Z', finishTime: '2026-08-11T00:00:01Z' }],
      })],
    ]),
  }
}

function evaluateSavedFixture(fixture: ReturnType<typeof savedExecutionFixture>): TrackEvaluation {
  return evaluateTrackArtifacts(fixture.track, {
    readText: (path) => {
      const value = fixture.artifacts.get(path)
      if (value === undefined) throw new Error(`missing ${path}`)
      return value
    },
  }, undefined, new Date('2026-08-11T00:00:00Z'), fixture.current)
}

test('--check rejects an empty TRX with Total=0', () => {
  const fixture = savedExecutionFixture()
  fixture.artifacts.set(fixture.track.report, '<TestRun><Results></Results></TestRun>')

  const result = evaluateSavedFixture(fixture)

  assert.equal(result.total, 0)
  assert.equal(result.passed, false)
  assert.match(result.reportError ?? '', /TRX test count does not match execution ledger/)
})

test('--check rejects a TRX-ledger outcome mismatch', () => {
  const fixture = savedExecutionFixture()
  fixture.artifacts.set(fixture.track.report, '<TestRun><Results><UnitTestResult testName="Ns.Cli.Fast" outcome="Failed" duration="00:00:00.9000000"/></Results></TestRun>')

  const result = evaluateSavedFixture(fixture)

  assert.equal(result.passed, false)
  assert.match(result.reportError ?? '', /TRX and execution ledger outcome mismatch/)
})

test('--check rejects a missing execution ledger', () => {
  const fixture = savedExecutionFixture()
  fixture.artifacts.delete(fixture.track.executionLedger!)

  const result = evaluateSavedFixture(fixture)

  assert.equal(result.total, 0)
  assert.equal(result.passed, false)
  assert.match(result.reportError ?? '', /missing reports\/cli\.execution-ledger\.json/)
})

test('--check rejects a stale but internally complete artifact bundle', () => {
  const fixture = savedExecutionFixture()
  const staleFixture = {
    ...fixture,
    current: { ...fixture.current, sourceSha256: 'c'.repeat(64) },
  }

  const result = evaluateSavedFixture(staleFixture)

  assert.equal(result.passed, false)
  assert.match(result.reportError ?? '', /saved execution provenance is stale:.*source hash/)
})

test('suite deadline breach remains visible in summary and report errors fail the track', () => {
  const run: TrackRun = {
    trackId: 'slow',
    timedOut: true,
    timeoutReason: 'suite',
    exitCode: null,
    elapsedMs: 1000,
    deadlineMs: 1000,
    command: 'not started: suite deadline',
    reportReady: false,
    reportError: 'report reports/slow.trx was not refreshed because the suite deadline expired',
  }
  const evaluation: TrackEvaluation = {
    trackId: 'slow',
    enforce: true,
    reportError: run.reportError,
    total: 0,
    failedTests: [],
    rules: [],
    passed: false,
  }
  const summary = summarize([run], [evaluation], true, 1250)
  const output = [formatSummary(summary, 1000), ...formatEvaluation(evaluation)].join('\n')

  assert.match(output, /1 failing, 1 timed out/)
  assert.match(output, /suite deadline: 1\.00s BREACHED after 1\.25s/)
  assert.match(output, /REPORT ERROR: report reports\/slow\.trx was not refreshed/)
})

test('parseArgs: focused without any argument leaves the request unresolved', () => {
  const args = parseArgs(['focused'])
  assert.equal(args.mode, 'focused')
  assert.equal(args.focused, undefined)
})

test('parseArgs: focused with only the csproj leaves the request unresolved', () => {
  const args = parseArgs(['focused', 'packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj'])
  assert.equal(args.mode, 'focused')
  assert.equal(args.focused, undefined)
})

test('main: missing focused arguments print usage and return 2 without throwing', async () => {
  const stderr = captureStderr()
  try {
    const code = await main(['focused'])
    assert.equal(code, 2)
    assert.match(stderr.calls(), /usage: guard focused <csproj> <ClassName\.FQN>/)
  } finally {
    stderr.restore()
  }
})

test('main: partially provided focused arguments (csproj only) fail explicitly', async () => {
  const stderr = captureStderr()
  try {
    const code = await main(['focused', 'packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj'])
    assert.equal(code, 2)
    assert.match(stderr.calls(), /usage: guard focused <csproj> <ClassName\.FQN>/)
  } finally {
    stderr.restore()
  }
})

test('main: unreadable csproj fails explicitly with exit 2 instead of an unhandled exception', async () => {
  const stderr = captureStderr()
  try {
    const code = await main(['focused', 'no/such/project/Mohist.Cli.Tests.csproj', 'X.Y.Z'])
    assert.equal(code, 2)
    assert.match(stderr.calls(), /focused run failed/)
  } finally {
    stderr.restore()
  }
})
