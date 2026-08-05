import type { RuleDiagnosis, TrackEvaluation, TrackRun } from './types.js'

function ms(value: number): string {
  return value >= 1000 ? `${(value / 1000).toFixed(2)}s` : `${value.toFixed(1)}ms`
}

function describeRule(rule: RuleDiagnosis): string {
  const parts: string[] = []
  for (const [p, value] of Object.entries(rule.percentiles)) {
    parts.push(`p${p}=${ms(value)}`)
  }
  parts.push(`max=${ms(rule.maxMs)}`)
  return parts.join(' ')
}

export function formatTrackRun(run: TrackRun): string {
  const flag = run.timedOut
    ? run.timeoutReason === 'suite' ? 'SUITE TIMEOUT' : 'TIMEOUT'
    : run.exitCode === 0 ? 'ok' : `exit ${run.exitCode}`
  const report = run.reportReady ? '' : '  [report missing/stale]'
  return `  ${run.trackId}: ${ms(run.elapsedMs)} / ${ms(run.deadlineMs)} deadline  [${flag}]${report}  ${run.command}`
}

export function formatEvaluation(eval_: TrackEvaluation): string[] {
  const lines: string[] = []
  const head = eval_.enforce
    ? `${eval_.trackId}: ${eval_.total} tests  ${eval_.passed ? 'PASS' : 'FAIL'}`
    : `${eval_.trackId}: ${eval_.total} tests  ratchet ${eval_.status ?? 'baseline-pending'} (deadline-governed only)`
  lines.push(`  ${head}`)
  if (eval_.reason) lines.push(`    reason: ${eval_.reason}`)
  if (eval_.reportError) lines.push(`      >> REPORT ERROR: ${eval_.reportError}`)
  if (!eval_.enforce) return lines
  for (const rule of eval_.rules) {
    lines.push(`    ${rule.ruleId} (n=${rule.total}): ${describeRule(rule)}`)
    if (rule.percentileViolation) {
      lines.push(
      `      >> percentile p${rule.percentileViolation.p} ${ms(rule.percentileViolation.valueMs)} exceeds budget ${ms(rule.percentileViolation.budgetMs)}`,
      )
    }
    for (const v of rule.absoluteViolations) {
      lines.push(`      >> OVER BUDGET ${ms(v.durationMs)}: ${v.name}`)
    }
    for (const g of rule.governed) {
      lines.push(`      governed ${ms(g.durationMs)} (baseline ${ms(g.observedMs)}, by ${g.deadline}, ${g.owner}): ${g.name}  [${g.reason}]`)
    }
    for (const s of rule.staleAllowlist) {
      lines.push(`      >> STALE allowlist "${s.key}" matched no test  [${s.reason}]`)
    }
    for (const e of rule.expiredAllowlist) {
      lines.push(`      >> EXPIRED allowlist "${e.key}" past removal deadline ${e.deadline} (${e.owner})  [${e.reason}]`)
    }
  }
  for (const failed of eval_.failedTests) {
    lines.push(`      >> FAILED TEST: ${failed}`)
  }
  return lines
}

export interface GuardSummary {
  readonly totalTracks: number
  readonly failedTracks: number
  readonly timeoutTracks: number
  readonly governed: number
  readonly overBudget: number
  readonly suiteDeadlineBreached: boolean
  readonly suiteElapsedMs?: number
}

export function summarize(
  runs: readonly TrackRun[],
  evaluations: readonly TrackEvaluation[],
  suiteDeadlineBreached = false,
  suiteElapsedMs?: number,
): GuardSummary {
  const timeoutTracks = runs.filter((r) => r.timedOut).length
  const failedTrackIds = new Set([
    ...evaluations.filter((e) => !e.passed).map((e) => e.trackId),
    ...runs.filter((r) => r.timedOut || r.exitCode !== 0).map((r) => r.trackId),
  ])
  const governed = evaluations.reduce(
    (sum, e) => sum + e.rules.reduce((s, r) => s + r.governed.length, 0),
    0,
  )
  const overBudget = evaluations.reduce(
    (sum, e) => sum + e.rules.reduce((s, r) => s + r.absoluteViolations.length, 0),
    0,
  )
  return {
    totalTracks: Math.max(runs.length, evaluations.length),
    failedTracks: failedTrackIds.size + (suiteDeadlineBreached && failedTrackIds.size === 0 ? 1 : 0),
    timeoutTracks,
    governed,
    overBudget,
    suiteDeadlineBreached,
    suiteElapsedMs,
  }
}

export function formatSummary(summary: GuardSummary, suiteDeadlineMs: number): string {
  const suite = summary.suiteDeadlineBreached && summary.suiteElapsedMs !== undefined
    ? `${ms(suiteDeadlineMs)} BREACHED after ${ms(summary.suiteElapsedMs)}`
    : ms(suiteDeadlineMs)
  return [
    `test-duration: ${summary.totalTracks} tracks, ${summary.failedTracks} failing, ${summary.timeoutTracks} timed out`,
    `  governed (allowlisted) slow tests: ${summary.governed}  | over-budget (not allowlisted): ${summary.overBudget}`,
    `  suite deadline: ${suite}`,
  ].join('\n')
}
