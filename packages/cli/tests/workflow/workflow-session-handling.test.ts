import { describe, it, expect, vi, afterEach } from 'vitest';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import { Stage, IssueStatus } from '../../src/types';
import type { IssueRepo, ChangeArtifactsManager, WorktreeManager, ProjectRepo, CheckpointManager } from '../../src/workflow/stage-context';
import type { Check } from '../../src/workflow/checks';
import type { StageRunner } from '../../src/workflow/check-stage-runner';
import { BaseStageRunner } from '../../src/workflow/base-stage-runner';
import { WorkflowEngine } from '../../src/workflow/workflow-engine';
import { EventBus } from '../../src/services/event-bus';

class PassCheck implements Check {
  name: string;
  constructor(name: string) { this.name = name; }
  async run(): Promise<any> { return { name: this.name, status: 'pass' }; }
}

class FailCheck implements Check {
  name: string;
  runFn: () => Promise<any>;
  constructor(name: string, runFn?: () => Promise<any>) {
    this.name = name;
    this.runFn = runFn ?? (async () => ({ name: this.name, status: 'fail', message: `${this.name} failed` }));
  }
  async run(): Promise<any> { return this.runFn(); }
}

class SimpleStageRunner extends BaseStageRunner {
  private checks: Check[];
  private nextStage: Stage;
  private handledStage: Stage;
  private executeTasksFn: () => Promise<unknown>;
  executeTasksCalls = 0;

  constructor(opts: {
    checks: Check[];
    nextStage: Stage;
    stage?: Stage;
    executeTasksFn?: () => Promise<unknown>;
  }) {
    super();
    this.checks = opts.checks;
    this.nextStage = opts.nextStage;
    this.handledStage = opts.stage ?? Stage.Plan;
    this.executeTasksFn = opts.executeTasksFn ?? (async () => ({ done: true }));
  }

  canHandle(s: Stage): boolean { return s === this.handledStage; }
  protected async executeTasks(): Promise<unknown> { this.executeTasksCalls++; return this.executeTasksFn(); }
  protected getChecks(): Check[] { return this.checks; }
  protected getNextStage(): Stage { return this.nextStage; }
}

function makeIssue(overrides?: Partial<any>): any {
  return {
    id: 'issue-1',
    number: 1,
    title: 'Test Issue',
    stage: Stage.Plan,
    status: IssueStatus.Active,
    projectId: 'proj-1',
    labels: [],
    priority: 'p2',
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString(),
    ...overrides,
  };
}

function makeEngineContext(overrides?: Partial<{
  runners: StageRunner[];
  issueRepo: IssueRepo;
  eventBus: EventBus;
  checkpointManager: CheckpointManager;
  artifactManager: ChangeArtifactsManager;
  signal?: AbortSignal;
  stageExecutionRepo?: any;
}>) {
  const issueRepo: IssueRepo = {
    updateStage: vi.fn().mockImplementation((id: string, stage: Stage) => makeIssue({ stage, id })),
    setApprovalState: vi.fn(),
    clearApprovalState: vi.fn(),
    updateStatus: vi.fn(),
    findById: vi.fn().mockImplementation((id: string) => makeIssue({ id })),
  } as unknown as IssueRepo;

  return {
    runners: overrides?.runners ?? [],
    issueRepo: overrides?.issueRepo ?? issueRepo,
    eventBus: overrides?.eventBus ?? new EventBus(),
    checkpointManager: overrides?.checkpointManager ?? {
      save: vi.fn(),
      load: vi.fn(),
      deleteAll: vi.fn(),
    } as unknown as CheckpointManager,
    artifactManager: overrides?.artifactManager ?? {
      getChangeDir: vi.fn().mockReturnValue('/tmp/change'),
      createChangeDir: vi.fn(),
      readArtifact: vi.fn(),
      writeArtifact: vi.fn(),
      exists: vi.fn(),
      readTasks: vi.fn(),
      updateTaskPasses: vi.fn(),
      archiveChange: vi.fn(),
    } as unknown as ChangeArtifactsManager,
    signal: overrides?.signal,
    stageExecutionRepo: overrides?.stageExecutionRepo,
  };
}

function createMinimalChange(tempDir: string): any {
  const changeDir = path.join(tempDir, 'openspec', 'changes', '42-test');
  fs.mkdirSync(changeDir, { recursive: true });
  fs.mkdirSync(path.join(changeDir, 'session-memories'), { recursive: true });

  const tasksFile = {
    version: 1,
    tasks: [{ id: 'T-001', order: 1, title: 'Task 1', description: 'desc', passes: false, attempts: 0 }],
  };
  fs.writeFileSync(path.join(changeDir, 'tasks.json'), JSON.stringify(tasksFile));
  fs.writeFileSync(path.join(changeDir, 'proposal.md'), '# Test');
  fs.writeFileSync(path.join(changeDir, 'design.md'), '# Design');

  return {
    changePath: changeDir,
    tasksPath: path.join(changeDir, 'tasks.json'),
    sessionMemoriesPath: path.join(changeDir, 'session-memories'),
    proposalPath: path.join(changeDir, 'proposal.md'),
    designPath: path.join(changeDir, 'design.md'),
    specsPath: path.join(changeDir, 'specs'),
  };
}

describe('T-005: Workflow consumes session failure without judging liveness', () => {
  describe('REQ-WA-001: Workflow consumes session results without judging liveness', () => {
    it('RalphExecutor consumes session_failed result as task failure without workflow independently probing', async () => {
      const { setAcpSessionRunner, resetAcpSessionRunner, runRalphLoop } = await import('../../src/openspec/ralph-executor');

      setAcpSessionRunner(vi.fn().mockResolvedValue({
        success: false,
        error: 'Session liveness probe timed out',
        failureKind: 'session_failed',
        failureReason: 'probe_timeout',
      }));

      try {
        const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-session-fail-test-'));
        const change = createMinimalChange(tempDir);

        const context = {
          worktreePath: tempDir,
          projectPath: tempDir,
          issueId: 'issue-42',
        };

        const result = await runRalphLoop(change, context, { maxRetries: 0 });

        expect(result.success).toBe(false);
        expect(result.failed).toBe(1);
        expect(result.taskResults[0].status).toBe('failed');
        expect(result.taskResults[0].error).toContain('Session liveness probe timed out');

        fs.rmSync(tempDir, { recursive: true, force: true });
      } finally {
        resetAcpSessionRunner();
      }
    });

    it('RalphExecutor does not mark task as passed when session fails', async () => {
      const { setAcpSessionRunner, resetAcpSessionRunner, runRalphLoop } = await import('../../src/openspec/ralph-executor');

      setAcpSessionRunner(vi.fn().mockResolvedValue({
        success: false,
        error: 'Session liveness probe timed out',
        failureKind: 'session_failed',
        failureReason: 'probe_timeout',
      }));

      try {
        const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-session-fail-test-'));
        const change = createMinimalChange(tempDir);

        const context = {
          worktreePath: tempDir,
          projectPath: tempDir,
          issueId: 'issue-42',
        };

        await runRalphLoop(change, context, { maxRetries: 0 });

        const updated = JSON.parse(fs.readFileSync(change.tasksPath, 'utf-8'));
        expect(updated.tasks[0].passes).toBe(false);
        expect(updated.tasks[0].error).toContain('Session liveness probe timed out');

        fs.rmSync(tempDir, { recursive: true, force: true });
      } finally {
        resetAcpSessionRunner();
      }
    });

    it('RalphExecutor treats session_failed as a failureKind and categorizes it appropriately', async () => {
      const { setAcpSessionRunner, resetAcpSessionRunner, runRalphLoop, categorizeFailure } = await import('../../src/openspec/ralph-executor');

      setAcpSessionRunner(vi.fn().mockResolvedValue({
        success: false,
        error: 'Session liveness probe timed out',
        failureKind: 'session_failed',
        failureReason: 'probe_timeout',
      }));

      try {
        const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-session-cat-test-'));
        const change = createMinimalChange(tempDir);

        const context = {
          worktreePath: tempDir,
          projectPath: tempDir,
          issueId: 'issue-42',
        };

        const result = await runRalphLoop(change, context, { maxRetries: 0 });

        expect(result.success).toBe(false);
        expect(result.failed).toBe(1);

        const category = categorizeFailure('Session liveness probe timed out', { failureKind: 'session_failed' });
        expect(category).toBe('session_failed');

        fs.rmSync(tempDir, { recursive: true, force: true });
      } finally {
        resetAcpSessionRunner();
      }
    });
  });

  describe('Scenario: Session state does not mutate issue state directly', () => {
    it('session probing does not directly call issueRepo.updateStage or issueRepo.updateStatus', async () => {
      const issueRepo: IssueRepo = {
        updateStage: vi.fn().mockImplementation((id: string, stage: Stage) => makeIssue({ stage, id })),
        setApprovalState: vi.fn(),
        clearApprovalState: vi.fn(),
        updateStatus: vi.fn(),
        findById: vi.fn().mockImplementation((id: string) => makeIssue({ id })),
      } as unknown as IssueRepo;

      const updateStageCalls: Array<{ id: string; stage: Stage }> = [];
      const updateStatusCalls: Array<{ id: string; status: IssueStatus }> = [];

      vi.spyOn(issueRepo, 'updateStage').mockImplementation((id: string, stage: Stage) => {
        updateStageCalls.push({ id, stage });
        return makeIssue({ stage, id });
      });

      vi.spyOn(issueRepo, 'updateStatus').mockImplementation((id: string, status: IssueStatus) => {
        updateStatusCalls.push({ id, status });
        return makeIssue({ status, id });
      });

      const planRunner = new SimpleStageRunner({
        checks: [new PassCheck('proposal-complete')],
        nextStage: Stage.Build,
        stage: Stage.Plan,
        executeTasksFn: async () => ({ status: 'probing', lastDataAt: Date.now() }),
      });

      const ctx = makeEngineContext({
        runners: [planRunner],
        issueRepo,
      });

      const engine = new WorkflowEngine(ctx);
      const issue = makeIssue({ stage: Stage.Plan });

      await engine.run(issue, {} as any);

      const sessionRelatedStageCalls = updateStageCalls.filter(c => c.stage !== Stage.Plan && c.stage !== Stage.Build);
      const sessionRelatedStatusCalls = updateStatusCalls.filter(c => c.status !== IssueStatus.Completed);

      expect(sessionRelatedStageCalls).toHaveLength(0);
      expect(sessionRelatedStatusCalls).toHaveLength(0);
    });

    it('session failure result does not directly call issueRepo.updateStage or issueRepo.updateStatus before workflow decision', async () => {
      const updateStageCalls: Array<{ id: string; stage: Stage }> = [];
      const updateStatusCalls: Array<{ id: string; status: IssueStatus }> = [];

      const issueRepo: IssueRepo = {
        updateStage: vi.fn().mockImplementation((id: string, stage: Stage) => {
          updateStageCalls.push({ id, stage });
          return makeIssue({ stage, id });
        }),
        setApprovalState: vi.fn(),
        clearApprovalState: vi.fn(),
        updateStatus: vi.fn().mockImplementation((id: string, status: IssueStatus) => {
          updateStatusCalls.push({ id, status });
          return makeIssue({ status, id });
        }),
        findById: vi.fn().mockImplementation((id: string) => makeIssue({ id })),
      } as unknown as IssueRepo;

      const planRunner = new SimpleStageRunner({
        checks: [new PassCheck('proposal-complete')],
        nextStage: Stage.Build,
        stage: Stage.Plan,
        executeTasksFn: async () => ({
          success: false,
          failureKind: 'session_failed',
          failureReason: 'probe_timeout',
        }),
      });

      const ctx = makeEngineContext({
        runners: [planRunner],
        issueRepo,
      });

      const engine = new WorkflowEngine(ctx);
      const issue = makeIssue({ stage: Stage.Plan });

      await engine.run(issue, {} as any);

      const sessionRelatedStageCalls = updateStageCalls.filter(c =>
        c.stage !== Stage.Plan && c.stage !== Stage.Build && c.stage !== Stage.Integrate && c.stage !== Stage.Check && c.stage !== Stage.Done
      );
      const sessionRelatedStatusCalls = updateStatusCalls.filter(c => c.status !== IssueStatus.Completed && c.status !== IssueStatus.Active);

      expect(sessionRelatedStageCalls).toHaveLength(0);
      expect(sessionRelatedStatusCalls).toHaveLength(0);
    });

    it('failed check due to session failure does not trigger direct issue state change from session layer', async () => {
      const issueRepo: IssueRepo = {
        updateStage: vi.fn().mockImplementation((id: string, stage: Stage) => makeIssue({ stage, id })),
        setApprovalState: vi.fn(),
        clearApprovalState: vi.fn(),
        updateStatus: vi.fn(),
        blockIssue: vi.fn(),
        findById: vi.fn().mockImplementation((id: string) => makeIssue({ id })),
      } as unknown as IssueRepo;

      let sessionFailureInTask = false;
      const planRunner = new SimpleStageRunner({
        checks: [new FailCheck('proposal-complete', async () => {
          if (sessionFailureInTask) {
            return {
              name: 'proposal-complete',
              status: 'fail',
              message: 'Task failed due to session failure: Session liveness probe timed out',
            };
          }
          return { name: 'proposal-complete', status: 'fail', message: 'Check failed' };
        })],
        nextStage: Stage.Build,
        stage: Stage.Plan,
        executeTasksFn: async () => {
          sessionFailureInTask = true;
          return {
            success: false,
            failureKind: 'session_failed',
            failureReason: 'probe_timeout',
          };
        },
      });

      const ctx = makeEngineContext({
        runners: [planRunner],
        issueRepo,
      });

      const engine = new WorkflowEngine(ctx);
      const issue = makeIssue({ stage: Stage.Plan });

      const result = await engine.run(issue, {} as any);

      expect(result.completed).toBe(false);
      expect(result.message).toContain('Task failed due to session failure');
      expect(issueRepo.blockIssue).not.toHaveBeenCalled();
    });
  });

  describe('Workflow policy handles session failure through existing task retry/block policy', () => {
    it('session_failed task is retried with retryable category config', async () => {
      let callCount = 0;
      const { setAcpSessionRunner, resetAcpSessionRunner, runRalphLoop } = await import('../../src/openspec/ralph-executor');

      setAcpSessionRunner(vi.fn().mockImplementation(() => {
        callCount++;
        if (callCount === 1) {
          return Promise.resolve({
            success: false,
            error: 'Session liveness probe timed out',
            failureKind: 'session_failed',
            failureReason: 'probe_timeout',
          });
        }
        return Promise.resolve({ success: true, text: 'done' });
      }));

      try {
        const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-session-retry-test-'));
        const change = createMinimalChange(tempDir);

        const context = {
          worktreePath: tempDir,
          projectPath: tempDir,
          issueId: 'issue-42',
        };

        const result = await runRalphLoop(change, context, { maxRetries: 2 });

        expect(result.success).toBe(true);
        expect(result.completed).toBe(1);
        expect(callCount).toBe(2);

        fs.rmSync(tempDir, { recursive: true, force: true });
      } finally {
        resetAcpSessionRunner();
      }
    });
  });
});
