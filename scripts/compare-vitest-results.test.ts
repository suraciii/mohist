import assert from 'node:assert/strict'
import { test } from 'node:test'
import { compareVitestResults } from './compare-vitest-results.js'

function report(assertions: Array<string | { fullName: string; status: string }>, status = 'passed') {
  return {
    testResults: [{
      name: 'example.spec.ts',
      status,
      assertionResults: assertions.map((assertion) => {
        const value = typeof assertion === 'string' ? { fullName: assertion, status: 'passed' } : assertion
        return { fullName: value.fullName, status: value.status }
      }),
    }],
  }
}

test('retains baseline identities and permits additions', () => {
  const summary = compareVitestResults(
    report(['alpha', 'beta']),
    report(['alpha', 'beta', 'gamma']),
  )

  assert.equal(summary.unchangedAssertions, 2)
  assert.equal(summary.additions, 1)
})

test('requires declared renames and removals', () => {
  assert.throws(
    () => compareVitestResults(report(['old']), report(['new'])),
    /Missing baseline test identities/,
  )

  const renamed = compareVitestResults(
    report(['old']),
    report(['new']),
    { renames: [{ from: 'old', to: 'new' }], removals: [] },
  )
  assert.equal(renamed.renames, 1)

  const removed = compareVitestResults(
    report(['obsolete']),
    { testResults: [] },
    { renames: [], removals: [{ fullName: 'obsolete', reason: 'duplicate coverage' }] },
  )
  assert.equal(removed.removals, 1)
})

test('rejects non-passing after reports', () => {
  assert.throws(
    () => compareVitestResults(report(['failed'], 'failed'), report(['failed'], 'failed')),
    /After report contains non-passed results/,
  )
})
