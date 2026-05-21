import { vi } from 'vitest';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { DatabaseManager } from '../../../../src/db/database';
import { initializeDatabase } from '../../../../src/db/migrations';
import { IssueRepo } from '../../../../src/db/issue-repo';
import { ProjectRepo } from '../../../../src/db/project-repo';
import { WorkflowRunRepo } from '../../../../src/db/workflow-run-repo';
import { WorkflowRunService } from '../../../../src/services/workflow-run-service';
import { WorkflowApplicationService } from '../../../../src/services/workflow-application-service';
import { StageStateService } from '../../../../src/services/stage-state-service';
import { StageExecutionRepo } from '../../../../src/db/stage-execution-repo';
import { PipelineCheckpointRepo } from '../../../../src/db/pipeline-checkpoint-repo';
import { createDefaultCheckRegistry } from '../../../../src/services/agent-runner-service';
import { CheckpointManager } from '../../../../src/workflow/checkpoint-manager';
import { EventBus } from '../../../../src/services/event-bus';
import { GenericStageRunner } from '../../../../src/workflow/generic-stage-runner';
import { WorkflowEngine } from '../../../../src/workflow/workflow-engine';
import {
  createDefaultWorkflowDefinitionSnapshot,
  DEFAULT_STAGE_DEFINITIONS,
} from '../../../../src/workflow/builtins/workflows/mohist-default';
import {
  type CompiledStageDefinition,
} from '../../../../src/workflow/model';
import {
  createDefaultTaskDispatchFactoryRegistry,
  defaultServiceCallTaskHandler,
  createDefaultStaticTaskLoader,
  createTaskLoaderRegistry,
  type ExecutableTask,
  type AgentSessionTaskInput,
  type ServiceCallTaskInput,
} from '../../../../src/workflow/tasks';
import type { CheckContext, CheckResult, StageContext, StageTaskResult } from '../../../../src/workflow/stage-context';
import { MergeState, Stage, type Issue } from '../../../../src/types';

export type DefaultWorkflowScenario = {
  reviewFailuresBeforePass?: number;
  healthFailuresBeforePass?: Partial<Record<string, number>>;
  markerFailuresBeforePass?: Partial<Record<string, number>>;
  omitArtifacts?: string[];
  failAgentTasks?: Partial<Record<string, string>>;
  failServices?: Partial<Record<string, string>>;
  mergeReadyFailuresBeforePass?: number;
  mergeReadinessRepairRaisesCodeChanged?: boolean;
};

type HarnessTask = Pick<ExecutableTask, 'taskId' | 'title'> & Partial<AgentSessionTaskInput> & {
  serviceFn?: (ctx: StageContext) => Promise<unknown>;
};

export class DefaultWorkflowExternalWorld {
  readonly worktreePath: string;
  readonly changeDir: string;
  readonly taskCalls: string[] = [];
  readonly checkCalls: string[] = [];
  readonly agentCalls: string[] = [];
  readonly serviceCalls: string[] = [];
  private codeChangeCounter = 0;
  private aiReviewAttempts = 0;
  private selfReviewAttempts = 0;
  private simulatedHeadSha = 'candidate-head';
  private simulatedHeadChangeCounter = 0;
  private readonly checkAttempts = new Map<string, number>();
  private readonly omittedArtifacts: Set<string>;

  constructor(private readonly scenario: DefaultWorkflowScenario = {}) {
    this.omittedArtifacts = new Set(scenario.omitArtifacts ?? []);
    this.worktreePath = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-default-workflow-harness-'));
    this.changeDir = path.join(this.worktreePath, 'openspec', 'changes', '188-default-workflow');
    fs.mkdirSync(this.changeDir, { recursive: true });
    execFileSync('git', ['init'], { cwd: this.worktreePath, stdio: 'ignore' });
    execFileSync('git', ['config', 'user.email', 'test@example.com'], { cwd: this.worktreePath });
    execFileSync('git', ['config', 'user.name', 'Workflow Harness'], { cwd: this.worktreePath });
    fs.writeFileSync(path.join(this.worktreePath, 'README.md'), '# harness\n');
    execFileSync('git', ['add', 'README.md'], { cwd: this.worktreePath });
    execFileSync('git', ['commit', '-m', 'initial'], { cwd: this.worktreePath, stdio: 'ignore' });
    execFileSync('git', ['branch', '-M', 'main'], { cwd: this.worktreePath });
  }

  cleanup(): void {
    fs.rmSync(this.worktreePath, { recursive: true, force: true });
  }

  agentTask(task: HarnessTask): StageTaskResult {
    this.taskCalls.push(task.taskId);
    const baseTaskId = baseRuntimeTaskId(task.taskId);
    this.agentCalls.push(baseTaskId);
    const configuredFailure = this.scenario.failAgentTasks?.[task.taskId] ?? this.scenario.failAgentTasks?.[baseTaskId];
    if (configuredFailure) return this.failed(task, configuredFailure);

    switch (baseTaskId) {
      case 'proposal':
        return this.writeArtifactTask(task, 'proposal.md', '# Proposal\n');
      case 'specs':
        return this.writeArtifactTask(task, 'specs', '# Spec\n', 'specs/feature.md');
      case 'design':
        return this.writeArtifactTask(task, 'design.md', '# Design\n');
      case 'tasks':
        return this.writeTasksArtifact(task);
      case 'self-review':
        return this.writeSelfReviewArtifact(task);
      case 'ai-review':
        return this.writeAiReviewArtifact(task);
      case 'fix-plan-review':
        return this.fixPlanReview(task);
      case 'fix-review-findings':
        return this.fixReviewFindings(task);
      case 'fix-build-health':
      case 'fix-check-health':
      case 'fix-integrate-health':
        return this.fixHealth(task, baseTaskId);
      case 'fix-merge-readiness':
        return this.failed(task, 'fix-merge-readiness must run through mohist/rebase service-call');
      default:
        if (/^T-\d+$/.test(baseTaskId)) return this.implementOpenSpecTask(task);
        return this.failed(task, `Unexpected agent task routed to fake external agent: ${task.taskId}`);
    }
  }

  async serviceCall(task: HarnessTask, ctx: StageContext): Promise<StageTaskResult> {
    this.taskCalls.push(task.taskId);
    this.serviceCalls.push(task.taskId);
    const input = task as HarnessTask & { serviceFn?: (ctx: StageContext) => Promise<unknown>; attempt?: number; stage?: string };
    const configuredFailure = this.scenario.failServices?.[task.taskId];
    const serviceFn = configuredFailure
      ? async () => { throw new Error(configuredFailure); }
      : input.serviceFn ?? (async () => ({ ok: true }));

    return defaultServiceCallTaskHandler({
      taskId: input.taskId,
      title: input.title,
      serviceFn,
      stage: input.stage ?? ctx.issue.stage,
      attempt: input.attempt ?? 1,
    } satisfies ServiceCallTaskInput, ctx);
  }

  fakeHealthCheck(checkName: string) {
    return {
      name: checkName,
      run: async (): Promise<CheckResult> => {
        this.checkCalls.push(checkName);
        const attempt = this.nextCheckAttempt(checkName);
        const failuresBeforePass = this.scenario.healthFailuresBeforePass?.[checkName] ?? 0;
        if (attempt <= failuresBeforePass) {
          return {
            name: checkName,
            status: 'fail',
            message: `${checkName} failed`,
            output: { kind: 'health-gate', command: 'fake', candidateHeadSha: 'candidate-head', attempt },
          };
        }
        return {
          name: checkName,
          status: 'pass',
          message: `${checkName} passed`,
          output: { kind: 'health-gate', command: 'fake', candidateHeadSha: 'candidate-head' },
        };
      },
    };
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
      getHeadSha: vi.fn().mockImplementation(() => Promise.resolve(this.simulatedHeadSha)),
      isWorktreeClean: vi.fn().mockResolvedValue(true),
      getWorktreeChangeSignature: vi.fn().mockResolvedValue('clean'),
      canFastForward: vi.fn().mockImplementation(() => Promise.resolve(!this.scenario.mergeReadinessRepairRaisesCodeChanged)),
      rebaseOntoMaster: vi.fn().mockImplementation(() => {
        if (this.scenario.mergeReadinessRepairRaisesCodeChanged) {
          this.simulatedHeadChangeCounter += 1;
          this.simulatedHeadSha = `candidate-head-rebased-${this.simulatedHeadChangeCounter}`;
        }
        return Promise.resolve({ success: true, conflicts: [] });
      }),
      checkSquashMergeability: vi.fn().mockImplementation(() => Promise.resolve({
        kind: 'merge-ready',
        strategy: 'squash',
        targetBranch: 'main',
        baseSha: 'base-sha',
        candidateHeadSha: 'candidate-head',
        mergeBaseSha: 'base-sha',
        canMerge: !this.shouldFailCheck('merge-ready', this.scenario.mergeReadyFailuresBeforePass ?? 0),
        conflictFiles: [],
        checkedAt: '2026-05-20T00:00:00.000Z',
      })),
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

  private writeArtifactTask(task: Pick<HarnessTask, 'taskId' | 'title'>, artifact: string, content: string, filePath = artifact): StageTaskResult {
    if (this.omittedArtifacts.has(artifact)) return this.completed(task, []);
    this.write(filePath, content);
    return this.completed(task, [artifact]);
  }

  private writeTasksArtifact(task: Pick<HarnessTask, 'taskId' | 'title'>): StageTaskResult {
    if (this.omittedArtifacts.has('tasks.json')) return this.completed(task, []);
    this.write('tasks.json', JSON.stringify({ tasks: defaultBuildTasks() }, null, 2));
    return this.completed(task, ['tasks.json']);
  }

  private writeSelfReviewArtifact(task: Pick<HarnessTask, 'taskId' | 'title'>): StageTaskResult {
    if (this.omittedArtifacts.has('self-review.md')) return this.completed(task, []);
    this.selfReviewAttempts += 1;
    this.write('self-review.md', this.selfReviewAttempts <= (this.scenario.markerFailuresBeforePass?.['self-review-passed'] ?? 0)
      ? '# Self review\nPlan findings\n<promise>FAIL</promise>\n'
      : '# Self review\n<promise>PASS</promise>\n');
    return this.completed(task, ['self-review.md']);
  }

  private writeAiReviewArtifact(task: Pick<HarnessTask, 'taskId' | 'title'>): StageTaskResult {
    this.aiReviewAttempts += 1;
    const reviewFailed = this.aiReviewAttempts <= (this.scenario.reviewFailuresBeforePass ?? 0);
    if (reviewFailed) {
      this.write('review.md', [
        '# Review',
        '<promise>FAIL</promise>',
        '',
        '- [ID: F-001]',
        '  Severity: high',
        '  Evidence: Blocking finding from fake review',
        '  Status: open',
        '',
      ].join('\n'));
      return this.completed(task, ['review.md']);
    }

    this.write('review.md', this.aiReviewAttempts > 1
      ? [
          '# Review',
          '<promise>PASS</promise>',
          '',
          '- [ID: F-001]',
          '  Severity: high',
          '  Evidence: Blocking finding from fake review',
          '  Verification: npm test -- tests/workflow/builtins/workflows/mohist-default/default-workflow-harness.test.ts',
          '  Status: resolved',
          '',
        ].join('\n')
      : '# Review\nAll clear\n<promise>PASS</promise>\n');
    return this.completed(task, ['review.md']);
  }

  private fixPlanReview(task: Pick<HarnessTask, 'taskId' | 'title'>): StageTaskResult {
    this.write('proposal.md', '# Proposal fixed\n');
    this.write('specs/feature.md', '# Spec fixed\n');
    this.write('design.md', '# Design fixed\n');
    this.write('tasks.json', JSON.stringify({ tasks: defaultBuildTasks() }, null, 2));
    return this.completed(task);
  }

  private fixReviewFindings(task: Pick<HarnessTask, 'taskId' | 'title'>): StageTaskResult {
    this.codeChangeCounter += 1;
    fs.writeFileSync(path.join(this.worktreePath, 'src-feature.ts'), `export const value = ${this.codeChangeCounter};\n`);
    return this.completed(task, [], {
      events: ['code.changed'],
      output: { attemptedItemIds: ['F-001'], resolvedItemIds: ['F-001'], unresolvedItemIds: [] },
    });
  }

  private fixHealth(task: Pick<HarnessTask, 'taskId' | 'title'>, baseTaskId: string): StageTaskResult {
    this.codeChangeCounter += 1;
    fs.writeFileSync(path.join(this.worktreePath, `${baseTaskId}.ts`), `export const value = ${this.codeChangeCounter};\n`);
    return this.completed(task, [], { events: ['code.changed'] });
  }

  private implementOpenSpecTask(task: Pick<HarnessTask, 'taskId' | 'title'>): StageTaskResult {
    this.codeChangeCounter += 1;
    fs.writeFileSync(path.join(this.worktreePath, `${task.taskId}.ts`), `export const value = ${this.codeChangeCounter};\n`);
    return this.completed(task, [], { events: ['code.changed'], output: { implementedTaskId: task.taskId } });
  }

  private write(relativePath: string, content: string): void {
    const target = path.join(this.changeDir, relativePath);
    fs.mkdirSync(path.dirname(target), { recursive: true });
    fs.writeFileSync(target, content);
  }

  private shouldFailCheck(checkName: string, failuresBeforePass: number): boolean {
    return this.nextCheckAttempt(checkName) <= failuresBeforePass;
  }

  private nextCheckAttempt(checkName: string): number {
    const next = (this.checkAttempts.get(checkName) ?? 0) + 1;
    this.checkAttempts.set(checkName, next);
    return next;
  }

  private failed(
    task: Pick<HarnessTask, 'taskId' | 'title'>,
    reason: string,
    overrides: Partial<StageTaskResult> = {},
  ): StageTaskResult {
    return { taskId: task.taskId, title: task.title, status: 'failed', attempts: 1, duration: 1, reason, ...overrides };
  }

  private completed(
    task: Pick<HarnessTask, 'taskId' | 'title'>,
    artifacts: string[] = [],
    overrides: Partial<StageTaskResult> = {},
  ): StageTaskResult {
    return { taskId: task.taskId, title: task.title, status: 'completed', artifacts, attempts: 1, duration: 1, ...overrides };
  }
}

export class DefaultWorkflowHarness {
  readonly db = new DatabaseManager({ inMemory: true });
  readonly issueRepo: IssueRepo;
  readonly projectRepo: ProjectRepo;
  readonly workflowRunRepo: WorkflowRunRepo;
  readonly workflowRunService: WorkflowRunService;
  readonly workflowApplicationService: WorkflowApplicationService;
  readonly issue: Issue;
  readonly world: DefaultWorkflowExternalWorld;
  readonly engine: WorkflowEngine;

  constructor(scenario: DefaultWorkflowScenario = {}) {
    initializeDatabase(this.db);
    this.issueRepo = new IssueRepo(this.db);
    this.projectRepo = new ProjectRepo(this.db);
    this.workflowRunRepo = new WorkflowRunRepo(this.db);
    this.workflowRunService = new WorkflowRunService(this.db);
    this.workflowApplicationService = new WorkflowApplicationService(this.db);
    this.world = new DefaultWorkflowExternalWorld(scenario);
    const project = this.projectRepo.create({ name: 'default-workflow-harness', path: this.world.worktreePath, baseBranch: 'main' });
    this.issue = this.issueRepo.create({ number: 188, projectId: project.id, title: 'Default workflow harness' });

    const checkRegistry = createDefaultCheckRegistry({
      worktreePath: this.world.worktreePath,
      workflowDefinitionSnapshot: createDefaultWorkflowDefinitionSnapshot(),
    });
    checkRegistry.register('mohist/health-gate', {
      id: 'mohist/health-gate',
      build: ({ check }) => this.world.fakeHealthCheck(check.name),
    });

    const runner = new GenericStageRunner({
      taskLoaderRegistry: createTaskLoaderRegistry([
        createDefaultStaticTaskLoader(this.world.worktreePath),
        {
          kind: 'openspec',
          load: (): ExecutableTask[] => {
            const parsed = JSON.parse(fs.readFileSync(path.join(this.world.changeDir, 'tasks.json'), 'utf-8'));
            return parsed.tasks.map((task: any) => ({
              taskId: task.id,
              title: task.title,
              uses: 'mohist/agent',
              input: {
                session: 'build',
                prompt: {
                  inline: `<task>\n  <id>${task.id}</id>\n  <title>${task.title}</title>\n  <description>${task.description ?? ''}</description>\n</task>`,
                },
              },
            }));
          },
        },
        { kind: 'runtime', load: () => [] },
      ]),
      checkRegistry,
      getStageDefinition: stageDefinition,
      worktreePath: this.world.worktreePath,
      taskDispatchFactoryRegistry: createDefaultTaskDispatchFactoryRegistry({
        agentSessionHandler: async (input: AgentSessionTaskInput) => this.world.agentTask(input),
        overrides: {
          rebase: async input => this.world.serviceCall({
            taskId: input.task.taskId,
            title: input.task.title,
            serviceFn: async () => {
              const project = input.ctx.projectRepo?.findById(input.ctx.issue.projectId);
              if (!project) throw new Error(`Project not found: ${input.ctx.issue.projectId}`);
              const beforeHeadSha = await input.ctx.worktreeManager.getHeadSha(this.world.worktreePath);
              const canFF = await input.ctx.worktreeManager.canFastForward(project.path, project.name, input.ctx.issue.number, project.baseBranch);
              if (!canFF) {
                await input.ctx.worktreeManager.rebaseOntoMaster(project.path, project.name, input.ctx.issue.number, project.baseBranch, { abortOnConflict: false });
              }
              const afterHeadSha = await input.ctx.worktreeManager.getHeadSha(this.world.worktreePath);
              return {
                rebased: !canFF,
                baseBranch: project.baseBranch,
                beforeBaseSha: 'base-sha',
                afterBaseSha: 'base-sha',
                beforeHeadSha,
                afterHeadSha,
                shaChanged: beforeHeadSha !== afterHeadSha,
                conflicts: [],
              };
            },
          }, input.ctx),
          openspecSync: async input => this.world.serviceCall({ taskId: input.task.taskId, title: input.task.title }, input.ctx),
          archiveChange: async input => this.world.serviceCall({
            taskId: input.task.taskId,
            title: input.task.title,
            serviceFn: async () => ({
              step: input.task.taskId,
              archivePath: path.relative(this.world.worktreePath, this.world.changeDir),
              success: true,
            }),
          }, input.ctx),
          merge: async input => this.world.serviceCall({
            taskId: input.task.taskId,
            title: input.task.title,
            serviceFn: async () => {
              const project = input.ctx.projectRepo?.findById(input.ctx.issue.projectId);
              if (!project) throw new Error(`Project not found: ${input.ctx.issue.projectId}`);
              const mergeTruth = await input.ctx.worktreeManager.mergeApprovedCandidate(project.path, project.name, input.ctx.issue.number, project.baseBranch);
              if ('failingStep' in mergeTruth) throw new Error(mergeTruth.error);
              input.ctx.issueRepo.setMergeState?.(input.ctx.issue.id, MergeState.Merged);
              return mergeTruth;
            },
          }, input.ctx),
        },
      }),
    });

    this.engine = new WorkflowEngine({
      runners: [runner],
      issueRepo: this.issueRepo,
      eventBus: new EventBus(),
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

function defaultBuildTasks() {
  return [
    { id: 'T-001', order: 1, title: 'Implement feature', description: 'Implement feature', passes: false, attempts: 0 },
    { id: 'T-002', order: 2, title: 'Add regression coverage', description: 'Add tests', dependsOn: ['T-001'], passes: false, attempts: 0 },
  ];
}

function baseRuntimeTaskId(taskId: string): string {
  return taskId.replace(/:\d+$/, '');
}
