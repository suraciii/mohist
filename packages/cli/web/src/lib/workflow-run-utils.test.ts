import { describe, expect, it } from 'vitest'
import { workflowRunToStageStateMap } from './workflow-run-utils'
import { Stage } from './types'
import type { WorkflowRun } from './types'

function baseWorkflowRun(overrides: Partial<WorkflowRun> = {}): WorkflowRun {
  return {
    id: 'run-1',
    issueId: 'issue-1',
    issueNumber: 1,
    status: 'running',
    currentStage: Stage.Check,
    workflowDefinition: null,
    stageRuns: [],
    failure: null,
    ...overrides,
  }
}

describe('workflowRunToStageStateMap', () => {
  it('projects check repair state from stage definition check failure policy', () => {
    const run = baseWorkflowRun({
      currentStage: Stage.Plan,
      workflowDefinition: {
        workflowId: 'custom',
        source: { type: 'runtime', id: 'custom' },
        capturedAt: '2026-05-19T00:00:00.000Z',
        stageOrder: [Stage.Plan],
        stageDefinitions: [
          {
            stage: Stage.Plan,
            checkFailurePolicies: [
              {
                checkName: 'quality-approved',
                fixTaskId: 'repair-quality',
                fixTaskTitle: 'Repair quality',
                maxAttempts: 3,
              },
            ],
          },
        ],
      },
      stageRuns: [
        {
          stage: Stage.Plan,
          status: 'failed',
          definition: {
            stage: Stage.Plan,
            checkFailurePolicies: [
              {
                checkName: 'quality-approved',
                fixTaskId: 'repair-quality',
                fixTaskTitle: 'Repair quality',
                maxAttempts: 3,
              },
            ],
          },
          tasks: [
            {
              id: 'task-1',
              taskId: 'repair-quality',
              title: 'Repair quality',
              status: 'completed',
              taskOrder: 1,
              attempts: 1,
              duration: 0,
              artifacts: [],
              output: null,
              reason: null,
              causedBy: { type: 'check-failure', checkName: 'quality-approved', message: 'Quality failed' },
              startedAt: null,
              completedAt: null,
            },
          ],
          checks: [
            {
              checkName: 'quality-approved',
              title: 'Quality approved',
              status: 'failed',
              message: 'Quality failed',
              output: { verdict: 'FAIL', summary: 'Needs quality fixes' },
              runCount: 2,
              lastRunAt: null,
            },
          ],
          approvalStatus: null,
          approvalOutput: null,
          approvalRequestedAt: null,
          approvalRespondedAt: null,
          approval: null,
          failure: null,
          deliveryMetadata: null,
          attempts: 1,
          startedAt: null,
          completedAt: null,
          updatedAt: '2026-05-19T00:00:00.000Z',
        },
      ],
    })

    const stageState = workflowRunToStageStateMap(run).get(Stage.Plan)

    expect(stageState?.checkRepair).toMatchObject({
      checkName: 'quality-approved',
      fixTaskId: 'repair-quality',
      status: 'available',
      attemptsUsed: 1,
      attemptsMax: 3,
      attemptsRemaining: 2,
      repairAvailable: true,
      followUpReviewStatus: 'failed',
      unresolvedSummary: 'Needs quality fixes',
    })
  })
})
