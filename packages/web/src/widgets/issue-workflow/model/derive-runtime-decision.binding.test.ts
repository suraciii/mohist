import { describe, expect, it } from 'vitest'
import { IssueHealth, IssueStatus, WorkflowStage } from '../../../entities/issue'
import {
  deriveRuntimeDecision,
  type RuntimeDecisionInput,
} from './derive-runtime-decision'

describe('deriveRuntimeDecision binding state', () => {
  it('returns queued while a workflow binding is starting', () => {
    const issue: NonNullable<RuntimeDecisionInput['issue']> = {
      status: IssueStatus.InProgress,
      workflowStage: WorkflowStage.Build,
      workflowStatus: 'starting',
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
    }

    const decision = deriveRuntimeDecision({ issue })

    expect(decision.summary).toBe('queued')
    expect(decision.headline).toMatch(/waiting to start/i)
    expect(decision.actions).toEqual([])
  })
})
