import { describe, it, expect, vi, beforeEach } from 'vitest';
import { Stage, IssueStatus, MergeState, type Issue } from '../../src/types';
import type {
  StageContext,
  StageRunResult,
  IssueRepo,
  ChangeArtifactsManager,
  CheckpointManager,
  WorkflowApplicationRuntime,
} from '../../src/workflow/stage-context';
import type { StageRunner } from '../../src/workflow/stage-runner';
import { EventBus } from '../../src/services/event-bus';
import { WorkflowEngine } from '../../src/workflow/workflow-engine';
import type { ConfigInfo } from '../../src/config/config-schema';
import { WorkflowRun, type WorkflowWork } from '../../src/workflow/model';
import { DEFAULT_STAGE_DEFINITIONS } from '../../src/workflow/definitions/default-workflow';

class CapturingRunner implements StageRunner {
  capturedContexts: StageContext[] = [];
  private handledStage: Stage;
  private nextStage: Stage;

  constructor(stage: Stage, nextStage: Stage) {
    this.handledStage = stage;
    this.nextStage = nextStage;
  }

  canHandle(s: Stage): boolean {
    return s === this.handledStage;
  }

  async run(ctx: StageContext): Promise<StageRunResult> {
    this.capturedContexts.push(ctx);
    return {
      success: true,
      output: {},
      checkResults: [],
      nextStage: this.nextStage,
    };
  }
}

function makeIssue(stage: Stage): Issue {
  return {
    id: 'issue-1',
    number: 1,
    title: 'Test Issue',
    body: 'Test',
    stage,
    status: IssueStatus.Active,
    projectId: 'proj-1',
    labels: [],
    priority: 'p2',
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString(),
  };
}

function makeMockIssueRepo(): IssueRepo {
  return {
    updateStage: vi.fn().mockImplementation((_id: string, stage: Stage) => makeIssue(stage)),
    setApprovalState: vi.fn(),
    clearApprovalState: vi.fn(),
    updateStatus: vi.fn().mockReturnValue(makeIssue(Stage.Done)),
    findById: vi.fn().mockReturnValue(null),
  } as unknown as IssueRepo;
}

function makeEngine(options: {
  runners: StageRunner[];
  config?: ConfigInfo;
  issueRepo?: IssueRepo;
  workflowApplicationService?: WorkflowApplicationRuntime;
  workflowRunService?: any;
}) {
  return new WorkflowEngine({
    runners: options.runners,
    issueRepo: options.issueRepo ?? makeMockIssueRepo(),
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
    config: options.config,
    workflowRunService: options.workflowRunService,
    workflowApplicationService: options.workflowApplicationService,
  });
}

function makeSequencedWorkflowService(issue: Issue, work: WorkflowWork[]): WorkflowApplicationRuntime {
  const { run } = WorkflowRun.startWorkflow({ id: 'run-1', issueId: issue.id, issueNumber: issue.number, definitions: DEFAULT_STAGE_DEFINITIONS });
  let index = 0;
  const next = () => work[index++] ?? { kind: 'complete' as const };
  return {
    startWorkflow: vi.fn(() => ({ run, decision: { events: [], nextWork: next() } })),
    resumeDecision: vi.fn(() => ({ run, nextWork: next() })),
    completeTask: vi.fn(() => ({ run, decision: { events: [], nextWork: next() } })),
    recordCheckResult: vi.fn(() => ({ run, decision: { events: [], nextWork: next() } })),
    materializeTasks: vi.fn(() => ({ run, decision: { events: [], nextWork: next() } })),
    approveStage: vi.fn(() => ({ run, decision: { events: [], nextWork: next() } })),
    retryStage: vi.fn(() => ({ run, decision: { events: [], nextWork: next() } })),
  };
}

describe('WorkflowEngine.buildContext model injection', () => {
  it('injects stage-specific model into acpOptions when stageModels override exists', async () => {
    const planRunner = new CapturingRunner(Stage.Plan, Stage.Build);
    const buildRunner = new CapturingRunner(Stage.Build, Stage.Check);
    const checkRunner = new CapturingRunner(Stage.Check, Stage.Done);

    const config: ConfigInfo = {
      opencode: {
        model: 'global-model',
        stageModels: {
          plan: 'plan-model',
          build: 'build-model',
          check: 'check-model',
        },
      },
    };

    const issue = makeIssue(Stage.Plan);
    const engine = makeEngine({
      runners: [planRunner, buildRunner, checkRunner],
      config,
      workflowApplicationService: makeSequencedWorkflowService(issue, [
        { kind: 'task', stage: Stage.Plan, taskId: 'proposal' },
        { kind: 'task', stage: Stage.Build, taskId: 'build-task' },
        { kind: 'task', stage: Stage.Check, taskId: 'ai-review' },
        { kind: 'complete' },
      ]),
    });

    await engine.run(issue, { cwd: '/tmp' });

    expect(planRunner.capturedContexts).toHaveLength(1);
    expect(planRunner.capturedContexts[0].acpOptions.model).toBe('plan-model');

    expect(buildRunner.capturedContexts).toHaveLength(1);
    expect(buildRunner.capturedContexts[0].acpOptions.model).toBe('build-model');

    expect(checkRunner.capturedContexts).toHaveLength(1);
    expect(checkRunner.capturedContexts[0].acpOptions.model).toBe('check-model');
  });

  it('falls back to global model when no stage-specific override', async () => {
    const planRunner = new CapturingRunner(Stage.Plan, Stage.Build);
    const buildRunner = new CapturingRunner(Stage.Build, Stage.Check);

    const config: ConfigInfo = {
      opencode: {
        model: 'global-model',
        stageModels: {
          plan: 'plan-model',
        },
      },
    };

    const issue = makeIssue(Stage.Plan);
    const engine = makeEngine({
      runners: [planRunner, buildRunner],
      config,
      workflowApplicationService: makeSequencedWorkflowService(issue, [
        { kind: 'task', stage: Stage.Plan, taskId: 'proposal' },
        { kind: 'task', stage: Stage.Build, taskId: 'build-task' },
        { kind: 'complete' },
      ]),
    });

    await engine.run(issue, { cwd: '/tmp' });

    expect(planRunner.capturedContexts[0].acpOptions.model).toBe('plan-model');
    expect(buildRunner.capturedContexts[0].acpOptions.model).toBe('global-model');
  });

  it('leaves acpOptions.model unchanged when config is absent', async () => {
    const planRunner = new CapturingRunner(Stage.Plan, Stage.Build);

    const issue = makeIssue(Stage.Plan);
    const engine = makeEngine({
      runners: [planRunner],
      workflowApplicationService: makeSequencedWorkflowService(issue, [
        { kind: 'task', stage: Stage.Plan, taskId: 'proposal' },
        { kind: 'complete' },
      ]),
    });

    await engine.run(issue, { cwd: '/tmp', model: 'pre-existing-model' });

    expect(planRunner.capturedContexts[0].acpOptions.model).toBe('pre-existing-model');
  });

  it('does not inject model when config.opencode.model is undefined and no stage override', async () => {
    const planRunner = new CapturingRunner(Stage.Plan, Stage.Build);

    const config: ConfigInfo = {
      opencode: {},
    };

    const issue = makeIssue(Stage.Plan);
    const engine = makeEngine({
      runners: [planRunner],
      config,
      workflowApplicationService: makeSequencedWorkflowService(issue, [
        { kind: 'task', stage: Stage.Plan, taskId: 'proposal' },
        { kind: 'complete' },
      ]),
    });

    await engine.run(issue, { cwd: '/tmp' });

    expect(planRunner.capturedContexts[0].acpOptions.model).toBeUndefined();
  });

  it('preserves other acpOptions fields while injecting model', async () => {
    const planRunner = new CapturingRunner(Stage.Plan, Stage.Build);

    const config: ConfigInfo = {
      opencode: { model: 'injected-model' },
    };

    const issue = makeIssue(Stage.Plan);
    const engine = makeEngine({
      runners: [planRunner],
      config,
      workflowApplicationService: makeSequencedWorkflowService(issue, [
        { kind: 'task', stage: Stage.Plan, taskId: 'proposal' },
        { kind: 'complete' },
      ]),
    });

    await engine.run(issue, { cwd: '/tmp', taskId: 'task-123' });

    expect(planRunner.capturedContexts[0].acpOptions.cwd).toBe('/tmp');
    expect(planRunner.capturedContexts[0].acpOptions.taskId).toBe('task-123');
    expect(planRunner.capturedContexts[0].acpOptions.model).toBe('injected-model');
  });
});

describe('WorkflowEngine aggregate retry startup', () => {
  it('uses aggregate retryability instead of failed current-stage shape checks', async () => {
    const issue = makeIssue(Stage.Plan);
    const service = makeSequencedWorkflowService(issue, [{ kind: 'complete' }]);
    const workflowRunService = {
      canRetryStage: vi.fn().mockReturnValue(false),
      getLatestRunForIssue: vi.fn().mockReturnValue({ status: 'failed', currentStage: Stage.Plan }),
      getActiveRunForIssue: vi.fn().mockReturnValue(null),
    };

    const engine = makeEngine({
      runners: [new CapturingRunner(Stage.Plan, Stage.Build)],
      workflowApplicationService: service,
      workflowRunService,
    });

    await engine.run(issue, { cwd: '/tmp' });

    expect(workflowRunService.canRetryStage).toHaveBeenCalledWith(issue.id, Stage.Plan);
    expect(service.retryStage).not.toHaveBeenCalled();
    expect(service.resumeDecision).toHaveBeenCalledWith(issue.id);
  });

  it('preserves rejected approval feedback before retryStage clears approval state', async () => {
    const issue = makeIssue(Stage.Plan);
    const runner = new CapturingRunner(Stage.Plan, Stage.Build);
    const service = makeSequencedWorkflowService(issue, [
      { kind: 'task', stage: Stage.Plan, taskId: 'proposal' },
      { kind: 'complete' },
    ]);
    const workflowRunService = {
      canRetryStage: vi.fn().mockReturnValue(true),
      getLatestRunForIssue: vi.fn().mockReturnValue({
        status: 'failed',
        currentStage: Stage.Plan,
        stageRuns: [{ stage: Stage.Plan, approvalStatus: 'rejected', approvalOutput: 'Please rewrite the plan' }],
      }),
      getActiveRunForIssue: vi.fn().mockReturnValue({
        stageRuns: [{ stage: Stage.Plan, approvalStatus: null, approvalOutput: null, tasks: [] }],
      }),
    };

    const engine = makeEngine({
      runners: [runner],
      workflowApplicationService: service,
      workflowRunService,
    });

    await engine.run(issue, { cwd: '/tmp' });

    expect(service.retryStage).toHaveBeenCalledWith({
      issueId: issue.id,
      stage: Stage.Plan,
      startedBy: 'retry',
    });
    expect(runner.capturedContexts[0].rejectionFeedback).toBe('Please rewrite the plan');
  });

  it('surfaces aggregate blocked work instead of running the stage again', async () => {
    const issue = makeIssue(Stage.Build);
    const runner = new CapturingRunner(Stage.Build, Stage.Check);
    const service = makeSequencedWorkflowService(issue, [
      {
        kind: 'blocked',
        stage: Stage.Build,
        reason: { complete: false, reason: 'dynamic-source-missing', stage: Stage.Build },
      },
    ]);

    const engine = makeEngine({
      runners: [runner],
      workflowApplicationService: service,
    });

    const result = await engine.run(issue, { cwd: '/tmp' });

    expect(result).toEqual({
      completed: false,
      stage: Stage.Build,
      message: 'dynamic-source-missing: build',
    });
    expect(runner.capturedContexts).toHaveLength(0);
  });
});

describe('WorkflowEngine merge-gated completion', () => {
  function makeMockIssueRepoWithIssue(initialIssue: Issue) {
    let currentIssue = initialIssue;
    return {
      updateStage: vi.fn().mockImplementation((_id: string, stage: Stage) => {
        currentIssue = { ...currentIssue, stage };
        return currentIssue;
      }),
      findById: vi.fn().mockReturnValue(currentIssue),
      setApprovalState: vi.fn(),
      clearApprovalState: vi.fn(),
      updateStatus: vi.fn().mockImplementation((_id: string, status: IssueStatus) => {
        currentIssue = { ...currentIssue, status };
        return currentIssue;
      }),
      updateBlockedReason: vi.fn(),
      setMergeState: vi.fn(),
    } as unknown as IssueRepo;
  }

  it('allows Check stage to transition to Integrate then Done', async () => {
    const checkRunner = new class implements StageRunner {
      canHandle(s: Stage): boolean { return s === Stage.Check; }
      async run(): Promise<StageRunResult> {
        return { success: true, nextStage: Stage.Integrate, checkResults: [], output: {} };
      }
    }();

    const integrateRunner = new class implements StageRunner {
      canHandle(s: Stage): boolean { return s === Stage.Integrate; }
      async run(): Promise<StageRunResult> {
        return { success: true, nextStage: Stage.Done, checkResults: [], output: {} };
      }
    }();

    const issue = makeIssue(Stage.Check);
    const mockRepo = makeMockIssueRepoWithIssue(issue);
    const workflowApplicationService = makeSequencedWorkflowService(issue, [
      { kind: 'task', stage: Stage.Check, taskId: 'ai-review' },
      { kind: 'task', stage: Stage.Integrate, taskId: 'integrate:merge' },
      { kind: 'complete' },
    ]);

    const engine = new WorkflowEngine({
      runners: [checkRunner, integrateRunner],
      issueRepo: mockRepo,
      eventBus: new EventBus(),
      checkpointManager: { save: vi.fn(), load: vi.fn(), deleteAll: vi.fn(), markStepComplete: vi.fn(), getResumeSteps: vi.fn() } as unknown as CheckpointManager,
      artifactManager: { getChangeDir: vi.fn().mockReturnValue('/tmp/change'), createChangeDir: vi.fn(), readArtifact: vi.fn(), writeArtifact: vi.fn(), exists: vi.fn(), readTasks: vi.fn(), updateTaskPasses: vi.fn(), archiveChange: vi.fn() } as unknown as ChangeArtifactsManager,
      workflowApplicationService,
    });

    const result = await engine.run(issue, { cwd: '/tmp' });

    expect(result.completed).toBe(true);
    expect(result.stage).toBe(Stage.Done);
  });

  it('marks done/completed only when mergeState is merged', async () => {
    const buildRunner = new class implements StageRunner {
      canHandle(s: Stage): boolean { return s === Stage.Build; }
      async run(): Promise<StageRunResult> {
        return { success: true, nextStage: Stage.Check, checkResults: [], output: {} };
      }
    }();

    const checkRunner = new class implements StageRunner {
      canHandle(s: Stage): boolean { return s === Stage.Check; }
      async run(): Promise<StageRunResult> {
        return { success: false, checkResults: [], message: 'Waiting for approval' };
      }
    }();

    const issue = makeIssue(Stage.Build);
    const mockRepo = makeMockIssueRepoWithIssue({ ...issue, mergeState: MergeState.Merged });
    const updateStatusSpy = vi.spyOn(mockRepo, 'updateStatus');
    const workflowApplicationService = makeSequencedWorkflowService(issue, [
      { kind: 'task', stage: Stage.Build, taskId: 'build-task' },
      { kind: 'await-approval', stage: Stage.Check },
    ]);

    const engine = new WorkflowEngine({
      runners: [buildRunner, checkRunner],
      issueRepo: mockRepo,
      eventBus: new EventBus(),
      checkpointManager: { save: vi.fn(), load: vi.fn(), deleteAll: vi.fn(), markStepComplete: vi.fn(), getResumeSteps: vi.fn() } as unknown as CheckpointManager,
      artifactManager: { getChangeDir: vi.fn().mockReturnValue('/tmp/change'), createChangeDir: vi.fn(), readArtifact: vi.fn(), writeArtifact: vi.fn(), exists: vi.fn(), readTasks: vi.fn(), updateTaskPasses: vi.fn(), archiveChange: vi.fn() } as unknown as ChangeArtifactsManager,
      workflowApplicationService,
    });

    await engine.run(issue, { cwd: '/tmp' });

    expect(updateStatusSpy).not.toHaveBeenCalled();
  });
});
