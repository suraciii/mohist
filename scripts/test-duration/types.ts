export type TestOutcome = 'passed' | 'failed' | 'skipped' | 'other'

export interface TestCase {
  readonly name: string
  readonly durationMs: number
  readonly outcome: TestOutcome
  readonly file?: string
}

export interface AllowlistEntry {
  readonly id?: string
  readonly pattern?: string
  readonly observedMs: number
  readonly reason: string
  readonly owner: string
  readonly deadline: string
}

export interface ExpiredAllowlist {
  readonly key: string
  readonly reason: string
  readonly owner: string
  readonly deadline: string
}

export interface BudgetRule {
  readonly id: string
  readonly namePattern?: string
  readonly absoluteMs: number
  readonly percentile?: number
  readonly percentileMs?: number
  readonly allowlist?: readonly AllowlistEntry[]
}

export type TrackKind = 'vitest' | 'dotnet-apphost' | 'dotnet-vstest' | 'report-only'

export type ReportFormat = 'trx' | 'vitest'

export interface TrackConfig {
  readonly id: string
  readonly kind: TrackKind
  readonly csproj?: string
  readonly apphost?: string
  readonly apphostArgs?: readonly string[]
  readonly tfm?: string
  readonly run?: readonly string[]
  readonly report: string
  readonly reportFormat: ReportFormat
  readonly deadlineMs: number
  readonly enforce: boolean
  readonly status?: string
  readonly reason?: string
  readonly rules?: readonly BudgetRule[]
}

export interface SuiteConfig {
  readonly suiteDeadlineMs: number
  readonly killGraceMs?: number
  readonly tracks: readonly TrackConfig[]
}

export interface AbsoluteViolation {
  readonly name: string
  readonly durationMs: number
}

export interface GovernedCase {
  readonly name: string
  readonly durationMs: number
  readonly reason: string
  readonly owner: string
  readonly deadline: string
  readonly observedMs: number
}

export interface StaleAllowlist {
  readonly key: string
  readonly reason: string
}

export interface PercentileViolation {
  readonly p: number
  readonly valueMs: number
  readonly budgetMs: number
}

export interface RuleDiagnosis {
  readonly ruleId: string
  readonly total: number
  readonly percentiles: Readonly<Record<number, number>>
  readonly maxMs: number
  readonly absoluteViolations: readonly AbsoluteViolation[]
  readonly governed: readonly GovernedCase[]
  readonly staleAllowlist: readonly StaleAllowlist[]
  readonly expiredAllowlist: readonly ExpiredAllowlist[]
  readonly percentileViolation?: PercentileViolation
}

export interface TrackEvaluation {
  readonly trackId: string
  readonly enforce: boolean
  readonly status?: string
  readonly reason?: string
  readonly total: number
  readonly failedTests: readonly string[]
  readonly rules: readonly RuleDiagnosis[]
  readonly passed: boolean
}

export interface TrackRun {
  readonly trackId: string
  readonly timedOut: boolean
  readonly timeoutReason?: 'track' | 'suite'
  readonly exitCode: number | null
  readonly elapsedMs: number
  readonly deadlineMs: number
  readonly command: string
}
