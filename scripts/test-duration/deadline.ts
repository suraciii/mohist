export interface RunResult {
  exitCode: number | null
}

export interface DeadlineDeps<TimeoutReason = void> {
  readonly start: () => Promise<RunResult>
  readonly kill: () => Promise<void>
  readonly timeout: Promise<TimeoutReason>
  readonly now: () => number
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
  const elapsedMs = deps.now() - t0
  const status: DeadlineStatus = settled.result.exitCode === 0 ? 'passed' : 'failed'
  return { status, exitCode: settled.result.exitCode, elapsedMs }
}
