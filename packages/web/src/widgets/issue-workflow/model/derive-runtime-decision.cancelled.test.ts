import { describe, expect, it } from 'vitest'
import {
  deriveRuntimeDecision,
  type RuntimeDecisionInput,
} from './derive-runtime-decision'
import { IssueHealth, IssueStatus, WorkflowStage } from '../../../entities/issue'

function baseIssue(overrides: Partial<RuntimeDecisionInput['issue']> = {}): RuntimeDecisionInput['issue'] {
  return {
    status: IssueStatus.InProgress,
    workflowStage: WorkflowStage.Build,
    workflowStatus: 'running',
    health: IssueHealth.Active,
    approvalState: undefined,
    blockedReason: undefined,
    recovery: undefined,
    convergence: undefined,
    drift: undefined,
    workflowStageProgress: undefined,
    prerequisites: [],
    isDraft: false,
    canStart: true,
    blocker: null,
    ...overrides,
  }
}

describe('deriveRuntimeDecision — cancelled and stopped terminal states', () => {
  it('returns cancelled when the issue is cancelled, even with a runner assigned and a non-terminal stage', () => {
    const decision = deriveRuntimeDecision({
      issue: baseIssue({
        status: IssueStatus.Cancelled,
        health: IssueHealth.Cancelled,
        workflowStage: WorkflowStage.Build,
        workflowStatus: 'stopped',
        recovery: {
          currentWorkItem: { type: 'task', id: 't1', title: 'Abandoned task' },
          latestAttemptState: 'running',
          workflowSummaryState: 'running',
          allowedActions: ['stop'],
        },
      }),
      hasActiveAgent: true,
    })

    expect(decision.summary).toBe('cancelled')
    expect(decision.headline).toContain('cancelled')
    expect(decision.actions.some((a) => a.enabled)).toBe(false)
  })

  it('returns cancelled from health even when status has not flipped', () => {
    const decision = deriveRuntimeDecision({
      issue: baseIssue({
        status: IssueStatus.InProgress,
        health: IssueHealth.Cancelled,
        workflowStage: WorkflowStage.Build,
      }),
    })

    expect(decision.summary).toBe('cancelled')
  })

  it('returns blocked when the run was stopped manually (workflowStatus stopped), not running', () => {
    const decision = deriveRuntimeDecision({
      issue: baseIssue({
        status: IssueStatus.InProgress,
        health: IssueHealth.Active,
        workflowStage: WorkflowStage.Build,
        workflowStatus: 'stopped',
      }),
    })

    expect(decision.summary).toBe('blocked')
    expect(decision.rationale).toContain('stopped manually')
  })
})
