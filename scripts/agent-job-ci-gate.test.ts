import assert from 'node:assert/strict'
import { test } from 'node:test'

import {
  parseTrxSummary,
  parseXunitSummary,
  runParallelFailFast,
  validateResult,
  validateSummary,
  type RunningCommand,
} from './agent-job-ci-gate.js'

const passedTrx = `<TestRun><Results>
  <UnitTestResult testName="Ns.Observer.One" outcome="Passed" />
  <UnitTestResult testName="Ns.Observer.Two" outcome="Passed" />
</Results></TestRun>`

test('parseTrxSummary reads structured xUnit outcomes', () => {
  assert.deepEqual(parseTrxSummary(passedTrx), {
    total: 2,
    passed: 2,
    failed: 0,
    errors: 0,
    skipped: 0,
    notRun: 0,
    other: 0,
  })
})

test('parseXunitSummary reads the xUnit execution summary counts', () => {
  assert.deepEqual(parseXunitSummary('Total: 20, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 3.740s'), {
    total: 20,
    passed: 20,
    failed: 0,
    errors: 0,
    skipped: 0,
    notRun: 0,
    other: 0,
  })
  assert.throws(() => parseXunitSummary('Total: 20'), /summary is missing/)
})

test('validateSummary rejects zero totals and every non-passing outcome', () => {
  const reasons = validateSummary({ total: 5, passed: 0, failed: 1, errors: 1, skipped: 1, notRun: 1, other: 1 })
  assert.deepEqual(reasons, [
    'Failed: 1',
    'Errors: 1',
    'Skipped: 1',
    'Not Run: 1',
    'unknown outcomes: 1',
    'Passed: 0/5',
  ])
  assert.deepEqual(validateSummary({ total: 0, passed: 0, failed: 0, errors: 0, skipped: 0, notRun: 0, other: 0 }), [
    'Total: 0',
  ])
})

test('validateResult rejects a nonzero process even when its report is absent', () => {
  const validation = validateResult({
    exitCode: 7,
    signal: null,
    elapsedMs: 12,
    reportPath: 'missing-report.trx',
    stdoutPath: 'stdout.log',
    stderrPath: 'stderr.log',
  })
  assert.equal(validation.ok, false)
  assert.deepEqual(validation.reasons, ['exit code: 7', 'missing report: missing-report.trx'])
})

test('parseTrxSummary rejects a missing structured report root', () => {
  assert.throws(() => parseTrxSummary('not a trx report'), /no TestRun root/)
})

test('runParallelFailFast kills the other command on the first failed result', async () => {
  const controls: Array<{ resolve: (value: { ok: boolean }) => void; killed: boolean }> = []
  const pending = runParallelFailFast<string, { ok: boolean }, { ok: boolean }>(
    ['observer', 'grain'],
    (): RunningCommand<{ ok: boolean }> => {
      let resolveResult!: (value: { ok: boolean }) => void
      const control = {
        resolve: (value: { ok: boolean }) => resolveResult(value),
        killed: false,
      }
      controls.push(control)
      return {
        result: new Promise<{ ok: boolean }>((resolve) => {
          resolveResult = resolve
        }),
        kill: () => {
          control.killed = true
          control.resolve({ ok: false })
        },
      }
    },
    (result) => result.ok,
    (_command, result) => result,
  )

  controls[0].resolve({ ok: false })
  const results = await pending
  assert.equal(controls[1].killed, true)
  assert.deepEqual(results, [{ ok: false }, { ok: false }])
})
