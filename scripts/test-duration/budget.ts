import type { BudgetRule, RuleDiagnosis, TestCase, TrackConfig, TrackEvaluation } from './types.js'

function countOutcomes(cases: readonly TestCase[]) {
  let passed = 0
  let failed = 0
  let errors = 0
  let skipped = 0
  let notRun = 0
  let other = 0
  for (const test of cases) {
    switch (test.outcome) {
      case 'passed':
        passed++
        break
      case 'failed':
        failed++
        break
      case 'error':
        errors++
        break
      case 'skipped':
        skipped++
        break
      case 'not-run':
        notRun++
        break
      default:
        other++
        break
    }
  }
  return { total: cases.length, passed, failed, errors, skipped, notRun, other }
}

export function percentile(values: readonly number[], p: number): number {
  if (values.length === 0) return 0
  const sorted = [...values].sort((a, b) => a - b)
  const idx = Math.min(sorted.length - 1, Math.max(0, Math.ceil((p / 100) * sorted.length) - 1))
  return sorted[idx]
}

function safeRegex(pattern: string): RegExp {
  try {
    return new RegExp(pattern)
  } catch {
    return /$^/
  }
}

interface CompiledRule {
  rule: BudgetRule
  regex: RegExp | undefined
}

export function classify(
  cases: readonly TestCase[],
  rules: readonly BudgetRule[],
): ReadonlyMap<BudgetRule, TestCase[]> {
  const compiled: CompiledRule[] = rules.map((rule) => ({
    rule,
    regex: rule.namePattern ? safeRegex(rule.namePattern) : undefined,
  }))
  const buckets = new Map<BudgetRule, TestCase[]>()
  for (const { rule } of compiled) buckets.set(rule, [])
  for (const test of cases) {
    for (const { rule, regex } of compiled) {
      if (regex === undefined || regex.test(test.name)) {
        buckets.get(rule)!.push(test)
        break
      }
    }
  }
  return buckets
}

export function evaluateRule(rule: BudgetRule, cases: readonly TestCase[]): RuleDiagnosis {
  const active = cases.filter((c) => c.outcome === 'passed' || c.outcome === 'failed')
  const durations = active.map((c) => c.durationMs)

  const percentiles: Record<number, number> = {}
  if (rule.percentile !== undefined) {
    percentiles[rule.percentile] = percentile(durations, rule.percentile)
  }

  let percentileViolation
  if (rule.percentile !== undefined && rule.percentileMs !== undefined) {
    const value = percentiles[rule.percentile]
    if (value > rule.percentileMs) {
      percentileViolation = { p: rule.percentile, valueMs: value, budgetMs: rule.percentileMs }
    }
  }

  return {
    ruleId: rule.id,
    total: cases.length,
    percentiles,
    maxMs: durations.length ? Math.max(...durations) : 0,
    percentileViolation,
  }
}

export function evaluateTrack(track: TrackConfig, cases: readonly TestCase[]): TrackEvaluation {
  const outcomes = countOutcomes(cases)
  const failedTests = cases.filter((c) => c.outcome !== 'passed').map((c) => c.name)
  if (!track.enforce) {
    return {
      trackId: track.id,
      enforce: false,
      status: track.status,
      reason: track.reason,
      total: cases.length,
      outcomes,
      failedTests,
      rules: [],
      passed: cases.length > 0 && failedTests.length === 0,
    }
  }
  const rules = track.rules ?? []
  const buckets = classify(cases, rules)
  const diagnoses = rules.map((rule) => evaluateRule(rule, buckets.get(rule) ?? []))
  const ruleFailing = diagnoses.some((d) => d.percentileViolation !== undefined)
  // An enforced track with a parseable but empty report produced no evidence;
  // treat it as failed so a broken producer cannot fake green.
  const passed = cases.length > 0 && failedTests.length === 0 && !ruleFailing
  return { trackId: track.id, enforce: true, total: cases.length, outcomes, failedTests, rules: diagnoses, passed }
}
