import { describe, expect, it } from 'vitest';
import { WorkflowApplicationService, type WorkflowRunProjectionPort, type WorkflowRunRepositoryPort } from '../src/services/workflow-application-service';
import { WorkflowRun } from '../src/workflow/domain';
import { Stage } from '../src/types';

function createRunningPlanRun(issueId = 'issue-1'): WorkflowRun {
  return WorkflowRun.startWorkflow({ id: 'wr-1', issueId, issueNumber: 188 }).run;
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
