import assert from 'node:assert/strict'
import { test } from 'node:test'

import { classify, evaluateRule, evaluateTrack, percentile } from './budget.js'
import type { BudgetRule, TestCase, TrackConfig } from './types.js'

function case_(name: string, durationMs: number, outcome: TestCase['outcome'] = 'passed'): TestCase {
  return { name, durationMs, outcome }
}

test('percentile uses nearest-rank over sorted values', () => {
  assert.equal(percentile([], 95), 0)
  assert.equal(percentile([10], 95), 10)
  assert.equal(percentile([1, 2, 3, 40], 95), 40)
  assert.equal(
    percentile(
      Array.from({ length: 20 }, (_, i) => i + 1),
      90,
    ),
    18,
  )
})

test('classify routes by first matching pattern, default catches the rest', () => {
  const rules: BudgetRule[] = [{ id: 'spec', namePattern: 'Specs\\.' }, { id: 'unit' }]
  const cases = [case_('Ns.UpdateServerSpecs.Foo', 10), case_('Ns.ATests.Bar', 10), case_('Ns.Edge', 10)]
  const buckets = classify(cases, rules)
  assert.deepEqual(
    buckets.get(rules[0])!.map((c) => c.name),
    ['Ns.UpdateServerSpecs.Foo'],
  )
  assert.deepEqual(
    buckets.get(rules[1])!.map((c) => c.name),
    ['Ns.ATests.Bar', 'Ns.Edge'],
  )
})

test('evaluateRule reports maximum duration without making one sample a failure boundary', () => {
  const rule: BudgetRule = { id: 'unit', percentile: 95, percentileMs: 50 }
  const cases = [...Array.from({ length: 19 }, (_, index) => case_(`fast-${index}`, 5)), case_('slow', 120)]
  const d = evaluateRule(rule, cases)
  assert.equal(d.total, 20)
  assert.equal(d.maxMs, 120)
  assert.equal(d.percentiles[95], 5)
  assert.equal(d.percentileViolation, undefined)
})

test('evaluateRule reports a percentile breach', () => {
  const rule: BudgetRule = { id: 'unit', percentile: 95, percentileMs: 5 }
  const cases = Array.from({ length: 20 }, (_, i) => case_(`t${i}`, i + 1))
  const d = evaluateRule(rule, cases)
  assert.equal(d.percentiles[95], 19)
  assert.ok(d.percentileViolation)
  assert.equal(d.percentileViolation!.budgetMs, 5)
  assert.equal(d.percentileViolation!.valueMs, 19)
})

test('evaluateTrack: enforce=false only reports failed tests, no rule evaluation', () => {
  const track: TrackConfig = {
    id: 'pending',
    kind: 'report-only',
    report: 'x',
    reportFormat: 'trx',
    deadlineMs: 1000,
    enforce: false,
    status: 'baseline-pending',
  }
  const evaluation = evaluateTrack(track, [case_('ok', 5), case_('bad', 5, 'failed')])
  assert.equal(evaluation.enforce, false)
  assert.equal(evaluation.passed, false)
  assert.deepEqual(evaluation.failedTests, ['bad'])
  assert.equal(evaluation.rules.length, 0)
})

test('evaluateTrack: enforce=true fails on a percentile breach', () => {
  const rules: BudgetRule[] = [{ id: 'unit', percentile: 95, percentileMs: 50 }]
  const track: TrackConfig = {
    id: 'enforced',
    kind: 'report-only',
    report: 'x',
    reportFormat: 'trx',
    deadlineMs: 1000,
    enforce: true,
    rules,
  }
  const passing = evaluateTrack(track, [
    ...Array.from({ length: 19 }, (_, i) => case_(`fast-${i}`, 5)),
    case_('slow', 120),
  ])
  assert.equal(passing.passed, true)

  const failing = evaluateTrack(track, [case_('slow', 120), case_('also-slow', 200)])
  assert.equal(failing.passed, false)
  assert.equal(failing.rules[0].percentileViolation?.valueMs, 200)
})

test('evaluateTrack: enforce=true fails on a parseable but empty report (0 cases)', () => {
  const track: TrackConfig = {
    id: 'enforced',
    kind: 'report-only',
    report: 'x',
    reportFormat: 'trx',
    deadlineMs: 1000,
    enforce: true,
    rules: [{ id: 'unit', percentile: 95, percentileMs: 50 }],
  }
  const evaluation = evaluateTrack(track, [])
  assert.equal(evaluation.passed, false)
  assert.equal(evaluation.total, 0)
  assert.equal(evaluation.rules[0].total, 0)
})

test('evaluateTrack: baseline-pending still requires a nonzero total', () => {
  const track: TrackConfig = {
    id: 'pending',
    kind: 'report-only',
    report: 'x',
    reportFormat: 'trx',
    deadlineMs: 1000,
    enforce: false,
    status: 'baseline-pending',
  }
  const evaluation = evaluateTrack(track, [])
  assert.equal(evaluation.passed, false)
  assert.equal(evaluation.total, 0)
})

test('evaluateTrack fails every skipped, not-run, and unknown outcome', () => {
  const track: TrackConfig = {
    id: 'enforced',
    kind: 'report-only',
    report: 'x',
    reportFormat: 'trx',
    deadlineMs: 1000,
    enforce: true,
    rules: [{ id: 'unit', percentile: 95, percentileMs: 50 }],
  }
  const evaluation = evaluateTrack(track, [
    case_('skipped', 0, 'skipped'),
    case_('not-run', 0, 'not-run'),
    case_('other', 0, 'other'),
  ])
  assert.equal(evaluation.passed, false)
  assert.deepEqual(evaluation.outcomes, {
    total: 3,
    passed: 0,
    failed: 0,
    errors: 0,
    skipped: 1,
    notRun: 1,
    other: 1,
  })
})

test('model: L0 and L1 populations use their own percentile budgets', () => {
  const specRule: BudgetRule = { id: 'spec', percentile: 95, percentileMs: 500 }
  const unitRule: BudgetRule = { id: 'unit', percentile: 95, percentileMs: 50 }
  const cases = Array.from({ length: 20 }, (_, index) => case_(`case-${index}`, index >= 18 ? 100 : 10))
  assert.equal(evaluateRule(specRule, cases).percentileViolation, undefined)
  assert.equal(evaluateRule(unitRule, cases).percentileViolation?.valueMs, 100)
})
