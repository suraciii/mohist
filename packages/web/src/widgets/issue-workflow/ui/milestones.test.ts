import { describe, expect, it } from 'vitest'
import type { WorkflowRunSession } from '../../../entities/coder-session/model/types'
import {
  deriveMilestones,
  isInlineAgentTask,
  isTaskLogMilestone,
  mergeTimelineRows,
  serializeMilestoneForExport,
} from './milestones'

function sessionFixture(overrides: Partial<WorkflowRunSession> = {}): WorkflowRunSession {
  return {
    id: 'session-id',
    workflowRunId: 'wr-1',
    sessionName: 'plan',
    runtimeSessionId: null,
    projectId: null,
    issueNumber: null,
    runnerId: null,
    // Issue 484: the default fixture is an executing session (`active`). The
    // session-ended/"Session idle" milestone is only emitted when activity
    // drops back to `idle` (or `unknown` for failures), so tests that exercise
    // that branch now set `activity` explicitly.
    activity: 'active',
    stage: null,
    model: null,
    workDir: null,
    processPid: null,
    createdAt: '2026-06-15T10:00:00.000Z',
    startedAt: null,
    completedAt: null,
    lastDataAt: null,
    failureReason: null,
    exitCode: null,
    ...overrides,
  }
}

describe('isInlineAgentTask', () => {
  it('returns true when origin.uses is "mohist/opencode" and sessionName is non-empty', () => {
    expect(isInlineAgentTask({ origin: { uses: 'mohist/opencode' }, sessionName: 'plan-1', classification: 'UserFacing' })).toBe(true)
  })

  it('returns true for the Pi Workflow Action when sessionName is non-empty', () => {
    expect(isInlineAgentTask({ origin: { uses: 'mohist/pi' }, sessionName: 'plan-1', classification: 'UserFacing' })).toBe(true)
  })

  it('does not require classification for eligibility; classification is retained context only', () => {
    expect(isInlineAgentTask({ origin: { uses: 'mohist/opencode' }, sessionName: 'plan-1', classification: undefined })).toBe(true)
    expect(isInlineAgentTask({ origin: { uses: 'mohist/opencode' }, sessionName: 'plan-1', classification: null })).toBe(true)
    expect(isInlineAgentTask({ origin: { uses: 'mohist/opencode' }, sessionName: 'plan-1', classification: '' })).toBe(true)
  })

  it('returns false for ops uses (mohist/rebase, core/process) even when sessionName is present', () => {
    expect(isInlineAgentTask({ origin: { uses: 'mohist/rebase' }, sessionName: 'plan-1', classification: 'UserFacing' })).toBe(false)
    expect(isInlineAgentTask({ origin: { uses: 'core/process' }, sessionName: 'plan-1', classification: 'UserFacing' })).toBe(false)
  })

  it('returns false when origin.uses is the agent action but sessionName is empty', () => {
    expect(isInlineAgentTask({ origin: { uses: 'mohist/opencode' }, sessionName: '', classification: 'UserFacing' })).toBe(false)
    expect(isInlineAgentTask({ origin: { uses: 'mohist/opencode' }, sessionName: '   ', classification: 'UserFacing' })).toBe(false)
  })

  it('returns false when sessionName is missing entirely even with the agent uses', () => {
    expect(isInlineAgentTask({ origin: { uses: 'mohist/opencode' }, sessionName: null, classification: 'UserFacing' })).toBe(false)
    expect(isInlineAgentTask({ origin: { uses: 'mohist/opencode' }, classification: 'UserFacing' })).toBe(false)
  })

  it('returns false when origin.uses is non-string or missing', () => {
    expect(isInlineAgentTask({ origin: null, sessionName: 'plan-1', classification: 'UserFacing' })).toBe(false)
    expect(isInlineAgentTask({ origin: {}, sessionName: 'plan-1', classification: 'UserFacing' })).toBe(false)
    expect(isInlineAgentTask({ origin: { uses: undefined }, sessionName: 'plan-1', classification: 'UserFacing' })).toBe(false)
  })

  it('returns false for empty/null/undefined input', () => {
    expect(isInlineAgentTask(null)).toBe(false)
    expect(isInlineAgentTask(undefined)).toBe(false)
  })

  it('never reads workType (workType is not a task-level field)', () => {
    const inputs: unknown[] = [
      { origin: { uses: 'mohist/opencode' }, sessionName: 'plan-1', classification: 'UserFacing', workType: 'ops' },
      { origin: { uses: 'mohist/rebase' }, sessionName: 'plan-1', classification: 'UserFacing', workType: 'agent' },
    ]
    for (const input of inputs) {
      expect(isInlineAgentTask(input as never)).toBe(['mohist/opencode', 'mohist/pi'].includes((input as { origin?: { uses?: string } }).origin?.uses ?? ''))
    }
  })
})

describe('deriveMilestones', () => {
  it('returns [] for null or missing session', () => {
    expect(deriveMilestones(null)).toEqual([])
    expect(deriveMilestones(undefined)).toEqual([])
  })

  it('emits the model-bound milestone from eventSummary.resolvedModel with startedAt timestamp', () => {
    const out = deriveMilestones(sessionFixture({
      startedAt: '2026-06-15T10:01:00.000Z',
      eventSummary: { resolvedModel: 'minimax/MiniMax-M3' },
    }))
    expect(out).toHaveLength(1)
    expect(out[0]).toEqual({
      kind: 'model-bound',
      timestamp: '2026-06-15T10:01:00.000Z',
      label: 'Model bound',
      detail: 'minimax/MiniMax-M3',
    })
  })

  it('falls back to createdAt when startedAt is null but resolvedModel is set', () => {
    const out = deriveMilestones(sessionFixture({
      startedAt: null,
      createdAt: '2026-06-15T10:00:00.000Z',
      eventSummary: { resolvedModel: 'mohist/coder-agent' },
    }))
    expect(out[0]).toMatchObject({ kind: 'model-bound', timestamp: '2026-06-15T10:00:00.000Z', detail: 'mohist/coder-agent' })
  })

  it('falls back to session.model when resolvedModel is missing', () => {
    const out = deriveMilestones(sessionFixture({
      model: 'minimax/MiniMax-M3',
      startedAt: '2026-06-15T10:01:00.000Z',
      eventSummary: {},
    }))
    expect(out[0]).toMatchObject({ detail: 'minimax/MiniMax-M3' })
  })

  it('omits the model-bound milestone when no resolved or fallback model is set, and emits only the session-ended milestone for an idle session', () => {
    // Issue 484: with no model anchor only the "Session idle" milestone is
    // emitted, and only when activity has dropped back from `active`. The
    // timestamp comes from lastDataAt (falling back to createdAt), and the
    // detail is the activity value.
    expect(deriveMilestones(sessionFixture({
      model: null,
      eventSummary: {},
      activity: 'idle',
      lastDataAt: '2026-06-15T10:02:00.000Z',
    }))).toEqual([
      {
        kind: 'session-ended',
        timestamp: '2026-06-15T10:02:00.000Z',
        label: 'Session idle',
        detail: 'idle',
      },
    ])
  })

  it('omits the model-bound milestone when resolvedModel is an empty/whitespace string and no model fallback exists', () => {
    expect(deriveMilestones(sessionFixture({
      model: null,
      eventSummary: { resolvedModel: '   ' },
    }))).toEqual([])
  })

  it('emits the session-ended milestone for an idle session, using lastDataAt as the timestamp and the activity as the detail', () => {
    const out = deriveMilestones(sessionFixture({
      activity: 'idle',
      lastDataAt: '2026-06-15T10:02:00.000Z',
    }))
    expect(out).toHaveLength(1)
    expect(out[0]).toEqual({
      kind: 'session-ended',
      timestamp: '2026-06-15T10:02:00.000Z',
      label: 'Session idle',
      detail: 'idle',
    })
  })

  it('falls back to createdAt for the session-ended timestamp when lastDataAt is missing', () => {
    const out = deriveMilestones(sessionFixture({
      activity: 'idle',
      createdAt: '2026-06-15T10:00:00.000Z',
      lastDataAt: null,
    }))
    expect(out[0]).toMatchObject({ kind: 'session-ended', timestamp: '2026-06-15T10:00:00.000Z' })
  })

  it('marks the session-ended milestone as failed when activity is unknown (unconfirmable session)', () => {
    // Issue 484: the failed flag is now driven by `activity === 'unknown'`
    // (a session whose state can't be resolved) rather than by a terminal
    // `status: 'failed'`. The milestone detail carries only the activity
    // value; failureReason is no longer appended to the milestone detail.
    const out = deriveMilestones(sessionFixture({
      activity: 'unknown',
      lastDataAt: '2026-06-15T10:02:00.000Z',
      failureReason: 'something blew up\nwith a newline',
    }))
    expect(out[0]).toEqual({
      kind: 'session-ended',
      timestamp: '2026-06-15T10:02:00.000Z',
      label: 'Session idle',
      detail: 'unknown',
      failed: true,
    })
  })

  it('does not flag the session-ended milestone as failed when activity resolves to idle', () => {
    // Issue 484: `idle` (execution ended cleanly) is not a failure; only the
    // unconfirmable `unknown` activity triggers the failed styling. The legacy
    // failureReason-text gating no longer applies.
    const out = deriveMilestones(sessionFixture({
      activity: 'idle',
      lastDataAt: '2026-06-15T10:02:00.000Z',
      failureReason: '',
    }))
    expect(out[0]).not.toHaveProperty('failed', true)
    expect(out[0].detail).toBe('idle')
  })

  it('returns both milestones when a session that was active has returned to idle', () => {
    // Issue 484: model-bound is anchored on startedAt; session-ended ("Session
    // idle") fires once activity drops back from `active` to `idle`.
    const out = deriveMilestones(sessionFixture({
      startedAt: '2026-06-15T10:01:00.000Z',
      activity: 'idle',
      lastDataAt: '2026-06-15T10:02:00.000Z',
      eventSummary: { resolvedModel: 'minimax/MiniMax-M3' },
    }))
    expect(out.map((m) => m.kind)).toEqual(['model-bound', 'session-ended'])
  })

  it('returns [] when neither anchor is present', () => {
    expect(deriveMilestones(sessionFixture({
      startedAt: null,
      completedAt: null,
      model: null,
      eventSummary: {},
    }))).toEqual([])
  })

  it('omits the session-ended milestone when completedAt is missing (only the model-bound remains)', () => {
    const out = deriveMilestones(sessionFixture({
      startedAt: '2026-06-15T10:01:00.000Z',
      completedAt: null,
      model: 'minimax/MiniMax-M3',
      eventSummary: { resolvedModel: 'minimax/MiniMax-M3' },
    }))
    expect(out.map((m) => m.kind)).toEqual(['model-bound'])
  })
})

describe('mergeTimelineRows', () => {
  it('inserts milestone rows by timestamp around the seq-ordered ops stream', () => {
    const rows = mergeTimelineRows(
      [
        { seq: 1, timestamp: '2026-06-15T10:00:00.000Z', source: 'action:rebase', text: 'before' },
        { seq: 2, timestamp: '2026-06-15T10:02:00.000Z', source: 'action:rebase', text: 'after' },
      ],
      [
        { kind: 'model-bound' as const, timestamp: '2026-06-15T10:01:00.000Z', label: 'Model bound', detail: 'foo' },
      ],
    )
    expect(rows.map((row) => (isTaskLogMilestone(row) ? row.kind : row.text))).toEqual([
      'before',
      'model-bound',
      'after',
    ])
  })

  it('sorts the mixed timeline globally by timestamp even when ops seq order disagrees', () => {
    const rows = mergeTimelineRows(
      [
        { seq: 1, timestamp: '2026-06-15T10:05:00.000Z', source: 'action:rebase', text: 'seq-1-late-clock' },
        { seq: 2, timestamp: '2026-06-15T10:00:00.000Z', source: 'action:rebase', text: 'seq-2-early-clock' },
      ],
      [
        { kind: 'session-ended' as const, timestamp: '2026-06-15T10:04:00.000Z', label: 'Session ended', detail: 'completed' },
      ],
    )
    const rendered = rows.map((row) => (isTaskLogMilestone(row) ? row.kind : row.text))
    expect(rendered).toEqual(['seq-2-early-clock', 'session-ended', 'seq-1-late-clock'])
  })

  it('preserves ops seq order when there are no visible milestones', () => {
    const rows = mergeTimelineRows(
      [
        { seq: 1, timestamp: '2026-06-15T10:05:00.000Z', source: 'action:rebase', text: 'seq-1-late-clock' },
        { seq: 2, timestamp: '2026-06-15T10:00:00.000Z', source: 'action:rebase', text: 'seq-2-early-clock' },
      ],
      [],
    )
    expect(rows.map((row) => (isTaskLogMilestone(row) ? row.kind : row.text))).toEqual([
      'seq-1-late-clock',
      'seq-2-early-clock',
    ])
  })

  it('keeps ops lines in seq order at the same timestamp and places milestones after them', () => {
    const rows = mergeTimelineRows(
      [
        { seq: 2, timestamp: '2026-06-15T10:01:00.000Z', source: 'action:rebase', text: 'b' },
        { seq: 1, timestamp: '2026-06-15T10:01:00.000Z', source: 'action:rebase', text: 'a' },
      ],
      [
        { kind: 'session-ended' as const, timestamp: '2026-06-15T10:01:00.000Z', label: 'Session ended', detail: 'completed' },
      ],
    )
    expect(rows[0]).toMatchObject({ seq: 1 })
    expect(rows[1]).toMatchObject({ seq: 2 })
    expect(rows[2]).toMatchObject({ kind: 'session-ended' })
  })
})

describe('isTaskLogMilestone', () => {
  it('returns true for milestones and false for ops lines', () => {
    expect(isTaskLogMilestone({ kind: 'model-bound', timestamp: 'x', label: 'l', detail: 'd' })).toBe(true)
    expect(isTaskLogMilestone({ seq: 1, timestamp: 'x', source: 's', text: 't' })).toBe(false)
  })
})

describe('serializeMilestoneForExport', () => {
  it('formats as "<timestamp> [session] <label>: <detail>"', () => {
    expect(serializeMilestoneForExport({
      kind: 'session-ended',
      timestamp: '2026-06-15T10:02:00.000Z',
      label: 'Session ended',
      detail: 'failed\nboom',
      failed: true,
    })).toBe('2026-06-15T10:02:00.000Z [session] Session ended: failed\nboom')
  })
})
