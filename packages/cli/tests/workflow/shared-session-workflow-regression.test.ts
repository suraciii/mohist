import { describe, it, expect, vi } from 'vitest';
import { Stage, IssueStatus, type Issue } from '../../src/types';
import type {
  StageContext,
  StageRunResult,
  IssueRepo,
  ChangeArtifactsManager,
  CheckpointManager,
} from '../../src/workflow/stage-context';
import type { StageRunner } from '../../src/workflow/stage-runner';
import { EventBus } from '../../src/services/event-bus';
import { WorkflowEngine } from '../../src/workflow/workflow-engine';
import type { WorkflowApplicationRuntime } from '../../src/workflow/stage-context';
import { WorkflowRun, type WorkflowWork } from '../../src/workflow/model';
import { DEFAULT_STAGE_DEFINITIONS } from '../../src/workflow/definition/default-workflow';

class RegistryCapturingRunner implements StageRunner {
  capturedRegistries: StageContext['agentSessionRegistry'][] = [];
  private handledStage: Stage;

  constructor(stage: Stage) {
    this.handledStage = stage;
  }

  canHandle(s: Stage): boolean {
    return s === this.handledStage;
  }

  async run(ctx: StageContext): Promise<StageRunResult> {
    this.capturedRegistries.push(ctx.agentSessionRegistry);
    return {
      success: true,
      output: {},
      checkResults: [],
      nextStage: Stage.Done,
    };
  }
}

function makeIssue(stage: Stage): Issue {
  return {
    id: 'issue-1',
    number: 231,
    title: 'Shared Session Issue',
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

function makeEngine(runners: StageRunner[], workflowRunService?: any, workflowApplicationService?: WorkflowApplicationRuntime) {
  return new WorkflowEngine({
    runners,
    issueRepo: makeMockIssueRepo(),
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
    workflowRunService,
    workflowApplicationService,
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

function makeWorkflowRunServiceWithRun(runId: string) {
  return {
    getActiveRunForIssue: vi.fn().mockReturnValue({
      id: runId,
      stageRuns: [{ stage: Stage.Plan, status: 'running', tasks: [] }],
    }),
    getLatestRunForIssue: vi.fn().mockReturnValue(null),
    canRetryStage: vi.fn().mockReturnValue(false),
  };
}

describe('T-006: WorkflowEngine shared-session registry lifecycle', () => {
  it('provides registry when workflowRun is available', async () => {
    const runner = new RegistryCapturingRunner(Stage.Plan);
    const issue = makeIssue(Stage.Plan);
    const wfRunService = makeWorkflowRunServiceWithRun('run-abc');
    const service = makeSequencedWorkflowService(issue, [
      { kind: 'task', stage: Stage.Plan, taskId: 'proposal' },
      { kind: 'complete' },
    ]);

    const engine = makeEngine([runner], wfRunService, service);
    await engine.run(issue, { cwd: '/tmp' });

    expect(runner.capturedRegistries.length).toBeGreaterThanOrEqual(1);
    expect(runner.capturedRegistries[0]).toBeDefined();
  });

  it('provides no registry when no workflowRun is available', async () => {
    const runner = new RegistryCapturingRunner(Stage.Plan);
    const issue = makeIssue(Stage.Plan);
    const service = makeSequencedWorkflowService(issue, [{ kind: 'complete' }]);

    const engine = makeEngine([runner], undefined, service);
    await engine.run(issue, { cwd: '/tmp' });

    for (const reg of runner.capturedRegistries) {
      expect(reg).toBeUndefined();
    }
  });

  it('provides same registry for sequential tasks in same stage attempt', async () => {
    const capturedContexts: StageContext[] = [];
    const runner: StageRunner = {
      canHandle: (s: Stage) => s === Stage.Plan,
      run: async (ctx: StageContext): Promise<StageRunResult> => {
        capturedContexts.push(ctx);
        return { success: true, output: {}, checkResults: [] };
      },
    };

    const issue = makeIssue(Stage.Plan);
    const wfRunService = makeWorkflowRunServiceWithRun('run-stable');
    const service = makeSequencedWorkflowService(issue, [
      { kind: 'task', stage: Stage.Plan, taskId: 'proposal' },
      { kind: 'task', stage: Stage.Plan, taskId: 'specs' },
      { kind: 'complete' },
    ]);

    const engine = makeEngine([runner], wfRunService, service);
    await engine.run(issue, { cwd: '/tmp' });

    const withRegistries = capturedContexts.filter(c => c.agentSessionRegistry !== undefined);
    expect(withRegistries.length).toBeGreaterThanOrEqual(2);
    for (let i = 1; i < withRegistries.length; i++) {
      expect(withRegistries[i].agentSessionRegistry).toBe(withRegistries[0].agentSessionRegistry);
    }
  });

  it('engine.run closes registries in finally block', async () => {
    let capturedRegistry: StageContext['agentSessionRegistry'] | undefined;
    const closeSpy = vi.fn().mockResolvedValue(undefined);
    const runner: StageRunner = {
      canHandle: (s: Stage) => s === Stage.Plan,
      run: async (ctx: StageContext): Promise<StageRunResult> => {
        const session = {
          execute: vi.fn().mockResolvedValue({ success: true, acpSessionId: 'test' }),
          close: closeSpy,
        };
        await ctx.agentSessionRegistry?.getOrCreate('plan-artifacts', () => Promise.resolve(session as any));
        capturedRegistry = ctx.agentSessionRegistry;
        return { success: true, output: {}, checkResults: [] };
      },
    };

    const issue = makeIssue(Stage.Plan);
    const wfRunService = makeWorkflowRunServiceWithRun('run-close');
    const service = makeSequencedWorkflowService(issue, [
      { kind: 'task', stage: Stage.Plan, taskId: 'proposal' },
      { kind: 'complete' },
    ]);

    const engine = makeEngine([runner], wfRunService, service);
    await engine.run(issue, { cwd: '/tmp' });

    expect(capturedRegistry).toBeDefined();
    expect(closeSpy).toHaveBeenCalled();
  });

  it('closes the current stage registry when work reaches approval boundary', async () => {
    const closeSpy = vi.fn().mockResolvedValue(undefined);
    const runner: StageRunner = {
      canHandle: (s: Stage) => s === Stage.Plan,
      run: async (ctx: StageContext): Promise<StageRunResult> => {
        await ctx.agentSessionRegistry?.getOrCreate('plan-artifacts', () => Promise.resolve({
          execute: vi.fn().mockResolvedValue({ success: true, acpSessionId: 'approval-boundary' }),
          close: closeSpy,
        } as any));
        return { success: true, output: {}, checkResults: [] };
      },
    };

    const issue = makeIssue(Stage.Plan);
    const wfRunService = makeWorkflowRunServiceWithRun('run-approval-boundary');
    const service = makeSequencedWorkflowService(issue, [
      { kind: 'task', stage: Stage.Plan, taskId: 'proposal' },
      { kind: 'await-approval', stage: Stage.Plan },
    ]);

    const engine = makeEngine([runner], wfRunService, service);
    const result = await engine.run(issue, { cwd: '/tmp' });

    expect(result.completed).toBe(false);
    expect(result.stage).toBe(Stage.Plan);
    expect(result.message).toBe('Awaiting plan approval');
    expect(closeSpy).toHaveBeenCalledTimes(1);
  });

  it('uses different registry for different workflow run IDs', async () => {
    let firstRegistry: StageContext['agentSessionRegistry'] | undefined;
    const runner1: StageRunner = {
      canHandle: (s: Stage) => s === Stage.Plan,
      run: async (ctx: StageContext): Promise<StageRunResult> => {
        firstRegistry = ctx.agentSessionRegistry;
        return { success: true, output: {}, checkResults: [] };
      },
    };

    const issue = makeIssue(Stage.Plan);
    const wfRunService1 = makeWorkflowRunServiceWithRun('run-first');
    const service1 = makeSequencedWorkflowService(issue, [
      { kind: 'task', stage: Stage.Plan, taskId: 'proposal' },
      { kind: 'complete' },
    ]);

    const engine1 = makeEngine([runner1], wfRunService1, service1);
    await engine1.run(issue, { cwd: '/tmp' });

    let secondRegistry: StageContext['agentSessionRegistry'] | undefined;
    const runner2: StageRunner = {
      canHandle: (s: Stage) => s === Stage.Plan,
      run: async (ctx: StageContext): Promise<StageRunResult> => {
        secondRegistry = ctx.agentSessionRegistry;
        return { success: true, output: {}, checkResults: [] };
      },
    };

    const wfRunService2 = makeWorkflowRunServiceWithRun('run-second');
    const service2 = makeSequencedWorkflowService(issue, [
      { kind: 'task', stage: Stage.Plan, taskId: 'proposal' },
      { kind: 'complete' },
    ]);

    const engine2 = makeEngine([runner2], wfRunService2, service2);
    await engine2.run(issue, { cwd: '/tmp' });

    expect(firstRegistry).toBeDefined();
    expect(secondRegistry).toBeDefined();
    expect(firstRegistry).not.toBe(secondRegistry);
  });

  it('uses a fresh registry after retrying the same stage within one workflow run', async () => {
    const issue = makeIssue(Stage.Plan);
    const { run } = WorkflowRun.startWorkflow({ id: 'run-shared-retry', issueId: issue.id, issueNumber: issue.number, definitions: DEFAULT_STAGE_DEFINITIONS });
    let visit = 0;
    const sessionIds: string[] = [];
    const factoryCalls: string[] = [];

    const service: WorkflowApplicationRuntime = {
      startWorkflow: vi.fn(() => ({ run, decision: { events: [], nextWork: run.nextWork() } })),
      resumeDecision: vi.fn(() => ({ run, nextWork: run.nextWork() })),
      completeTask: vi.fn(({ stage, taskId, result }) => ({ run, decision: run.completeTask(stage, taskId, result) })),
      recordCheckResult: vi.fn(({ stage, result }) => ({ run, decision: run.recordCheckResult(stage, result) })),
      materializeTasks: vi.fn(({ stage, tasks }) => ({ run, decision: run.materializeTasks(stage, tasks) })),
      approveStage: vi.fn(({ stage, approval }) => ({ run, decision: run.approveStage(stage, approval) })),
      retryStage: vi.fn(({ stage }) => ({ run, decision: run.retryStage(stage) })),
    };

    const runner: StageRunner = {
      canHandle: stage => stage === Stage.Plan,
      run: async (ctx: StageContext): Promise<StageRunResult> => {
        if (ctx.requestedWork?.kind !== 'task' || ctx.requestedWork.taskId !== 'proposal') {
          return { success: true, output: {}, checkResults: [] };
        }

        visit += 1;
        const sessionId = visit === 1 ? 'attempt-1-session' : 'attempt-2-session';
        const sharedSession = await ctx.agentSessionRegistry?.getOrCreate('plan-artifacts', async () => {
          factoryCalls.push(sessionId);
          return {
            execute: vi.fn().mockResolvedValue({ success: true, acpSessionId: sessionId }),
            close: vi.fn().mockResolvedValue(undefined),
          } as any;
        });

        sessionIds.push((await sharedSession?.execute({} as any))?.acpSessionId ?? 'missing');

        if (visit === 1) {
          service.completeTask({
            issueId: issue.id,
            stage: Stage.Plan,
            taskId: 'proposal',
            result: { status: 'failed', reason: 'retry this stage' },
          });
          service.retryStage({ issueId: issue.id, stage: Stage.Plan });
          return { success: true, output: {}, checkResults: [] };
        }

        service.completeTask({
          issueId: issue.id,
          stage: Stage.Plan,
          taskId: 'proposal',
          result: { status: 'completed' },
        });
        return { success: true, output: {}, checkResults: [] };
      },
    };

    const workflowRunService = {
      getActiveRunForIssue: vi.fn(() => run.snapshot()),
      getLatestRunForIssue: vi.fn(() => run.snapshot()),
      canRetryStage: vi.fn(() => false),
    };

    const engine = makeEngine([runner], workflowRunService, service);
    const result = await engine.run(issue, { cwd: '/tmp' });

    expect(result.completed).toBe(false);
    expect(result.stage).toBe(Stage.Plan);
    expect(result.message).toBe('Aggregate workflow made no progress while executing task:plan:specs');
    expect(run.id).toBe('run-shared-retry');
    expect(run.stageRun(Stage.Plan).attemptSequence).toBe(2);
    expect(sessionIds).toEqual(['attempt-1-session', 'attempt-2-session']);
    expect(factoryCalls).toEqual(['attempt-1-session', 'attempt-2-session']);
  });
});
