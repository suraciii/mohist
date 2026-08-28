import { describe, expect, it } from 'vitest'
import { IssueHealth, IssueStatus, WorkflowStage } from '../../../entities/issue'
import { deriveRuntimeDecision, type RuntimeDecisionInput } from './derive-runtime-decision'

function baseInput(
  overrides: Partial<RuntimeDecisionInput['issue']> = {},
  timeline: RuntimeDecisionInput['timeline'] = {
    currentStage: WorkflowStage.Plan,
    status: 'AwaitingApproval',
    stages: [],
    pendingWork: null,
    availableActions: [
      { name: 'approve', label: 'Approve', target: null },
      { name: 'reject', label: 'Send back', target: null },
    ],
  },
): RuntimeDecisionInput {
  return {
    issue: {
      status: IssueStatus.InProgress,
      workflowStage: WorkflowStage.Plan,
      workflowStatus: 'awaiting-approval',
      health: IssueHealth.Paused,
      approvalState: {
        status: 'awaiting',
        stage: WorkflowStage.Plan,
        requestedAt: '2026-01-01T00:00:00.000Z',
      },
      blockedReason: undefined,
      recovery: {
        currentWorkItem: null,
        latestAttemptState: null,
        workflowSummaryState: 'awaiting-approval',
        allowedActions: ['approve', 'reject'],
      },
      convergence: undefined,
      drift: undefined,
      workflowStageProgress: undefined,
      prerequisites: [],
      isDraft: false,
      canStart: true,
      blocker: null,
      ...overrides,
    },
    timeline,
  }
}

describe('runtime-presentations approval pause copy', () => {
  it('identifies a paused workflow as awaiting an approval decision, not as manually stopped', () => {
    const decision = deriveRuntimeDecision(baseInput())

    expect(decision.summary).toBe('approval-required')
    expect(decision.rationale).toMatch(/approval decision is pending/i)
    expect(decision.rationale).not.toMatch(/your review|review and approve|you must|you should/i)
    expect(decision.rationale).not.toMatch(/stopped manually|interrupted/i)
  })

  it('does not assume the viewer is the approver in the next-action copy', () => {
    const decision = deriveRuntimeDecision(baseInput())

    expect(decision.nextAction).not.toMatch(/review and approve to continue/i)
    expect(decision.nextAction).toMatch(/approval decision is needed/i)
  })
})

describe('runtime-presentations approval actions', () => {
  const approvalTimeline = (names: string[]) => ({
    currentStage: WorkflowStage.Plan,
    status: 'AwaitingApproval',
    stages: [],
    pendingWork: null,
    availableActions: names.map((name) => ({ name, label: name, target: null })),
  })

  it('enables approve, stop, and send back for the server action names', () => {
    const decision = deriveRuntimeDecision(
      baseInput(
        {
          recovery: {
            currentWorkItem: null,
            latestAttemptState: null,
            workflowSummaryState: 'awaiting-approval',
            allowedActions: [],
          },
        },
        approvalTimeline(['approve', 'stop', 'request-changes']),
      ),
    )

    expect(decision.actions.find((action) => action.kind === 'approve')?.enabled).toBe(true)
    expect(decision.actions.find((action) => action.kind === 'stop')?.enabled).toBe(true)
    expect(decision.actions.find((action) => action.kind === 'send-back')?.enabled).toBe(true)
  })

  it('leaves send back disabled when only approve and stop are available', () => {
    const decision = deriveRuntimeDecision(
      baseInput(
        {
          recovery: {
            currentWorkItem: null,
            latestAttemptState: null,
            workflowSummaryState: 'awaiting-approval',
            allowedActions: ['approve', 'stop'],
          },
        },
        approvalTimeline(['approve', 'stop']),
      ),
    )

    const sendBack = decision.actions.find((action) => action.kind === 'send-back')
    expect(sendBack?.enabled).toBe(false)
    expect(sendBack?.reason).toBe('Send-back is not available right now.')
  })

  it.each(['reject', 'send-back', 'send_back'])('keeps legacy %s enabled', (legacyAction) => {
    const decision = deriveRuntimeDecision(
      baseInput(
        {
          recovery: {
            currentWorkItem: null,
            latestAttemptState: null,
            workflowSummaryState: 'awaiting-approval',
            allowedActions: [legacyAction],
          },
        },
        approvalTimeline([legacyAction]),
      ),
    )

    expect(decision.actions.find((action) => action.kind === 'send-back')?.enabled).toBe(true)
  })
})

describe('runtime-presentations summary precedence is unchanged', () => {
  it('preserves running summary when an active agent exists with no failed checks', () => {
    const decision = deriveRuntimeDecision({
      issue: {
        status: IssueStatus.InProgress,
        workflowStage: WorkflowStage.Build,
        workflowStatus: 'running',
        health: IssueHealth.Active,
        approvalState: undefined,
        blockedReason: undefined,
        recovery: {
          currentWorkItem: { type: 'task', id: 't1', title: 'Build it' },
          latestAttemptState: 'running',
          workflowSummaryState: 'running',
          allowedActions: ['stop'],
        },
        convergence: undefined,
        drift: undefined,
        workflowStageProgress: undefined,
        prerequisites: [],
        isDraft: false,
        canStart: true,
        blocker: null,
      },
      timeline: {
        currentStage: WorkflowStage.Build,
        status: 'Running',
        stages: [],
        pendingWork: null,
        availableActions: [{ name: 'stop', label: 'Stop', target: null }],
      },
      hasActiveAgent: true,
    })

    expect(decision.summary).toBe('running')
    expect(decision.currentTask?.title).toBe('Build it')
  })

  it('preserves done summary when the workflow is in the done stage', () => {
    const decision = deriveRuntimeDecision(
      baseInput({
        status: IssueStatus.Done,
        workflowStage: WorkflowStage.Done,
        workflowStatus: 'completed',
        health: IssueHealth.Done,
        approvalState: undefined,
        recovery: undefined,
      }),
    )

    expect(decision.summary).toBe('done')
  })
})
