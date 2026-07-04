import { describe, expect, it } from 'vitest'
import type { WorkflowRunSession } from '../../../entities/coder-session/model/types'
import {
  compareTimelineRows,
  deriveMilestones,
  isAcpAgentTask,
  isTaskLogMilestone,
  serializeMilestoneForExport,
} from './milestones'

function sessionFixture(overrides: Partial<WorkflowRunSession> = {}): WorkflowRunSession {
  return {
    id: 'session-id',
    workflowRunId: 'wr-1',
    sessionName: 'plan',
    acpSessionId: null,
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

describe('isAcpAgentTask', () => {
  it('returns true only when origin.uses is "mohist/acp-agent" and sessionName is a non-empty string', () => {
    expect(isAcpAgentTask({ origin: { uses: 'mohist/acp-agent' }, sessionName: 'plan-1' })).toBe(true)
  })

  it('ignores classification once the agent criterion is satisfied (classification is retained but not deciding)', () => {
    expect(isAcpAgentTask({ origin: { uses: 'mohist/acp-agent' }, sessionName: 'plan-1', classification: 'Orchestration' })).toBe(true)
    expect(isAcpAgentTask({ origin: { uses: 'mohist/acp-agent' }, sessionName: 'plan-1', classification: undefined })).toBe(true)
  })

  it('returns false for ops uses (mohist/rebase, core/process) even when sessionName is present', () => {
    expect(isAcpAgentTask({ origin: { uses: 'mohist/rebase' }, sessionName: 'plan-1' })).toBe(false)
    expect(isAcpAgentTask({ origin: { uses: 'core/process' }, sessionName: 'plan-1' })).toBe(false)
  })

  it('returns false when origin.uses is the agent action but sessionName is empty', () => {
    expect(isAcpAgentTask({ origin: { uses: 'mohist/acp-agent' }, sessionName: '' })).toBe(false)
  })

  it('returns false when sessionName is missing entirely even with the agent uses', () => {
    expect(isAcpAgentTask({ origin: { uses: 'mohist/acp-agent' }, sessionName: null })).toBe(false)
    expect(isAcpAgentTask({ origin: { uses: 'mohist/acp-agent' } })).toBe(false)
  })

  it('returns false when origin.uses is non-string or missing', () => {
    expect(isAcpAgentTask({ origin: null, sessionName: 'plan-1' })).toBe(false)
    expect(isAcpAgentTask({ origin: {}, sessionName: 'plan-1' })).toBe(false)
    expect(isAcpAgentTask({ origin: { uses: undefined }, sessionName: 'plan-1' })).toBe(false)
  })

  it('returns false for empty/null/undefined input', () => {
    expect(isAcpAgentTask(null)).toBe(false)
    expect(isAcpAgentTask(undefined)).toBe(false)
  })

  it('never reads workType (workType is not a task-level field)', () => {
    const inputs: unknown[] = [
      { origin: { uses: 'mohist/acp-agent' }, sessionName: 'plan-1', workType: 'ops' },
      { origin: { uses: 'mohist/rebase' }, sessionName: 'plan-1', workType: 'agent' },
    ]
    for (const input of inputs) {
      expect(isAcpAgentTask(input as never)).toBe((input as { origin?: { uses?: string }; sessionName?: string | null }).origin?.uses === 'mohist/acp-agent')
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

describe('compareTimelineRows', () => {
  it('orders by ISO timestamp ascending', () => {
    const rows = [
      { seq: 1, timestamp: '2026-06-15T10:02:00.000Z', source: 'action:rebase', text: 'late' },
      { kind: 'model-bound' as const, timestamp: '2026-06-15T10:00:00.000Z', label: 'Model bound', detail: 'foo' },
    ]
    rows.sort(compareTimelineRows)
    expect(rows[0]).toMatchObject({ kind: 'model-bound' })
    expect(rows[1]).toMatchObject({ seq: 1 })
  })

  it('keeps ops lines in seq order at the same timestamp and places milestones after them', () => {
    const rows = [
      { kind: 'session-ended' as const, timestamp: '2026-06-15T10:01:00.000Z', label: 'Session ended', detail: 'completed' },
      { seq: 2, timestamp: '2026-06-15T10:01:00.000Z', source: 'action:rebase', text: 'b' },
      { seq: 1, timestamp: '2026-06-15T10:01:00.000Z', source: 'action:rebase', text: 'a' },
    ]
    rows.sort(compareTimelineRows)
    expect((rows[0] as { seq: number }).seq).toBe(1)
    expect((rows[1] as { seq: number }).seq).toBe(2)
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
