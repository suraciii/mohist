import { describe, expect, it } from 'vitest'
import { IssueHealth, IssueStatus } from '../../../entities/issue'
import type { RuntimeDecision, RuntimeAvailableAction } from '../../../widgets/issue-workflow'
import type { WorkflowRunSession } from '../../../entities/coder-session'
import {
  deriveIssueDecisionActions,
  selectTranscriptSession,
  type IssueDecisionContextInput,
} from './issueDecisionActions'

function makeIssue(overrides: Partial<IssueDecisionContextInput['issue']> = {}): IssueDecisionContextInput['issue'] {
  return {
    number: 14,
    status: IssueStatus.InProgress,
    workflowStatus: 'running',
    health: IssueHealth.Active,
    isDraft: false,
    canStart: false,
    workflowStage: null,
    workflowRunId: 'wr-1',
    archivedAt: undefined,
    children: [],
    childIssuesSummary: null,
    blocker: null,
    ...overrides,
  }
}

function makeDecision(overrides: Partial<RuntimeDecision> = {}): RuntimeDecision {
  const stop: RuntimeAvailableAction = { kind: 'stop', label: 'Stop', enabled: true }
  return {
    summary: 'running',
    headline: 'Workflow running',
    rationale: 'The workflow is currently executing.',
    currentTask: null,
    nextAction: 'No user action required right now.',
    primary: stop,
    actions: [stop],
    stopRecoverable: true,
    waitReason: null,
    driftNote: null,
    blockedReason: null,
    approvalStage: null,
    ...overrides,
  }
}

function makeContext(overrides: Partial<IssueDecisionContextInput> = {}): IssueDecisionContextInput {
  return {
    decision: null,
    issue: makeIssue(),
    agentStatus: null,
    workflowSessions: [],
    projectPath: (path: string) => path,
    ...overrides,
  }
}

describe('deriveIssueDecisionActions', () => {
  it('copies runtime actions from RuntimeDecision when one exists', () => {
    const stop: RuntimeAvailableAction = { kind: 'stop', label: 'Stop', enabled: true }
    const retry: RuntimeAvailableAction = { kind: 'retry', label: 'Retry', enabled: true }
    const result = deriveIssueDecisionActions(makeContext({
      decision: makeDecision({ primary: stop, actions: [stop, retry] }),
      issue: makeIssue({ status: IssueStatus.Cancelled }),
    }))

    expect(result.actions.map((a) => a.kind)).toEqual(['stop', 'retry'])
    expect(result.primary?.kind).toBe('stop')
    expect(result.primary?.enabled).toBe(true)
  })

  it('marks workflow stop as destructive with confirmation mode', () => {
    const stop: RuntimeAvailableAction = { kind: 'stop', label: 'Stop', enabled: true }
    const result = deriveIssueDecisionActions(makeContext({
      decision: makeDecision({ primary: stop, actions: [stop] }),
    }))

    const stopAction = result.actions.find((a) => a.kind === 'stop')
    expect(stopAction?.destructive).toBe(true)
    expect(stopAction?.mode).toBe('confirmation')
  })

  it('routes send-back through feedback mode', () => {
    const approve: RuntimeAvailableAction = { kind: 'approve', label: 'Approve', enabled: true }
    const sendBack: RuntimeAvailableAction = { kind: 'send-back', label: 'Send back', enabled: true }
    const result = deriveIssueDecisionActions(makeContext({
      decision: makeDecision({
        summary: 'approval-required',
        primary: approve,
        actions: [approve, sendBack],
        approvalStage: 'check',
      }),
    }))

    expect(result.actions.find((a) => a.kind === 'approve')?.mode).toBe('immediate')
    expect(result.actions.find((a) => a.kind === 'send-back')?.mode).toBe('feedback')
  })

  it('preserves disabled reason and enables=false from the runtime decision', () => {
    const start: RuntimeAvailableAction = {
      kind: 'start',
      label: 'Start',
      enabled: false,
      reason: 'Mark the issue ready before starting.',
    }
    const result = deriveIssueDecisionActions(makeContext({
      decision: makeDecision({
        summary: 'queued',
        primary: start,
        actions: [start],
        stopRecoverable: null,
      }),
    }))

    const startAction = result.actions.find((a) => a.kind === 'start')
    expect(startAction?.enabled).toBe(false)
    expect(startAction?.reason).toMatch(/mark the issue ready/i)
  })

  it('adds mark-ready for a draft issue without a runtime decision', () => {
    const result = deriveIssueDecisionActions(makeContext({
      decision: null,
      issue: makeIssue({ status: IssueStatus.Backlog, isDraft: true, canStart: false }),
    }))

    expect(result.actions.find((a) => a.kind === 'mark-ready')).toBeTruthy()
  })

  it('omits mark-ready when the issue is not a draft', () => {
    const result = deriveIssueDecisionActions(makeContext({
      decision: null,
      issue: makeIssue({ isDraft: false }),
    }))

    expect(result.actions.find((a) => a.kind === 'mark-ready')).toBeFalsy()
  })

  it('omits mark-ready on archived issues', () => {
    const result = deriveIssueDecisionActions(makeContext({
      decision: null,
      issue: makeIssue({ isDraft: true, archivedAt: '2026-07-20T00:00:00Z' }),
    }))

    expect(result.actions.find((a) => a.kind === 'mark-ready')).toBeFalsy()
  })

  it('omits mark-ready on terminal issues (done / cancelled)', () => {
    for (const status of [IssueStatus.Done, IssueStatus.Cancelled]) {
      const result = deriveIssueDecisionActions(makeContext({
        decision: null,
        issue: makeIssue({ isDraft: true, status }),
      }))
      expect(result.actions.find((a) => a.kind === 'mark-ready')).toBeFalsy()
    }
  })

  it('emits close for an active non-parent issue without an active agent', () => {
    const result = deriveIssueDecisionActions(makeContext({
      decision: null,
      issue: makeIssue({ health: IssueHealth.Active }),
      agentStatus: null,
    }))

    expect(result.actions.find((a) => a.kind === 'close')).toBeTruthy()
  })

  it('emits close for composite parent issues when no agent is running on them', () => {
    const result = deriveIssueDecisionActions(makeContext({
      decision: null,
      issue: makeIssue({ children: [{ number: 12, title: 'kid', status: IssueStatus.InProgress, health: IssueHealth.Active, repositoryName: null }] }),
    }))

    expect(result.actions.find((a) => a.kind === 'close')).toBeTruthy()
  })

  it('omits close for issues with an active agent on the same issue', () => {
    const result = deriveIssueDecisionActions(makeContext({
      decision: null,
      issue: makeIssue({}),
      agentStatus: { runnerAvailable: true, runnerMessage: null, capacity: { active: 0, max: 1 }, activeAgents: [{ issueNumber: 14, projectId: 'proj-1' }] },
    }))

    expect(result.actions.find((a) => a.kind === 'close')).toBeFalsy()
  })

  it('omits close on archived / done / cancelled issues', () => {
    for (const status of [IssueStatus.Done, IssueStatus.Cancelled]) {
      const result = deriveIssueDecisionActions(makeContext({
        decision: null,
        issue: makeIssue({ status }),
      }))
      expect(result.actions.find((a) => a.kind === 'close')).toBeFalsy()
    }
    const archivedResult = deriveIssueDecisionActions(makeContext({
      decision: null,
      issue: makeIssue({ archivedAt: '2026-07-20T00:00:00Z' }),
    }))
    expect(archivedResult.actions.find((a) => a.kind === 'close')).toBeFalsy()
  })

  it('emits mark-as-done only for stopped/completed leaf issues without children or running agent', () => {
    for (const status of ['stopped', 'completed']) {
      const result = deriveIssueDecisionActions(makeContext({
        decision: null,
        issue: makeIssue({ status: IssueStatus.InProgress, workflowStatus: status }),
      }))
      expect(result.actions.find((a) => a.kind === 'mark-as-done')).toBeTruthy()
    }
  })

  it('hides mark-as-done on composite parents, active agent issues, and other workflow statuses', () => {
    const parentResult = deriveIssueDecisionActions(makeContext({
      decision: null,
      issue: makeIssue({
        status: IssueStatus.InProgress,
        workflowStatus: 'stopped',
        children: [{ number: 12, title: 'kid', status: IssueStatus.InProgress, health: IssueHealth.Active, repositoryName: null }],
      }),
    }))
    expect(parentResult.actions.find((a) => a.kind === 'mark-as-done')).toBeFalsy()

    const agentResult = deriveIssueDecisionActions(makeContext({
      decision: null,
      issue: makeIssue({ status: IssueStatus.InProgress, workflowStatus: 'stopped' }),
      agentStatus: { runnerAvailable: true, runnerMessage: null, capacity: { active: 0, max: 1 }, activeAgents: [{ issueNumber: 14, projectId: 'proj-1' }] },
    }))
    expect(agentResult.actions.find((a) => a.kind === 'mark-as-done')).toBeFalsy()

    const runningResult = deriveIssueDecisionActions(makeContext({
      decision: null,
      issue: makeIssue({ status: IssueStatus.InProgress, workflowStatus: 'running' }),
    }))
    expect(runningResult.actions.find((a) => a.kind === 'mark-as-done')).toBeFalsy()
  })

  it('offers Ask Agent for an in-progress non-terminal issue', () => {
    const result = deriveIssueDecisionActions(makeContext({
      decision: null,
      issue: makeIssue({}),
    }))

    const askAgent = result.actions.find((a) => a.kind === 'ask-agent')
    expect(askAgent?.enabled).toBe(true)
    expect(askAgent?.to).toBe('/agent-sessions/new?issue=14')
  })

  it('hides Ask Agent on archived / terminal / backlog issues', () => {
    const archived = deriveIssueDecisionActions(makeContext({
      decision: null,
      issue: makeIssue({ archivedAt: '2026-07-20T00:00:00Z' }),
    }))
    expect(archived.actions.find((a) => a.kind === 'ask-agent')).toBeFalsy()

    const backlog = deriveIssueDecisionActions(makeContext({
      decision: null,
      issue: makeIssue({ status: IssueStatus.Backlog }),
    }))
    expect(backlog.actions.find((a) => a.kind === 'ask-agent')).toBeFalsy()

    const done = deriveIssueDecisionActions(makeContext({
      decision: null,
      issue: makeIssue({ status: IssueStatus.Done }),
    }))
    expect(done.actions.find((a) => a.kind === 'ask-agent')).toBeFalsy()
  })

  it('omits mark-as-done for composite parents even when a workflow decision is present', () => {
    const stop: RuntimeAvailableAction = { kind: 'stop', label: 'Stop', enabled: true }
    const result = deriveIssueDecisionActions(makeContext({
      decision: makeDecision({ primary: stop, actions: [stop] }),
      issue: makeIssue({ children: [{ number: 12, title: 'kid', status: IssueStatus.InProgress, health: IssueHealth.Active, repositoryName: null }] }),
    }))

    // composite parents should not carry a workflow decision in practice, but ensure
    // mark-as-done is never offered on a parent
    expect(result.actions.find((a) => a.kind === 'mark-as-done')).toBeFalsy()
  })

  it('emits a transcript action only when a concrete session exists and labels it with the session name', () => {
    const session: Pick<WorkflowRunSession, 'sessionName' | 'status' | 'startedAt' | 'createdAt'> = {
      sessionName: 'review-1',
      status: 'completed',
      startedAt: '2026-07-20T00:00:00Z',
      createdAt: '2026-07-19T00:00:00Z',
    }
    const result = deriveIssueDecisionActions(makeContext({
      decision: null,
      workflowSessions: [session],
    }))

    const transcript = result.actions.find((a) => a.kind === 'view-transcript')
    expect(transcript).toBeTruthy()
    expect(transcript?.enabled).toBe(true)
    expect(transcript?.to).toBe('/issues/14/workflow/sessions/review-1')
    expect(transcript?.label).toContain('review-1')
    expect(result.transcript?.sessionName).toBe('review-1')
  })

  it('omits transcript when no workflow sessions exist', () => {
    const result = deriveIssueDecisionActions(makeContext({
      decision: null,
      workflowSessions: [],
    }))

    expect(result.actions.find((a) => a.kind === 'view-transcript')).toBeFalsy()
    expect(result.transcript).toBeNull()
  })

  it('keeps transcript navigation available on archived and terminal issues when sessions exist', () => {
    const session: Pick<WorkflowRunSession, 'sessionName' | 'status' | 'startedAt' | 'createdAt'> = {
      sessionName: 'review-1',
      status: 'completed',
      startedAt: '2026-07-20T00:00:00Z',
      createdAt: '2026-07-19T00:00:00Z',
    }
    const archived = deriveIssueDecisionActions(makeContext({
      decision: null,
      workflowSessions: [session],
      issue: makeIssue({ archivedAt: '2026-07-20T00:00:00Z' }),
    }))
    expect(archived.actions.find((a) => a.kind === 'view-transcript')?.enabled).toBe(true)

    const done = deriveIssueDecisionActions(makeContext({
      decision: null,
      workflowSessions: [session],
      issue: makeIssue({ status: IssueStatus.Done }),
    }))
    expect(done.actions.find((a) => a.kind === 'view-transcript')?.enabled).toBe(true)
  })

  it('omits transcript when there is no workflowRunId', () => {
    const session: Pick<WorkflowRunSession, 'sessionName' | 'status' | 'startedAt' | 'createdAt'> = {
      sessionName: 'review-1',
      status: 'completed',
      startedAt: '2026-07-20T00:00:00Z',
      createdAt: '2026-07-19T00:00:00Z',
    }
    const result = deriveIssueDecisionActions(makeContext({
      decision: null,
      workflowSessions: [session],
      issue: makeIssue({ workflowRunId: null }),
    }))
    expect(result.actions.find((a) => a.kind === 'view-transcript')).toBeFalsy()
  })

  it('omits every action for an archived issue without a runtime decision', () => {
    const result = deriveIssueDecisionActions(makeContext({
      decision: null,
      issue: makeIssue({ archivedAt: '2026-07-20T00:00:00Z' }),
    }))

    expect(result.actions).toHaveLength(0)
  })

  it('preserves lifecycle action applicability for stopped completed workflows', () => {
    const result = deriveIssueDecisionActions(makeContext({
      decision: null,
      issue: makeIssue({ status: IssueStatus.InProgress, workflowStatus: 'stopped' }),
    }))
    expect(result.actions.find((a) => a.kind === 'mark-as-done')).toBeTruthy()
    expect(result.actions.find((a) => a.kind === 'close')).toBeTruthy()
  })

  it('never enables an action that the runtime decision or existing lifecycle predicates do not authorize', () => {
    const disabledStart: RuntimeAvailableAction = { kind: 'start', label: 'Start', enabled: false, reason: 'Draft blocker' }
    const result = deriveIssueDecisionActions(makeContext({
      decision: makeDecision({ summary: 'queued', primary: disabledStart, actions: [disabledStart], stopRecoverable: null }),
      issue: makeIssue({ status: IssueStatus.Cancelled }),
    }))

    expect(result.actions.find((a) => a.kind === 'start')?.enabled).toBe(false)
    expect(result.actions.find((a) => a.kind === 'mark-as-done')).toBeFalsy()
  })

  it('orders workflow actions before lifecycle / delegation actions', () => {
    const stop: RuntimeAvailableAction = { kind: 'stop', label: 'Stop', enabled: true }
    const result = deriveIssueDecisionActions(makeContext({
      decision: makeDecision({ primary: stop, actions: [stop] }),
      issue: makeIssue({ isDraft: true }),
      workflowSessions: [{ sessionName: 'review-1', status: 'completed', startedAt: '2026-07-20T00:00:00Z', createdAt: '2026-07-19T00:00:00Z' }],
    }))

    const kinds = result.actions.map((a) => a.kind)
    expect(kinds[0]).toBe('stop')
    expect(kinds.indexOf('mark-ready')).toBeGreaterThan(0)
    expect(kinds.indexOf('ask-agent')).toBeGreaterThan(kinds.indexOf('mark-ready'))
    expect(kinds.indexOf('view-transcript')).toBeGreaterThan(kinds.indexOf('ask-agent'))
  })

  it('exposes a primary action usable as the phone launcher when one exists', () => {
    const stop: RuntimeAvailableAction = { kind: 'stop', label: 'Stop', enabled: true }
    const result = deriveIssueDecisionActions(makeContext({
      decision: makeDecision({ primary: stop, actions: [stop] }),
    }))
    expect(result.primary?.kind).toBe('stop')
  })

  it('falls back to Ask Agent or View transcript when no workflow action is executable', () => {
    const result = deriveIssueDecisionActions(makeContext({
      decision: null,
      issue: makeIssue({}),
    }))
    expect(result.primary?.kind).toBe('ask-agent')
  })
})

describe('selectTranscriptSession', () => {
  const sessions: Array<Pick<WorkflowRunSession, 'sessionName' | 'status' | 'startedAt' | 'createdAt'>> = [
    { sessionName: 'old', status: 'completed', startedAt: '2026-01-01T00:00:00Z', createdAt: '2025-12-31T00:00:00Z' },
    { sessionName: 'live', status: 'running', startedAt: '2026-02-01T00:00:00Z', createdAt: '2026-01-31T00:00:00Z' },
    { sessionName: 'queued', status: 'pending', startedAt: '2026-03-01T00:00:00Z', createdAt: '2026-02-28T00:00:00Z' },
  ]

  it('prefers an active session over older or newer completed sessions', () => {
    expect(selectTranscriptSession(sessions)?.sessionName).toBe('live')
  })

  it('prefers active over probing when both exist', () => {
    const probed: typeof sessions = [
      { sessionName: 'probe', status: 'probing', startedAt: '2026-04-01T00:00:00Z', createdAt: '2026-03-31T00:00:00Z' },
      { sessionName: 'active', status: 'active', startedAt: '2026-04-02T00:00:00Z', createdAt: '2026-04-01T00:00:00Z' },
    ]
    expect(selectTranscriptSession(probed)?.sessionName).toBe('active')
  })

  it('falls back to the most recently started session when no active one exists', () => {
    const noActive: typeof sessions = sessions.filter((s) => !['active', 'running', 'probing'].includes(s.status))
    expect(selectTranscriptSession(noActive)?.sessionName).toBe('queued')
  })

  it('uses session name as a stable tie-break when timestamps are equal', () => {
    const tied: typeof sessions = [
      { sessionName: 'zeta', status: 'completed', startedAt: '2026-01-01T00:00:00Z', createdAt: '2025-12-31T00:00:00Z' },
      { sessionName: 'alpha', status: 'completed', startedAt: '2026-01-01T00:00:00Z', createdAt: '2025-12-31T00:00:00Z' },
    ]
    expect(selectTranscriptSession(tied)?.sessionName).toBe('alpha')
  })

  it('returns null when no sessions exist', () => {
    expect(selectTranscriptSession([])).toBeNull()
  })

  it('falls back to createdAt when startedAt is missing', () => {
    const onlyCreated: typeof sessions = [
      { sessionName: 'older', status: 'completed', startedAt: null, createdAt: '2026-01-01T00:00:00Z' },
      { sessionName: 'newer', status: 'completed', startedAt: null, createdAt: '2026-02-01T00:00:00Z' },
    ]
    expect(selectTranscriptSession(onlyCreated)?.sessionName).toBe('newer')
  })
})
