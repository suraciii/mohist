import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { DatabaseManager } from '../../src/db/database';
import { initializeDatabase } from '../../src/db/migrations';
import { IssueRepo } from '../../src/db/issue-repo';
import { ProjectRepo } from '../../src/db/project-repo';
import { WorkflowRunRepo } from '../../src/db/workflow-run-repo';
import { WorkflowRunService } from '../../src/services/workflow-run-service';
import { WorkflowApplicationService } from '../../src/services/workflow-application-service';
import { StageStateService } from '../../src/services/stage-state-service';
import { StageExecutionRepo } from '../../src/db/stage-execution-repo';
import { PipelineCheckpointRepo } from '../../src/db/pipeline-checkpoint-repo';
import { CheckpointManager } from '../../src/workflow/checkpoint-manager';
import { EventBus } from '../../src/services/event-bus';
import { GenericStageRunner } from '../../src/workflow/generic-stage-runner';
import { WorkflowEngine } from '../../src/workflow/workflow-engine';
import {
  DEFAULT_STAGE_DEFINITIONS,
  type CompiledStageDefinition,
} from '../../src/workflow/domain';
import {
  createTaskHandlerRegistry,
  createTaskLoaderRegistry,
  type DispatchableTask,
  type ExecutableTask,
  type TaskHandler,
} from '../../src/workflow/task-runtime';
import { createCheckRegistry, type CheckFactory } from '../../src/workflow/checks';
import type { CheckContext, CheckResult, StageContext, StageTaskResult } from '../../src/workflow/stage-context';
import { IssueStatus, MergeState, Stage, type Issue } from '../../src/types';

type ExternalWorldOptions = {
  reviewFailuresBeforePass?: number;
};

class WorkflowExternalWorld {
  readonly worktreePath: string;
  readonly changeDir: string;
  readonly taskCalls: string[] = [];
  readonly checkCalls: string[] = [];
  readonly agentCalls: string[] = [];
  readonly serviceCalls: string[] = [];
  codeChangeCounter = 0;
  private aiReviewAttempts = 0;

  constructor(private readonly options: ExternalWorldOptions = {}) {
    this.worktreePath = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-default-workflow-harness-'));
    this.changeDir = path.join(this.worktreePath, 'openspec', 'changes', '188-default-workflow');
    fs.mkdirSync(this.changeDir, { recursive: true });
    execFileSync('git', ['init'], { cwd: this.worktreePath, stdio: 'ignore' });
    execFileSync('git', ['config', 'user.email', 'test@example.com'], { cwd: this.worktreePath });
    execFileSync('git', ['config', 'user.name', 'Workflow Harness'], { cwd: this.worktreePath });
    fs.writeFileSync(path.join(this.worktreePath, 'README.md'), '# harness\n');
    execFileSync('git', ['add', 'README.md'], { cwd: this.worktreePath });
    execFileSync('git', ['commit', '-m', 'initial'], { cwd: this.worktreePath, stdio: 'ignore' });
  }

  cleanup(): void {
    fs.rmSync(this.worktreePath, { recursive: true, force: true });
  }

  agentTask(task: DispatchableTask): StageTaskResult {
    this.taskCalls.push(task.taskId);
    this.agentCalls.push(task.taskId);
    switch (task.taskId) {
      case 'proposal':
        this.write('proposal.md', '# Proposal\n');
        return this.completed(task, ['proposal.md']);
      case 'specs':
        this.write('specs/feature.md', '# Spec\n');
        return this.completed(task, ['specs']);
      case 'design':
        this.write('design.md', '# Design\n');
        return this.completed(task, ['design.md']);
      case 'tasks':
        this.write('tasks.json', JSON.stringify({
          tasks: [
            { id: 'T-001', order: 1, title: 'Implement feature', description: 'Implement feature', passes: false, attempts: 0 },
            { id: 'T-002', order: 2, title: 'Add regression coverage', description: 'Add tests', dependsOn: ['T-001'], passes: false, attempts: 0 },
          ],
        }, null, 2));
        return this.completed(task, ['tasks.json']);
      case 'self-review':
        this.write('self-review.md', '# Self review\n<promise>PASS</promise>\n');
        return this.completed(task, ['self-review.md']);
      case 'ai-review':
        this.aiReviewAttempts += 1;
        if (this.aiReviewAttempts <= (this.options.reviewFailuresBeforePass ?? 0)) {
          this.write('review.md', '# Review\nBlocking finding F-001\n<promise>FAIL</promise>\n');
        } else {
          this.write('review.md', '# Review\nAll clear\n<promise>PASS</promise>\n');
        }
        return this.completed(task, ['review.md']);
      case 'fix-review-findings':
        this.codeChangeCounter += 1;
        fs.writeFileSync(path.join(this.worktreePath, 'src-feature.ts'), `export const value = ${this.codeChangeCounter};\n`);
        return this.completed(task, [], {
          events: ['code.changed'],
          output: {
            attemptedItemIds: ['F-001'],
            resolvedItemIds: ['F-001'],
            unresolvedItemIds: [],
          },
        });
      default:
        return this.completed(task);
    }
  }

  ralphTask(task: DispatchableTask): StageTaskResult {
    this.taskCalls.push(task.taskId);
    const tasksPath = path.join(this.changeDir, 'tasks.json');
    const parsed = JSON.parse(fs.readFileSync(tasksPath, 'utf-8'));
    parsed.tasks = parsed.tasks.map((item: any) => item.id === task.taskId ? { ...item, passes: true, attempts: (item.attempts ?? 0) + 1 } : item);
    fs.writeFileSync(tasksPath, JSON.stringify(parsed, null, 2));
    return this.completed(task, [], { output: { kind: 'ralph-task', success: true } });
  }

  async serviceCall(task: DispatchableTask, ctx: StageContext): Promise<StageTaskResult> {
    this.taskCalls.push(task.taskId);
    this.serviceCalls.push(task.taskId);
    const input = task as DispatchableTask & { serviceFn?: (ctx: StageContext) => Promise<unknown>; attempt?: number; stage?: string };
    const result = input.serviceFn ? await input.serviceFn(ctx) : { ok: true };
    return this.completed(task, [], {
      output: {
        kind: 'service-call-task',
        stage: input.stage ?? ctx.issue.stage,
        attempt: input.attempt ?? 1,
        success: true,
        result,
      },
    });
  }

  checkFactory(checkName: string): CheckFactory {
    return () => ({
      name: checkName,
      run: async (ctx: CheckContext): Promise<CheckResult> => {
        this.checkCalls.push(checkName);
        switch (checkName) {
          case 'proposal-complete':
            return this.exists(checkName, 'proposal.md');
          case 'specs-complete':
            return this.exists(checkName, 'specs');
          case 'design-complete':
            return this.exists(checkName, 'design.md');
          case 'tasks-valid':
            return this.exists(checkName, 'tasks.json');
          case 'self-review-passed':
            return this.marker(checkName, 'self-review.md');
          case 'review-passed':
            return this.marker(checkName, 'review.md', { snapshotSha: 'candidate-head' });
          case 'merge-ready':
            return {
              name: checkName,
              status: 'pass',
              message: 'Merge ready',
              output: {
                kind: 'merge-ready',
                targetBranch: 'main',
                strategy: 'squash',
                baseSha: 'base-sha',
                candidateHeadSha: 'candidate-head',
                mergeBaseSha: 'base-sha',
                canMerge: true,
                conflictFiles: [],
                checkedAt: '2026-05-20T00:00:00.000Z',
              },
            };
          default:
            if (checkName.startsWith('health:')) {
              return {
                name: checkName,
                status: 'pass',
                message: `${checkName} passed`,
                output: { kind: 'health-gate', command: 'fake', candidateHeadSha: 'candidate-head' },
              };
            }
            return { name: checkName, status: 'error', message: `Unknown fake check ${checkName}` };
        }
      },
    });
  }

  artifactManager() {
    return {
      getChangeDir: vi.fn().mockImplementation((_issueNumber: number) => this.changeDir),
      createChangeDir: vi.fn().mockImplementation((_issueNumber: number, _title: string) => {
        fs.mkdirSync(this.changeDir, { recursive: true });
        return this.changeDir;
      }),
      readArtifact: vi.fn().mockImplementation((_changeDir: string, artifactPath: string) => {
        const filePath = path.join(this.changeDir, artifactPath);
        return fs.existsSync(filePath) ? fs.readFileSync(filePath, 'utf-8') : null;
      }),
      writeArtifact: vi.fn().mockImplementation((_changeDir: string, artifactPath: string, content: string) => {
        this.write(artifactPath, content);
        return true;
      }),
      exists: vi.fn().mockImplementation((changeDir: string) => fs.existsSync(changeDir)),
      readTasks: vi.fn().mockImplementation(() => JSON.parse(fs.readFileSync(path.join(this.changeDir, 'tasks.json'), 'utf-8'))),
      updateTaskPasses: vi.fn().mockImplementation((_issueNumber: number, taskId: string, passes: boolean, error?: string | null) => {
        const tasksPath = path.join(this.changeDir, 'tasks.json');
        const parsed = JSON.parse(fs.readFileSync(tasksPath, 'utf-8'));
        parsed.tasks = parsed.tasks.map((item: any) => item.id === taskId ? { ...item, passes, error: error ?? null } : item);
        fs.writeFileSync(tasksPath, JSON.stringify(parsed, null, 2));
        return true;
      }),
      syncTasksToStageState: vi.fn(),
      archiveChange: vi.fn().mockResolvedValue(undefined),
    };
  }

  worktreeManager() {
    return {
      getPath: vi.fn().mockReturnValue(this.worktreePath),
      exists: vi.fn().mockReturnValue(true),
      getHeadSha: vi.fn().mockResolvedValue('candidate-head'),
      isWorktreeClean: vi.fn().mockResolvedValue(true),
      getWorktreeChangeSignature: vi.fn().mockResolvedValue('clean'),
      canFastForward: vi.fn().mockResolvedValue(true),
      rebaseOntoMaster: vi.fn().mockResolvedValue({ success: true, conflicts: [] }),
      checkSquashMergeability: vi.fn().mockResolvedValue({
        kind: 'merge-ready',
        strategy: 'squash',
        targetBranch: 'main',
        baseSha: 'base-sha',
        candidateHeadSha: 'candidate-head',
        mergeBaseSha: 'base-sha',
        canMerge: true,
        conflictFiles: [],
        checkedAt: '2026-05-20T00:00:00.000Z',
      }),
      mergeApprovedCandidate: vi.fn().mockResolvedValue({
        targetBranch: 'main',
        baseSha: 'base-sha',
        candidateHeadSha: 'candidate-head',
        landedSha: 'landed-sha',
        rebased: false,
      }),
      remove: vi.fn(),
      findWipCommit: vi.fn(),
      createWipCommit: vi.fn(),
      abortRebase: vi.fn(),
      isRebaseInProgress: vi.fn(),
      mergeBack: vi.fn(),
      create: vi.fn(),
      list: vi.fn(),
      getWorktreeStatus: vi.fn(),
      prune: vi.fn(),
      createCheckConvergenceCommit: vi.fn(),
    };
  }

  private write(relativePath: string, content: string): void {
    const target = path.join(this.changeDir, relativePath);
    fs.mkdirSync(path.dirname(target), { recursive: true });
    fs.writeFileSync(target, content);
  }

  private exists(name: string, relativePath: string): CheckResult {
    const target = path.join(this.changeDir, relativePath);
    return fs.existsSync(target)
      ? { name, status: 'pass', output: { kind: 'artifact-exists', path: target } }
      : { name, status: 'fail', message: `${relativePath} missing` };
  }

  private marker(name: string, relativePath: string, extraOutput: Record<string, unknown> = {}): CheckResult {
    const target = path.join(this.changeDir, relativePath);
    const content = fs.existsSync(target) ? fs.readFileSync(target, 'utf-8') : '';
    const pass = content.includes('<promise>PASS</promise>');
    return {
      name,
      status: pass ? 'pass' : 'fail',
      message: pass ? `${relativePath} passed` : `${relativePath} failed`,
      output: {
        kind: 'artifact-marker',
        path: target,
        marker: pass ? 'PASS' : 'FAIL',
        verdict: pass ? 'PASS' : 'FAIL',
        reviewReport: content,
        ...extraOutput,
      },
    };
  }

  private completed(
    task: Pick<DispatchableTask, 'taskId' | 'title'>,
    artifacts: string[] = [],
    overrides: Partial<StageTaskResult> = {},
  ): StageTaskResult {
    return {
      taskId: task.taskId,
      title: task.title,
      status: 'completed',
      artifacts,
      attempts: 1,
      duration: 1,
      ...overrides,
    };
  }
}

class DefaultWorkflowHarness {
  readonly db = new DatabaseManager({ inMemory: true });
  readonly issueRepo: IssueRepo;
  readonly projectRepo: ProjectRepo;
  readonly workflowRunRepo: WorkflowRunRepo;
  readonly workflowRunService: WorkflowRunService;
  readonly workflowApplicationService: WorkflowApplicationService;
  readonly issue: Issue;
  readonly world: WorkflowExternalWorld;
  readonly engine: WorkflowEngine;

  constructor(options: ExternalWorldOptions = {}) {
    initializeDatabase(this.db);
    this.issueRepo = new IssueRepo(this.db);
    this.projectRepo = new ProjectRepo(this.db);
    this.workflowRunRepo = new WorkflowRunRepo(this.db);
    this.workflowRunService = new WorkflowRunService(this.db);
    this.workflowApplicationService = new WorkflowApplicationService(this.db);
    this.world = new WorkflowExternalWorld(options);
    const project = this.projectRepo.create({ name: 'default-workflow-harness', path: this.world.worktreePath, baseBranch: 'main' });
    this.issue = this.issueRepo.create({ number: 188, projectId: project.id, title: 'Default workflow harness' });

    const taskLoaderRegistry = createTaskLoaderRegistry([
      {
        kind: 'static',
        load: (ctx: StageContext): ExecutableTask[] => {
          const definition = stageDefinition(ctx.issue.stage);
          return (definition?.tasks ?? []).map(task => ({ taskId: task.id, title: task.title, kind: 'agent-session' as const }));
        },
      },
      {
        kind: 'ralph',
        load: (): ExecutableTask[] => {
          const parsed = JSON.parse(fs.readFileSync(path.join(this.world.changeDir, 'tasks.json'), 'utf-8'));
          return parsed.tasks.map((task: any) => ({ taskId: task.id, title: task.title, kind: 'ralph-task' as const, input: task.id }));
        },
      },
      { kind: 'runtime', load: () => [] },
    ]);

    const taskHandlers: Partial<Record<'agent-session' | 'ralph-task' | 'service-call', TaskHandler>> = {
      'agent-session': async (task) => this.world.agentTask(task as DispatchableTask),
      'ralph-task': async (task) => this.world.ralphTask(task as DispatchableTask),
      'service-call': async (task, ctx) => this.world.serviceCall(task as DispatchableTask, ctx),
    };

    const checkRegistry = createCheckRegistry(Object.fromEntries(
      ['proposal-complete', 'specs-complete', 'design-complete', 'tasks-valid', 'self-review-passed', 'health:plan', 'health:build', 'health:check', 'review-passed', 'merge-ready', 'health:integrate']
        .map(name => [name, this.world.checkFactory(name)]),
    ));

    const runner = new GenericStageRunner({
      taskLoaderRegistry,
      taskHandlerRegistry: createTaskHandlerRegistry(taskHandlers),
      checkRegistry,
      getStageDefinition: stageDefinition,
      worktreePath: this.world.worktreePath,
    });

    const eventBus = new EventBus();
    this.engine = new WorkflowEngine({
      runners: [runner],
      issueRepo: this.issueRepo,
      eventBus,
      checkpointManager: new CheckpointManager(new PipelineCheckpointRepo(this.db)),
      artifactManager: this.world.artifactManager() as any,
      worktreeManager: this.world.worktreeManager() as any,
      projectRepo: this.projectRepo,
      stageExecutionRepo: new StageExecutionRepo(this.db),
      stageStateService: new StageStateService(this.db),
      workflowRunService: this.workflowRunService,
      workflowApplicationService: this.workflowApplicationService,
    });
  }

  cleanup(): void {
    this.db.close();
    this.world.cleanup();
  }

  async runUntilBoundary(): Promise<Awaited<ReturnType<WorkflowEngine['run']>>> {
    const issue = this.issueRepo.findById(this.issue.id)!;
    return this.engine.run(issue, { cwd: this.world.worktreePath } as any);
  }

  approve(stage: Stage): void {
    this.workflowApplicationService.approveStage({
      issueId: this.issue.id,
      stage,
      approval: { output: { approved: true, by: 'test' } },
    });
  }
}

function stageDefinition(stage: Stage): CompiledStageDefinition | undefined {
  return DEFAULT_STAGE_DEFINITIONS.find(definition => definition.stage === stage);
}

describe('default workflow external-system harness', () => {
  let harnesses: DefaultWorkflowHarness[] = [];

  afterEach(() => {
    for (const harness of harnesses) harness.cleanup();
    harnesses = [];
  });

  function createHarness(options: ExternalWorldOptions = {}): DefaultWorkflowHarness {
    const harness = new DefaultWorkflowHarness(options);
    harnesses.push(harness);
    return harness;
  }

  it('runs the default workflow from Plan to Done through fake agent, checks, build, and integrate systems', async () => {
    const harness = createHarness();

    const planBoundary = await harness.runUntilBoundary();
    expect(planBoundary).toMatchObject({ completed: false, stage: Stage.Plan, message: 'Awaiting plan approval' });
    expect(harness.world.agentCalls).toEqual(['proposal', 'specs', 'design', 'tasks', 'self-review']);
    expect(harness.workflowRunService.getLatestRunForIssue(harness.issue.id)?.stageRuns.find(stageRun => stageRun.stage === Stage.Plan)?.approvalStatus).toBe('awaiting');

    harness.approve(Stage.Plan);
    const checkBoundary = await harness.runUntilBoundary();
    expect(checkBoundary).toMatchObject({ completed: false, stage: Stage.Check, message: 'Awaiting check approval' });
    expect(harness.world.taskCalls).toEqual(expect.arrayContaining(['T-001', 'T-002', 'ai-review']));
    expect(harness.world.checkCalls).toEqual(expect.arrayContaining(['health:build', 'health:check', 'review-passed', 'merge-ready']));

    harness.approve(Stage.Check);
    const done = await harness.runUntilBoundary();
    expect(done).toEqual({ completed: true, stage: Stage.Done, message: 'Pipeline completed' });

    const latest = harness.workflowRunService.getLatestRunForIssue(harness.issue.id)!;
    expect(latest.status).toBe('passed');
    expect(latest.stageRuns.map(stageRun => [stageRun.stage, stageRun.status])).toEqual([
      [Stage.Plan, 'passed'],
      [Stage.Build, 'passed'],
      [Stage.Check, 'passed'],
      [Stage.Integrate, 'passed'],
    ]);
    expect(harness.issueRepo.findById(harness.issue.id)).toMatchObject({
      stage: Stage.Done,
      status: IssueStatus.Completed,
      mergeState: MergeState.Merged,
    });
    expect(harness.world.serviceCalls).toEqual([
      'integrate:spec-sync',
      'integrate:archive-change',
      'integrate:merge',
    ]);
  });

  it('exercises review failure, auto-fix, code.changed reset, and re-review before Check approval', async () => {
    const harness = createHarness({ reviewFailuresBeforePass: 1 });

    const planBoundary = await harness.runUntilBoundary();
    expect(planBoundary.stage).toBe(Stage.Plan);
    harness.approve(Stage.Plan);

    const checkBoundary = await harness.runUntilBoundary();
    expect(checkBoundary).toMatchObject({ completed: false, stage: Stage.Check, message: 'Awaiting check approval' });
    expect(harness.world.agentCalls.filter(taskId => taskId === 'ai-review')).toHaveLength(2);
    expect(harness.world.agentCalls).toContain('fix-review-findings');

    const checkRun = harness.workflowRunService.getLatestRunForIssue(harness.issue.id)!.stageRuns.find(stageRun => stageRun.stage === Stage.Check)!;
    expect(checkRun.tasks.filter(task => task.taskId === 'ai-review')).toHaveLength(1);
    expect(checkRun.tasks.find(task => task.taskId === 'fix-review-findings')).toMatchObject({
      status: 'completed',
      events: ['code.changed'],
    });
    expect(checkRun.checks.find(check => check.checkName === 'review-passed')).toMatchObject({
      status: 'passed',
    });

    harness.approve(Stage.Check);
    const done = await harness.runUntilBoundary();
    expect(done.completed).toBe(true);
    expect(harness.issueRepo.findById(harness.issue.id)).toMatchObject({
      stage: Stage.Done,
      status: IssueStatus.Completed,
    });
  });
});
