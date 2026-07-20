import { describe, it, expect } from 'vitest'
import {
  BASE_PLAN_STEPS,
  coderRecoveryStatusReducer,
  compactionEventReducer,
  contextCompactedReducer,
  contextHealthUpdateReducer,
  contextHealthUpdatedReducer,
  mapLivenessToRecoveryStatus,
  mergeContextHealth,
  planRoundCompleteReducer,
  planRoundStartReducer,
  sessionLivenessReducer,
  toContextHealthStatus,
  usageUpdatedReducer,
  type ContextHealthState,
  type Round,
  type SessionTimelineEnv,
  type SessionTimelineState,
} from './session-timeline-reducer'

function emptyTimelineState(): SessionTimelineState {
  return {
    rounds: [],
    planProgress: null,
    recoveryStatus: null,
    contextHealth: null,
  }
}

function roundsOf(seeds: Array<Pick<Round, 'roundIndex' | 'label' | 'startedAt'>>): Round[] {
  return seeds.map((s) => ({
    roundIndex: s.roundIndex,
    label: s.label,
    startedAt: s.startedAt,
    completedAt: null,
    userText: '',
    agentText: '',
    thoughtText: '',
    toolCalls: [],
    recoveryEvents: [],
    compactions: [],
  }))
}

function contextHealthState(fields: Partial<ContextHealthState>): ContextHealthState {
  return {
    status: fields.status ?? null,
    contextWindowUsed: fields.contextWindowUsed ?? null,
    contextWindowSize: fields.contextWindowSize ?? null,
    contextUsagePercent: fields.contextUsagePercent ?? null,
    recordedAt: fields.recordedAt ?? null,
  }
}

function makeEnv(overrides: Partial<SessionTimelineEnv> = {}): SessionTimelineEnv {
  return {
    now: overrides.now ?? 0,
    isoNow: overrides.isoNow ?? '1970-01-01T00:00:00.000Z',
    randomId: overrides.randomId ?? (() => 'id'),
  }
}

const ENV = makeEnv({
  now: 1_700_000_000_000,
  isoNow: '2024-01-01T00:00:00.000Z',
  randomId: () => 'abc123',
})

function withRounds(state: SessionTimelineState, rounds: Round[]): SessionTimelineState {
  return { ...state, rounds }
}

describe('mergeContextHealth', () => {
  it('returns next when prev is null', () => {
    const next = contextHealthState({ status: 'red', contextWindowUsed: 100, contextWindowSize: 1000, contextUsagePercent: 10 })
    expect(mergeContextHealth(null, next)).toBe(next)
  })

  it('returns prev when all four fields match', () => {
    const prev = contextHealthState({ status: 'green', contextWindowUsed: 100, contextWindowSize: 1000, contextUsagePercent: 10 })
    const next = contextHealthState({ status: 'green', contextWindowUsed: 100, contextWindowSize: 1000, contextUsagePercent: 10 })
    expect(mergeContextHealth(prev, next)).toBe(prev)
  })

  it('returns next when any tracked field differs', () => {
    const prev = contextHealthState({ status: 'green', contextWindowUsed: 100, contextWindowSize: 1000, contextUsagePercent: 10 })
    const changed = contextHealthState({ status: 'green', contextWindowUsed: 100, contextWindowSize: 1000, contextUsagePercent: 11 })
    expect(mergeContextHealth(prev, changed)).toBe(changed)
  })
})

describe('planRoundStartReducer', () => {
  it('appends a Round and marks the matching BASE step running', () => {
    const prev = emptyTimelineState()
    const detail = {
      issueNumber: 1,      projectId: 'p',
      roundType: 'proposal',
      roundLabel: 'Proposal',
      roundIndex: 0,
      sessionId: 'c',
    }
    const next = planRoundStartReducer(prev, detail, ENV)

    expect(next.rounds).toHaveLength(1)
    expect(next.rounds[0]).toMatchObject({
      roundIndex: 0,
      label: 'Proposal',
      completedAt: null,
      startedAt: '2024-01-01T00:00:00.000Z',
    })
    const proposal = next.planProgress?.steps.find((s) => s.roundType === 'proposal')
    expect(proposal?.status).toBe('running')
  })

  it('appends a new step when roundType is not in BASE_PLAN_STEPS', () => {
    const prev = emptyTimelineState()
    const next = planRoundStartReducer(prev, {
      issueNumber: 1,      projectId: 'p',
      roundType: 'custom-step',
      roundLabel: 'Custom Step',
      roundIndex: 7,
      sessionId: 'c',
    }, ENV)

    const custom = next.planProgress?.steps.find((s) => s.roundType === 'custom-step')
    expect(custom).toMatchObject({
      roundType: 'custom-step',
      roundLabel: 'Custom Step',
      roundIndex: 7,
      status: 'running',
    })
  })

  it('falls back to Round N+1 when roundLabel is absent', () => {
    const prev = emptyTimelineState()
    const detail = {
      issueNumber: 1,      projectId: 'p',
      roundType: 'proposal',
      roundLabel: undefined as unknown as string,
      roundIndex: 0,
      sessionId: 'c',
    }
    const next = planRoundStartReducer(prev, detail, ENV)
    expect(next.rounds[0].label).toBe('Round 1')
  })

  it('does not mutate prev', () => {
    const prev = emptyTimelineState()
    const snapshot = JSON.stringify(prev)
    planRoundStartReducer(prev, {
      issueNumber: 1,      projectId: 'p',
      roundType: 'proposal',
      roundLabel: 'Proposal',
      roundIndex: 0,
      sessionId: 'c',
    }, ENV)
    expect(JSON.stringify(prev)).toBe(snapshot)
  })
})

describe('planRoundCompleteReducer', () => {
  it('marks the matching step completed on PASS and stamps verdict', () => {
    const start = planRoundStartReducer(emptyTimelineState(), {
      issueNumber: 1,      projectId: 'p',
      roundType: 'specs',
      roundLabel: 'Specs',
      roundIndex: 1,
      sessionId: 'c',
    }, ENV)

    const next = planRoundCompleteReducer(start, {
      issueNumber: 1,      projectId: 'p',
      roundType: 'specs',
      roundIndex: 1,
      duration: 500,
      verdict: 'PASS',
    }, ENV)

    const specs = next.planProgress?.steps.find((s) => s.roundType === 'specs')
    expect(specs).toMatchObject({
      status: 'completed',
      duration: 500,
      verdict: 'PASS',
    })
  })

  it('marks the matching step failed on FAIL and extends with auto-fix + re-self-review for self-review', () => {
    const start = planRoundStartReducer(emptyTimelineState(), {
      issueNumber: 1,      projectId: 'p',
      roundType: 'self-review',
      roundLabel: 'Self Review',
      roundIndex: 4,
      sessionId: 'c',
    }, ENV)

    const after = planRoundCompleteReducer(start, {
      issueNumber: 1,      projectId: 'p',
      roundType: 'self-review',
      roundIndex: 4,
      duration: 1234,
      verdict: 'FAIL',
    }, ENV)

    const selfReview = after.planProgress?.steps.find((s) => s.roundType === 'self-review')
    expect(selfReview).toMatchObject({
      status: 'failed',
      duration: 1234,
      verdict: 'FAIL',
    })
    expect(after.planProgress?.steps.some((s) => s.roundType === 'auto-fix')).toBe(true)
    expect(after.planProgress?.steps.some((s) => s.roundType === 're-self-review')).toBe(true)
  })

  it('does not duplicate auto-fix / re-self-review on repeated self-review FAIL', () => {
    let state = planRoundStartReducer(emptyTimelineState(), {
      issueNumber: 1,      projectId: 'p',
      roundType: 'self-review',
      roundLabel: 'Self Review',
      roundIndex: 4,
      sessionId: 'c',
    }, ENV)

    state = planRoundCompleteReducer(state, {
      issueNumber: 1,      projectId: 'p',
      roundType: 'self-review',
      roundIndex: 4,
      duration: 1000,
      verdict: 'FAIL',
    }, ENV)

    state = planRoundCompleteReducer(state, {
      issueNumber: 1,      projectId: 'p',
      roundType: 'self-review',
      roundIndex: 4,
      duration: 999,
      verdict: 'FAIL',
    }, ENV)

    const steps = state.planProgress?.steps ?? []
    expect(steps.filter((s) => s.roundType === 'auto-fix')).toHaveLength(1)
    expect(steps.filter((s) => s.roundType === 're-self-review')).toHaveLength(1)
  })

  it('does not extend steps when FAIL is not on self-review', () => {
    let state = planRoundStartReducer(emptyTimelineState(), {
      issueNumber: 1,      projectId: 'p',
      roundType: 'proposal',
      roundLabel: 'Proposal',
      roundIndex: 0,
      sessionId: 'c',
    }, ENV)

    state = planRoundCompleteReducer(state, {
      issueNumber: 1,      projectId: 'p',
      roundType: 'proposal',
      roundIndex: 0,
      duration: 100,
      verdict: 'FAIL',
    }, ENV)

    expect(state.planProgress?.steps.some((s) => s.roundType === 'auto-fix')).toBe(false)
  })
})

describe('coderRecoveryStatusReducer', () => {
  it('sets recoveryStatus and appends to last round on detected/recovering', () => {
    const state = withRounds(emptyTimelineState(), roundsOf([
      { roundIndex: 0, label: 'Proposal', startedAt: '2024-01-01T00:00:00.000Z' },
    ]))

    const next = coderRecoveryStatusReducer(state, {
      issueNumber: 1,      projectId: 'p',
      executionId: 'exec-1',
      runtimeSessionId: 'runtime-1',
      status: 'recovering',
      attempt: 2,
      reason: 'lost contact',
    }, ENV)

    expect(next.recoveryStatus).toMatchObject({
      status: 'recovering',
      attempt: 2,
      reason: 'lost contact',
    })
    expect(next.rounds[0].recoveryEvents).toHaveLength(1)
    expect(next.rounds[0].recoveryEvents[0]).toMatchObject({
      status: 'recovering',
      attempt: 2,
      reason: 'lost contact',
      timestamp: ENV.now,
    })
  })

  it('clears recoveryStatus on recovered and still appends the event', () => {
    const state = withRounds(emptyTimelineState(), roundsOf([
      { roundIndex: 0, label: 'Proposal', startedAt: '2024-01-01T00:00:00.000Z' },
    ]))

    const next = coderRecoveryStatusReducer(state, {
      issueNumber: 1,      projectId: 'p',
      executionId: 'exec-1',
      runtimeSessionId: 'runtime-1',
      status: 'recovered',
      attempt: 2,
    }, ENV)

    expect(next.recoveryStatus).toBeNull()
    expect(next.rounds[0].recoveryEvents).toHaveLength(1)
    expect(next.rounds[0].recoveryEvents[0].status).toBe('recovered')
  })

  it('does nothing to rounds when there is no current round', () => {
    const next = coderRecoveryStatusReducer(emptyTimelineState(), {
      issueNumber: 1,      projectId: 'p',
      executionId: 'exec-1',
      runtimeSessionId: 'runtime-1',
      status: 'recovering',
      attempt: 1,
    }, ENV)

    expect(next.recoveryStatus).toMatchObject({ status: 'recovering', attempt: 1 })
    expect(next.rounds).toHaveLength(0)
  })
})

describe('sessionLivenessReducer', () => {
  it('maps probing to recovering with activeProbeVersion as attempt', () => {
    const state = withRounds(emptyTimelineState(), roundsOf([
      { roundIndex: 0, label: 'Proposal', startedAt: '2024-01-01T00:00:00.000Z' },
    ]))

    const next = sessionLivenessReducer(state, {
      sessionId: 'c',
      runtimeSessionId: 'a',
      status: 'probing',
      lastDataAt: '2024-01-01T00:00:00.000Z',
      activeProbeVersion: 3,
      probeDeadlineAt: '2024-01-01T00:00:30.000Z',
    }, ENV)

    expect(next.recoveryStatus).toMatchObject({ status: 'recovering', attempt: 3 })
    expect(next.rounds[0].recoveryEvents[0]).toMatchObject({
      status: 'recovering',
      attempt: 3,
    })
  })

  it('falls back through satisfiedProbeVersion then probeVersion then 1', () => {
    const state = withRounds(emptyTimelineState(), roundsOf([
      { roundIndex: 0, label: 'Proposal', startedAt: '2024-01-01T00:00:00.000Z' },
    ]))

    const a = sessionLivenessReducer(state, {
      sessionId: 'c',
      runtimeSessionId: 'a',
      status: 'probing',
      lastDataAt: '2024-01-01T00:00:00.000Z',
      satisfiedProbeVersion: 7,
      probeVersion: 9,
    }, ENV)
    expect(a.recoveryStatus?.attempt).toBe(7)

    const b = sessionLivenessReducer(a, {
      sessionId: 'c',
      runtimeSessionId: 'a',
      status: 'probing',
      lastDataAt: '2024-01-01T00:00:00.000Z',
      probeVersion: 9,
    }, ENV)
    expect(b.recoveryStatus?.attempt).toBe(9)

    const c = sessionLivenessReducer(b, {
      sessionId: 'c',
      runtimeSessionId: 'a',
      status: 'probing',
      lastDataAt: '2024-01-01T00:00:00.000Z',
    }, ENV)
    expect(c.recoveryStatus?.attempt).toBe(1)
  })

  it('maps running to recovered, clears recoveryStatus, and uses failureReason when present for failed', () => {
    const state = withRounds(emptyTimelineState(), roundsOf([
      { roundIndex: 0, label: 'Proposal', startedAt: '2024-01-01T00:00:00.000Z' },
    ]))

    const running = sessionLivenessReducer(state, {
      sessionId: 'c',
      runtimeSessionId: 'a',
      status: 'running',
      lastDataAt: '2024-01-01T00:00:00.000Z',
    }, ENV)
    expect(running.recoveryStatus).toBeNull()
    expect(running.rounds[0].recoveryEvents[0].status).toBe('recovered')

    const failed = sessionLivenessReducer(state, {
      sessionId: 'c',
      runtimeSessionId: 'a',
      status: 'failed',
      lastDataAt: '2024-01-01T00:00:00.000Z',
      failureReason: 'no response',
      probeVersion: 4,
    }, ENV)
    expect(failed.recoveryStatus).toBeNull()
    expect(failed.rounds[0].recoveryEvents[0]).toMatchObject({
      status: 'failed',
      attempt: 4,
      reason: 'no response',
    })
  })
})

describe('usageUpdatedReducer', () => {
  it('returns prev unchanged when all four fields are absent', () => {
    const state = emptyTimelineState()
    const next = usageUpdatedReducer(state, {
      runtimeSessionId: 'a',
      sessionId: 'c',
    }, ENV)
    expect(next).toBe(state)
  })

  it('applies server-provided values verbatim and does not derive from window ratio', () => {
    const next = usageUpdatedReducer(emptyTimelineState(), {
      runtimeSessionId: 'a',
      sessionId: 'c',
      contextWindowUsed: 45_000,
      contextWindowSize: 100_000,
      contextUsagePercent: 72,
      healthStatus: 'red',
    }, ENV)

    expect(next.contextHealth).toMatchObject({
      status: 'red',
      contextWindowUsed: 45_000,
      contextWindowSize: 100_000,
      contextUsagePercent: 72,
      recordedAt: ENV.isoNow,
    })
  })

  it('merges with prev via mergeContextHealth', () => {
    const initial = emptyTimelineState()
    const a = usageUpdatedReducer(initial, {
      runtimeSessionId: 'a',
      contextWindowUsed: 100,
      contextWindowSize: 1000,
      contextUsagePercent: 10,
      healthStatus: 'green',
    }, ENV)

    const b = usageUpdatedReducer(a, {
      runtimeSessionId: 'a',
      contextWindowUsed: 100,
      contextWindowSize: 1000,
      contextUsagePercent: 10,
      healthStatus: 'green',
    }, ENV)
    expect(b.contextHealth).toBe(a.contextHealth)

    const c = usageUpdatedReducer(b, {
      runtimeSessionId: 'a',
      contextWindowUsed: 200,
      contextWindowSize: 1000,
      contextUsagePercent: 20,
      healthStatus: 'yellow',
    }, ENV)
    expect(c.contextHealth).not.toBe(b.contextHealth)
  })
})

describe('contextHealthUpdateReducer', () => {
  it('applies server-provided values verbatim', () => {
    const next = contextHealthUpdateReducer(emptyTimelineState(), {
      sessionId: 'c',
      runtimeSessionId: 'a',
      healthStatus: 'green',
      contextWindowUsed: 72_000,
      contextWindowSize: 100_000,
      contextUsagePercent: 72,
      recordedAt: '2024-01-01T00:00:00.000Z',
    }, ENV)

    expect(next.contextHealth).toMatchObject({
      status: 'green',
      contextWindowUsed: 72_000,
      contextWindowSize: 100_000,
      contextUsagePercent: 72,
      recordedAt: '2024-01-01T00:00:00.000Z',
    })
  })

  it('normalises an unknown healthStatus to null', () => {
    const next = contextHealthUpdateReducer(emptyTimelineState(), {
      sessionId: 'c',
      runtimeSessionId: 'a',
      healthStatus: undefined as unknown as 'green',
      contextWindowUsed: 1,
      contextWindowSize: 1,
      contextUsagePercent: 1,
    }, ENV)
    expect(next.contextHealth?.status).toBeNull()
  })
})

describe('compactionEventReducer', () => {
  it('appends a CompactionEntry to the last round and resets contextHealth from contextWindowUsedAfter', () => {
    const state = withRounds(emptyTimelineState(), roundsOf([
      { roundIndex: 0, label: 'Proposal', startedAt: '2024-01-01T00:00:00.000Z' },
    ]))

    const next = compactionEventReducer(state, {
      sessionId: 'c',
      runtimeSessionId: 'a',
      strategy: 'summary',
      contextWindowUsedBefore: 950_000,
      contextWindowUsedAfter: 400_000,
      contextWindowSize: 1_000_000,
      summary: 'kept original task instructions',
      recordedAt: '2024-01-01T00:00:05.000Z',
    }, ENV)

    expect(next.rounds[0].compactions).toHaveLength(1)
    expect(next.rounds[0].compactions[0]).toMatchObject({
      id: 'compaction-2024-01-01T00:00:05.000Z-abc123',
      strategy: 'summary',
      contextWindowUsedBefore: 950_000,
      contextWindowUsedAfter: 400_000,
      contextWindowSize: 1_000_000,
      summary: 'kept original task instructions',
      recordedAt: '2024-01-01T00:00:05.000Z',
      timestamp: new Date('2024-01-01T00:00:05.000Z').getTime(),
    })
    expect(next.contextHealth).toMatchObject({
      status: null,
      contextWindowUsed: 400_000,
      contextWindowSize: 1_000_000,
      contextUsagePercent: null,
    })
  })

  it('synthesises a placeholder Compaction round when no round exists', () => {
    const next = compactionEventReducer(emptyTimelineState(), {
      sessionId: 'c',
      runtimeSessionId: 'a',
      contextWindowUsedAfter: 100_000,
      recordedAt: '2024-01-01T00:00:01.000Z',
    }, ENV)

    expect(next.rounds).toHaveLength(1)
    expect(next.rounds[0]).toMatchObject({
      label: 'Compaction',
      startedAt: '2024-01-01T00:00:01.000Z',
      completedAt: '2024-01-01T00:00:01.000Z',
    })
    expect(next.rounds[0].compactions).toHaveLength(1)
  })
})

describe('contextCompactedReducer', () => {
  it('appends a CompactionEntry to the last round and resets contextHealth', () => {
    const state = withRounds(emptyTimelineState(), roundsOf([
      { roundIndex: 0, label: 'Proposal', startedAt: '2024-01-01T00:00:00.000Z' },
    ]))

    const next = contextCompactedReducer(state, {
      issueNumber: 1,      projectId: 'p',
      strategy: 'summary',
      contextWindowUsedBefore: 800_000,
      contextWindowUsedAfter: 200_000,
      contextWindowSize: 1_000_000,
      summary: 'pruned',
      recordedAt: '2024-01-01T00:00:07.000Z',
    }, ENV)

    expect(next.rounds[0].compactions).toHaveLength(1)
    expect(next.rounds[0].compactions[0]).toMatchObject({
      id: 'compaction-domain-2024-01-01T00:00:07.000Z-abc123',
      strategy: 'summary',
      contextWindowUsedAfter: 200_000,
      recordedAt: '2024-01-01T00:00:07.000Z',
    })
    expect(next.contextHealth).toMatchObject({
      status: null,
      contextWindowUsed: 200_000,
      contextWindowSize: 1_000_000,
      contextUsagePercent: null,
    })
  })

  it('synthesises a placeholder Compaction round when no round exists', () => {
    const next = contextCompactedReducer(emptyTimelineState(), {
      issueNumber: 1,      projectId: 'p',
      contextWindowUsedAfter: 100_000,
      recordedAt: '2024-01-01T00:00:01.000Z',
    }, ENV)

    expect(next.rounds).toHaveLength(1)
    expect(next.rounds[0].label).toBe('Compaction')
    expect(next.rounds[0].compactions).toHaveLength(1)
  })
})

describe('contextHealthUpdatedReducer', () => {
  it('applies server-provided values verbatim', () => {
    const next = contextHealthUpdatedReducer(emptyTimelineState(), {
      issueNumber: 1,      projectId: 'p',
      healthStatus: 'yellow',
      contextUsagePercent: 65,
      contextWindowUsed: 65_000,
      contextWindowSize: 100_000,
      recordedAt: '2024-01-01T00:00:00.000Z',
    }, ENV)

    expect(next.contextHealth).toMatchObject({
      status: 'yellow',
      contextWindowUsed: 65_000,
      contextWindowSize: 100_000,
      contextUsagePercent: 65,
      recordedAt: '2024-01-01T00:00:00.000Z',
    })
  })

  it('falls back to env.isoNow when recordedAt is missing', () => {
    const next = contextHealthUpdatedReducer(emptyTimelineState(), {
      issueNumber: 1,      projectId: 'p',
      healthStatus: 'green',
      contextWindowUsed: 1,
      contextWindowSize: 1,
      contextUsagePercent: 1,
    }, ENV)
    expect(next.contextHealth?.recordedAt).toBe(ENV.isoNow)
  })
})

describe('helpers', () => {
  it('toContextHealthStatus accepts green/yellow/red and rejects everything else', () => {
    expect(toContextHealthStatus('green')).toBe('green')
    expect(toContextHealthStatus('yellow')).toBe('yellow')
    expect(toContextHealthStatus('red')).toBe('red')
    expect(toContextHealthStatus('blue')).toBeNull()
    expect(toContextHealthStatus(null)).toBeNull()
    expect(toContextHealthStatus(undefined)).toBeNull()
  })

  it('mapLivenessToRecoveryStatus maps probing → recovering, running → recovered, else → failed', () => {
    expect(mapLivenessToRecoveryStatus('probing')).toBe('recovering')
    expect(mapLivenessToRecoveryStatus('running')).toBe('recovered')
    expect(mapLivenessToRecoveryStatus('failed')).toBe('failed')
  })

  it('BASE_PLAN_STEPS exposes the canonical five steps', () => {
    expect(BASE_PLAN_STEPS.map((s) => s.roundType)).toEqual([
      'proposal', 'specs', 'design', 'tasks', 'self-review',
    ])
  })
})