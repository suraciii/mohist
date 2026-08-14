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
  const exit = run.exitCode === null ? 'exit null' : `exit ${run.exitCode}`
  const flag = run.cancelled
    ? `CANCELLED${run.cancellationReason ? ` ${run.cancellationReason}` : ''} (${exit})`
    : run.timedOut
      ? run.timeoutReason === 'suite'
        ? 'SUITE TIMEOUT'
        : 'TIMEOUT'
      : run.exitCode === 0 && run.reportReady
        ? 'ok'
        : exit
  const report = run.cancelled
    ? `  [report ${run.reportReady ? 'ignored after cancellation' : 'unavailable after cancellation'}]`
    : run.reportReady
      ? ''
      : '  [report missing/stale]'
  const cleanup = `  [process-tree ${run.cleanupComplete ? 'terminal' : 'NOT TERMINAL'}]`
  const reportPath = run.reportPath ? `  report=${run.reportPath}` : ''
  const evidence = run.stdoutPath && run.stderrPath ? `  logs=${run.stdoutPath},${run.stderrPath}` : ''
  return `  ${run.trackId}: ${ms(run.elapsedMs)} / ${ms(run.deadlineMs)} deadline  [${flag}]${report}${cleanup}${reportPath}  ${run.command}${evidence}`
}

export function formatEvaluation(eval_: TrackEvaluation): string[] {
  const lines: string[] = []
  const counts = eval_.outcomes
  const outcomeSummary = `Total=${counts.total} Passed=${counts.passed} Failed=${counts.failed} Errors=${counts.errors} Skipped=${counts.skipped} NotRun=${counts.notRun} Other=${counts.other}`
  const head = eval_.enforce
    ? `${eval_.trackId}: ${outcomeSummary}  ${eval_.passed ? 'PASS' : 'FAIL'}`
    : `${eval_.trackId}: ${outcomeSummary}  ratchet ${eval_.status ?? 'baseline-pending'} (deadline-governed only)`
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
      lines.push(
        `      governed ${ms(g.durationMs)} (baseline ${ms(g.observedMs)}, by ${g.deadline}, ${g.owner}): ${g.name}  [${g.reason}]`,
      )
    }
    for (const s of rule.staleAllowlist) {
      lines.push(`      >> STALE allowlist "${s.key}" matched no test  [${s.reason}]`)
    }
    for (const e of rule.expiredAllowlist) {
      lines.push(
        `      >> EXPIRED allowlist "${e.key}" past removal deadline ${e.deadline} (${e.owner})  [${e.reason}]`,
      )
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
  readonly cancelledTracks: number
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
  const runsByPolicy = new Map<string, TrackRun[]>()
  for (const run of runs) {
    const policyTrackId = run.policyTrackId ?? run.trackId
    const policyRuns = runsByPolicy.get(policyTrackId) ?? []
    policyRuns.push(run)
    runsByPolicy.set(policyTrackId, policyRuns)
  }
  const cancelledTrackIds = new Set(
    [...runsByPolicy.entries()]
      .filter(([, policyRuns]) => policyRuns.length > 0 && policyRuns.every((run) => run.cancelled))
      .map(([trackId]) => trackId),
  )
  const failedTrackIds = new Set([
    ...evaluations.filter((e) => !e.passed && !cancelledTrackIds.has(e.trackId)).map((e) => e.trackId),
    ...runs
      .filter((r) => !r.cancelled && (r.timedOut || r.exitCode !== 0 || !r.reportReady || !r.cleanupComplete))
      .map((r) => r.policyTrackId ?? r.trackId),
  ])
  const governed = evaluations.reduce((sum, e) => sum + e.rules.reduce((s, r) => s + r.governed.length, 0), 0)
  const overBudget = evaluations.reduce(
    (sum, e) => sum + e.rules.reduce((s, r) => s + r.absoluteViolations.length, 0),
    0,
  )
  return {
    totalTracks: evaluations.length > 0 ? evaluations.length : runs.length,
    failedTracks: failedTrackIds.size + (suiteDeadlineBreached && failedTrackIds.size === 0 ? 1 : 0),
    cancelledTracks: cancelledTrackIds.size,
    timeoutTracks,
    governed,
    overBudget,
    suiteDeadlineBreached,
    suiteElapsedMs,
  }
}

export function formatSummary(summary: GuardSummary, suiteDeadlineMs: number): string {
  const suite =
    summary.suiteDeadlineBreached && summary.suiteElapsedMs !== undefined
      ? `${ms(suiteDeadlineMs)} BREACHED after ${ms(summary.suiteElapsedMs)}`
      : ms(suiteDeadlineMs)
  return [
    `test-duration: ${summary.totalTracks} tracks, ${summary.failedTracks} failing, ${summary.cancelledTracks} cancelled, ${summary.timeoutTracks} timed out`,
    `  governed (allowlisted) slow tests: ${summary.governed}  | over-budget (not allowlisted): ${summary.overBudget}`,
    `  suite deadline: ${suite}`,
  ].join('\n')
}
