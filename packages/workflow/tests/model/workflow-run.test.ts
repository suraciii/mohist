import { describe, expect, it } from 'vitest';
import {
  WorkflowRun,
  createWorkflowDefinitionSnapshot,
  type WorkflowDefinition,
} from '../../src';

function definition(): WorkflowDefinition {
  return {
    id: 'custom/default',
    stages: [
      {
        stage: 'plan',
        tasks: [{ id: 'proposal', title: 'Proposal' }],
        checks: [{ name: 'proposal-ok', title: 'Proposal OK' }],
        requiresApproval: true,
      },
      {
        stage: 'build',
        tasks: [{ id: 'implement', title: 'Implement' }],
        checks: [{ name: 'build-ok', title: 'Build OK' }],
      },
    ],
  };
}

function startRun(def = definition()): WorkflowRun {
  return WorkflowRun.startWorkflow({
    id: 'run-1',
    issueId: 'issue-1',
    issueNumber: 1,
    workflowDefinitionSnapshot: createWorkflowDefinitionSnapshot({
      definition: def,
      capturedAt: '2026-05-21T00:00:00.000Z',
    }),
  }).run;
}

describe('workflow run aggregate', () => {
  it('runs custom stages through task, check, approval, and workflow completion', () => {
    const run = startRun();

    expect(run.currentStage).toBe('plan');
    expect(run.nextWork()).toEqual({ kind: 'task', stage: 'plan', taskId: 'proposal' });

    run.completeTask('plan', 'proposal', { status: 'completed' });
    expect(run.nextWork()).toEqual({ kind: 'check', stage: 'plan', checkName: 'proposal-ok' });

    run.recordCheckResult('plan', { name: 'proposal-ok', status: 'pass' });
    expect(run.nextWork()).toEqual({ kind: 'await-approval', stage: 'plan' });

    run.approveStage('plan');
    expect(run.currentStage).toBe('build');
    expect(run.nextWork()).toEqual({ kind: 'task', stage: 'build', taskId: 'implement' });

    run.completeTask('build', 'implement', { status: 'completed' });
    run.recordCheckResult('build', { name: 'build-ok', status: 'pass' });

    expect(run.status).toBe('passed');
    expect(run.nextWork()).toEqual({ kind: 'complete' });
    expect(run.snapshot().stageRuns.map(stage => [stage.stage, stage.status])).toEqual([
      ['plan', 'passed'],
      ['build', 'passed'],
    ]);
  });

  it('schedules retry tasks from check failure policy and stops after max attempts', () => {
    const run = startRun({
      id: 'custom/retry',
      stages: [
        {
          stage: 'review',
          tasks: [{ id: 'review', title: 'Review' }],
          checks: [
            {
              name: 'review-passed',
              title: 'Review passed',
              onFailure: {
                retry: {
                  limit: 1,
                  task: { id: 'fix-review', title: 'Fix review' },
                },
              },
            },
          ],
        },
      ],
    });

    run.completeTask('review', 'review', { status: 'completed' });
    const retryDecision = run.recordCheckResult('review', {
      name: 'review-passed',
      status: 'fail',
      message: 'blocking finding',
    });

    expect(retryDecision.events).toContainEqual(expect.objectContaining({
      type: 'retry-task-scheduled',
      stage: 'review',
      taskId: 'fix-review',
    }));
    expect(run.nextWork()).toEqual({ kind: 'task', stage: 'review', taskId: 'fix-review' });

    run.completeTask('review', 'fix-review', { status: 'completed' });
    const failedDecision = run.recordCheckResult('review', {
      name: 'review-passed',
      status: 'fail',
      message: 'still failing',
    });

    expect(run.status).toBe('failed');
    expect(failedDecision.nextWork).toEqual(expect.objectContaining({
      kind: 'failed',
      reason: expect.objectContaining({
        reason: 'check-unrepaired',
        checkName: 'review-passed',
      }),
    }));
  });

  it('resets checks and approval when a task raises a workflow-defined event', () => {
    const run = startRun({
      id: 'custom/event-reset',
      stages: [
        {
          stage: 'review',
          tasks: [
            { id: 'review', title: 'Review' },
            { id: 'fix-review', title: 'Fix review' },
          ],
          checks: [{ name: 'review-passed', title: 'Review passed' }],
          on: {
            'code.changed': {
              reset: {
                checks: 'all',
                approval: true,
              },
            },
          },
          requiresApproval: true,
        },
      ],
    });

    run.completeTask('review', 'review', { status: 'completed' });
    run.completeTask('review', 'fix-review', { status: 'completed' });
    run.recordCheckResult('review', { name: 'review-passed', status: 'pass' });
    expect(run.nextWork()).toEqual({ kind: 'await-approval', stage: 'review' });

    const decision = run.rerunStage('review');
    expect(decision.events).toEqual([{ type: 'stage-retried', stage: 'review' }]);

    run.completeTask('review', 'review', { status: 'completed' });
    const resetDecision = run.completeTask('review', 'fix-review', {
      status: 'completed',
      events: ['code.changed'],
    });

    expect(resetDecision.events).toContainEqual({
      type: 'check-invalidated',
      stage: 'review',
      checkName: 'review-passed',
      reason: 'code.changed reset',
    });
    expect(run.stageRun('review').checks.find(check => check.name === 'review-passed')?.status).toBe('pending');
  });
});
