import { describe, expect, it, vi } from 'vitest';
import { Stage, IssueStatus, type Issue } from '../src/types';
import { WorkflowEngine } from '../src/workflow/workflow-engine';
import { EventBus } from '../src/services/event-bus';
import { DEFAULT_STAGE_DEFINITIONS, WorkflowRun } from '../src/workflow/domain';
import type { WorkflowApplicationRuntime } from '../src/workflow/stage-context';
import type {
  ChangeArtifactsManager,
  CheckpointManager,
  IssueRepo,
  StageContext,
  StageRunResult,
} from '../src/workflow/stage-context';
import type { StageRunner } from '../src/workflow/check-stage-runner';

function makeIssue(stage: Stage = Stage.Backlog): Issue {
  return {
    id: 'issue-1',
    number: 188,
    title: 'Aggregate Engine',
    body: '',
    stage,
    status: IssueStatus.Active,
    projectId: 'project-1',
    labels: [],
    priority: 'p2',
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString(),
  };
}

function makeIssueRepo(issue: Issue): IssueRepo {
  let current = issue;
  return {
    updateStage: vi.fn().mockImplementation((_id: string, stage: Stage) => {
      current = { ...current, stage };
      return current;
    }),
    setApprovalState: vi.fn(),
    clearApprovalState: vi.fn(),
    updateStatus: vi.fn().mockImplementation((_id: string, status: IssueStatus) => {
      current = { ...current, status };
      return current;
    }),
    findById: vi.fn().mockImplementation(() => current),
  } as unknown as IssueRepo;
}

function makeEngine(
  issue: Issue,
  service: WorkflowApplicationRuntime,
  runners: StageRunner[],
  workflowRunService?: { getActiveRunForIssue: (issueId: string) => unknown; getLatestRunForIssue: (issueId: string) => unknown },
) {
  return new WorkflowEngine({
    runners,
    issueRepo: makeIssueRepo(issue),
    eventBus: new EventBus(),
    checkpointManager: {
      save: vi.fn(),
      load: vi.fn(),
      deleteAll: vi.fn(),
    } as unknown as CheckpointManager,
    artifactManager: {
      getChangeDir: vi.fn().mockReturnValue('/tmp/change'),
      createChangeDir: vi.fn(),
      readArtifact: vi.fn(),
      writeArtifact: vi.fn(),
      exists: vi.fn(),
      readTasks: vi.fn(),
      updateTaskPasses: vi.fn(),
      archiveChange: vi.fn(),
    } as unknown as ChangeArtifactsManager,
    workflowApplicationService: service,
    workflowRunService: workflowRunService as never,
  });
}

class AggregatePlanRunner implements StageRunner {
  seenStages: Stage[] = [];

  constructor(private readonly service: WorkflowApplicationRuntime, private readonly issueId: string) {}

  canHandle(stage: Stage): boolean {
    return stage === Stage.Plan;
  }

  async run(ctx: StageContext): Promise<StageRunResult> {
    this.seenStages.push(ctx.issue.stage);
    if (ctx.requestedWork?.kind === 'task') {
      this.service.completeTask({
        issueId: this.issueId,
        stage: ctx.requestedWork.stage,
        taskId: ctx.requestedWork.taskId,
        result: { status: 'completed' },
      });
    } else if (ctx.requestedWork?.kind === 'check') {
      this.service.recordCheckResult({
        issueId: this.issueId,
        stage: ctx.requestedWork.stage,
        result: { name: ctx.requestedWork.checkName, status: 'pass' },
      });
    }
    return { success: true, output: null, checkResults: [] };
  }
}

describe('WorkflowEngine aggregate progression', () => {
  it('advances Plan by aggregate stageOrder instead of runner nextStage', async () => {
    const issue = makeIssue(Stage.Backlog);
    const definitions = DEFAULT_STAGE_DEFINITIONS.map(definition => definition.stage === Stage.Plan
      ? { ...definition, requiresApproval: false }
      : definition);
    const { run } = WorkflowRun.startWorkflow({ id: 'run-1', issueId: issue.id, issueNumber: issue.number, definitions });

    const service: WorkflowApplicationRuntime = {
      startWorkflow: vi.fn(() => ({ run, decision: { events: [], nextWork: run.nextWork() } })),
      resumeDecision: vi.fn(() => ({ run, nextWork: run.nextWork() })),
      completeTask: vi.fn(({ stage, taskId, result }) => ({ run, decision: run.completeTask(stage, taskId, result) })),
      recordCheckResult: vi.fn(({ stage, result }) => ({ run, decision: run.recordCheckResult(stage, result) })),
      materializeTasks: vi.fn(({ stage, tasks }) => ({ run, decision: run.materializeTasks(stage, tasks) })),
      approveStage: vi.fn(({ stage, approval }) => ({ run, decision: run.approveStage(stage, approval) })),
    };
    const runner = new AggregatePlanRunner(service, issue.id);

    const engine = makeEngine(issue, service, [runner]);

    const result = await engine.run(issue, { cwd: '/tmp' });

    expect(result.completed).toBe(false);
    expect(result.stage).toBe(Stage.Build);
    expect(result.message).toBe('Pipeline cannot handle stage: build');
    expect(run.stageRun(Stage.Plan).status).toBe('passed');
    expect(run.currentStage).toBe(Stage.Build);
    expect(runner.seenStages).toEqual([
      Stage.Plan,
      Stage.Plan,
      Stage.Plan,
      Stage.Plan,
      Stage.Plan,
      Stage.Plan,
      Stage.Plan,
      Stage.Plan,
      Stage.Plan,
      Stage.Plan,
      Stage.Plan,
    ]);
  });

  it('keeps failed stage local with aggregate failure reason visible', async () => {
    const issue = makeIssue(Stage.Backlog);
    const { run } = WorkflowRun.startWorkflow({ id: 'run-1', issueId: issue.id, issueNumber: issue.number });

    const service: WorkflowApplicationRuntime = {
      startWorkflow: vi.fn(() => ({ run, decision: { events: [], nextWork: run.nextWork() } })),
      resumeDecision: vi.fn(() => ({ run, nextWork: run.nextWork() })),
      completeTask: vi.fn(({ stage, taskId, result }) => ({ run, decision: run.completeTask(stage, taskId, result) })),
      recordCheckResult: vi.fn(({ stage, result }) => ({ run, decision: run.recordCheckResult(stage, result) })),
      materializeTasks: vi.fn(({ stage, tasks }) => ({ run, decision: run.materializeTasks(stage, tasks) })),
      approveStage: vi.fn(({ stage, approval }) => ({ run, decision: run.approveStage(stage, approval) })),
    };

    const runner: StageRunner = {
      canHandle: stage => stage === Stage.Plan,
      run: async ctx => {
        if (ctx.requestedWork?.kind === 'task') {
          service.completeTask({
            issueId: issue.id,
            stage: ctx.requestedWork.stage,
            taskId: ctx.requestedWork.taskId,
            result: { status: 'failed', reason: 'proposal generation failed' },
          });
        }
        return { success: false, output: null, checkResults: [], message: 'runner failed' };
      },
    };

    const workflowRunService = {
      getActiveRunForIssue: vi.fn(() => run.snapshot()),
      getLatestRunForIssue: vi.fn(() => run.snapshot()),
    };
    const engine = makeEngine(issue, service, [runner], workflowRunService);

    const result = await engine.run(issue, { cwd: '/tmp' });

    expect(result.completed).toBe(false);
    expect(result.stage).toBe(Stage.Plan);
    expect(result.message).toBe('proposal generation failed');
    expect(run.currentStage).toBe(Stage.Plan);
    expect(run.stageRun(Stage.Plan).status).toBe('failed');
    expect(run.failure).toMatchObject({ reason: 'task-failed', stage: Stage.Plan, taskId: 'proposal' });
  });

  it('returns completed when runner finishes the aggregate without another resumeDecision', async () => {
    const issue = makeIssue(Stage.Backlog);
    const definitions = [
      {
        ...DEFAULT_STAGE_DEFINITIONS.find(definition => definition.stage === Stage.Plan)!,
        tasks: [{ id: 'proposal', title: 'Generate proposal' }],
        checks: [{ name: 'health:plan', title: 'Plan health gate' }],
        requiresApproval: false,
      },
    ];
    const { run } = WorkflowRun.startWorkflow({ id: 'run-1', issueId: issue.id, issueNumber: issue.number, definitions });

    const service: WorkflowApplicationRuntime = {
      startWorkflow: vi.fn(() => ({ run, decision: { events: [], nextWork: run.nextWork() } })),
      resumeDecision: vi.fn(() => ({ run, nextWork: run.nextWork() })),
      completeTask: vi.fn(({ stage, taskId, result }) => ({ run, decision: run.completeTask(stage, taskId, result) })),
      recordCheckResult: vi.fn(({ stage, result }) => ({ run, decision: run.recordCheckResult(stage, result) })),
      materializeTasks: vi.fn(({ stage, tasks }) => ({ run, decision: run.materializeTasks(stage, tasks) })),
      approveStage: vi.fn(({ stage, approval }) => ({ run, decision: run.approveStage(stage, approval) })),
    };

    const runner: StageRunner = {
      canHandle: stage => stage === Stage.Plan,
      run: async ctx => {
        if (ctx.requestedWork?.kind === 'task') {
          service.completeTask({
            issueId: issue.id,
            stage: ctx.requestedWork.stage,
            taskId: ctx.requestedWork.taskId,
            result: { status: 'completed' },
          });
        }
        if (ctx.requestedWork?.kind === 'check') {
          service.recordCheckResult({
            issueId: issue.id,
            stage: ctx.requestedWork.stage,
            result: { name: ctx.requestedWork.checkName, status: 'pass' },
          });
        }
        return { success: true, output: null, checkResults: [] };
      },
    };

    const workflowRunService = {
      getActiveRunForIssue: vi.fn(() => run.snapshot()),
      getLatestRunForIssue: vi.fn(() => run.snapshot()),
    };
    const engine = makeEngine(issue, service, [runner], workflowRunService);

    const result = await engine.run(issue, { cwd: '/tmp' });

    expect(result).toEqual({ completed: true, stage: Stage.Done, message: 'Pipeline completed' });
    expect(run.status).toBe('passed');
    expect(workflowRunService.getLatestRunForIssue).toHaveBeenCalled();
    expect(service.resumeDecision).toHaveBeenCalledTimes(1);
  });
});
