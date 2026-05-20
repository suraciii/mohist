import { describe, expect, it } from 'vitest';
import { WorkflowApplicationService, type WorkflowRunProjectionPort, type WorkflowRunRepositoryPort } from '../src/services/workflow-application-service';
import { WorkflowRun, createWorkflowDefinitionSnapshot, parseWorkflowDefinitionSource } from '../src/workflow/model';
import { DEFAULT_STAGE_DEFINITIONS } from '../src/workflow/definitions/default-workflow';
import { Stage } from '../src/types';
import type { WorkflowAttemptEvidencePort } from '../src/services/attempt-reconciliation-service';

function createRunningPlanRun(issueId = 'issue-1'): WorkflowRun {
  return WorkflowRun.startWorkflow({ id: 'wr-1', issueId, issueNumber: 188, definitions: DEFAULT_STAGE_DEFINITIONS }).run;
}

describe('WorkflowApplicationService', () => {
  it('starts or reuses one active aggregate run and projects after repository creation', () => {
    const calls: string[] = [];
    const run = createRunningPlanRun();
    const repo: WorkflowRunRepositoryPort = {
      createOrLoadActiveAggregate: () => {
        calls.push('repo.createOrLoadActiveAggregate');
        return run;
      },
      loadActiveAggregate: () => run,
      saveAggregate: () => calls.push('repo.saveAggregate'),
    };
    const projection: WorkflowRunProjectionPort = {
      apply: input => {
        calls.push(`projection.apply:${input.run.currentStage}`);
      },
    };

    const service = new WorkflowApplicationService(repo, projection);
    const result = service.startWorkflow({ issueId: 'issue-1', issueNumber: 188 });

    expect(result.run).toBe(run);
    expect(calls).toEqual(['repo.createOrLoadActiveAggregate', 'projection.apply:plan']);
  });

  it('saves aggregate before projecting a completed task decision', () => {
    const calls: string[] = [];
    const run = createRunningPlanRun();
    const repo: WorkflowRunRepositoryPort = {
      createOrLoadActiveAggregate: () => run,
      loadActiveAggregate: () => {
        calls.push('repo.loadActiveAggregate');
        return run;
      },
      saveAggregate: saved => {
        calls.push(`repo.saveAggregate:${saved.stageRun(Stage.Plan).findTask('proposal').status}`);
      },
    };
    const projection: WorkflowRunProjectionPort = {
      apply: input => calls.push(`projection.apply:${input.run.stageRun(Stage.Plan).findTask('proposal').status}`),
    };

    const service = new WorkflowApplicationService(repo, projection);
    service.completeTask({ issueId: 'issue-1', stage: Stage.Plan, taskId: 'proposal', result: { status: 'completed' } });

    expect(calls).toEqual([
      'repo.loadActiveAggregate',
      'repo.saveAggregate:completed',
      'projection.apply:completed',
    ]);
  });

  it('saves aggregate before projecting a recorded check decision', () => {
    const calls: string[] = [];
    const run = createRunningPlanRun();
    for (const taskId of ['proposal', 'specs', 'design', 'tasks', 'self-review']) {
      run.completeTask(Stage.Plan, taskId, { status: 'completed' });
    }
    const repo: WorkflowRunRepositoryPort = {
      createOrLoadActiveAggregate: () => run,
      loadActiveAggregate: () => {
        calls.push('repo.loadActiveAggregate');
        return run;
      },
      saveAggregate: saved => {
        calls.push(`repo.saveAggregate:${saved.stageRun(Stage.Plan).findCheck('proposal-complete').status}`);
      },
    };
    const projection: WorkflowRunProjectionPort = {
      apply: input => calls.push(`projection.apply:${input.run.stageRun(Stage.Plan).findCheck('proposal-complete').status}`),
    };

    const service = new WorkflowApplicationService(repo, projection);
    service.recordCheckResult({ issueId: 'issue-1', stage: Stage.Plan, result: { name: 'proposal-complete', status: 'pass' } });

    expect(calls).toEqual([
      'repo.loadActiveAggregate',
      'repo.saveAggregate:passed',
      'projection.apply:passed',
    ]);
  });

  it('updates approval in the aggregate before projecting approval state', () => {
    const calls: string[] = [];
    const run = createRunningPlanRun();
    for (const taskId of ['proposal', 'specs', 'design', 'tasks', 'self-review']) {
      run.completeTask(Stage.Plan, taskId, { status: 'completed' });
    }
    for (const checkName of ['proposal-complete', 'specs-complete', 'design-complete', 'tasks-valid', 'self-review-passed', 'health:plan']) {
      run.recordCheckResult(Stage.Plan, { name: checkName, status: 'pass' });
    }
    expect(run.stageRun(Stage.Plan).approval?.status).toBe('awaiting');

    const repo: WorkflowRunRepositoryPort = {
      createOrLoadActiveAggregate: () => run,
      loadActiveAggregate: () => run,
      saveAggregate: saved => calls.push(`repo.saveAggregate:${saved.stageRun(Stage.Plan).approval?.status}`),
    };
    const projection: WorkflowRunProjectionPort = {
      apply: input => calls.push(`projection.apply:${input.run.stageRun(Stage.Plan).approval?.status}:${input.run.currentStage}`),
    };

    const service = new WorkflowApplicationService(repo, projection);
    service.approveStage({ issueId: 'issue-1', stage: Stage.Plan, approval: { output: { approved: true } } });

    expect(calls).toEqual(['repo.saveAggregate:approved', 'projection.apply:approved:build']);
  });

  it('does not save or project when aggregate validation fails', () => {
    const calls: string[] = [];
    const run = createRunningPlanRun();
    const repo: WorkflowRunRepositoryPort = {
      createOrLoadActiveAggregate: () => run,
      loadActiveAggregate: () => {
        calls.push('repo.loadActiveAggregate');
        return run;
      },
      saveAggregate: () => calls.push('repo.saveAggregate'),
    };
    const projection: WorkflowRunProjectionPort = {
      apply: () => calls.push('projection.apply'),
    };

    const service = new WorkflowApplicationService(repo, projection);

    expect(() => service.completeTask({ issueId: 'issue-1', stage: Stage.Plan, taskId: 'specs', result: { status: 'completed' } })).toThrow(/cannot complete before earlier tasks/);
    expect(calls).toEqual(['repo.loadActiveAggregate']);
  });

  it('reruns a failed latest aggregate when no active run exists', () => {
    const calls: string[] = [];
    const run = createRunningPlanRun();
    run.completeTask(Stage.Plan, 'proposal', { status: 'failed', reason: 'agent stopped' });
    expect(run.snapshot().status).toBe('failed');

    const repo: WorkflowRunRepositoryPort = {
      createOrLoadActiveAggregate: () => run,
      loadActiveAggregate: () => null,
      loadRunningAggregate: () => {
        calls.push('repo.loadRunningAggregate');
        return null;
      },
      loadLatestAggregate: () => {
        calls.push('repo.loadLatestAggregate');
        return run;
      },
      saveAggregate: saved => calls.push(`repo.saveAggregate:${saved.snapshot().status}`),
    };
    const projection: WorkflowRunProjectionPort = {
      apply: input => calls.push(`projection.apply:${input.run.snapshot().status}:${input.decision.nextWork.kind}`),
    };

    const service = new WorkflowApplicationService(repo, projection);
    const result = service.rerunStage({ issueId: 'issue-1', stage: Stage.Plan, startedBy: 'rerun' });

    expect(result.run).toBe(run);
    expect(result.decision.nextWork).toEqual({ kind: 'task', stage: Stage.Plan, taskId: 'proposal' });
    expect(calls).toEqual([
      'repo.loadRunningAggregate',
      'repo.loadLatestAggregate',
      'repo.saveAggregate:running',
      'projection.apply:running:task',
    ]);
  });

  it('can inspect a failed latest aggregate when no active run exists', () => {
    const calls: string[] = [];
    const run = createRunningPlanRun();
    run.completeTask(Stage.Plan, 'proposal', { status: 'failed', reason: 'agent stopped' });

    const repo: WorkflowRunRepositoryPort = {
      createOrLoadActiveAggregate: () => run,
      loadActiveAggregate: () => null,
      loadRunningAggregate: () => {
        calls.push('repo.loadRunningAggregate');
        return null;
      },
      loadLatestAggregate: () => {
        calls.push('repo.loadLatestAggregate');
        return run;
      },
      saveAggregate: saved => calls.push(`repo.saveAggregate:${saved.snapshot().status}`),
    };
    const projection: WorkflowRunProjectionPort = {
      apply: input => calls.push(`projection.apply:${input.decision.nextWork.kind}`),
    };

    const service = new WorkflowApplicationService(repo, projection);
    const result = service.resumeDecision('issue-1', { startedBy: 'rerun' });

    expect(result.nextWork.kind).toBe('failed');
    expect(calls).toEqual([
      'repo.loadRunningAggregate',
      'repo.loadLatestAggregate',
      'repo.loadRunningAggregate',
      'repo.loadLatestAggregate',
      'repo.saveAggregate:failed',
      'projection.apply:failed',
    ]);
  });

  it('stores string rejection feedback in approval output', () => {
    const run = createRunningPlanRun();
    for (const taskId of ['proposal', 'specs', 'design', 'tasks', 'self-review']) {
      run.completeTask(Stage.Plan, taskId, { status: 'completed' });
    }
    for (const checkName of ['proposal-complete', 'specs-complete', 'design-complete', 'tasks-valid', 'self-review-passed', 'health:plan']) {
      run.recordCheckResult(Stage.Plan, { name: checkName, status: 'pass' });
    }
    expect(run.stageRun(Stage.Plan).approval?.status).toBe('awaiting');

    const repo: WorkflowRunRepositoryPort = {
      createOrLoadActiveAggregate: () => run,
      loadActiveAggregate: () => run,
      saveAggregate: () => {},
    };
    const projection: WorkflowRunProjectionPort = { apply: () => {} };

    const service = new WorkflowApplicationService(repo, projection);
    const result = service.rejectStage({
      issueId: 'issue-1',
      stage: Stage.Plan,
      approval: { output: 'Please make the proposal more specific' },
    });

    const planApproval = result.run.stageRun(Stage.Plan).approval;
    expect(planApproval?.status).toBe('rejected');
    expect(planApproval?.output).toBe('Please make the proposal more specific');
    expect(result.run.failure?.reason).toBe('approval-rejected');
    expect(result.run.failure?.message).toBe('Please make the proposal more specific');
  });

  it('stores structured rejection feedback in approval output', () => {
    const run = createRunningPlanRun();
    for (const taskId of ['proposal', 'specs', 'design', 'tasks', 'self-review']) {
      run.completeTask(Stage.Plan, taskId, { status: 'completed' });
    }
    for (const checkName of ['proposal-complete', 'specs-complete', 'design-complete', 'tasks-valid', 'self-review-passed', 'health:plan']) {
      run.recordCheckResult(Stage.Plan, { name: checkName, status: 'pass' });
    }

    const repo: WorkflowRunRepositoryPort = {
      createOrLoadActiveAggregate: () => run,
      loadActiveAggregate: () => run,
      saveAggregate: () => {},
    };
    const projection: WorkflowRunProjectionPort = { apply: () => {} };

    const service = new WorkflowApplicationService(repo, projection);
    const rejectionFeedback = { feedback: 'Proposal needs more detail', approvalContext: 'Prior approval request was approved' };
    const result = service.rejectStage({
      issueId: 'issue-1',
      stage: Stage.Plan,
      approval: { output: rejectionFeedback },
    });

    const planApproval = result.run.stageRun(Stage.Plan).approval;
    expect(planApproval?.status).toBe('rejected');
    expect(planApproval?.output).toEqual(rejectionFeedback);
    const output = planApproval?.output as { feedback?: string; approvalContext?: string };
    expect(output.feedback).toBe('Proposal needs more detail');
    expect(output.approvalContext).toBe('Prior approval request was approved');
  });

  it('rejection feedback replaces prior approval output rather than being shadowed by it', () => {
    const run = createRunningPlanRun();
    for (const taskId of ['proposal', 'specs', 'design', 'tasks', 'self-review']) {
      run.completeTask(Stage.Plan, taskId, { status: 'completed' });
    }
    for (const checkName of ['proposal-complete', 'specs-complete', 'design-complete', 'tasks-valid', 'self-review-passed', 'health:plan']) {
      run.recordCheckResult(Stage.Plan, { name: checkName, status: 'pass' });
    }

    const repo: WorkflowRunRepositoryPort = {
      createOrLoadActiveAggregate: () => run,
      loadActiveAggregate: () => run,
      saveAggregate: () => {},
    };
    const projection: WorkflowRunProjectionPort = { apply: () => {} };

    const service = new WorkflowApplicationService(repo, projection);
    const priorApprovalOutput = { approved: true, snapshotSha: 'abc123' };
    const newRejectionFeedback = 'The proposal is too vague, please redo';
    service.approveStage({
      issueId: 'issue-1',
      stage: Stage.Plan,
      approval: { output: priorApprovalOutput },
    });

    const latestRun = createRunningPlanRun();
    for (const taskId of ['proposal', 'specs', 'design', 'tasks', 'self-review']) {
      latestRun.completeTask(Stage.Plan, taskId, { status: 'completed' });
    }
    for (const checkName of ['proposal-complete', 'specs-complete', 'design-complete', 'tasks-valid', 'self-review-passed', 'health:plan']) {
      latestRun.recordCheckResult(Stage.Plan, { name: checkName, status: 'pass' });
    }

    const repo2: WorkflowRunRepositoryPort = {
      createOrLoadActiveAggregate: () => latestRun,
      loadActiveAggregate: () => latestRun,
      saveAggregate: () => {},
    };
    const projection2: WorkflowRunProjectionPort = { apply: () => {} };
    const service2 = new WorkflowApplicationService(repo2, projection2);

    const result = service2.rejectStage({
      issueId: 'issue-1',
      stage: Stage.Plan,
      approval: { output: newRejectionFeedback },
    });

    const planApproval = result.run.stageRun(Stage.Plan).approval;
    expect(planApproval?.output).toBe(newRejectionFeedback);
    expect(planApproval?.output).not.toEqual(priorApprovalOutput);
  });

  it('rejects stage and enqueues resume-pipeline through workflow application service', () => {
    const run = createRunningPlanRun();
    for (const taskId of ['proposal', 'specs', 'design', 'tasks', 'self-review']) {
      run.completeTask(Stage.Plan, taskId, { status: 'completed' });
    }
    for (const checkName of ['proposal-complete', 'specs-complete', 'design-complete', 'tasks-valid', 'self-review-passed', 'health:plan']) {
      run.recordCheckResult(Stage.Plan, { name: checkName, status: 'pass' });
    }

    const repo: WorkflowRunRepositoryPort = {
      createOrLoadActiveAggregate: () => run,
      loadActiveAggregate: () => run,
      saveAggregate: () => {},
    };
    const projection: WorkflowRunProjectionPort = { apply: () => {} };

    const service = new WorkflowApplicationService(repo, projection);
    const result = service.rejectStage({
      issueId: 'issue-1',
      stage: Stage.Plan,
      approval: { output: 'Try again with more detail' },
    });

    expect(result.run.snapshot().status).toBe('failed');
    expect(result.decision.events).toContainEqual(expect.objectContaining({ type: 'approval-rejected', stage: Stage.Plan }));
    expect(result.decision.nextWork.kind).toBe('failed');
  });

  it('schedules approval repair from the stage retry policy instead of Check defaults', () => {
    const workflowDefinitionSnapshot = createWorkflowDefinitionSnapshot({
      definition: parseWorkflowDefinitionSource({
        id: 'custom/approval-repair',
        stages: [
          {
            id: Stage.Plan,
            tasks: [{ id: 'draft', title: 'Draft', uses: 'mohist/agent' }],
            checks: [
              {
                id: 'quality-approved',
                title: 'Quality approved',
                uses: 'mohist/verdict',
                onFailure: {
                  retry: {
                    limit: 2,
                    task: { id: 'repair-quality', title: 'Repair quality', uses: 'mohist/agent' },
                  },
                },
              },
              {
                id: 'quality-verification',
                title: 'Quality verification',
                uses: 'mohist/health-gate',
              },
              {
                id: 'quality-candidate',
                title: 'Quality candidate',
                uses: 'mohist/merge-ready',
              },
            ],
            approval: true,
          },
        ],
      }),
      source: { type: 'runtime', id: 'custom/approval-repair' },
      capturedAt: '2026-05-19T00:00:00.000Z',
    });
    const run = WorkflowRun.startWorkflow({
      id: 'wr-custom',
      issueId: 'issue-custom',
      issueNumber: 188,
      workflowDefinitionSnapshot,
    }).run;
    run.completeTask(Stage.Plan, 'draft', { status: 'completed' });
    run.recordCheckResult(Stage.Plan, {
      name: 'quality-approved',
      status: 'fail',
      message: 'Quality failed',
      output: { verdict: 'FAIL', summary: 'Quality failed' },
    });

    const repo: WorkflowRunRepositoryPort = {
      createOrLoadActiveAggregate: () => run,
      loadActiveAggregate: () => run,
      saveAggregate: () => {},
    };
    const projection: WorkflowRunProjectionPort = { apply: () => {} };
    const service = new WorkflowApplicationService(repo, projection);

    const result = service.scheduleApprovalVerdictRepair({ issueId: 'issue-custom', stage: Stage.Plan });

    expect(result.repairStatus).toBe('already-running');
    expect(result.repairTaskId).toBe('repair-quality');
    expect(result.run.stageRun(Stage.Plan).tasks.some(task => task.id === 'repair-quality')).toBe(true);
  });

  it('rejection with string message stores message, not prior approval output', () => {
    const run = createRunningPlanRun();
    for (const taskId of ['proposal', 'specs', 'design', 'tasks', 'self-review']) {
      run.completeTask(Stage.Plan, taskId, { status: 'completed' });
    }
    for (const checkName of ['proposal-complete', 'specs-complete', 'design-complete', 'tasks-valid', 'self-review-passed', 'health:plan']) {
      run.recordCheckResult(Stage.Plan, { name: checkName, status: 'pass' });
    }

    const repo: WorkflowRunRepositoryPort = {
      createOrLoadActiveAggregate: () => run,
      loadActiveAggregate: () => run,
      saveAggregate: () => {},
    };
    const projection: WorkflowRunProjectionPort = { apply: () => {} };

    const service = new WorkflowApplicationService(repo, projection);
    service.approveStage({ issueId: 'issue-1', stage: Stage.Plan, approval: { output: { approved: true, snapshotSha: 'xyz' } } });

    const freshRun = createRunningPlanRun();
    for (const taskId of ['proposal', 'specs', 'design', 'tasks', 'self-review']) {
      freshRun.completeTask(Stage.Plan, taskId, { status: 'completed' });
    }
    for (const checkName of ['proposal-complete', 'specs-complete', 'design-complete', 'tasks-valid', 'self-review-passed', 'health:plan']) {
      freshRun.recordCheckResult(Stage.Plan, { name: checkName, status: 'pass' });
    }
    const repo2: WorkflowRunRepositoryPort = {
      createOrLoadActiveAggregate: () => freshRun,
      loadActiveAggregate: () => freshRun,
      saveAggregate: () => {},
    };
    const service2 = new WorkflowApplicationService(repo2, projection);
    const result = service2.rejectStage({
      issueId: 'issue-1',
      stage: Stage.Plan,
      approval: { output: 'The design section is incomplete' },
    });

    const planApproval = result.run.stageRun(Stage.Plan).approval;
    expect(planApproval?.output).toBe('The design section is incomplete');
    expect(planApproval?.output).not.toEqual({ approved: true, snapshotSha: 'xyz' });
  });
});

describe('WorkflowApplicationService.checkRetryAvailability', () => {
  it('returns no-failed-workflow-run when no aggregate exists', () => {
    const repo: WorkflowRunRepositoryPort = {
      createOrLoadActiveAggregate: () => { throw new Error('unexpected'); },
      loadActiveAggregate: () => null,
      saveAggregate: () => {},
    };
    const projection: WorkflowRunProjectionPort = { apply: () => {} };
    const service = new WorkflowApplicationService(repo, projection);

    const result = service.checkRetryAvailability({ issueId: 'issue-1', stage: Stage.Plan });

    expect(result.available).toBe(false);
    expect(result.reason).toBe('no-failed-workflow-run');
    expect(result.message).toContain('No workflow run found');
  });

  it('returns no-retryable-failed-work when run is not failed', () => {
    const run = createRunningPlanRun();
    const repo: WorkflowRunRepositoryPort = {
      createOrLoadActiveAggregate: () => run,
      loadActiveAggregate: () => run,
      saveAggregate: () => {},
    };
    const projection: WorkflowRunProjectionPort = { apply: () => {} };
    const service = new WorkflowApplicationService(repo, projection);

    const result = service.checkRetryAvailability({ issueId: 'issue-1', stage: Stage.Plan });

    expect(result.available).toBe(false);
    expect(result.reason).toBe('no-retryable-failed-work');
  });

  it('returns stage-mismatch when current stage differs', () => {
    const run = createRunningPlanRun();
    run.completeTask(Stage.Plan, 'proposal', { status: 'failed', reason: 'agent stopped' });
    expect(run.snapshot().status).toBe('failed');

    const repo: WorkflowRunRepositoryPort = {
      createOrLoadActiveAggregate: () => run,
      loadActiveAggregate: () => run,
      saveAggregate: () => {},
    };
    const projection: WorkflowRunProjectionPort = { apply: () => {} };
    const service = new WorkflowApplicationService(repo, projection);

    const result = service.checkRetryAvailability({ issueId: 'issue-1', stage: Stage.Build });

    expect(result.available).toBe(false);
    expect(result.reason).toBe('stage-mismatch');
  });

  it('returns no-retryable-failed-work when run status is not failed', () => {
    const run = createRunningPlanRun();
    for (const taskId of ['proposal', 'specs', 'design', 'tasks', 'self-review']) {
      run.completeTask(Stage.Plan, taskId, { status: 'completed' });
    }
    for (const checkName of ['proposal-complete', 'specs-complete', 'design-complete', 'tasks-valid', 'self-review-passed', 'health:plan']) {
      run.recordCheckResult(Stage.Plan, { name: checkName, status: 'pass' });
    }
    expect(run.snapshot().status).toBe('running');
    expect(run.stageRun(Stage.Plan).status).toBe('awaiting-approval');

    const repo: WorkflowRunRepositoryPort = {
      createOrLoadActiveAggregate: () => run,
      loadActiveAggregate: () => run,
      saveAggregate: () => {},
    };
    const projection: WorkflowRunProjectionPort = { apply: () => {} };
    const service = new WorkflowApplicationService(repo, projection);

    const result = service.checkRetryAvailability({ issueId: 'issue-1', stage: Stage.Plan });

    expect(result.available).toBe(false);
    expect(result.reason).toBe('no-retryable-failed-work');
  });

  it('returns available when run is failed with a failed task', () => {
    const run = createRunningPlanRun();
    run.startTaskAttempt(Stage.Plan, 'proposal', '2026-05-19T00:00:00.000Z', { executionId: 'plan-188-proposal-1' });
    run.completeTask(Stage.Plan, 'proposal', { status: 'failed', reason: 'agent stopped' });
    expect(run.snapshot().status).toBe('failed');

    const repo: WorkflowRunRepositoryPort = {
      createOrLoadActiveAggregate: () => run,
      loadActiveAggregate: () => run,
      saveAggregate: () => {},
    };
    const projection: WorkflowRunProjectionPort = { apply: () => {} };
    const service = new WorkflowApplicationService(repo, projection);

    const result = service.checkRetryAvailability({ issueId: 'issue-1', stage: Stage.Plan });

    expect(result.available).toBe(true);
    expect(result.reason).toBeNull();
  });

  it('returns available when run is failed with a failed check', () => {
    const run = createRunningPlanRun();
    for (const taskId of ['proposal', 'specs', 'design', 'tasks', 'self-review']) {
      run.completeTask(Stage.Plan, taskId, { status: 'completed' });
    }
    run.startCheckAttempt(Stage.Plan, 'proposal-complete', '2026-05-19T00:00:00.000Z', { executionId: 'plan-188-proposal-complete-check' });
    run.recordCheckResult(Stage.Plan, { name: 'proposal-complete', status: 'fail', message: 'incomplete' });
    expect(run.snapshot().status).toBe('failed');

    const repo: WorkflowRunRepositoryPort = {
      createOrLoadActiveAggregate: () => run,
      loadActiveAggregate: () => run,
      saveAggregate: () => {},
    };
    const projection: WorkflowRunProjectionPort = { apply: () => {} };
    const service = new WorkflowApplicationService(repo, projection);

    const result = service.checkRetryAvailability({ issueId: 'issue-1', stage: Stage.Plan });

    expect(result.available).toBe(true);
    expect(result.reason).toBeNull();
  });

  it('returns available when latest aggregate is failed even when active is null', () => {
    const run = createRunningPlanRun();
    run.startTaskAttempt(Stage.Plan, 'proposal', '2026-05-19T00:00:00.000Z', { executionId: 'plan-188-proposal-1' });
    run.completeTask(Stage.Plan, 'proposal', { status: 'failed', reason: 'agent stopped' });
    expect(run.snapshot().status).toBe('failed');

    const calls: string[] = [];
    const repo: WorkflowRunRepositoryPort = {
      createOrLoadActiveAggregate: () => run,
      loadActiveAggregate: () => {
        calls.push('loadActiveAggregate');
        return null;
      },
      loadRunningAggregate: () => {
        calls.push('loadRunningAggregate');
        return null;
      },
      loadLatestAggregate: () => {
        calls.push('loadLatestAggregate');
        return run;
      },
      saveAggregate: () => {},
    };
    const projection: WorkflowRunProjectionPort = { apply: () => {} };
    const service = new WorkflowApplicationService(repo, projection);

    const result = service.checkRetryAvailability({ issueId: 'issue-1', stage: Stage.Plan });

    expect(result.available).toBe(true);
    expect(calls).toContain('loadLatestAggregate');
  });

  it('reconciles only attempts without matching live evidence', () => {
    const run = createRunningPlanRun();
    run.startTaskAttempt(Stage.Plan, 'proposal', new Date().toISOString(), { executionId: 'plan-188-proposal-1' });
    run.startTaskAttempt(Stage.Plan, 'specs', new Date().toISOString(), { executionId: 'plan-188-specs-1' });

    const repo: WorkflowRunRepositoryPort = {
      createOrLoadActiveAggregate: () => run,
      loadActiveAggregate: () => run,
      loadRunningAggregate: () => run,
      loadLatestAggregate: () => run,
      saveAggregate: () => {},
    };
    const projection: WorkflowRunProjectionPort = { apply: () => {} };
    const service = new WorkflowApplicationService(repo, projection);
    const evidencePort: WorkflowAttemptEvidencePort = {
      hasActiveQueueTask: () => false,
      hasLiveProcess: () => true,
      findQueueTaskById: () => null,
      findRunningCoderSessionsByAttemptEvidence: attempt => {
        if (attempt.executionId === 'plan-188-proposal-1') {
          return [{ id: 'session-1', issueId: 'issue-1', acpSessionId: 'acp-1', executionId: 'plan-188-proposal-1', taskDescription: null, createdAt: '', updatedAt: '', stage: 'plan', title: null, processPid: 321, status: 'running', completedAt: null, failureReason: null, model: null, coderType: null, lastDataAt: null, probeSentAt: null, probeDeadlineAt: null }];
        }
        return [];
      },
    };
    service.setEvidencePort(evidencePort);

    const result = service.reconcileIssueWorkflow('issue-1');

    expect(result.reconciled).toBe(true);
    expect(result.interruptedCount).toBe(1);
    expect(run.stageRun(Stage.Plan).findTask('proposal').latestAttempt?.state).toBe('running');
    expect(run.stageRun(Stage.Plan).findTask('specs').latestAttempt?.state).toBe('interrupted');
  });

  it('keeps rerun attempt live when it has only generated execution id and active issue queue evidence', () => {
    const run = createRunningPlanRun();
    run.startTaskAttempt(Stage.Plan, 'proposal', new Date().toISOString(), { executionId: 'plan-188-proposal-1' });

    const repo: WorkflowRunRepositoryPort = {
      createOrLoadActiveAggregate: () => run,
      loadActiveAggregate: () => run,
      loadRunningAggregate: () => run,
      loadLatestAggregate: () => run,
      saveAggregate: () => {},
    };
    const projection: WorkflowRunProjectionPort = { apply: () => {} };
    const service = new WorkflowApplicationService(repo, projection);
    const evidencePort: WorkflowAttemptEvidencePort = {
      hasActiveQueueTask: () => true,
      hasLiveProcess: () => false,
      findQueueTaskById: () => null,
      findRunningCoderSessionsByAttemptEvidence: () => [],
    };
    service.setEvidencePort(evidencePort);

    const result = service.reconcileIssueWorkflow('issue-1');

    expect(result.reconciled).toBe(false);
    expect(result.interruptedCount).toBe(0);
    expect(run.stageRun(Stage.Plan).findTask('proposal').latestAttempt?.state).toBe('running');
  });

  it('keeps running attempt live when only active issue queue evidence exists', () => {
    const run = createRunningPlanRun();
    run.startTaskAttempt(Stage.Plan, 'proposal', new Date().toISOString());

    const repo: WorkflowRunRepositoryPort = {
      createOrLoadActiveAggregate: () => run,
      loadActiveAggregate: () => run,
      loadRunningAggregate: () => run,
      loadLatestAggregate: () => run,
      saveAggregate: () => {},
    };
    const projection: WorkflowRunProjectionPort = { apply: () => {} };
    const service = new WorkflowApplicationService(repo, projection);
    const evidencePort: WorkflowAttemptEvidencePort = {
      hasActiveQueueTask: () => true,
      hasLiveProcess: () => false,
      findQueueTaskById: () => null,
      findRunningCoderSessionsByAttemptEvidence: () => [],
    };
    service.setEvidencePort(evidencePort);

    const result = service.reconcileIssueWorkflow('issue-1');

    expect(result.reconciled).toBe(false);
    expect(result.interruptedCount).toBe(0);
    expect(run.stageRun(Stage.Plan).findTask('proposal').latestAttempt?.state).toBe('running');
  });

  it('keeps attempt running when queue evidence is stale but matching session process is live', () => {
    const run = createRunningPlanRun();
    run.startTaskAttempt(Stage.Plan, 'proposal', new Date().toISOString(), {
      queueTaskId: 'stale-queue-task',
      executionId: 'live-execution',
    });

    const repo: WorkflowRunRepositoryPort = {
      createOrLoadActiveAggregate: () => run,
      loadActiveAggregate: () => run,
      loadRunningAggregate: () => run,
      loadLatestAggregate: () => run,
      saveAggregate: () => {},
    };
    const projection: WorkflowRunProjectionPort = { apply: () => {} };
    const service = new WorkflowApplicationService(repo, projection);
    const evidencePort: WorkflowAttemptEvidencePort = {
      hasActiveQueueTask: () => false,
      hasLiveProcess: () => true,
      findQueueTaskById: () => ({ status: 'completed' }),
      findRunningCoderSessionsByAttemptEvidence: () => [{
        id: 'session-1',
        issueId: 'issue-1',
        acpSessionId: 'acp-1',
        executionId: 'live-execution',
        taskDescription: null,
        createdAt: '',
        updatedAt: '',
        stage: 'plan',
        title: null,
        processPid: 321,
        status: 'running',
        completedAt: null,
        failureReason: null,
        model: null,
        coderType: null,
        lastDataAt: null,
        probeSentAt: null,
        probeDeadlineAt: null,
      }],
    };
    service.setEvidencePort(evidencePort);

    const result = service.reconcileIssueWorkflow('issue-1');

    expect(result.reconciled).toBe(false);
    expect(result.interruptedCount).toBe(0);
    expect(run.stageRun(Stage.Plan).findTask('proposal').latestAttempt?.state).toBe('running');
  });

  it('reconciles PID-less running coder session evidence without liveness proof', () => {
    const run = createRunningPlanRun();
    run.startTaskAttempt(Stage.Plan, 'proposal', new Date().toISOString(), {
      executionId: 'pidless-live-execution',
      acpSessionId: 'acp-pidless',
    });

    const repo: WorkflowRunRepositoryPort = {
      createOrLoadActiveAggregate: () => run,
      loadActiveAggregate: () => run,
      loadRunningAggregate: () => run,
      loadLatestAggregate: () => run,
      saveAggregate: () => {},
    };
    const projection: WorkflowRunProjectionPort = { apply: () => {} };
    const service = new WorkflowApplicationService(repo, projection);
    const evidencePort: WorkflowAttemptEvidencePort = {
      hasActiveQueueTask: () => false,
      hasLiveProcess: () => false,
      findQueueTaskById: () => null,
      findRunningCoderSessionsByAttemptEvidence: () => [{
        id: 'session-1',
        issueId: 'issue-1',
        acpSessionId: 'acp-pidless',
        executionId: 'pidless-live-execution',
        taskDescription: null,
        createdAt: '',
        updatedAt: '',
        stage: 'plan',
        title: null,
        processPid: null,
        status: 'running',
        completedAt: null,
        failureReason: null,
        model: null,
        coderType: null,
        lastDataAt: null,
        probeSentAt: null,
        probeDeadlineAt: null,
      }],
    };
    service.setEvidencePort(evidencePort);

    const result = service.reconcileIssueWorkflow('issue-1');

    expect(result.reconciled).toBe(true);
    expect(result.interruptedCount).toBe(1);
    expect(run.stageRun(Stage.Plan).findTask('proposal').latestAttempt?.state).toBe('interrupted');
  });

  it('reconciles stale running latest attempts on failed latest runs', () => {
    const calls: string[] = [];
    const run = createRunningPlanRun();
    run.completeTask(Stage.Plan, 'proposal', { status: 'failed', reason: 'agent stopped' });
    const proposalTask = run.stageRun(Stage.Plan).findTask('proposal');
    proposalTask.latestAttempt = {
      ...proposalTask.latestAttempt!,
      state: 'running',
      completedAt: null,
      error: null,
      diagnostic: null,
      executionId: 'stale-failed-run-attempt',
    };

    const repo: WorkflowRunRepositoryPort = {
      createOrLoadActiveAggregate: () => run,
      loadActiveAggregate: () => {
        calls.push('repo.loadActiveAggregate');
        return null;
      },
      loadRunningAggregate: () => {
        calls.push('repo.loadRunningAggregate');
        return null;
      },
      loadLatestAggregate: () => {
        calls.push('repo.loadLatestAggregate');
        return run;
      },
      saveAggregate: saved => {
        calls.push(`repo.saveAggregate:${saved.snapshot().status}:${saved.stageRun(Stage.Plan).findTask('proposal').latestAttempt?.state}`);
      },
    };
    const projection: WorkflowRunProjectionPort = {
      apply: input => calls.push(`projection.apply:${input.run.snapshot().status}:${input.decision.nextWork.kind}`),
    };
    const service = new WorkflowApplicationService(repo, projection);
    const evidencePort: WorkflowAttemptEvidencePort = {
      hasActiveQueueTask: () => false,
      hasLiveProcess: () => false,
      findQueueTaskById: () => null,
      findRunningCoderSessionsByAttemptEvidence: () => [],
    };
    service.setEvidencePort(evidencePort);

    const result = service.reconcileIssueWorkflow('issue-1');

    expect(result.reconciled).toBe(true);
    expect(result.interruptedCount).toBe(1);
    expect(run.snapshot().status).toBe('failed');
    expect(run.stageRun(Stage.Plan).findTask('proposal').status).toBe('pending');
    expect(run.stageRun(Stage.Plan).findTask('proposal').latestAttempt?.state).toBe('interrupted');
    expect(run.workflowRecoverySummary()).toBe('waiting-for-recovery');
    expect(calls).toEqual([
      'repo.loadRunningAggregate',
      'repo.loadActiveAggregate',
      'repo.loadLatestAggregate',
      'repo.saveAggregate:failed:interrupted',
      'projection.apply:failed:failed',
    ]);
  });

  it('does not interrupt a just-rerun attempt while its queue task is active but evidence is not attached yet', () => {
    const run = createRunningPlanRun();
    run.completeTask(Stage.Plan, 'proposal', { status: 'failed', reason: 'agent stopped' });
    run.rerunStage(Stage.Plan);
    run.startTaskAttempt(Stage.Plan, 'proposal', '2026-05-19T00:00:00.000Z');
    const proposalTask = run.stageRun(Stage.Plan).findTask('proposal');

    expect(proposalTask.latestAttempt?.state).toBe('running');
    expect(proposalTask.latestAttempt?.queueTaskId).toBeNull();
    expect(proposalTask.latestAttempt?.acpSessionId).toBeNull();

    const repo: WorkflowRunRepositoryPort = {
      createOrLoadActiveAggregate: () => run,
      loadActiveAggregate: () => null,
      loadRunningAggregate: () => run,
      loadLatestAggregate: () => run,
      saveAggregate: () => {
        throw new Error('saveAggregate should not be called when active queue task proves liveness');
      },
    };
    const projection: WorkflowRunProjectionPort = {
      apply: () => {
        throw new Error('projection should not be called when active queue task proves liveness');
      },
    };
    const service = new WorkflowApplicationService(repo, projection);
    service.setEvidencePort({
      hasActiveQueueTask: () => true,
      hasLiveProcess: () => false,
      findQueueTaskById: () => null,
      findRunningCoderSessionsByAttemptEvidence: () => [],
    });

    const result = service.reconcileIssueWorkflow('issue-1');

    expect(result.reconciled).toBe(false);
    expect(result.interruptedCount).toBe(0);
    expect(proposalTask.latestAttempt?.state).toBe('running');
    expect(run.snapshot().status).toBe('running');
  });

  it('derives recovery from the current work item after a repairable check failure', () => {
    const run = createRunningPlanRun();
    for (const taskId of ['proposal', 'specs', 'design', 'tasks', 'self-review']) {
      run.completeTask(Stage.Plan, taskId, { status: 'completed' });
    }
    for (const checkName of ['proposal-complete', 'specs-complete', 'design-complete', 'tasks-valid']) {
      run.recordCheckResult(Stage.Plan, { name: checkName, status: 'pass' });
    }
    run.startCheckAttempt(Stage.Plan, 'self-review-passed', '2026-05-19T00:00:00.000Z', { executionId: 'plan-188-self-review-passed-check' });
    run.recordCheckResult(Stage.Plan, {
      name: 'self-review-passed',
      status: 'fail',
      message: 'Self review failed',
    });

    expect(run.stageRun(Stage.Plan).findCheck('self-review-passed').latestAttempt?.state).toBe('failed');
    expect(run.nextWork()).toEqual({ kind: 'task', stage: Stage.Plan, taskId: 'fix-plan-review' });

    const repo: WorkflowRunRepositoryPort = {
      createOrLoadActiveAggregate: () => run,
      loadActiveAggregate: () => run,
      loadRunningAggregate: () => run,
      loadLatestAggregate: () => run,
      saveAggregate: () => {},
    };
    const projection: WorkflowRunProjectionPort = { apply: () => {} };
    const service = new WorkflowApplicationService(repo, projection);

    const recovery = service.getRecoveryProjection('issue-1');
    expect(recovery?.currentWorkItem).toEqual({
      type: 'task',
      id: 'fix-plan-review',
      title: 'Fix plan review findings',
    });
    expect(recovery?.latestAttemptState).toBeNull();
    expect(recovery?.allowedActions).not.toContain('retry');

    const retry = service.checkRetryAvailability({ issueId: 'issue-1', stage: Stage.Plan });
    expect(retry.available).toBe(false);
  });

  it('projects passed terminal checks as awaiting approval without evidence-specific recovery', () => {
    const run = createRunningPlanRun();
    run.completeTask(Stage.Plan, 'proposal', { status: 'completed' });
    run.completeTask(Stage.Plan, 'specs', { status: 'completed' });
    run.completeTask(Stage.Plan, 'design', { status: 'completed' });
    run.completeTask(Stage.Plan, 'tasks', { status: 'completed' });
    run.completeTask(Stage.Plan, 'self-review', { status: 'completed' });
    run.recordCheckResult(Stage.Plan, { name: 'proposal-complete', status: 'pass' });
    run.recordCheckResult(Stage.Plan, { name: 'specs-complete', status: 'pass' });
    run.recordCheckResult(Stage.Plan, { name: 'design-complete', status: 'pass' });
    run.recordCheckResult(Stage.Plan, { name: 'tasks-valid', status: 'pass' });
    run.recordCheckResult(Stage.Plan, {
      name: 'self-review-passed',
      status: 'pass',
      output: { verdict: 'PASS', selfReviewNotes: 'ok', dimensions: [] },
    });
    run.recordCheckResult(Stage.Plan, { name: 'health:plan', status: 'pass' });
    run.approveStage(Stage.Plan, { output: { approved: true } });

    run.materializeTasks(Stage.Build, [{ id: 'T-001', title: 'Build task', order: 0 }]);
    run.completeTask(Stage.Build, 'T-001', { status: 'completed' });
    run.recordCheckResult(Stage.Build, { name: 'health:build', status: 'pass' });
    run.completeTask(Stage.Check, 'ai-review', { status: 'completed' });
    run.recordCheckResult(Stage.Check, { name: 'health:check', status: 'pass', output: { candidateHeadSha: 'candidate-new' } });
    run.recordCheckResult(Stage.Check, {
      name: 'review-passed',
      status: 'pass',
      output: { verdict: 'PASS', reviewReport: 'PASS report', snapshotSha: 'candidate-old' },
    });
    run.recordCheckResult(Stage.Check, {
      name: 'merge-ready',
      status: 'pass',
      output: {
        kind: 'merge-ready',
        targetBranch: 'master',
        strategy: 'squash',
        baseSha: 'base-sha',
        candidateHeadSha: 'candidate-new',
        mergeBaseSha: 'base-sha',
        canMerge: true,
        conflictFiles: [],
      },
    });

    expect(run.nextWork()).toEqual({ kind: 'await-approval', stage: Stage.Check });
    expect(run.workflowRecoverySummary()).toBe('awaiting-approval');

    const repo: WorkflowRunRepositoryPort = {
      createOrLoadActiveAggregate: () => run,
      loadActiveAggregate: () => run,
      loadRunningAggregate: () => run,
      loadLatestAggregate: () => run,
      saveAggregate: () => {},
    };
    const projection: WorkflowRunProjectionPort = { apply: () => {} };
    const service = new WorkflowApplicationService(repo, projection);

    const recovery = service.getRecoveryProjection('issue-1');
    expect(recovery?.workflowSummaryState).toBe('awaiting-approval');
    expect(recovery?.allowedActions).not.toContain('rerun');
  });
});
