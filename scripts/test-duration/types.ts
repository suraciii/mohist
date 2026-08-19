export type TestOutcome = 'passed' | 'failed' | 'error' | 'skipped' | 'not-run' | 'other'

export type ExecutionLedgerOutcome = 'passed' | 'failed' | 'skipped' | 'not-run'

export interface TestCase {
  readonly name: string
  readonly durationMs: number
  readonly outcome: TestOutcome
  readonly file?: string
  readonly uid?: string
}

export interface ExecutionLedgerCase {
  readonly uid: string
  readonly testCaseUid: string
  readonly name: string
  readonly outcome: ExecutionLedgerOutcome
  readonly executionTimeMs: number
  readonly startTime: string
  readonly finishTime: string
  readonly className: string
  readonly collectionName: string
}

export interface ExecutionLedger {
  readonly schemaVersion: 2
  readonly runId: string
  readonly manifestHash: string
  readonly manifestCount: number
  readonly assemblyPath: string
  readonly assemblySha256: string
  readonly sourceSha256: string
  readonly xunitVersion: string
  readonly mtpVersion: string
  readonly parallelism: string
  readonly durationSource: 'xunit.v3.ITestResultMessage.ExecutionTime'
  readonly durationUnit: 'seconds'
  readonly cases: readonly ExecutionLedgerCase[]
}

export interface ExecutionManifest {
  readonly hash: string
  readonly cases: readonly ExecutionManifestCase[]
}

export interface ExecutionManifestCase {
  readonly uid: string
  readonly name: string
  readonly className: string
  readonly methodName: string
}

export interface ExecutionLedgerExpectation {
  readonly runId: string
  readonly manifest: ExecutionManifest
  readonly assemblyPath: string
  readonly assemblySha256: string
  readonly sourceSha256: string
  readonly parallelism: string
}

export type CurrentExecutionIdentity = Omit<ExecutionLedgerExpectation, 'runId'>

export interface ExecutionLedgerProvenance {
  readonly schemaVersion: 2
  readonly runId: string
  readonly manifestHash: string
  readonly manifestCount: number
  readonly manifestCases: readonly ExecutionManifestCase[]
  readonly assemblyPath: string
  readonly assemblySha256: string
  readonly sourceSha256: string
  readonly parallelism: string
}

export interface ExecutionLedgerValidation {
  readonly cases: readonly TestCase[]
  readonly errors: readonly string[]
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
  readonly executionLedger?: string
  readonly executionProvenance?: string
  readonly executionSourceRoots?: readonly string[]
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
  readonly canonical?: CanonicalGateConfig
  readonly tracks: readonly TrackConfig[]
}

export interface CanonicalGateConfig {
  readonly maxConcurrentLanes: number
  readonly resourceLimits: Readonly<Record<string, number>>
  readonly durationMeasurementTracks?: readonly string[]
  readonly durationIsolationTrack?: string
}

export interface OutcomeCounts {
  readonly total: number
  readonly passed: number
  readonly failed: number
  readonly errors: number
  readonly skipped: number
  readonly notRun: number
  readonly other: number
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
  readonly reportError?: string
  readonly total: number
  readonly outcomes: OutcomeCounts
  readonly failedTests: readonly string[]
  readonly rules: readonly RuleDiagnosis[]
  readonly passed: boolean
}

export interface TrackRun {
  readonly trackId: string
  readonly policyTrackId?: string
  readonly reportPath?: string
  readonly cancelled?: boolean
  readonly cancellationReason?: string
  readonly timedOut: boolean
  readonly timeoutReason?: 'track' | 'suite'
  readonly exitCode: number | null
  readonly elapsedMs: number
  readonly deadlineMs: number
  readonly command: string
  readonly reportReady: boolean
  readonly cleanupComplete: boolean
  readonly reportError?: string
  readonly executionLedgerReady?: boolean
  readonly executionLedgerError?: string
  readonly executionLedgerExpectation?: ExecutionLedgerExpectation
  readonly stdoutPath?: string
  readonly stderrPath?: string
}
