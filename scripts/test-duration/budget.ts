import type {
  AllowlistEntry,
  BudgetRule,
  ExpiredAllowlist,
  GovernedCase,
  RuleDiagnosis,
  StaleAllowlist,
  TestCase,
  TrackConfig,
  TrackEvaluation,
} from './types.js'

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

function entryMatches(entry: AllowlistEntry, name: string): boolean {
  if (entry.id !== undefined) return entry.id === name
  if (entry.pattern !== undefined) return safeRegex(entry.pattern).test(name)
  return false
}

function isAllowed(rule: BudgetRule, name: string): AllowlistEntry | undefined {
  return rule.allowlist?.find((entry) => entryMatches(entry, name))
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

export function evaluateRule(rule: BudgetRule, cases: readonly TestCase[], today: Date): RuleDiagnosis {
  const active = cases.filter((c) => c.outcome !== 'skipped')
  const durations = active.map((c) => c.durationMs)
  const absoluteViolations: GovernedCase[] = []
  const governed: GovernedCase[] = []

  for (const c of active) {
    if (c.durationMs > rule.absoluteMs) {
      const entry = isAllowed(rule, c.name)
      if (entry) {
        governed.push(toGoverned(c, entry))
      } else {
        absoluteViolations.push({
          name: c.name,
          durationMs: c.durationMs,
          reason: '',
          owner: '',
          deadline: '',
          observedMs: c.durationMs,
        })
      }
    }
  }

  const names = new Set(cases.map((c) => c.name))
  const staleAllowlist: StaleAllowlist[] = []
  const expiredAllowlist: ExpiredAllowlist[] = []
  const todayMs = today.getTime()
  for (const entry of rule.allowlist ?? []) {
    const key = entry.id ?? entry.pattern ?? ''
    const matched =
      entry.id !== undefined
        ? names.has(entry.id)
        : entry.pattern !== undefined
          ? cases.some((c) => safeRegex(entry.pattern!).test(c.name))
          : false
    if (!matched) staleAllowlist.push({ key, reason: entry.reason })
    if (Date.parse(entry.deadline) < todayMs) {
      expiredAllowlist.push({ key, reason: entry.reason, owner: entry.owner, deadline: entry.deadline })
    }
  }

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

  const sortDesc = (a: GovernedCase, b: GovernedCase) => b.durationMs - a.durationMs
  return {
    ruleId: rule.id,
    total: cases.length,
    percentiles,
    maxMs: durations.length ? Math.max(...durations) : 0,
    absoluteViolations: absoluteViolations.sort(sortDesc),
    governed: governed.sort(sortDesc),
    staleAllowlist,
    expiredAllowlist,
    percentileViolation,
  }
}

function toGoverned(c: TestCase, entry: AllowlistEntry): GovernedCase {
  return {
    name: c.name,
    durationMs: c.durationMs,
    reason: entry.reason,
    owner: entry.owner,
    deadline: entry.deadline,
    observedMs: entry.observedMs,
  }
}

export function evaluateTrack(
  track: TrackConfig,
  cases: readonly TestCase[],
  today: Date = new Date(),
): TrackEvaluation {
  const failedTests = cases.filter((c) => c.outcome === 'failed').map((c) => c.name)
  if (!track.enforce) {
    return {
      trackId: track.id,
      enforce: false,
      status: track.status,
      reason: track.reason,
      total: cases.length,
      failedTests,
      rules: [],
      passed: failedTests.length === 0,
    }
  }
  const rules = track.rules ?? []
  const buckets = classify(cases, rules)
  const diagnoses = rules.map((rule) => evaluateRule(rule, buckets.get(rule) ?? [], today))
  const ruleFailing = diagnoses.some(
    (d) =>
      d.absoluteViolations.length > 0 ||
      d.staleAllowlist.length > 0 ||
      d.expiredAllowlist.length > 0 ||
      d.percentileViolation !== undefined,
  )
  // An enforced track with a parseable but empty report produced no evidence;
  // treat it as failed so a broken producer cannot fake green.
  const passed = cases.length > 0 && failedTests.length === 0 && !ruleFailing
  return { trackId: track.id, enforce: true, total: cases.length, failedTests, rules: diagnoses, passed }
}
