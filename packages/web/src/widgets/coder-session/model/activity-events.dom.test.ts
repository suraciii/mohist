import { describe, expect, it } from 'vitest'
import { buildActivityEvents } from './activity-events'
import type { ProjectEventDto } from '../../../entities/project'
import type { AgentActivitySession, AgentActivityWaiting } from '../../../entities/agent'
import type { RunnerStatusRow } from '../../../entities/runner'
function makeProjectEvent(overrides: Partial<ProjectEventDto> = {}): ProjectEventDto {
  return {
    id: 1,
    origin: 'issue',
    sourceAggregateKind: 'issue',
    sourceAggregateId: 'issue-1',
    source: '/mohist/issues/issue-1',
    type: 'com.mohist.issue.created',
    time: '2026-01-01T00:00:00.000Z',
    envelopeId: 'env-1',
    specVersion: '1.0',
    subject: '1',
    dataContentType: 'application/json',
    data: {},
    runnerId: null,
    ...overrides,
  }
}

function makeSession(overrides: Partial<AgentActivitySession> = {}): AgentActivitySession {
  return {
    issueNumber: 1,
    issueTitle: 'Test issue',
    issueStage: 'Build',
    issueStatus: null,
    sessionId: 'session-1',
    status: 'active',
    model: null,
    taskDescription: null,
    createdAt: '2026-01-01T00:00:00.000Z',
    completedAt: null,
    lastActivityAt: '2026-01-01T00:00:00.000Z',
    currentWorkItem: null,
    taskProgress: null,
    lastActivity: null,
    failureReason: null,
    ...overrides,
  }
}

function makeWaiting(overrides: Partial<AgentActivityWaiting> = {}): AgentActivityWaiting {
  return {
    issueNumber: 1,
    issueTitle: 'Test issue',
    stage: 'Review',
    label: 'Needs Approval',
    requestedAt: '2026-01-01T00:00:00.000Z',
    preview: 'Please review',
    ...overrides,
  }
}

function makeRunner(overrides: Partial<RunnerStatusRow> = {}): RunnerStatusRow {
  return {
    id: 'runner-1',
    kind: 'external',
    hostname: 'host',
    scope: { type: 'global' },
    status: 'idle',
    capabilities: [],
    coderModels: [],
    coderModelCount: 0,
    activeWorks: [],
    ...overrides,
  }
}

describe('buildActivityEvents', () => {
  it('classifies issue state change events as issue-state', () => {
    const events = buildActivityEvents({
      recordedEvents: [
        makeProjectEvent({ id: 1, type: 'com.mohist.issue.created', data: { title: 'New issue' } }),
        makeProjectEvent({ id: 2, type: 'com.mohist.issue.work-started' }),
        makeProjectEvent({ id: 3, type: 'com.mohist.issue.completed' }),
      ],
      sessions: [],
      waiting: [],
      runners: [],
    })

    expect(events).toHaveLength(3)
    expect(events.every((e) => e.type === 'issue-state')).toBe(true)
    expect(events.every((e) => e.attention === 'routine')).toBe(true)
    const nullPayloadEvents = buildActivityEvents({ recordedEvents: [makeProjectEvent({ data: null })], sessions: [], waiting: [], runners: [] })
    expect(nullPayloadEvents[0]).toMatchObject({ type: 'issue-state', title: 'Issue #1 created' })

    const scalarPayloadEvents = buildActivityEvents({ recordedEvents: [makeProjectEvent({ data: 'created' })], sessions: [], waiting: [], runners: [] })
    const arrayPayloadEvents = buildActivityEvents({ recordedEvents: [makeProjectEvent({ data: ['created'] })], sessions: [], waiting: [], runners: [] })
    expect(scalarPayloadEvents[0]).toMatchObject({ type: 'issue-state', title: 'Issue #1 created' })
    expect(arrayPayloadEvents[0]).toMatchObject({ type: 'issue-state', title: 'Issue #1 created' })
  })

  it('classifies workflow stage events and promotes failures to failure type', () => {
    const events = buildActivityEvents({
      recordedEvents: [
        makeProjectEvent({ origin: 'workflow-run', sourceAggregateKind: 'workflow-run', sourceAggregateId: 'wr-1', source: '/mohist/workflow-runs/wr-1', type: 'com.mohist.workflow.stage.started', data: { stage: 'Plan' } }),
        makeProjectEvent({ origin: 'workflow-run', sourceAggregateKind: 'workflow-run', sourceAggregateId: 'wr-1', source: '/mohist/workflow-runs/wr-1', type: 'com.mohist.workflow.stage.failed', data: { stage: 'Build', reason: 'compile error' } }),
        makeProjectEvent({ origin: 'workflow-run', sourceAggregateKind: 'workflow-run', sourceAggregateId: 'wr-1', source: '/mohist/workflow-runs/wr-1', type: 'com.mohist.workflow.stage.approval-requested', data: { stage: 'Review' } }),
        makeProjectEvent({ origin: 'workflow-run', sourceAggregateKind: 'workflow-run', sourceAggregateId: 'wr-1', source: '/mohist/workflow-runs/wr-1', type: 'com.mohist.workflow.run.paused' }),
      ],
      sessions: [],
      waiting: [],
      runners: [],
    })

    const started = events.find((e) => e.title === 'Workflow stage Plan started')
    const failed = events.find((e) => e.title === 'Workflow stage Build failed')
    const approval = events.find((e) => e.title === 'Workflow stage Review needs approval')
    const paused = events.find((e) => e.title === 'Workflow run paused')

    expect(started?.type).toBe('workflow-stage')
    expect(started?.attention).toBe('routine')
    expect(failed?.type).toBe('failure')
    expect(failed?.attention).toBe('failure')
    expect(approval?.type).toBe('workflow-stage')
    expect(approval?.attention).toBe('approval')
    expect(paused?.type).toBe('workflow-stage')
    expect(paused?.attention).toBe('blocked')
  })

  it('classifies agent session lifecycle events and context exhaustion as failure', () => {
    const events = buildActivityEvents({
      recordedEvents: [
        makeProjectEvent({ origin: 'agent-session', sourceAggregateKind: 'agent-session', sourceAggregateId: 'session-1', source: '/mohist/agent-session/session-1', type: 'com.mohist.agent-session.runtime-bound', data: { agentRuntimeSessionId: 'acp-1' } }),
        makeProjectEvent({ origin: 'agent-session', sourceAggregateKind: 'agent-session', sourceAggregateId: 'session-1', source: '/mohist/agent-session/session-1', type: 'com.mohist.agent-session.context-exhausted', data: { failureCategory: 'context exhaustion', contextUsagePercent: 96 } }),
      ],
      sessions: [makeSession({ sessionId: 'session-1', issueNumber: 42, })],
      waiting: [],
      runners: [],
    })

    const bound = events.find((e) => e.title === 'Issue #42 session runtime bound')
    const exhausted = events.find((e) => e.title === 'Issue #42 session context exhausted')

    expect(bound?.type).toBe('agent-session')
    expect(bound?.attention).toBe('routine')
    expect(exhausted?.type).toBe('failure')
    expect(exhausted?.attention).toBe('failure')
  })
  it('keeps a failed lifecycle record separate from the session lifecycle evidence', () => {
    const events = buildActivityEvents({
      recordedEvents: [
        makeProjectEvent({
          id: 1,
          origin: 'agent-session',
          sourceAggregateKind: 'agent-session',
          sourceAggregateId: 'session-1',
          source: '/mohist/agent-session/session-1',
          type: 'coder_session_started',
          time: '2026-01-01T01:00:00.000Z',
        }),
        makeProjectEvent({
          id: 2,
          origin: 'agent-session',
          sourceAggregateKind: 'agent-session',
          sourceAggregateId: 'session-1',
          source: '/mohist/agent-session/session-1',
          type: 'coder_session_completed',
          data: { status: 'failed', failureReason: 'runner timeout' },
          time: '2026-01-01T02:00:00.000Z',
        }),
      ],
      sessions: [makeSession({ sessionId: 'session-1', issueNumber: 42, })],
      waiting: [],
      runners: [],
    })

    const started = events.find((event) => event.time === '2026-01-01T01:00:00.000Z')
    const failed = events.find((event) => event.time === '2026-01-01T02:00:00.000Z')

    expect(started?.type).toBe('agent-session')
    expect(started?.attention).toBe('routine')
    expect(failed?.type).toBe('failure')
    expect(failed?.attention).toBe('failure')
    expect(failed?.description).toBe('runner timeout')
    expect(new Set(events.map((event) => event.id)).size).toBe(events.length)
  })
  it('classifies runner events as runner type with blocked attention for disconnected', () => {
    const events = buildActivityEvents({
      recordedEvents: [
        makeProjectEvent({ origin: 'agent-session', sourceAggregateKind: 'agent-session', sourceAggregateId: 'session-1', source: '/mohist/agent-session/session-1', type: 'com.mohist.runner.disconnected', runnerId: 'runner-1' }),
      ],
      sessions: [],
      waiting: [],
      runners: [],
    })

    const runner = events.find((e) => e.type === 'runner')
    expect(runner?.attention).toBe('blocked')
    expect(runner?.targets.primary?.path).toContain('/runners/runner-1')
  })
  it('keeps unrecognized recorded issue, workflow, agent, and runner events visible', () => {
    const events = buildActivityEvents({
      recordedEvents: [
        makeProjectEvent({ type: 'com.mohist.issue.renamed' }),
        makeProjectEvent({ origin: 'workflow-run', sourceAggregateKind: 'workflow-run', type: 'com.mohist.workflow.run.noted' }),
        makeProjectEvent({ origin: 'agent-session', sourceAggregateKind: 'agent-session', type: 'com.mohist.agent-session.checkpointed' }),
        makeProjectEvent({ type: 'com.mohist.runner.reconnected', runnerId: 'runner-1' }),
      ], sessions: [], waiting: [], runners: [],
    })

    expect(events.map((event) => event.type)).toEqual(['workflow-stage', 'issue-state', 'agent-session', 'runner'])
  })
  it('generates runner snapshot evidence for busy and stale runners, omitting idle', () => {
    const events = buildActivityEvents({
      recordedEvents: [],
      sessions: [],
      waiting: [],
      runners: [
        makeRunner({ id: 'r1', status: 'busy' }),
        makeRunner({ id: 'r2', status: 'stale' }),
        makeRunner({ id: 'r3', status: 'idle' }),
      ],
    })

    const busy = events.find((e) => e.title === 'Runner r1 busy')
    const stale = events.find((e) => e.title === 'Runner r2 stale/offline')
    expect(busy?.attention).toBe('routine')
    expect(stale?.attention).toBe('blocked')
    expect(events.some((e) => e.title.includes('r3'))).toBe(false)
  })
  it('generates approval attention from waiting rows', () => {
    const events = buildActivityEvents({
      recordedEvents: [],
      sessions: [],
      waiting: [makeWaiting()],
      runners: [],
    })

    const approval = events.find((e) => e.title === 'Issue #1 needs approval')
    expect(approval?.type).toBe('workflow-stage')
    expect(approval?.attention).toBe('approval')
  })
  it('does not fabricate terminal transitions from inactive session snapshots', () => {
    const events = buildActivityEvents({
      recordedEvents: [],
      sessions: [makeSession({ sessionId: 'session-1', status: 'inactive' })],
      waiting: [],
      runners: [],
    })

    const snapshot = events.find((e) => e.id === 'session-snapshot-session-1')
    expect(snapshot?.type).toBe('agent-session')
    expect(snapshot?.attention).toBe('routine')
    expect(snapshot?.description).toContain('inactive')
    expect(snapshot?.title).not.toContain('completed')
    expect(snapshot?.title).not.toContain('failed')
  })
  it('orders events by attention, then type, then time descending', () => {
    const events = buildActivityEvents({
      recordedEvents: [
        makeProjectEvent({ id: 1, origin: 'workflow-run', sourceAggregateKind: 'workflow-run', sourceAggregateId: 'wr-1', source: '/mohist/workflow-runs/wr-1', type: 'com.mohist.workflow.stage.started', data: { stage: 'Plan' }, time: '2026-01-01T03:00:00.000Z' }),
        makeProjectEvent({ id: 2, origin: 'workflow-run', sourceAggregateKind: 'workflow-run', sourceAggregateId: 'wr-1', source: '/mohist/workflow-runs/wr-1', type: 'com.mohist.workflow.stage.failed', data: { stage: 'Build' }, time: '2026-01-01T01:00:00.000Z' }),
        makeProjectEvent({ id: 3, origin: 'agent-session', sourceAggregateKind: 'agent-session', sourceAggregateId: 'session-1', source: '/mohist/agent-session/session-1', type: 'com.mohist.agent-session.context-exhausted', data: { failureCategory: 'context' }, time: '2026-01-01T02:00:00.000Z' }),
      ],
      sessions: [makeSession({ sessionId: 'session-1', issueNumber: 42, })],
      waiting: [],
      runners: [],
    })

    expect(events[0].attention).toBe('failure')
    expect(events[1].attention).toBe('failure')
    expect(events[2].attention).toBe('routine')
    expect(events[0].time).toBe('2026-01-01T02:00:00.000Z')
    expect(events[1].time).toBe('2026-01-01T01:00:00.000Z')
  })
  it('merges duplicate evidence only once', () => {
    const events = buildActivityEvents({
      recordedEvents: [
        makeProjectEvent({ id: 1, origin: 'agent-session', sourceAggregateKind: 'agent-session', sourceAggregateId: 'session-1', source: '/mohist/agent-session/session-1', type: 'com.mohist.agent-session.runtime-bound' }),
      ],
      sessions: [makeSession({ sessionId: 'session-1' })],
      waiting: [],
      runners: [],
    })

    const ids = new Set(events.map((e) => e.id))
    expect(ids.size).toBe(events.length)
  })

})
