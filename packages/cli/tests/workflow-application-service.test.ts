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
});
