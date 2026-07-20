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
    status: 'completed',
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
      expect(isInlineAgentTask(input as never)).toBe((input as { origin?: { uses?: string } }).origin?.uses === 'mohist/opencode')
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

  it('omits the model-bound milestone when no resolved or fallback model is set', () => {
    expect(deriveMilestones(sessionFixture({
      model: null,
      eventSummary: {},
      completedAt: '2026-06-15T10:02:00.000Z',
      status: 'completed',
    }))).toEqual([
      {
        kind: 'session-ended',
        timestamp: '2026-06-15T10:02:00.000Z',
        label: 'Session ended',
        detail: 'completed',
      },
    ])
  })

  it('omits the model-bound milestone when resolvedModel is an empty/whitespace string and no model fallback exists', () => {
    expect(deriveMilestones(sessionFixture({
      model: null,
      eventSummary: { resolvedModel: '   ' },
    }))).toEqual([])
  })

  it('emits the session-ended milestone when completedAt is set, using the raw status verbatim', () => {
    const out = deriveMilestones(sessionFixture({
      completedAt: '2026-06-15T10:02:00.000Z',
      status: 'completed',
    }))
    expect(out).toHaveLength(1)
    expect(out[0]).toEqual({
      kind: 'session-ended',
      timestamp: '2026-06-15T10:02:00.000Z',
      label: 'Session ended',
      detail: 'completed',
    })
  })

  it('marks session-ended as failed and appends failureReason when present', () => {
    const out = deriveMilestones(sessionFixture({
      completedAt: '2026-06-15T10:02:00.000Z',
      status: 'failed',
      failureReason: 'something blew up\nwith a newline',
    }))
    expect(out[0]).toEqual({
      kind: 'session-ended',
      timestamp: '2026-06-15T10:02:00.000Z',
      label: 'Session ended',
      detail: 'failed\nsomething blew up\nwith a newline',
      failed: true,
    })
  })

  it('treats empty-string failureReason as not failed (only non-empty triggers the flag)', () => {
    const out = deriveMilestones(sessionFixture({
      completedAt: '2026-06-15T10:02:00.000Z',
      status: 'failed',
      failureReason: '',
    }))
    expect(out[0]).not.toHaveProperty('failed', true)
    expect(out[0].detail).toBe('failed')
  })

  it('returns both milestones in a finished session', () => {
    const out = deriveMilestones(sessionFixture({
      startedAt: '2026-06-15T10:01:00.000Z',
      completedAt: '2026-06-15T10:02:00.000Z',
      status: 'completed',
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
