import { describe, expect, it } from 'vitest'
import { classifyPrChecks, parsePrStatusCheckRollupResult, type PrCheckEntry } from '../src/actions/github-pr-checks.js'

function classifyRollup(checks: unknown[]): ReturnType<typeof classifyPrChecks> {
  const parsed = parsePrStatusCheckRollupResult(JSON.stringify({ statusCheckRollup: checks }))
  if (parsed.kind !== 'ok') throw new Error(`rollup parse failed: ${parsed.message}`)
  return classifyPrChecks(parsed.checks)
}

function entry(overrides: Record<string, unknown>): Record<string, unknown> {
  return { name: 'check', ...overrides }
}

const TERMINAL_FAILURES = ['FAILURE', 'ERROR', 'CANCELLED', 'ACTION_REQUIRED', 'TIMED_OUT', 'STARTUP_FAILURE', 'STALE']
const RUNNING_OUTCOMES = ['QUEUED', 'IN_PROGRESS', 'REQUESTED', 'WAITING', 'PENDING']

describe('classifyRollupBucket through parsePrStatusCheckRollupResult + classifyPrChecks', () => {
  it.each(TERMINAL_FAILURES)('classifies conclusion %s as failed', (conclusion) => {
    expect(classifyRollup([entry({ conclusion })]).kind).toBe('failed')
  })

  it.each(['SUCCESS'])('classifies conclusion %s as passed', (conclusion) => {
    expect(classifyRollup([entry({ conclusion })]).kind).toBe('passed')
  })

  it.each(['SKIPPED', 'NEUTRAL'])('classifies conclusion %s as passed (skip bucket)', (conclusion) => {
    const parsed = parsePrStatusCheckRollupResult(JSON.stringify({ statusCheckRollup: [entry({ conclusion })] }))
    if (parsed.kind !== 'ok') throw new Error(`rollup parse failed: ${parsed.message}`)
    expect(parsed.checks[0]?.bucket).toBe('skip')
    expect(classifyPrChecks(parsed.checks).kind).toBe('passed')
  })

  it.each(RUNNING_OUTCOMES)('classifies status %s as pending', (status) => {
    expect(classifyRollup([entry({ status })]).kind).toBe('pending')
  })

  it.each(TERMINAL_FAILURES)('classifies state %s as failed', (state) => {
    // STALE is a CheckRun conclusion, but the shared failure set is authoritative for
    // both normalization paths, so it must resolve to fail here too.
    expect(classifyRollup([entry({ state })]).kind).toBe('failed')
  })

  it.each(['SUCCESS'])('classifies state %s as passed', (state) => {
    expect(classifyRollup([entry({ state })]).kind).toBe('passed')
  })

  it.each(['SKIPPED', 'NEUTRAL'])('classifies state %s as passed (skip bucket)', (state) => {
    expect(classifyRollup([entry({ state })]).kind).toBe('passed')
  })

  it('lets failed outrank pending in a mixed rollup', () => {
    const classification = classifyRollup([
      entry({ name: 'timed-out', conclusion: 'TIMED_OUT' }),
      entry({ name: 'running', status: 'IN_PROGRESS' }),
    ])
    expect(classification.kind).toBe('failed')
    if (classification.kind === 'failed') expect(classification.message).toContain('timed-out')
  })

  it('stays pending when a running check accompanies a passing check', () => {
    expect(classifyRollup([entry({ conclusion: 'SUCCESS' }), entry({ status: 'QUEUED' })]).kind).toBe('pending')
  })

  it('reports an empty rollup as pending', () => {
    expect(classifyPrChecks([]).kind).toBe('pending')
  })

  it('keeps the parsed entry state derived from conclusion first', () => {
    const parsed = parsePrStatusCheckRollupResult(
      JSON.stringify({ statusCheckRollup: [entry({ status: 'COMPLETED', conclusion: 'STALE' })] }),
    )
    if (parsed.kind !== 'ok') throw new Error(`rollup parse failed: ${parsed.message}`)
    const first: PrCheckEntry | undefined = parsed.checks[0]
    expect(first?.state).toBe('STALE')
    expect(classifyPrChecks(parsed.checks).kind).toBe('failed')
  })
})
