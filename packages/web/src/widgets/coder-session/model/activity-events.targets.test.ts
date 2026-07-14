import { describe, expect, it } from 'vitest'
import { buildActivityEvents } from './activity-events'
import type { ProjectEventDto } from '../../../entities/project'
import type { AgentActivitySession } from '../../../entities/agent'

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
    extensions: { issueno: '1' },
    runnerId: null,
    ...overrides,
  }
}

function makeSession(overrides: Partial<AgentActivitySession> = {}): AgentActivitySession {
  return {
    issueId: 'issue-1',
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

function firstRecordedEvent(event: ProjectEventDto) {
  return buildActivityEvents({ recordedEvents: [event], sessions: [], waiting: [], runners: [] })[0]
}

describe('activity event targets', () => {
  it('exposes generic session identity with agent context', () => {
    const events = buildActivityEvents({
      recordedEvents: [],
      sessions: [makeSession({ issueNumber: 0, agentId: 'agent-1', agentName: 'Reviewer' })],
      waiting: [],
      runners: [],
    })

    const event = events.find((entry) => entry.id === 'session-snapshot-session-1')
    expect(event).toMatchObject({
      title: 'Agent Reviewer session active',
      targets: { session: { isGeneric: true }, agent: { agentId: 'agent-1' } },
    })
    expect(event?.targets.primary?.path).toContain('/agent-sessions/session-1')
  })

  it('links workflow-bound sessions through their issue route', () => {
    const events = buildActivityEvents({
      recordedEvents: [],
      sessions: [makeSession({ issueNumber: 42 })],
      waiting: [],
      runners: [],
    })

    const event = events.find((entry) => entry.id === 'session-snapshot-session-1')
    expect(event?.targets.session?.isGeneric).toBe(false)
    expect(event?.targets.primary?.path).toContain('/issues/42/session/session-1?from=activity')
  })

  it('keeps historical workflow session targets without a snapshot row', () => {
    const event = firstRecordedEvent(makeProjectEvent({
      origin: 'agent-session',
      sourceAggregateKind: 'agent-session',
      sourceAggregateId: 'workflow-session-42',
      source: '/mohist/agent-sessions/workflow-session-42',
      type: 'session.closed',
      subject: null,
      extensions: {},
      data: { status: 'failed', failureReason: 'runner timeout' },
      issueNumber: 42,
      sessionSourceKind: 'workflow',
      workflowRunId: 'wr-42',
      runnerId: 'runner-42',
    }))

    expect(event).toMatchObject({
      type: 'failure',
      targets: {
        workflow: { path: expect.stringContaining('/issues/42') },
        runner: { path: expect.stringContaining('/runners/runner-42') },
      },
    })
    expect(event.targets.primary?.path).toContain('/issues/42/session/workflow-session-42')
  })

  it('keeps historical generic session targets without workflow context', () => {
    const event = firstRecordedEvent(makeProjectEvent({
      origin: 'agent-session',
      sourceAggregateKind: 'agent-session',
      sourceAggregateId: 'generic-session-1',
      source: '/mohist/agent-sessions/generic-session-1',
      type: 'coder_session_started',
      subject: null,
      extensions: {},
      data: { status: 'opened' },
      sessionSourceKind: 'agent-launch',
      agentId: 'agent-1',
      agentName: 'Reviewer',
      runnerId: 'runner-1',
    }))

    expect(event.targets.primary?.path).toContain('/agent-sessions/generic-session-1')
    expect(event.targets.agent?.path).toContain('/agents/agent-1')
    expect(event.targets.runner?.path).toContain('/runners/runner-1')
    expect(event.targets.workflow).toBeUndefined()
  })

  it('uses an event issue number when workflow events have no subject', () => {
    const event = firstRecordedEvent(makeProjectEvent({
      origin: 'workflow-run',
      sourceAggregateKind: 'workflow-run',
      sourceAggregateId: 'wr-42',
      source: '/mohist/workflow-runs/wr-42',
      type: 'com.mohist.workflow.stage.started',
      subject: null,
      extensions: {},
      issueNumber: 42,
      data: { stage: 'Build' },
    }))

    expect(event.targets.primary?.path).toContain('/issues/42')
    expect(event.targets.workflow?.path).toContain('/issues/42')
  })
})
