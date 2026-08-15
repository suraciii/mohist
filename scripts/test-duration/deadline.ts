export interface RunResult {
  exitCode: number | null
}

export interface SuiteDeadlines {
  readonly hardDeadlineAt: number
  readonly executionDeadlineAt: number
}

// Report parsing and the final summary are part of the five-minute wall, not a
// post-deadline best effort. The existing kill grace is reserved twice so TERM
// and KILL can both observe a process-tree terminal event before that wall.
const finalizationReserveMs = 1_000

// GNU timeout gives the canonical process ten seconds after TERM before KILL.
// Keep the child TERM/KILL window and final status flush inside that envelope.
export function externalAbortCleanupDeadlineAt(now: number, hardDeadlineAt: number, killGraceMs: number): number {
  return Math.min(hardDeadlineAt, now + killGraceMs + finalizationReserveMs)
}

export function suiteDeadlines(startedAt: number, suiteDeadlineMs: number, killGraceMs: number): SuiteDeadlines {
  const reservedMs = killGraceMs * 2 + finalizationReserveMs
  if (!Number.isFinite(startedAt) || !Number.isFinite(suiteDeadlineMs) || !Number.isFinite(killGraceMs)) {
    throw new Error('suite deadline inputs must be finite')
  }
  if (suiteDeadlineMs <= reservedMs) {
    throw new Error(`suite deadline must exceed the ${reservedMs}ms cleanup and finalization reserve`)
  }
  return suiteDeadlinesAt(startedAt + suiteDeadlineMs, killGraceMs)
}

export function suiteDeadlinesAt(hardDeadlineAt: number, killGraceMs: number): SuiteDeadlines {
  if (!Number.isFinite(hardDeadlineAt) || !Number.isFinite(killGraceMs)) {
    throw new Error('suite deadline inputs must be finite')
  }
  return {
    hardDeadlineAt,
    executionDeadlineAt: hardDeadlineAt - (killGraceMs * 2 + finalizationReserveMs),
  }
}

export interface DeadlineDeps<TimeoutReason = void> {
  readonly start: () => Promise<RunResult>
  readonly kill: () => Promise<void>
  readonly timeout: Promise<TimeoutReason>
  readonly now: () => number
  readonly hardDeadlineAt?: number
}

export type DeadlineStatus = 'passed' | 'failed' | 'timeout'

export interface DeadlineOutcome<TimeoutReason = void> {
  readonly status: DeadlineStatus
  readonly exitCode: number | null
  readonly elapsedMs: number
  readonly timeoutReason?: TimeoutReason
}

export async function runWithDeadline<TimeoutReason = void>(
  deps: DeadlineDeps<TimeoutReason>,
): Promise<DeadlineOutcome<TimeoutReason>> {
  const t0 = deps.now()
  const settled = await Promise.race([
    deps.start().then((result) => ({ kind: 'done' as const, result })),
    deps.timeout.then((reason) => ({ kind: 'timeout' as const, reason })),
  ])
  if (settled.kind === 'timeout') {
    await deps.kill()
    return { status: 'timeout', exitCode: null, elapsedMs: deps.now() - t0, timeoutReason: settled.reason }
  }
  if (deps.hardDeadlineAt !== undefined && deps.now() >= deps.hardDeadlineAt) {
    await deps.kill()
    return { status: 'timeout', exitCode: null, elapsedMs: deps.now() - t0 }
  }
  const elapsedMs = deps.now() - t0
  const status: DeadlineStatus = settled.result.exitCode === 0 ? 'passed' : 'failed'
  return { status, exitCode: settled.result.exitCode, elapsedMs }
}
