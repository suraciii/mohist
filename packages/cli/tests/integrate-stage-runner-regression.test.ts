import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import { Stage, IssueStatus } from '../src/types';
import type { StageContext } from '../src/workflow/stage-context';
import { EventBus } from '../src/services/event-bus';
import type { ChangeArtifactsManager } from '../src/workflow/stage-context';

function createMockContext(
  tmpDir: string,
  issueNumber = 42,
  overrides?: Partial<StageContext>
): StageContext {
  const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issueNumber}-test-change`);
  fs.mkdirSync(changeDir, { recursive: true });

  const emitSpy = vi.fn();
  const eventBus = new EventBus();
  vi.spyOn(eventBus, 'emit').mockImplementation(emitSpy);

  const worktreeManager = {
    checkSquashMergeability: vi.fn().mockResolvedValue({
      kind: 'squash-mergeability',
      strategy: 'squash',
      targetBranch: 'main',
      baseSha: 'abc123',
      candidateHeadSha: 'def456',
      mergeBaseSha: 'base456',
      canMerge: true,
      conflictFiles: [],
      checkedAt: new Date().toISOString(),
    }),
    mergeApprovedCandidate: vi.fn().mockResolvedValue({
      targetBranch: 'main',
      baseSha: 'abc123',
      candidateHeadSha: 'def456',
      landedSha: 'ghi789',
    }),
    ...(overrides?.worktreeManager as object | undefined),
  };

  const ctx = {
    issue: {
      id: `issue-${issueNumber}`,
      number: issueNumber,
      title: 'Test Issue',
      body: '',
      stage: Stage.Integrate,
      status: IssueStatus.Active,
      projectId: 'test-project',
      labels: [],
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
    },
    acpOptions: { worktreePath: tmpDir } as any,
    artifactManager: {
      getChangeDir: vi.fn().mockReturnValue(changeDir),
      createChangeDir: vi.fn(),
      readArtifact: vi.fn().mockReturnValue(null),
      writeArtifact: vi.fn().mockReturnValue(true),
      exists: vi.fn().mockReturnValue(true),
      readTasks: vi.fn(),
      updateTaskPasses: vi.fn(),
      archiveChange: vi.fn().mockResolvedValue(undefined),
    } as unknown as ChangeArtifactsManager,
    worktreeManager: worktreeManager as any,
    projectRepo: {
      findById: vi.fn().mockReturnValue({ id: 'test-project', name: 'test-project', baseBranch: 'main', path: tmpDir }),
    } as any,
    eventBus: eventBus as any,
    checkpointManager: { save: vi.fn(), load: vi.fn(), deleteAll: vi.fn() } as any,
    issueRepo: {
      updateStage: vi.fn(),
      setApprovalState: vi.fn(),
      clearApprovalState: vi.fn(),
      updateStatus: vi.fn(),
    } as any,
    stageExecutionRepo: {
      create: vi.fn().mockReturnValue({ id: 'exec-1', issueId: `issue-${issueNumber}`, stage: Stage.Integrate, status: 'running', taskResults: [], checkResults: [], createdAt: new Date().toISOString(), updatedAt: new Date().toISOString() }),
      appendTaskResult: vi.fn(),
      updateStatus: vi.fn(),
      updateCheckResults: vi.fn(),
      updateTaskResults: vi.fn(),
      findByIssueId: vi.fn().mockReturnValue([
        { id: 'exec-1', issueId: `issue-${issueNumber}`, stage: Stage.Integrate, status: 'passed', taskResults: [], checkResults: [], createdAt: new Date().toISOString(), updatedAt: new Date().toISOString() },
      ]),
    } as any,
    ...overrides,
    worktreeManager: worktreeManager as any,
  } as StageContext;
  ctx.emit = ctx.emit ?? ((event: string, data: unknown) => {
    try {
      (ctx.eventBus as any)?.emit?.(event, data);
    } catch {
      // fire-and-forget
    }
  });
  ctx.log = ctx.log ?? (() => {
    // fire-and-forget
  });
  return ctx;
}

function createMainSpec(tmpDir: string, capability: string, requirements: string[]) {
  const specDir = path.join(tmpDir, 'openspec', 'specs', capability);
  fs.mkdirSync(specDir, { recursive: true });
  let content = '# OpenSpec Capability: ' + capability + '\n\n';
  for (const req of requirements) {
    content += req + '\n\n';
  }
  fs.writeFileSync(path.join(specDir, 'spec.md'), content, 'utf-8');
}

function createChangeSpec(changeDir: string, capability: string, content: string) {
  const specsDir = path.join(changeDir, 'specs');
  fs.mkdirSync(specsDir, { recursive: true });
  const capabilityDir = path.join(specsDir, capability);
  fs.mkdirSync(capabilityDir, { recursive: true });
  fs.writeFileSync(path.join(capabilityDir, 'spec.md'), content, 'utf-8');
}

function appendedTaskResults(ctx: StageContext) {
  return (ctx.stageExecutionRepo.appendTaskResult as ReturnType<typeof vi.fn>).mock.calls
    .map((call: unknown[]) => call[1] as { taskId: string; status: string });
}

function appendedCheckResults(ctx: StageContext) {
  return (ctx.stageExecutionRepo.updateCheckResults as ReturnType<typeof vi.fn>).mock.calls
    .flatMap((call: unknown[]) => call[1] as Array<{ name: string; status: string }>);
}

describe('T-004: Integrate standardization regression coverage', () => {
  let tmpDir: string;

  beforeEach(() => {
    vi.resetModules();
    const execFileMock = vi.fn().mockImplementation((cmd: any, args: any, opts: any, cb: any) => {
      const err = new Error('ENOENT');
      (err as any).code = 'ENOENT';
      process.nextTick(() => {
        if (typeof opts === 'function') {
          opts(err, { stdout: '', stderr: '' });
        } else if (typeof cb === 'function') {
          cb(err, { stdout: '', stderr: '' });
        }
      });
      return {} as any;
    });
    vi.doMock('child_process', async () => ({
      ...await vi.importActual<typeof import('child_process')>('child_process'),
      execFile: execFileMock,
    }));
    tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-integrate-t004-'));
    fs.writeFileSync(
      path.join(tmpDir, 'package.json'),
      JSON.stringify({
        scripts: {
          build: 'node -e "process.exit(0)"',
          test: 'node -e "process.exit(0)"',
        },
      }),
      'utf-8',
    );
    const workflowYaml = path.join(tmpDir, 'workflow.yaml');
    fs.writeFileSync(workflowYaml, `
stages:
  - stage: plan
  - stage: build
  - stage: check
  - stage: integrate
  - stage: done
healthGates:
  postMerge:
    enabled: false
    command: npm run build
    timeout: 300000
    autoFix: false
    maxFixAttempts: 0
    fallbackReaction:
      type: ask-user
`);
  });

  afterEach(() => {
    fs.rmSync(tmpDir, { recursive: true, force: true });
  });

  describe('AC-1: Integrate tasks and health:integrate are seeded into WorkflowRun', () => {
    it('WorkflowRunService seeds integrate:spec-sync, integrate:archive-change, integrate:merge tasks and health:integrate check', async () => {
      const { DatabaseManager } = await import('../src/db/database');
      const { initializeDatabase } = await import('../src/db/migrations');
      const { ProjectRepo } = await import('../src/db/project-repo');
      const { IssueRepo } = await import('../src/db/issue-repo');
      const { WorkflowRunService } = await import('../src/services/workflow-run-service');

      const db = new DatabaseManager({ inMemory: true });
      initializeDatabase(db);

      const projectRepo = new ProjectRepo(db);
      const project = projectRepo.create({ name: 'Test', path: tmpDir });

      const issueRepo = new IssueRepo(db);
      const issue = issueRepo.create({ number: 1, projectId: project.id, title: 'Test' });

      const workflowRunService = new WorkflowRunService(db);
      const run = workflowRunService.startRun(issue.id, issue.number);

      const integrateStageRun = run.stageRuns.find(sr => sr.stage === Stage.Integrate)!;
      expect(integrateStageRun).toBeDefined();

      const taskIds = integrateStageRun.tasks.map(t => t.taskId);
      expect(taskIds).toContain('integrate:spec-sync');
      expect(taskIds).toContain('integrate:archive-change');
      expect(taskIds).toContain('integrate:merge');

      const checkNames = integrateStageRun.checks.map(c => c.checkName);
      expect(checkNames).toContain('health:integrate');

      const specSyncTask = integrateStageRun.tasks.find(t => t.taskId === 'integrate:spec-sync')!;
      expect(specSyncTask.status).toBe('pending');
      expect(specSyncTask.taskOrder).toBe(0);

      const archiveTask = integrateStageRun.tasks.find(t => t.taskId === 'integrate:archive-change')!;
      expect(archiveTask.status).toBe('pending');
      expect(archiveTask.taskOrder).toBe(1);

      const mergeTask = integrateStageRun.tasks.find(t => t.taskId === 'integrate:merge')!;
      expect(mergeTask.status).toBe('pending');
      expect(mergeTask.taskOrder).toBe(2);

      const healthCheck = integrateStageRun.checks.find(c => c.checkName === 'health:integrate')!;
      expect(healthCheck.status).toBe('pending');

      db.close();
    });
  });

  describe('AC-2: Integrate writes task results to workflow_tasks and final health results to workflow_checks', () => {
    it('IntegrateStageRunner calls appendTaskResult for each of the three tasks', async () => {
      const { IntegrateStageRunner } = await import('../src/workflow/integrate-stage-runner');

      const issueNumber = 200;
      const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issueNumber}-test`);
      fs.mkdirSync(path.join(changeDir, 'specs'), { recursive: true });

      createMainSpec(tmpDir, 'cap-ac2', [
        '### Requirement: ExistingAC2\n\nExisting content.\n\n#### Scenario: Existing scenario\n\nExisting scenario content.',
      ]);

      createChangeSpec(changeDir, 'cap-ac2', `## ADDED Requirements

### Requirement: NewAC2

New requirement content.

#### Scenario: New scenario
New scenario content.`);

      const runner = new IntegrateStageRunner({ worktreePath: tmpDir });
      const ctx = createMockContext(tmpDir, issueNumber);
      const result = await runner.run(ctx);

      expect(result.success).toBe(true);

      const taskResultCalls = appendedTaskResults(ctx);
      const taskIds = taskResultCalls.map(r => r.taskId);
      expect(taskIds).toContain('integrate:spec-sync');
      expect(taskIds).toContain('integrate:archive-change');
      expect(taskIds).toContain('integrate:merge');

      const specSyncCall = taskResultCalls.find(r => r.taskId === 'integrate:spec-sync');
      expect(specSyncCall?.status).toBe('completed');

      const archiveCall = taskResultCalls.find(r => r.taskId === 'integrate:archive-change');
      expect(archiveCall?.status).toBe('completed');

      const mergeCall = taskResultCalls.find(r => r.taskId === 'integrate:merge');
      expect(mergeCall?.status).toBe('completed');
    });

    it('IntegrateStageRunner updates check results with health:integrate (disabled health gate passes)', async () => {
      const { IntegrateStageRunner } = await import('../src/workflow/integrate-stage-runner');

      const issueNumber = 201;
      const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issueNumber}-test`);
      fs.mkdirSync(path.join(changeDir, 'specs'), { recursive: true });

      createMainSpec(tmpDir, 'cap-ac2b', [
        '### Requirement: ExistingAC2b\n\nExisting content.\n\n#### Scenario: Existing scenario\n\nExisting scenario content.',
      ]);

      createChangeSpec(changeDir, 'cap-ac2b', `## ADDED Requirements

### Requirement: NewAC2b

New requirement content.

#### Scenario: New scenario
New scenario content.`);

      const runner = new IntegrateStageRunner({ worktreePath: tmpDir });
      const ctx = createMockContext(tmpDir, issueNumber);
      const result = await runner.run(ctx);

      expect(result.success).toBe(true);

      const checkResultCalls = appendedCheckResults(ctx);
      const healthCheckResult = checkResultCalls.find(r => r.name === 'health:integrate');
      expect(healthCheckResult).toBeDefined();
      expect(healthCheckResult?.status).toBe('pass');
    });
  });

  describe('AC-4: integration events preserved', () => {
    it('emits integration_started, integration_step_updated, and integration_completed on success', async () => {
      const { IntegrateStageRunner } = await import('../src/workflow/integrate-stage-runner');

      const issueNumber = 220;
      const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issueNumber}-test`);
      fs.mkdirSync(path.join(changeDir, 'specs'), { recursive: true });

      createMainSpec(tmpDir, 'cap-evt', [
        '### Requirement: ExistingEvt\n\nExisting content.\n\n#### Scenario: Existing scenario\n\nExisting scenario content.',
      ]);

      createChangeSpec(changeDir, 'cap-evt', `## ADDED Requirements

### Requirement: NewEvt

New requirement content.

#### Scenario: New scenario
New scenario content.`);

      const runner = new IntegrateStageRunner({ worktreePath: tmpDir });
      const ctx = createMockContext(tmpDir, issueNumber);
      await runner.run(ctx);

      const emitCalls = (ctx.eventBus.emit as ReturnType<typeof vi.fn>).mock.calls;
      const eventNames = emitCalls.map(([name]) => name);

      expect(eventNames).toContain('integration_started');
      expect(eventNames).toContain('integration_step_updated');
      expect(eventNames).toContain('integration_completed');
      expect(eventNames).not.toContain('integration_failed');
    });
  });
});
