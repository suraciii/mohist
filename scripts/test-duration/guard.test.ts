import assert from 'node:assert/strict'
import { mock, test } from 'node:test'

import {
  cleanupDeadlineAt,
  commandFor,
  createTimeout,
  DEFAULT_XUNIT_PARALLELISM,
  evaluateTrackArtifacts,
  isLaneSuccessful,
  laneSandbox,
  main,
  parseArgs,
  parallelismFor,
  planTracks,
  prepareReportTarget,
  reportEvaluationFailureReason,
  writeExecutionProvenance,
} from './guard.js'
import { formatEvaluation, formatGuardOutput, formatSummary, formatTrackRun, summarize } from './diagnostics.js'
import { manifestFromDiscovery, serializeExecutionProvenance } from './execution-ledger.js'
import type {
  CurrentExecutionIdentity,
  ExecutionLedgerExpectation,
  TrackConfig,
  TrackEvaluation,
  TrackRun,
} from './types.js'

const fastCaseUid = '1'.repeat(64)

function fastManifest() {
  return manifestFromDiscovery(
    JSON.stringify([{ ID: fastCaseUid, DisplayName: 'Ns.Cli.Fast', Class: 'Ns.Cli', Method: 'Fast' }]),
  )
}

function captureStderr(): { calls: () => string; restore: () => void } {
  const stderrMock = mock.method(process.stderr, 'write', () => true)
  return {
    calls: () => stderrMock.mock.calls.map((c) => String(c.arguments[0])).join(''),
    restore: () => stderrMock.mock.restore(),
  }
}

test('parseArgs: focused with both arguments resolves the request', () => {
  const args = parseArgs([
    'focused',
    'packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj',
    'Mohist.Cli.Tests.Skills.SkillsContentTests',
  ])
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
  assert.deepEqual(command.args.slice(-7), [
    '-noColor',
    '-noLogo',
    '-noAutoReporters',
    '-trx',
    `${process.cwd()}/reports/unit.trx`,
    '-parallel',
    'none',
  ])
})

test('commandFor explicitly selects the execution-ledger reporter without environmental auto-reporters', () => {
  const track: TrackConfig = {
    id: 'cli',
    kind: 'dotnet-apphost',
    csproj: 'packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj',
    report: 'reports/cli.trx',
    executionLedger: 'reports/cli.execution-ledger.json',
    executionProvenance: 'reports/cli.execution-provenance.json',
    executionSourceRoots: ['packages/cli'],
    reportFormat: 'trx',
    deadlineMs: 1000,
    enforce: true,
  }

  const command = commandFor(track, '/evidence')

  assert.deepEqual(command.args.slice(0, 7), [
    '-noColor',
    '-noLogo',
    '-noAutoReporters',
    '-reporter',
    'mohist-ledger',
    '-trx',
    '/evidence/reports/cli.trx',
  ])
})

test('parallelism provenance records every effective xUnit override', () => {
  const track: TrackConfig = {
    id: 'cli',
    kind: 'dotnet-apphost',
    report: 'reports/cli.trx',
    reportFormat: 'trx',
    deadlineMs: 1000,
    enforce: false,
  }
  assert.equal(parallelismFor(track), DEFAULT_XUNIT_PARALLELISM)
  assert.equal(
    parallelismFor({
      ...track,
      apphostArgs: ['-parallel', 'collections', '-parallelAlgorithm', 'conservative', '-maxThreads', '8'],
    }),
    'xunit-v3:parallel=collections;parallelAlgorithm=conservative;maxThreads=8',
  )
  assert.throws(
    () => parallelismFor({ ...track, apphostArgs: ['-parallel', 'none', '-parallel', 'collections'] }),
    /duplicate xUnit option/,
  )
})

test('execution provenance writer uses the injected artifact store', () => {
  const calls: string[] = []
  writeExecutionProvenance(
    '/virtual/reports/cli.execution-provenance.json',
    {
      runId: 'run-1',
      manifest: fastManifest(),
      assemblyPath: '/virtual/Mohist.Cli.Tests.dll',
      assemblySha256: 'a'.repeat(64),
      sourceSha256: 'b'.repeat(64),
      parallelism: DEFAULT_XUNIT_PARALLELISM,
    },
    {
      ensureDirectory: (path) => calls.push(`mkdir:${path}`),
      writeText: (path, content) => calls.push(`write:${path}:${JSON.parse(content).runId}`),
    },
  )
  assert.deepEqual(calls, ['mkdir:/virtual/reports', 'write:/virtual/reports/cli.execution-provenance.json:run-1'])
})

test('guard evaluates CLI duration from the execution ledger and fails closed when evidence is missing', () => {
  const manifest = fastManifest()
  const expected: ExecutionLedgerExpectation = {
    runId: 'run-1',
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
    rules: [{ id: 'unit', percentile: 95, percentileMs: 50 }],
  }
  const artifacts = new Map<string, string>([
    [
      track.report,
      '<TestRun><Results><UnitTestResult testName="Ns.Cli.Fast" outcome="Passed" duration="00:00:00.9000000"/></Results></TestRun>',
    ],
    [track.executionProvenance!, serializeExecutionProvenance(expected)],
    [
      track.executionLedger!,
      JSON.stringify({
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
        cases: [
          {
            uid: 'runtime-uid',
            testCaseUid: fastCaseUid,
            name: 'Ns.Cli.Fast',
            className: 'Ns.Cli',
            collectionName: 'Ns.Cli collection',
            outcome: 'passed',
            executionTimeSeconds: 0.01,
            startTime: '2026-08-12T00:00:00Z',
            finishTime: '2026-08-12T00:00:01Z',
          },
        ],
      }),
    ],
  ])
  const reader = {
    readText: (path: string) => {
      const value = artifacts.get(path)
      if (value === undefined) throw new Error(`missing ${path}`)
      return value
    },
  }

  const evaluation = evaluateTrackArtifacts(track, reader, {
    trackId: 'cli',
    timedOut: false,
    exitCode: 0,
    elapsedMs: 1,
    deadlineMs: 60_000,
    command: 'cli',
    reportReady: true,
    cleanupComplete: true,
    executionLedgerReady: true,
    executionLedgerExpectation: expected,
  })
  assert.equal(evaluation.passed, true)
  assert.equal(evaluation.rules[0].maxMs, 10)

  artifacts.delete(track.executionLedger!)
  const missing = evaluateTrackArtifacts(track, reader, undefined, expected)
  assert.equal(missing.passed, false)
  assert.match(missing.reportError ?? '', /missing reports\/cli\.execution-ledger\.json/)
})

function savedExecutionFixture(): {
  readonly track: TrackConfig
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
    rules: [{ id: 'unit' }],
  }
  return {
    track,
    current: {
      manifest,
      assemblyPath: expected.assemblyPath,
      assemblySha256: expected.assemblySha256,
      sourceSha256: expected.sourceSha256,
      parallelism: expected.parallelism,
    },
    artifacts: new Map([
      [
        track.report,
        '<TestRun><Results><UnitTestResult testName="Ns.Cli.Fast" outcome="Passed" duration="00:00:00.9000000"/></Results></TestRun>',
      ],
      [track.executionProvenance!, serializeExecutionProvenance(expected)],
      [
        track.executionLedger!,
        JSON.stringify({
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
          cases: [
            {
              uid: 'runtime-uid',
              testCaseUid: fastCaseUid,
              name: 'Ns.Cli.Fast',
              className: 'Ns.Cli',
              collectionName: 'Ns.Cli collection',
              outcome: 'passed',
              executionTimeSeconds: 0.01,
              startTime: '2026-08-12T00:00:00Z',
              finishTime: '2026-08-12T00:00:01Z',
            },
          ],
        }),
      ],
    ]),
  }
}

function evaluateSavedFixture(fixture: ReturnType<typeof savedExecutionFixture>): TrackEvaluation {
  return evaluateTrackArtifacts(
    fixture.track,
    {
      readText: (path) => {
        const value = fixture.artifacts.get(path)
        if (value === undefined) throw new Error(`missing ${path}`)
        return value
      },
    },
    undefined,
    fixture.current,
  )
}

test('--check rejects an empty TRX instead of treating Total=0 as green', () => {
  const fixture = savedExecutionFixture()
  fixture.artifacts.set(fixture.track.report, '<TestRun><Results></Results></TestRun>')
  const result = evaluateSavedFixture(fixture)
  assert.equal(result.total, 0)
  assert.equal(result.passed, false)
  assert.match(result.reportError ?? '', /TRX test count does not match execution ledger/)
})

test('--check rejects a TRX-ledger outcome mismatch', () => {
  const fixture = savedExecutionFixture()
  fixture.artifacts.set(
    fixture.track.report,
    '<TestRun><Results><UnitTestResult testName="Ns.Cli.Fast" outcome="Failed" duration="00:00:00.9000000"/></Results></TestRun>',
  )
  const result = evaluateSavedFixture(fixture)
  assert.equal(result.passed, false)
  assert.match(result.reportError ?? '', /TRX and execution ledger outcome mismatch/)
})

test('--check rejects a stale but internally complete artifact bundle', () => {
  const fixture = savedExecutionFixture()
  const result = evaluateSavedFixture({
    ...fixture,
    current: { ...fixture.current, sourceSha256: 'c'.repeat(64) },
  })
  assert.equal(result.passed, false)
  assert.match(result.reportError ?? '', /saved execution provenance is stale:.*source hash/)
})

test('canonical lane commands keep reporter arguments on the final test process and reuse the built workflow graph', () => {
  const nodeTracks: readonly TrackConfig[] = [
    ['mohist-slack', 'packages/mohist-slack', 'test:duration'],
    ['runner', 'packages/runner', 'test:duration'],
    ['web', 'packages/web', 'test:duration'],
    ['runner-integration', 'packages/runner', 'test:duration:integration'],
  ].map(([id, workspace, script]) => ({
    id,
    kind: 'vitest',
    run: ['npm', 'run', script, '-w', workspace, '--', '--reporter=json', '--outputFile={report}'],
    report: `reports/${id}.json`,
    reportFormat: 'vitest',
    deadlineMs: 1000,
    enforce: false,
    status: 'baseline-pending',
    reason: 'fixture',
  }))

  for (const track of nodeTracks) {
    const command = commandFor(track, '/evidence')
    assert.equal(command.command, 'npm')
    assert.deepEqual(command.args.slice(-3), [
      '--',
      '--reporter=json',
      '--outputFile=/evidence/reports/' + track.id + '.json',
    ])
  }

  const workflow: TrackConfig = {
    id: 'workflow-def',
    kind: 'dotnet-vstest',
    csproj: 'packages/server/tests/Mohist.Workflow.Definition.Tests/Mohist.Workflow.Definition.Tests.csproj',
    report: 'reports/workflow-def.trx',
    reportFormat: 'trx',
    deadlineMs: 1000,
    enforce: true,
    rules: [{ id: 'unit' }],
  }
  const workflowCommand = commandFor(workflow, '/evidence')
  assert.equal(workflowCommand.command, 'dotnet')
  assert.ok(workflowCommand.args.includes('--no-build'))
  assert.ok(workflowCommand.args.includes('--no-restore'))
  assert.ok(workflowCommand.args.includes('--results-directory'))
})

test('prepareReportTarget creates the report parent and removes stale output before lane start', () => {
  const calls: string[] = []
  prepareReportTarget('/evidence/reports/lane.trx', {
    mkdir: (directory) => {
      calls.push(`mkdir:${directory}`)
    },
    unlink: (path) => {
      calls.push(`unlink:${path}`)
    },
  })
  assert.deepEqual(calls, ['mkdir:/evidence/reports', 'unlink:/evidence/reports/lane.trx'])

  prepareReportTarget('/evidence/reports/absent.trx', {
    mkdir: () => {},
    unlink: () => {
      throw Object.assign(new Error('absent'), { code: 'ENOENT' })
    },
  })
})

test('lane sandbox gives every lane distinct temporary resources and isolates server runtime only when requested', () => {
  const web = laneSandbox('/evidence', 'web', { PATH: '/bin' }, 0)
  const runner = laneSandbox('/evidence', 'runner', { PATH: '/bin' }, 1)
  const serverSpec = laneSandbox('/evidence', 'server-spec', { PATH: '/bin' }, 2, true)

  assert.equal(web.environment.TMPDIR, web.tempDir)
  assert.equal(web.environment.TEMP, web.tempDir)
  assert.equal(web.environment.TMP, web.tempDir)
  assert.equal(web.environment.XDG_RUNTIME_DIR, web.ipcDir)
  assert.equal(web.environment.HOME, web.homeDir)
  assert.equal(web.environment.USERPROFILE, web.homeDir)
  assert.equal(web.environment.MOHIST_TEST_LANE, 'web')
  assert.equal(web.environment.Logging__LogLevel__Default, 'Warning')
  assert.equal(web.environment.DOTNET_NOLOGO, 'true')
  assert.equal(web.environment.DOTNET_CLI_TELEMETRY_OPTOUT, 'true')
  assert.equal(web.environment.DOTNET_GENERATE_ASPNET_CERTIFICATE, 'false')
  assert.equal(web.environment.MOHIST_DB_PATH, undefined)
  assert.equal(web.environment.MOHIST__Otel__Port, undefined)
  assert.equal(serverSpec.environment.MOHIST_DB_PATH, serverSpec.databasePath)
  assert.equal(serverSpec.environment.MOHIST_OTEL_DB_PATH, serverSpec.otelDatabasePath)
  assert.equal(serverSpec.environment.MOHIST__Otel__DbPath, serverSpec.otelDatabasePath)
  assert.equal(serverSpec.environment.MOHIST__Otel__BindHost, 'localhost')
  assert.equal(serverSpec.environment.MOHIST__Otel__Port, '0')
  assert.equal(serverSpec.environment.MOHIST__Otel__Endpoint, 'http://localhost/otel')
  assert.equal(serverSpec.environment.OTEL_EXPORTER_OTLP_ENDPOINT, 'http://localhost')
  assert.notEqual(web.tempDir, runner.tempDir)
  assert.notEqual(web.ipcDir, runner.ipcDir)
  assert.notEqual(web.homeDir, runner.homeDir)
  assert.notEqual(web.databasePath, serverSpec.databasePath)
  assert.notEqual(web.otelDatabasePath, serverSpec.otelDatabasePath)
  assert.match(serverSpec.databasePath, /^\/evidence\/tmp\/server-spec\/mohist\/mohist\.db$/)
  assert.match(serverSpec.otelDatabasePath, /^\/evidence\/tmp\/server-spec\/mohist\/otel\.db$/)
  assert.match(web.homeDir, /^\/evidence\/tmp\/web\/home$/)

  const verbose = laneSandbox('/evidence', 'server-spec', { Logging__LogLevel__Default: 'Debug' })
  assert.equal(verbose.environment.Logging__LogLevel__Default, 'Debug')
})

test('a completed lane without its report fails fast while cancelled lanes remain distinct in the summary', () => {
  const missingReport: TrackRun = {
    trackId: 'mohist-slack',
    policyTrackId: 'mohist-slack',
    timedOut: false,
    exitCode: 0,
    elapsedMs: 10,
    deadlineMs: 1000,
    command: 'vitest run',
    reportReady: false,
    cleanupComplete: true,
  }
  const cancelled: TrackRun = {
    trackId: 'runner',
    policyTrackId: 'runner',
    cancelled: true,
    cancellationReason: 'after mohist-slack failed',
    timedOut: false,
    exitCode: null,
    elapsedMs: 20,
    deadlineMs: 1000,
    command: 'npm run test:duration',
    reportReady: false,
    cleanupComplete: true,
  }
  const failedEvaluation = (trackId: string): TrackEvaluation => ({
    trackId,
    enforce: true,
    total: 0,
    outcomes: { total: 0, passed: 0, failed: 0, errors: 0, skipped: 0, notRun: 0, other: 0 },
    failedTests: [],
    rules: [],
    passed: false,
  })

  assert.equal(isLaneSuccessful(missingReport), false)
  assert.equal(isLaneSuccessful({ ...missingReport, reportReady: true }), true)
  assert.equal(isLaneSuccessful({ ...missingReport, reportReady: true, cleanupComplete: false }), false)
  const summary = summarize([missingReport, cancelled], [failedEvaluation('mohist-slack'), failedEvaluation('runner')])
  assert.equal(summary.failedTracks, 1)
  assert.equal(summary.cancelledTracks, 1)
  assert.match(formatTrackRun(missingReport), /\[exit 0\].*\[report missing\/stale\]/)
  assert.match(formatTrackRun(cancelled), /\[CANCELLED after mohist-slack failed \(exit null\)\]/)
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
    cleanupComplete: true,
    reportError: 'report reports/slow.trx was not refreshed because the suite deadline expired',
  }
  const evaluation: TrackEvaluation = {
    trackId: 'slow',
    enforce: true,
    reportError: run.reportError,
    total: 0,
    outcomes: { total: 0, passed: 0, failed: 0, errors: 0, skipped: 0, notRun: 0, other: 0 },
    failedTests: [],
    rules: [],
    passed: false,
  }
  const summary = summarize([run], [evaluation], true, 1250)
  const output = [formatSummary(summary, 1000), ...formatEvaluation(evaluation)].join('\n')

  assert.match(output, /1 failing, 0 cancelled, 1 timed out/)
  assert.match(output, /suite deadline: 1\.00s BREACHED after 1\.25s/)
  assert.match(output, /REPORT ERROR: report reports\/slow\.trx was not refreshed/)
})

test('successful guard adds no output beyond the test framework reporter', () => {
  const evaluation: TrackEvaluation = {
    trackId: 'unit',
    enforce: true,
    total: 3,
    outcomes: { total: 3, passed: 3, failed: 0, errors: 0, skipped: 0, notRun: 0, other: 0 },
    failedTests: [],
    rules: [],
    passed: true,
  }
  const summary = summarize([], [evaluation], false, 1250)

  const output = formatGuardOutput({
    passed: true,
    summary,
    suiteDeadlineMs: 300_000,
    failedRuns: [],
    failedEvaluations: [],
  })

  assert.equal(output, '')
})

test('failed guard output reports failed work without captured process logs', () => {
  const run: TrackRun = {
    trackId: 'unit',
    timedOut: false,
    exitCode: 1,
    elapsedMs: 20,
    deadlineMs: 1000,
    command: 'unit-tests',
    reportReady: true,
    cleanupComplete: true,
  }
  const evaluation: TrackEvaluation = {
    trackId: 'unit',
    enforce: true,
    total: 1,
    outcomes: { total: 1, passed: 0, failed: 1, errors: 0, skipped: 0, notRun: 0, other: 0 },
    failedTests: ['Ns.Unit.fails'],
    rules: [],
    passed: false,
  }
  const summary = summarize([run], [evaluation], false, 20)
  const output = formatGuardOutput({
    passed: false,
    summary,
    suiteDeadlineMs: 300_000,
    failedRuns: [run],
    failedEvaluations: [evaluation],
  })

  assert.match(output, /^FAIL 1 of 1 track failed/)
  assert.match(output, /FAILED TEST: Ns\.Unit\.fails/)
  assert.doesNotMatch(output, /stdout|stderr|\.log/)
  assert.doesNotMatch(output, /runs:|budget:/)
})

test('parseArgs accepts an internal run root and the canonical absolute deadline', () => {
  const args = parseArgs([
    '--all',
    '--run-root',
    '/tmp/mohist-canonical-gate/run-1',
    '--suite-deadline-at-ms=301000',
    '--require-build-stamp',
    '--require-enforced',
  ])
  assert.equal(args.all, true)
  assert.equal(args.runRoot, '/tmp/mohist-canonical-gate/run-1')
  assert.equal(args.suiteDeadlineAtMs, 301000)
  assert.equal(args.requireBuildStamp, true)
  assert.equal(args.requireEnforced, true)
})

test('parseArgs accepts a closed application or repository scope', () => {
  const application = parseArgs(['--application', 'server'])
  assert.equal(application.application, 'server')
  assert.equal(application.repository, false)

  const repository = parseArgs(['--repository'])
  assert.equal(repository.repository, true)
  assert.equal(repository.application, undefined)
})

test('guard derives cancellation cleanup from the injected time value and never extends the hard deadline', () => {
  const hardDeadlineAt = 30_000
  assert.equal(cleanupDeadlineAt(10_000, hardDeadlineAt, 5_000), 20_000)
  assert.equal(cleanupDeadlineAt(27_000, hardDeadlineAt, 5_000), hardDeadlineAt)
})

test('report evaluation fails closed at the execution cutoff and after external termination', () => {
  const deadlines = { hardDeadlineAt: 301_000, executionDeadlineAt: 290_000 }

  assert.equal(reportEvaluationFailureReason(289_999, deadlines, false), undefined)
  assert.equal(
    reportEvaluationFailureReason(290_000, deadlines, false),
    'suite execution cutoff reached before report evaluation',
  )
  assert.equal(
    reportEvaluationFailureReason(1_000, deadlines, true),
    'external termination stopped report evaluation before the canonical cleanup wall',
  )
})

test('planTracks isolates the bounded Spec duration phase before remaining fan-out', () => {
  const cli: TrackConfig = {
    id: 'cli',
    kind: 'dotnet-apphost',
    apphost: 'bin/cli',
    report: 'reports/cli.trx',
    reportFormat: 'trx',
    deadlineMs: 1000,
    enforce: false,
  }
  const unit: TrackConfig = {
    id: 'server-unit',
    kind: 'dotnet-apphost',
    apphost: 'bin/unit',
    report: 'reports/unit.trx',
    reportFormat: 'trx',
    deadlineMs: 1000,
    enforce: false,
  }
  const runner: TrackConfig = {
    id: 'runner',
    kind: 'vitest',
    run: ['npm', 'run', 'test'],
    report: 'reports/runner.json',
    reportFormat: 'vitest',
    deadlineMs: 1000,
    enforce: false,
  }
  const web: TrackConfig = {
    id: 'web',
    kind: 'vitest',
    run: ['npm', 'run', 'test'],
    report: 'reports/web.json',
    reportFormat: 'vitest',
    deadlineMs: 1000,
    enforce: false,
  }
  const spec: TrackConfig = {
    id: 'server-spec',
    kind: 'dotnet-apphost',
    apphost: 'bin/spec',
    apphostArgs: ['-parallel', 'collections', '-parallelAlgorithm', 'conservative', '-maxThreads', '2'],
    report: 'reports/spec.trx',
    reportFormat: 'trx',
    deadlineMs: 1000,
    enforce: false,
  }
  const planned = planTracks([cli, unit, runner, web, spec], '/evidence', ['cli', 'server-spec'], 'runner')
  const byId = new Map(planned.map((plan) => [plan.lane.id, plan.lane]))

  assert.deepEqual(byId.get('cli')?.dependsOn, undefined)
  assert.ok(byId.get('cli')?.resources?.includes('duration-measurement'))
  assert.deepEqual(byId.get('server-unit')?.dependsOn, ['server-spec'])
  assert.ok(!byId.get('server-unit')?.resources?.includes('duration-measurement'))
  assert.deepEqual(byId.get('runner')?.dependsOn, ['server-spec'])
  assert.ok(byId.get('runner')?.resources?.includes('duration-measurement'))
  assert.deepEqual(byId.get('web')?.dependsOn, ['runner'])
  assert.deepEqual(byId.get('server-spec')?.dependsOn, ['cli'])
  assert.ok(byId.get('server-spec')?.resources?.includes('duration-measurement'))
  assert.ok(byId.get('server-spec')?.resources?.includes('server-spec'))

  const focused = planTracks([unit], '/evidence', ['cli', 'server-spec'], 'runner')
  assert.deepEqual(focused[0].lane.dependsOn, undefined)
  assert.ok(!focused[0].lane.resources?.includes('duration-measurement'))
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
