import { describe, it, expect, vi, beforeEach } from 'vitest';
import { Stage, IssueStatus, MergeState, type Issue } from '../../src/types';
import type {
  StageContext,
  StageRunResult,
  IssueRepo,
  ChangeArtifactsManager,
  CheckpointManager,
} from '../../src/workflow/stage-context';
import type { StageRunner } from '../../src/workflow/check-stage-runner';
import { EventBus } from '../../src/services/event-bus';
import { WorkflowEngine } from '../../src/workflow/workflow-engine';
import type { ConfigInfo } from '../../src/config/config-schema';

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
  } as unknown as IssueRepo;
}

function makeEngine(options: {
  runners: StageRunner[];
  config?: ConfigInfo;
  issueRepo?: IssueRepo;
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
  });
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

    const engine = makeEngine({
      runners: [planRunner, buildRunner, checkRunner],
      config,
    });

    await engine.run(makeIssue(Stage.Plan), { cwd: '/tmp' });

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

    const engine = makeEngine({
      runners: [planRunner, buildRunner],
      config,
    });

    await engine.run(makeIssue(Stage.Plan), { cwd: '/tmp' });

    expect(planRunner.capturedContexts[0].acpOptions.model).toBe('plan-model');
    expect(buildRunner.capturedContexts[0].acpOptions.model).toBe('global-model');
  });

  it('leaves acpOptions.model unchanged when config is absent', async () => {
    const planRunner = new CapturingRunner(Stage.Plan, Stage.Build);

    const engine = makeEngine({
      runners: [planRunner],
    });

    await engine.run(makeIssue(Stage.Plan), { cwd: '/tmp', model: 'pre-existing-model' });

    expect(planRunner.capturedContexts[0].acpOptions.model).toBe('pre-existing-model');
  });

  it('does not inject model when config.opencode.model is undefined and no stage override', async () => {
    const planRunner = new CapturingRunner(Stage.Plan, Stage.Build);

    const config: ConfigInfo = {
      opencode: {},
    };

    const engine = makeEngine({
      runners: [planRunner],
      config,
    });

    await engine.run(makeIssue(Stage.Plan), { cwd: '/tmp' });

    expect(planRunner.capturedContexts[0].acpOptions.model).toBeUndefined();
  });

  it('preserves other acpOptions fields while injecting model', async () => {
    const planRunner = new CapturingRunner(Stage.Plan, Stage.Build);

    const config: ConfigInfo = {
      opencode: { model: 'injected-model' },
    };

    const engine = makeEngine({
      runners: [planRunner],
      config,
    });

    await engine.run(makeIssue(Stage.Plan), { cwd: '/tmp', taskId: 'task-123' });

    expect(planRunner.capturedContexts[0].acpOptions.cwd).toBe('/tmp');
    expect(planRunner.capturedContexts[0].acpOptions.taskId).toBe('task-123');
    expect(planRunner.capturedContexts[0].acpOptions.model).toBe('injected-model');
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

  it('blocks Check stage from transitioning directly to Done', async () => {
    const checkRunner = new class implements StageRunner {
      canHandle(s: Stage): boolean { return s === Stage.Check; }
      async run(): Promise<StageRunResult> {
        return { success: true, nextStage: Stage.Done, checkResults: [], output: {} };
      }
    }();

    const issue = makeIssue(Stage.Check);
    const mockRepo = makeMockIssueRepoWithIssue(issue);

    const engine = new WorkflowEngine({
      runners: [checkRunner],
      issueRepo: mockRepo,
      eventBus: new EventBus(),
      checkpointManager: { save: vi.fn(), load: vi.fn(), deleteAll: vi.fn(), markStepComplete: vi.fn(), getResumeSteps: vi.fn() } as unknown as CheckpointManager,
      artifactManager: { getChangeDir: vi.fn().mockReturnValue('/tmp/change'), createChangeDir: vi.fn(), readArtifact: vi.fn(), writeArtifact: vi.fn(), exists: vi.fn(), readTasks: vi.fn(), updateTaskPasses: vi.fn(), archiveChange: vi.fn() } as unknown as ChangeArtifactsManager,
    });

    const result = await engine.run(issue, { cwd: '/tmp' });

    expect(result.completed).toBe(false);
    expect(result.message).toContain('Check stage cannot transition directly to Done');
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

    const engine = new WorkflowEngine({
      runners: [buildRunner, checkRunner],
      issueRepo: mockRepo,
      eventBus: new EventBus(),
      checkpointManager: { save: vi.fn(), load: vi.fn(), deleteAll: vi.fn(), markStepComplete: vi.fn(), getResumeSteps: vi.fn() } as unknown as CheckpointManager,
      artifactManager: { getChangeDir: vi.fn().mockReturnValue('/tmp/change'), createChangeDir: vi.fn(), readArtifact: vi.fn(), writeArtifact: vi.fn(), exists: vi.fn(), readTasks: vi.fn(), updateTaskPasses: vi.fn(), archiveChange: vi.fn() } as unknown as ChangeArtifactsManager,
    });

    await engine.run(issue, { cwd: '/tmp' });

    expect(updateStatusSpy).not.toHaveBeenCalled();
  });
});
