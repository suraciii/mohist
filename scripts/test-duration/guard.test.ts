import assert from 'node:assert/strict'
import { mock, test } from 'node:test'

import { commandFor, createTimeout, main, parseArgs } from './guard.js'
import { formatEvaluation, formatSummary, summarize } from './diagnostics.js'
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
  assert.deepEqual(command.args.slice(-6), ['-noColor', '-noLogo', '-trx', `${process.cwd()}/reports/unit.trx`, '-parallel', 'none'])
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
