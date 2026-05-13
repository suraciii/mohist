import { describe, expect, it, vi } from 'vitest';
import { Stage, IssueStatus, type Issue } from '../src/types';
import { BaseStageRunner } from '../src/workflow/base-stage-runner';
import type {
  ChangeArtifactsManager,
  CheckpointManager,
  IssueRepo,
  ProjectRepo,
  StageContext,
  StageExecutionRepo,
  StageTaskResult,
  WorktreeManager,
} from '../src/workflow/stage-context';
import type { Check } from '../src/workflow/checks';
import { EventBus } from '../src/services/event-bus';

function makeIssue(overrides: Partial<Issue> = {}): Issue {
  return {
    id: 'issue-1',
    number: 188,
    title: 'Test Issue',
    body: '',
    stage: Stage.Build,
    status: IssueStatus.Active,
    projectId: 'project-1',
    labels: [],
    priority: 'p2',
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString(),
    ...overrides,
  };
}

function makeContext(overrides: Partial<StageContext> = {}): StageContext {
  return {
    issue: makeIssue(),
    acpOptions: {} as never,
    artifactManager: {
      getChangeDir: vi.fn().mockReturnValue('/tmp/change'),
      createChangeDir: vi.fn(),
      readArtifact: vi.fn(),
      writeArtifact: vi.fn(),
      exists: vi.fn().mockReturnValue(true),
      readTasks: vi.fn().mockReturnValue(null),
      updateTaskPasses: vi.fn(),
      syncTasksToStageState: vi.fn(),
      archiveChange: vi.fn(),
    } as unknown as ChangeArtifactsManager,
    worktreeManager: {} as WorktreeManager,
    projectRepo: {} as ProjectRepo,
    eventBus: new EventBus() as never,
    checkpointManager: {
      save: vi.fn(),
      load: vi.fn(),
      deleteAll: vi.fn(),
      getResumeSteps: vi.fn().mockReturnValue([]),
      upsert: vi.fn(),
      markStepComplete: vi.fn(),
      deleteStep: vi.fn(),
    } as unknown as CheckpointManager,
    issueRepo: {
      updateStage: vi.fn(),
      setApprovalState: vi.fn(),
      clearApprovalState: vi.fn(),
      updateStatus: vi.fn(),
      findById: vi.fn(),
    } as unknown as IssueRepo,
    stageExecutionRepo: {
      create: vi.fn().mockReturnValue({ id: 'exec-1' }),
      updateCheckResults: vi.fn(),
      appendTaskResult: vi.fn(),
      updateStatus: vi.fn(),
    } as unknown as StageExecutionRepo,
    ...overrides,
  } as StageContext;
}

class ReportingRunner extends BaseStageRunner {
  constructor(
    private readonly checks: Check[],
    private readonly taskResults: StageTaskResult[] = [],
  ) {
    super();
  }

  canHandle(stage: Stage): boolean {
    return stage === Stage.Build;
  }

  protected async executeTasks(ctx: StageContext): Promise<unknown> {
    for (const result of this.taskResults) {
      this.appendTaskResult(ctx, result);
    }
    return { done: true };
  }

  protected getChecks(): Check[] {
    return this.checks;
  }

  protected getNextStage(): Stage {
    return Stage.Check;
  }
}

describe('BaseStageRunner aggregate reporting', () => {
  it('reports task results through WorkflowApplicationService with full task facts', async () => {
    const completeTask = vi.fn();
    const taskResult: StageTaskResult = {
      taskId: 'fix-build-health',
      title: 'Fix build health',
      status: 'completed',
      attempts: 2,
      duration: 123,
      artifacts: ['artifact.log'],
      output: { changed: true },
      reason: 'health repair completed',
      causedBy: { type: 'check-failure', checkName: 'health:build', message: 'typecheck failed' },
    };
    const runner = new ReportingRunner([
      { name: 'health:build', run: async () => ({ name: 'health:build', status: 'pass' }) } as Check,
    ], [taskResult]);

    const ctx = makeContext({
      workflowApplicationService: {
        completeTask,
        recordCheckResult: vi.fn(),
        materializeTasks: vi.fn(),
      },
    });

    await runner.run(ctx);

    expect(completeTask).toHaveBeenCalledWith({
      issueId: 'issue-1',
      stage: Stage.Build,
      taskId: 'fix-build-health',
      result: {
        status: 'completed',
        attempts: 2,
        duration: 123,
        artifacts: ['artifact.log'],
        output: { changed: true },
        reason: 'health repair completed',
        causedBy: { type: 'check-failure', checkName: 'health:build', message: 'typecheck failed' },
      },
    });
  });

  it('reports check evidence and does not run fix scheduling from private runner logic', async () => {
    const recordCheckResult = vi.fn();
    const runner = new ReportingRunner([
      { name: 'health:build', run: async () => ({ name: 'health:build', status: 'fail', message: 'typecheck failed', output: { command: 'npm run typecheck' } }) } as Check,
    ]);

    const ctx = makeContext({
      workflowApplicationService: {
        completeTask: vi.fn(),
        recordCheckResult,
        materializeTasks: vi.fn(),
      },
    });

    const result = await runner.run(ctx);

    expect(result.success).toBe(false);
    expect(recordCheckResult).toHaveBeenCalledWith({
      issueId: 'issue-1',
      stage: Stage.Build,
      result: {
        name: 'health:build',
        status: 'fail',
        message: 'typecheck failed',
        output: { command: 'npm run typecheck' },
      },
    });
    expect(ctx.stageExecutionRepo?.appendTaskResult).not.toHaveBeenCalled();
    expect(result.checkResults).toHaveLength(1);
  });

  it('leaves check failure classification and fix scheduling to aggregate decisions', async () => {
    const recordCheckResult = vi.fn().mockImplementation(() => ({
      decision: {
        events: [
          {
            type: 'fix-task-scheduled',
            stage: Stage.Build,
            taskId: 'fix-build-health',
            causedBy: { type: 'check-failure', checkName: 'health:build' },
          },
        ],
        nextWork: { kind: 'task', stage: Stage.Build, taskId: 'fix-build-health' },
      },
    }));
    const runner = new ReportingRunner([
      { name: 'health:build', run: async () => ({ name: 'health:build', status: 'fail', message: 'typecheck failed' }) } as Check,
    ]);

    const ctx = makeContext({
      workflowApplicationService: {
        completeTask: vi.fn(),
        recordCheckResult,
        materializeTasks: vi.fn(),
      },
    });

    await runner.run(ctx);

    expect(recordCheckResult).toHaveBeenCalledTimes(1);
    expect(ctx.stageExecutionRepo?.appendTaskResult).not.toHaveBeenCalled();
  });

  it('reports each aggregate-requested check when reusing one runner instance', async () => {
    const recordCheckResult = vi.fn();
    const runner = new ReportingRunner([
      { name: 'first-check', run: async () => ({ name: 'first-check', status: 'pass' }) } as Check,
      { name: 'second-check', run: async () => ({ name: 'second-check', status: 'pass' }) } as Check,
    ]);
    const workflowApplicationService = {
      completeTask: vi.fn(),
      recordCheckResult,
      materializeTasks: vi.fn(),
    };

    await runner.run(makeContext({
      workflowApplicationService,
      requestedWork: { kind: 'check', stage: Stage.Build, checkName: 'first-check' },
    }));
    await runner.run(makeContext({
      workflowApplicationService,
      requestedWork: { kind: 'check', stage: Stage.Build, checkName: 'second-check' },
    }));

    expect(recordCheckResult).toHaveBeenCalledTimes(2);
    expect(recordCheckResult).toHaveBeenNthCalledWith(1, expect.objectContaining({
      result: expect.objectContaining({ name: 'first-check', status: 'pass' }),
    }));
    expect(recordCheckResult).toHaveBeenNthCalledWith(2, expect.objectContaining({
      result: expect.objectContaining({ name: 'second-check', status: 'pass' }),
    }));
  });

  it('executes aggregate-requested repair task work as task facts with causedBy metadata', async () => {
    const seen: Array<{ failedCheckName?: string; attempt: number }> = [];
    class RequestedTaskRunner extends ReportingRunner {
      protected override async executeReportedTask(
        _ctx: StageContext,
        _taskId: string,
        failedCheck: import('../src/workflow/stage-context').CheckResult | undefined,
        attempt: number,
      ): Promise<StageTaskResult> {
        seen.push({ failedCheckName: failedCheck?.name, attempt });
        return {
          taskId: 'fix-build-health',
          title: 'Fix build health',
          status: 'completed',
          attempts: 1,
          duration: 25,
          artifacts: [],
          reason: 'scheduled by aggregate policy',
          causedBy: { type: 'check-failure', checkName: 'health:build' },
        };
      }
    }

    const completeTask = vi.fn();
    const runner = new RequestedTaskRunner([]);
    const ctx = makeContext({
      workflowApplicationService: {
        completeTask,
        recordCheckResult: vi.fn(),
        materializeTasks: vi.fn(),
      },
      workflowRun: {
        id: 'run-1',
        issueId: 'issue-1',
        issueNumber: 188,
        status: 'running',
        currentStage: Stage.Build,
        startedBy: null,
        createdAt: '',
        updatedAt: '',
        stageRuns: [{
          id: 'stage-1',
          workflowRunId: 'run-1',
          stage: Stage.Build,
          status: 'running',
          stageOrder: 1,
          approvalStatus: null,
          approvalOutput: null,
          approvalRequestedAt: null,
          approvalRespondedAt: null,
          startedAt: null,
          completedAt: null,
          createdAt: '',
          updatedAt: '',
          tasks: [],
          checks: [{
            id: 'check-1',
            workflowRunId: 'run-1',
            stageRunId: 'stage-1',
            checkName: 'health:build',
            title: 'Build health',
            status: 'failed',
            message: 'typecheck failed',
            output: { command: 'npm run build' },
            runCount: 1,
            lastRunAt: null,
            createdAt: '',
            updatedAt: '',
          }],
        }],
      },
      requestedWork: { kind: 'task', stage: Stage.Build, taskId: 'fix-build-health' },
      requestedTask: {
        id: 'fix-build-health',
        title: 'Fix build health',
        status: 'pending',
        order: 1,
        attempts: 1,
        duration: 0,
        artifacts: [],
        output: null,
        reason: 'typecheck failed',
        causedBy: { type: 'check-failure', checkName: 'health:build', message: 'typecheck failed' },
      },
    });

    await runner.run(ctx);

    expect(seen).toEqual([{ failedCheckName: 'health:build', attempt: 2 }]);
    expect(completeTask).toHaveBeenCalledWith(expect.objectContaining({
      issueId: 'issue-1',
      stage: Stage.Build,
      taskId: 'fix-build-health',
      result: expect.objectContaining({
        status: 'completed',
        reason: 'scheduled by aggregate policy',
        causedBy: { type: 'check-failure', checkName: 'health:build' },
      }),
    }));
  });
});
