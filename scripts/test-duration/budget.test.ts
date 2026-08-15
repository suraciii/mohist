import assert from 'node:assert/strict'
import { test } from 'node:test'

import { classify, evaluateRule, evaluateTrack, percentile } from './budget.js'
import type { AllowlistEntry, BudgetRule, TestCase, TrackConfig } from './types.js'

const TODAY = new Date('2026-08-05')
const FUTURE = '2026-11-30'
const PAST = '2026-01-01'

function case_(name: string, durationMs: number, outcome: TestCase['outcome'] = 'passed'): TestCase {
  return { name, durationMs, outcome }
}

function entry(over: Partial<AllowlistEntry>): AllowlistEntry {
  return { observedMs: 100, reason: 'r', owner: 'team', deadline: FUTURE, ...over }
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
  const rules: BudgetRule[] = [
    { id: 'spec', namePattern: 'Specs\\.', absoluteMs: 500 },
    { id: 'unit', absoluteMs: 50 },
  ]
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

test('evaluateRule: over-budget not allowlisted fails, allowlisted is governed', () => {
  const rule: BudgetRule = { id: 'unit', absoluteMs: 50, allowlist: [entry({ id: 'governed' })] }
  const cases = [case_('governed', 120), case_('wild', 80), case_('fine', 5), case_('skipped-but-slow', 200, 'skipped')]
  const d = evaluateRule(rule, cases, TODAY)
  assert.deepEqual(
    d.absoluteViolations.map((v) => v.name),
    ['wild'],
  )
  assert.deepEqual(
    d.governed.map((g) => g.name),
    ['governed'],
  )
  assert.equal(d.governed[0].reason, 'r')
  assert.equal(d.governed[0].observedMs, 100)
  assert.equal(d.total, 4)
  assert.equal(d.maxMs, 120)
  assert.equal(
    d.absoluteViolations.some((v) => v.name === 'skipped-but-slow'),
    false,
  )
})

test('evaluateRule flags stale allowlist entries that matched no test', () => {
  const rule: BudgetRule = {
    id: 'unit',
    absoluteMs: 50,
    allowlist: [
      entry({ id: 'gone', reason: 'deleted test' }),
      entry({ pattern: 'Legacy\\..*', reason: 'legacy module' }),
    ],
  }
  const d = evaluateRule(rule, [case_('current', 5)], TODAY)
  assert.equal(d.staleAllowlist.length, 2)
  assert.ok(d.staleAllowlist.some((s) => s.key === 'gone'))
})

test('evaluateRule fails when an allowlist entry is past its removal deadline', () => {
  const rule: BudgetRule = {
    id: 'unit',
    absoluteMs: 50,
    allowlist: [entry({ id: 'slow', observedMs: 120, deadline: PAST })],
  }
  const d = evaluateRule(rule, [case_('slow', 120)], TODAY)
  assert.equal(d.expiredAllowlist.length, 1)
  assert.equal(d.expiredAllowlist[0].deadline, PAST)
})

test('evaluateRule reports a percentile breach', () => {
  const rule: BudgetRule = { id: 'unit', absoluteMs: 10_000, percentile: 95, percentileMs: 5 }
  const cases = Array.from({ length: 20 }, (_, i) => case_(`t${i}`, i + 1))
  const d = evaluateRule(rule, cases, TODAY)
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
  const evaluation = evaluateTrack(track, [case_('ok', 5), case_('bad', 5, 'failed')], TODAY)
  assert.equal(evaluation.enforce, false)
  assert.equal(evaluation.passed, false)
  assert.deepEqual(evaluation.failedTests, ['bad'])
  assert.equal(evaluation.rules.length, 0)
})

test('evaluateTrack: enforce=true fails on absolute violation but passes when governed and unexpired', () => {
  const rules: BudgetRule[] = [{ id: 'unit', absoluteMs: 50, allowlist: [entry({ id: 'slow' })] }]
  const track: TrackConfig = {
    id: 'enforced',
    kind: 'report-only',
    report: 'x',
    reportFormat: 'trx',
    deadlineMs: 1000,
    enforce: true,
    rules,
  }
  const passing = evaluateTrack(track, [case_('slow', 120), case_('fast', 5)], TODAY)
  assert.equal(passing.passed, true)
  assert.equal(passing.rules[0].governed.length, 1)

  const failing = evaluateTrack(track, [case_('slow', 120), case_('unlisted', 200)], TODAY)
  assert.equal(failing.passed, false)
  assert.equal(failing.rules[0].absoluteViolations.length, 1)
})

test('evaluateTrack: enforce=true fails on a parseable but empty report (0 cases)', () => {
  const track: TrackConfig = {
    id: 'enforced',
    kind: 'report-only',
    report: 'x',
    reportFormat: 'trx',
    deadlineMs: 1000,
    enforce: true,
    rules: [{ id: 'unit', absoluteMs: 50 }],
  }
  const evaluation = evaluateTrack(track, [], TODAY)
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
  const evaluation = evaluateTrack(track, [], TODAY)
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
    rules: [{ id: 'unit', absoluteMs: 50 }],
  }
  const evaluation = evaluateTrack(
    track,
    [case_('skipped', 0, 'skipped'), case_('not-run', 0, 'not-run'), case_('other', 0, 'other')],
    TODAY,
  )
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

test('model: a spec test at 600ms passes the spec rule (5s cap) but violates the unit rule (500ms cap)', () => {
  const specRule: BudgetRule = {
    id: 'spec',
    namePattern: 'Specs\\.',
    absoluteMs: 5000,
    percentile: 95,
    percentileMs: 500,
  }
  const unitRule: BudgetRule = { id: 'unit', absoluteMs: 500, percentile: 95, percentileMs: 50 }
  const specCase = case_('Ns.UpdateServerSpecs.Slow', 600)
  assert.equal(evaluateRule(specRule, [specCase], TODAY).absoluteViolations.length, 0)
  assert.equal(evaluateRule(unitRule, [specCase], TODAY).absoluteViolations.length, 1)
})
