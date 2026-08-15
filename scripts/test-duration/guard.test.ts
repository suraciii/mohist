import assert from 'node:assert/strict'
import { mock, test } from 'node:test'

import {
  cleanupDeadlineAt,
  calendarNowFor,
  evaluateTrackAtCalendarDate,
  commandFor,
  createTimeout,
  isLaneSuccessful,
  laneSandbox,
  main,
  parseArgs,
  planTracks,
  prepareReportTarget,
  reportEvaluationFailureReason,
  specPartitionCommand,
} from './guard.js'
import { formatEvaluation, formatSummary, formatTrackRun, summarize } from './diagnostics.js'
import type { TrackConfig, TrackEvaluation, TrackRun } from './types.js'

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
  assert.deepEqual(command.args.slice(-7), ['-noColor', '-noLogo', '-noAutoReporters', '-trx', `${process.cwd()}/reports/unit.trx`, '-parallel', 'none'])
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
    assert.deepEqual(command.args.slice(-3), ['--', '--reporter=json', '--outputFile=/evidence/reports/' + track.id + '.json'])
  }

  const workflow: TrackConfig = {
    id: 'workflow-def',
    kind: 'dotnet-vstest',
    csproj: 'packages/server/tests/Mohist.Workflow.Definition.Tests/Mohist.Workflow.Definition.Tests.csproj',
    report: 'reports/workflow-def.trx',
    reportFormat: 'trx',
    deadlineMs: 1000,
    enforce: true,
    rules: [{ id: 'unit', absoluteMs: 500 }],
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
    mkdir: (directory) => { calls.push(`mkdir:${directory}`) },
    unlink: (path) => { calls.push(`unlink:${path}`) },
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
  const serverSpec = laneSandbox('/evidence', 'server-spec-0', { PATH: '/bin' }, 2, true)

  assert.equal(web.environment.TMPDIR, web.tempDir)
  assert.equal(web.environment.TEMP, web.tempDir)
  assert.equal(web.environment.TMP, web.tempDir)
  assert.equal(web.environment.XDG_RUNTIME_DIR, web.ipcDir)
  assert.equal(web.environment.HOME, web.homeDir)
  assert.equal(web.environment.USERPROFILE, web.homeDir)
  assert.equal(web.environment.MOHIST_TEST_LANE, 'web')
  assert.equal(web.environment.MOHIST_DB_PATH, undefined)
  assert.equal(web.environment.MOHIST__Otel__Port, undefined)
  assert.equal(serverSpec.environment.MOHIST_DB_PATH, serverSpec.databasePath)
  assert.equal(serverSpec.environment.MOHIST_OTEL_DB_PATH, serverSpec.otelDatabasePath)
  assert.equal(serverSpec.environment.MOHIST__Otel__DbPath, serverSpec.otelDatabasePath)
  assert.equal(serverSpec.environment.MOHIST__Otel__BindHost, '127.0.0.1')
  assert.equal(serverSpec.environment.MOHIST__Otel__Port, String(serverSpec.otelPort))
  assert.equal(serverSpec.environment.MOHIST__Otel__Endpoint, `http://127.0.0.1:${serverSpec.otelPort}/otel`)
  assert.equal(serverSpec.environment.OTEL_EXPORTER_OTLP_ENDPOINT, `http://127.0.0.1:${serverSpec.otelPort}`)
  assert.notEqual(web.tempDir, runner.tempDir)
  assert.notEqual(web.ipcDir, runner.ipcDir)
  assert.notEqual(web.homeDir, runner.homeDir)
  assert.notEqual(web.databasePath, serverSpec.databasePath)
  assert.notEqual(web.otelDatabasePath, serverSpec.otelDatabasePath)
  assert.notEqual(web.otelPort, serverSpec.otelPort)
  assert.match(serverSpec.databasePath, /^\/evidence\/tmp\/server-spec-0\/mohist\/mohist\.db$/)
  assert.match(serverSpec.otelDatabasePath, /^\/evidence\/tmp\/server-spec-0\/mohist\/otel\.db$/)
  assert.match(web.homeDir, /^\/evidence\/tmp\/web\/home$/)
})

test('budget calendar policy receives its injected wall-calendar source, never the monotonic duration clock', () => {
  const calendar = new Date('2026-08-09T00:00:00.000Z')
  const calendarNow = () => calendar

  const track: TrackConfig = {
    id: 'calendar-policy',
    kind: 'report-only',
    report: 'unused.trx',
    reportFormat: 'trx',
    deadlineMs: 1000,
    enforce: true,
    rules: [{
      id: 'unit',
      absoluteMs: 50,
      allowlist: [{ id: 'slow', observedMs: 100, reason: 'fixture', owner: 'test', deadline: '2026-01-01' }],
    }],
  }

  assert.equal(calendarNowFor({ calendarNow })(), calendar)
  const evaluation = evaluateTrackAtCalendarDate(
    track,
    [{ name: 'slow', durationMs: 100, outcome: 'passed' }],
    { calendarNow },
  )
  assert.equal(evaluation.passed, false)
  assert.equal(evaluation.rules[0].expiredAllowlist.length, 1)
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

test('parseArgs accepts an internal run root and the canonical absolute deadline', () => {
  const args = parseArgs(['--all', '--run-root', '/tmp/mohist-canonical-gate/run-1', '--suite-deadline-at-ms=301000', '--require-build-stamp'])
  assert.equal(args.all, true)
  assert.equal(args.runRoot, '/tmp/mohist-canonical-gate/run-1')
  assert.equal(args.suiteDeadlineAtMs, 301000)
  assert.equal(args.requireBuildStamp, true)
})

test('guard derives cancellation cleanup from the injected time value and never extends the hard deadline', () => {
  const hardDeadlineAt = 30_000
  assert.equal(cleanupDeadlineAt(10_000, hardDeadlineAt, 5_000), 20_000)
  assert.equal(cleanupDeadlineAt(27_000, hardDeadlineAt, 5_000), hardDeadlineAt)
})

test('report evaluation fails closed at the execution cutoff and after external termination', () => {
  const deadlines = { hardDeadlineAt: 301_000, executionDeadlineAt: 290_000 }

  assert.equal(reportEvaluationFailureReason(289_999, deadlines, false), undefined)
  assert.equal(reportEvaluationFailureReason(290_000, deadlines, false), 'suite execution cutoff reached before report evaluation')
  assert.equal(
    reportEvaluationFailureReason(1_000, deadlines, true),
    'external termination stopped report evaluation before the canonical cleanup wall',
  )
})

test('Spec partition lanes launch a Node-hosted executor instead of a shell script', () => {
  const command = specPartitionCommand(['run', '/tests/spec', '0', '4', '/tmp/manifests', '/tmp/report.trx'])
  assert.equal(command.command, process.execPath)
  assert.deepEqual(command.args.slice(0, 4), ['--import', 'tsx', `${process.cwd()}/scripts/test-duration/spec-partition.ts`, 'run'])
  assert.doesNotMatch(command.args.join(' '), /ci-spec-partition\.sh/)
})

test('planTracks gives every Server Spec partition its own report, temp, and port claim', () => {
  const spec: TrackConfig = {
    id: 'server-spec',
    kind: 'dotnet-apphost',
    apphost: 'bin/spec',
    report: 'reports/server-spec/partition-{partition}.trx',
    reportFormat: 'trx',
    partitions: 4,
    deadlineMs: 1000,
    enforce: true,
    rules: [{ id: 'spec', absoluteMs: 5000 }],
  }
  const planned = planTracks([spec], '/evidence')
  assert.deepEqual(planned.map((lane) => lane.lane.id), [
    'server-spec-0', 'server-spec-1', 'server-spec-2', 'server-spec-3', 'server-spec-coverage',
  ])
  assert.deepEqual(planned.slice(0, 4).map((lane) => lane.reportPath), [
    '/evidence/reports/server-spec/partition-0.trx',
    '/evidence/reports/server-spec/partition-1.trx',
    '/evidence/reports/server-spec/partition-2.trx',
    '/evidence/reports/server-spec/partition-3.trx',
  ])
  assert.deepEqual(planned[3].lane.resources?.slice(-3), ['spec-report-3', 'spec-temp-3', 'spec-port-3'])
  assert.deepEqual(planned[4].lane.dependsOn, ['server-spec-0', 'server-spec-1', 'server-spec-2', 'server-spec-3'])
  assert.deepEqual(planned.map((lane) => lane.sandboxOrdinal), [0, 1, 2, 3, 4])
})

test('planTracks isolates configured duration measurements before bounded throughput fan-out', () => {
  const cli: TrackConfig = {
    id: 'cli', kind: 'dotnet-apphost', apphost: 'bin/cli', report: 'reports/cli.trx', reportFormat: 'trx', deadlineMs: 1000, enforce: false,
  }
  const unit: TrackConfig = {
    id: 'server-unit', kind: 'dotnet-apphost', apphost: 'bin/unit', report: 'reports/unit.trx', reportFormat: 'trx', deadlineMs: 1000, enforce: false,
  }
  const web: TrackConfig = {
    id: 'web', kind: 'vitest', run: ['npm', 'run', 'test'], report: 'reports/web.json', reportFormat: 'vitest', deadlineMs: 1000, enforce: false,
  }
  const spec: TrackConfig = {
    id: 'server-spec', kind: 'dotnet-apphost', apphost: 'bin/spec', report: 'reports/spec-{partition}.trx', reportFormat: 'trx', partitions: 2, deadlineMs: 1000, enforce: false,
  }
  const planned = planTracks([cli, unit, web, spec], '/evidence', ['cli', 'server-unit'])
  const byId = new Map(planned.map((plan) => [plan.lane.id, plan.lane]))

  assert.deepEqual(byId.get('cli')?.dependsOn, undefined)
  assert.ok(byId.get('cli')?.resources?.includes('duration-measurement'))
  assert.deepEqual(byId.get('server-unit')?.dependsOn, ['cli'])
  assert.ok(byId.get('server-unit')?.resources?.includes('duration-measurement'))
  assert.deepEqual(byId.get('web')?.dependsOn, ['server-unit'])
  assert.deepEqual(byId.get('server-spec-0')?.dependsOn, ['server-unit'])
  assert.deepEqual(byId.get('server-spec-coverage')?.dependsOn, ['server-spec-0', 'server-spec-1', 'server-unit'])

  const focused = planTracks([unit], '/evidence', ['cli', 'server-unit'])
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
