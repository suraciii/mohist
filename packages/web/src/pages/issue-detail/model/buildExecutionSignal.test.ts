import { describe, expect, it } from 'vitest'
import { buildExecutionSignal } from './buildExecutionSignal'

describe('buildExecutionSignal', () => {
  it('returns null when no session is active and runner is not gating', () => {
    const signal = buildExecutionSignal({
      activeSession: null,
      agentStatus: {
        runnerAvailable: true,
        capacity: { active: 0, max: 4 },
      },
      blocker: null,
      summary: 'running',
    })
    expect(signal).toBeNull()
  })

  it('returns a session cue when a session is active and runner is available', () => {
    const signal = buildExecutionSignal({
      activeSession: {
        sessionName: 'review-repair',
        transcriptPath: '/proj/issues/14/workflow/sessions/review-repair',
      },
      agentStatus: {
        runnerAvailable: true,
        capacity: { active: 0, max: 4 },
      },
      blocker: null,
      summary: 'running',
    })
    expect(signal?.activeSession).toEqual({
      sessionName: 'review-repair',
      transcriptPath: '/proj/issues/14/workflow/sessions/review-repair',
    })
    expect(signal?.runnerGating).toBeNull()
  })

  it('returns runner-unavailable gating when summary is queued and runner is offline', () => {
    const signal = buildExecutionSignal({
      activeSession: null,
      agentStatus: {
        runnerAvailable: false,
        runnerMessage: 'Runner offline.',
        capacity: { active: 0, max: 4 },
      },
      blocker: null,
      summary: 'queued',
    })
    expect(signal?.runnerGating?.kind).toBe('runner-unavailable')
    expect(signal?.runnerGating?.reason).toBe('Runner offline.')
  })

  it('falls back to the default runner-unavailable message when agent status has no runnerMessage', () => {
    const signal = buildExecutionSignal({
      activeSession: null,
      agentStatus: {
        runnerAvailable: false,
        capacity: { active: 0, max: 4 },
      },
      blocker: null,
      summary: 'queued',
    })
    expect(signal?.runnerGating?.kind).toBe('runner-unavailable')
    expect(signal?.runnerGating?.reason).toBe('No runner is connected. Start a runner before this issue can run.')
  })

  it('returns capacity-full gating when summary is queued and capacity is full', () => {
    const signal = buildExecutionSignal({
      activeSession: null,
      agentStatus: {
        runnerAvailable: true,
        capacity: { active: 2, max: 2 },
      },
      blocker: null,
      summary: 'queued',
    })
    expect(signal?.runnerGating?.kind).toBe('capacity-full')
    expect(signal?.runnerGating?.reason).toBe('Runner capacity is full (2/2).')
  })

  it('does not gate when capacity.max === 0 (zero-max placeholder)', () => {
    const signal = buildExecutionSignal({
      activeSession: null,
      agentStatus: {
        runnerAvailable: true,
        capacity: { active: 0, max: 0 },
      },
      blocker: null,
      summary: 'queued',
    })
    expect(signal).toBeNull()
  })

  it('does not gate when the blocker is a draft', () => {
    const signal = buildExecutionSignal({
      activeSession: null,
      agentStatus: {
        runnerAvailable: false,
        capacity: { active: 0, max: 4 },
      },
      blocker: { kind: 'draft' },
      summary: 'queued',
    })
    expect(signal).toBeNull()
  })

  it('does not gate when the blocker is a waiting-for issue', () => {
    const signal = buildExecutionSignal({
      activeSession: null,
      agentStatus: {
        runnerAvailable: false,
        capacity: { active: 0, max: 4 },
      },
      blocker: { kind: 'waiting-for', issue: { number: 7, title: 'Blocker', health: 'active', status: 'in_progress' } },
      summary: 'queued',
    })
    expect(signal).toBeNull()
  })

  it('does not gate when the summary is not queued (e.g. running)', () => {
    const signal = buildExecutionSignal({
      activeSession: null,
      agentStatus: {
        runnerAvailable: false,
        capacity: { active: 0, max: 4 },
      },
      blocker: null,
      summary: 'running',
    })
    expect(signal).toBeNull()
  })

  it('returns both signals together when a session is active and runner is gating', () => {
    const signal = buildExecutionSignal({
      activeSession: {
        sessionName: 'build-task',
        transcriptPath: '/p/issues/1/workflow/sessions/build-task',
      },
      agentStatus: {
        runnerAvailable: false,
        runnerMessage: 'Offline.',
        capacity: { active: 0, max: 4 },
      },
      blocker: null,
      summary: 'queued',
    })
    expect(signal?.activeSession?.sessionName).toBe('build-task')
    expect(signal?.runnerGating?.kind).toBe('runner-unavailable')
  })
})