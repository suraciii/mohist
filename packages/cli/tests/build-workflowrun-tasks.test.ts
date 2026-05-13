import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { Stage, IssueStatus, type Issue } from '../src/types';
import { WorkflowRun } from '../src/workflow/domain';
import { BuildStageRunner } from '../src/workflow/build-stage-runner';
import { runRalphLoop, resetAcpSessionRunner, setAcpSessionRunner } from '../src/openspec/ralph-executor';
import type { OpenSpecChange } from '../src/openspec/detector';
import type { WorkflowApplicationRuntime } from '../src/workflow/stage-context';
import type { Check } from '../src/workflow/checks';

function makeIssue(overrides: Partial<Issue> = {}): Issue {
  return {
    id: 'issue-1',
    number: 188,
    title: 'Build tasks',
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

function makeChange(tempDir: string, tasks: Array<Record<string, unknown>>): OpenSpecChange {
  const changePath = path.join(tempDir, 'openspec', 'changes', '188-build');
  fs.mkdirSync(path.join(changePath, 'session-memories'), { recursive: true });
  fs.writeFileSync(path.join(changePath, 'tasks.json'), JSON.stringify({ version: 1, tasks }, null, 2));
  fs.writeFileSync(path.join(changePath, 'proposal.md'), '# Proposal');
  fs.writeFileSync(path.join(changePath, 'design.md'), '# Design');
  return {
    changePath,
    tasksPath: path.join(changePath, 'tasks.json'),
    sessionMemoriesPath: path.join(changePath, 'session-memories'),
    proposalPath: path.join(changePath, 'proposal.md'),
    designPath: path.join(changePath, 'design.md'),
    specsPath: path.join(changePath, 'specs'),
  };
}

function startBuildRun(issue: Issue): ReturnType<typeof WorkflowRun.startWorkflow>['run'] {
  const { run } = WorkflowRun.startWorkflow({ id: 'run-1', issueId: issue.id, issueNumber: issue.number });
  for (const task of run.stageRun(Stage.Plan).tasks.map(task => task.id)) {
    run.completeTask(Stage.Plan, task, { status: 'completed' });
  }
  for (const check of run.stageRun(Stage.Plan).checks.map(check => check.name)) {
    run.recordCheckResult(Stage.Plan, { name: check, status: 'pass' });
  }
  run.approveStage(Stage.Plan);
  return run;
}

function makeService(run: ReturnType<typeof startBuildRun>): WorkflowApplicationRuntime {
  return {
    startWorkflow: vi.fn(),
    resumeDecision: vi.fn(() => ({ run, nextWork: run.nextWork() })),
    materializeTasks: vi.fn(({ stage, tasks }) => ({ run, decision: run.materializeTasks(stage, tasks) })),
    completeTask: vi.fn(({ stage, taskId, result }) => ({ run, decision: run.completeTask(stage, taskId, result) })),
    recordCheckResult: vi.fn(({ stage, result }) => ({ run, decision: run.recordCheckResult(stage, result) })),
  };
}

class ExposedBuildStageRunner extends BuildStageRunner {
  checks(): Check[] {
    return this.getChecks();
  }
}

describe('Build aggregate-backed task runtime', () => {
  let tempDir: string;

  beforeEach(() => {
    tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-build-workflowrun-'));
  });

  afterEach(() => {
    fs.rmSync(tempDir, { recursive: true, force: true });
    resetAcpSessionRunner();
    vi.restoreAllMocks();
  });

  it('materializes tasks through WorkflowApplicationService idempotently before build execution', async () => {
    setAcpSessionRunner(vi.fn().mockResolvedValue({ success: true, text: 'done' }));
    const issue = makeIssue();
    const change = makeChange(tempDir, [
      { id: 'T-001', order: 1, title: 'First', description: 'd', passes: true, error: 'stale failure' },
      { id: 'T-002', order: 2, title: 'Second', description: 'd', passes: false },
    ]);
    const run = startBuildRun(issue);
    const service = makeService(run);
    service.materializeTasks({
      issueId: issue.id,
      stage: Stage.Build,
      tasks: [
        { id: 'T-001', title: 'First', order: 1 },
        { id: 'T-002', title: 'Second', order: 2 },
      ],
    });

    const result = await runRalphLoop(change, {
      worktreePath: tempDir,
      projectPath: tempDir,
      issueId: issue.id,
      workflowApplicationService: service,
    }, { maxRetries: 0, ignoreTaskFileProgress: true });

    expect(result.success).toBe(true);
    service.materializeTasks({
      issueId: issue.id,
      stage: Stage.Build,
      tasks: [
        { id: 'T-001', title: 'First', order: 1 },
        { id: 'T-002', title: 'Second', order: 2 },
      ],
    });
    service.materializeTasks({
      issueId: issue.id,
      stage: Stage.Build,
      tasks: [
        { id: 'T-001', title: 'First', order: 1 },
        { id: 'T-002', title: 'Second', order: 2 },
      ],
    });
    expect(run.stageRun(Stage.Build).tasks.map(task => task.id)).toEqual(['T-001', 'T-002']);
    expect(run.stageRun(Stage.Build).tasks.every(task => task.status === 'completed')).toBe(true);
  });

  it('BuildStageRunner materializes tasks before executing Ralph', async () => {
    const issue = makeIssue();
    const change = makeChange(tempDir, [
      { id: 'T-001', order: 1, title: 'First', description: 'd', passes: false },
    ]);
    const run = startBuildRun(issue);
    const service = makeService(run);
    const detectModule = await import('../src/openspec/detector');
    vi.spyOn(detectModule, 'detectOpenSpecChange').mockReturnValue(change);
    const ralphModule = await import('../src/openspec/ralph-executor');
    const execute = vi.fn().mockImplementation(() => {
      expect(run.stageRun(Stage.Build).tasks.map(task => task.id)).toEqual(['T-001']);
      return Promise.resolve({ completed: 1, failed: 0, skipped: 0, total: 1, taskResults: [], success: true });
    });
    vi.spyOn(ralphModule.RalphExecutor.prototype, 'execute').mockImplementation(execute);
    const runner = new BuildStageRunner({ worktreePath: tempDir, projectId: 'project-1' });
    (runner as any).gitCommitter = { commitBuildChanges: vi.fn().mockResolvedValue(undefined) };

    await runner.run({
      issue,
      acpOptions: { cwd: tempDir },
      artifactManager: {
        getChangeDir: vi.fn().mockReturnValue(change.changePath),
        createChangeDir: vi.fn(),
        readArtifact: vi.fn(),
        writeArtifact: vi.fn(),
        exists: vi.fn().mockReturnValue(true),
        readTasks: vi.fn(),
        updateTaskPasses: vi.fn(),
        syncTasksToStageState: vi.fn(),
        archiveChange: vi.fn(),
      } as never,
      worktreeManager: {} as never,
      projectRepo: {} as never,
      eventBus: { emit: vi.fn() } as never,
      checkpointManager: {
        getResumeSteps: vi.fn().mockReturnValue([]),
        markStepComplete: vi.fn(),
        delete: vi.fn(),
      } as never,
      issueRepo: { updateStage: vi.fn(), setApprovalState: vi.fn(), clearApprovalState: vi.fn(), updateStatus: vi.fn(), findById: vi.fn() },
      workflowApplicationService: service,
      requestedWork: { kind: 'task', stage: Stage.Build, taskId: 'T-001' },
    } as never);

    expect(service.materializeTasks).toHaveBeenCalledWith(expect.objectContaining({
      issueId: issue.id,
      stage: Stage.Build,
      tasks: [{ id: 'T-001', title: 'First', order: 1 }],
      tasksPath: change.tasksPath,
    }));
    expect(execute).toHaveBeenCalledWith(change, expect.objectContaining({ ignoreTaskFileProgress: true }));
    expect((runner as any).gitCommitter.commitBuildChanges).toHaveBeenCalledWith(issue);
  });

  it('does not replay legacy checkpoint skips while executing an aggregate requested task', async () => {
    const issue = makeIssue();
    const change = makeChange(tempDir, [
      { id: 'T-001', order: 1, title: 'First', description: 'd', passes: true },
      { id: 'T-002', order: 2, title: 'Second', description: 'd', passes: false },
    ]);
    const run = startBuildRun(issue);
    run.materializeTasks(Stage.Build, [
      { id: 'T-001', title: 'First', order: 1 },
      { id: 'T-002', title: 'Second', order: 2 },
    ]);
    run.completeTask(Stage.Build, 'T-001', { status: 'completed' });
    const service = makeService(run);
    const detectModule = await import('../src/openspec/detector');
    vi.spyOn(detectModule, 'detectOpenSpecChange').mockReturnValue(change);
    const ralphModule = await import('../src/openspec/ralph-executor');
    const execute = vi.fn().mockResolvedValue({ completed: 1, failed: 0, skipped: 0, total: 1, taskResults: [], success: true });
    vi.spyOn(ralphModule.RalphExecutor.prototype, 'execute').mockImplementation(execute);
    const runner = new BuildStageRunner({ worktreePath: tempDir, projectId: 'project-1' });
    (runner as any).gitCommitter = { commitBuildChanges: vi.fn().mockResolvedValue(undefined) };

    await runner.run({
      issue,
      acpOptions: { cwd: tempDir },
      artifactManager: {
        getChangeDir: vi.fn().mockReturnValue(change.changePath),
        createChangeDir: vi.fn(),
        readArtifact: vi.fn(),
        writeArtifact: vi.fn(),
        exists: vi.fn().mockReturnValue(true),
        readTasks: vi.fn(),
        updateTaskPasses: vi.fn(),
        syncTasksToStageState: vi.fn(),
        archiveChange: vi.fn(),
      } as never,
      worktreeManager: {} as never,
      projectRepo: {} as never,
      eventBus: { emit: vi.fn() } as never,
      checkpointManager: {
        getResumeSteps: vi.fn().mockReturnValue(['T-001']),
        markStepComplete: vi.fn(),
        delete: vi.fn(),
      } as never,
      issueRepo: { updateStage: vi.fn(), setApprovalState: vi.fn(), clearApprovalState: vi.fn(), updateStatus: vi.fn(), findById: vi.fn() },
      workflowApplicationService: service,
      requestedWork: { kind: 'task', stage: Stage.Build, taskId: 'T-002' },
    } as never);

    expect(execute).toHaveBeenCalledWith(change, expect.objectContaining({
      ignoreTaskFileProgress: true,
      onlyTaskId: 'T-002',
      skipTaskIds: undefined,
    }));
  });

  it('single-task Ralph mode leaves unrelated tasks.json progress unchanged', async () => {
    setAcpSessionRunner(vi.fn().mockResolvedValue({ success: true, text: 'done' }));
    const issue = makeIssue();
    const change = makeChange(tempDir, [
      { id: 'T-001', order: 1, title: 'First', description: 'd', passes: false },
      { id: 'T-002', order: 2, title: 'Second', description: 'd', passes: false, error: 'still pending' },
    ]);
    const run = startBuildRun(issue);
    run.materializeTasks(Stage.Build, [
      { id: 'T-001', title: 'First', order: 1 },
      { id: 'T-002', title: 'Second', order: 2 },
    ]);
    const service = makeService(run);

    const result = await runRalphLoop(change, {
      worktreePath: tempDir,
      projectPath: tempDir,
      issueId: issue.id,
      workflowApplicationService: service,
    }, { maxRetries: 0, ignoreTaskFileProgress: true, onlyTaskId: 'T-001' });

    expect(result.success).toBe(true);
    expect(run.stageRun(Stage.Build).findTask('T-001').status).toBe('completed');
    expect(run.stageRun(Stage.Build).findTask('T-002').status).toBe('pending');
    const tasksFile = JSON.parse(fs.readFileSync(change.tasksPath, 'utf-8'));
    expect(tasksFile.tasks.find((task: any) => task.id === 'T-002')).toMatchObject({ passes: false });
  });

  it('records skipped task validation through the aggregate immediately', async () => {
    setAcpSessionRunner(vi.fn().mockResolvedValue({ success: false, error: 'Timed out' }));
    const issue = makeIssue();
    const change = makeChange(tempDir, [
      { id: 'T-001', order: 1, title: 'First', description: 'd', passes: false, attempts: 0 },
    ]);
    const run = startBuildRun(issue);
    run.materializeTasks(Stage.Build, [{ id: 'T-001', title: 'First', order: 1 }]);
    const service = makeService(run);

    const result = await runRalphLoop(change, {
      worktreePath: tempDir,
      projectPath: tempDir,
      issueId: issue.id,
      workflowApplicationService: service,
    }, { maxRetries: 0, ignoreTaskFileProgress: true });

    expect(result.success).toBe(false);
    expect(result.taskResults[0].status).toBe('skipped');
    expect(service.completeTask).toHaveBeenCalledWith(expect.objectContaining({
      taskId: 'T-001',
      result: expect.objectContaining({ status: 'skipped', reason: expect.stringContaining('Auto-skipped') }),
    }));
    expect(run.stageRun(Stage.Build).findTask('T-001').status).toBe('skipped');
  });

  it('stops the Build stage when Ralph reports a task failure', async () => {
    setAcpSessionRunner(vi.fn().mockResolvedValue({ success: false, error: 'Session died', failureKind: 'session_failed' }));
    const issue = makeIssue();
    const change = makeChange(tempDir, [
      { id: 'T-001', order: 1, title: 'First', description: 'd', passes: false, attempts: 0 },
      { id: 'T-002', order: 2, title: 'Second', description: 'd', passes: false, attempts: 0 },
    ]);
    const run = startBuildRun(issue);
    run.materializeTasks(Stage.Build, [
      { id: 'T-001', title: 'First', order: 1 },
      { id: 'T-002', title: 'Second', order: 2 },
    ]);
    const service = makeService(run);

    const result = await runRalphLoop(change, {
      worktreePath: tempDir,
      projectPath: tempDir,
      issueId: issue.id,
      workflowApplicationService: service,
      onAskUser: vi.fn().mockResolvedValue('abort'),
    }, { maxRetries: 0, ignoreTaskFileProgress: true });

    expect(result.success).toBe(false);
    expect(run.status).toBe('failed');
    expect(run.failure).toMatchObject({ reason: 'task-failed', stage: Stage.Build, taskId: 'T-001' });
    expect(run.stageRun(Stage.Build).findTask('T-002').status).toBe('pending');
  });

  it('uses checkpoint resume rather than tasks.json passes as source of truth', async () => {
    setAcpSessionRunner(vi.fn().mockResolvedValue({ success: true, text: 'done' }));
    const issue = makeIssue();
    const change = makeChange(tempDir, [
      { id: 'T-001', order: 1, title: 'First', description: 'd', passes: true, error: 'stale' },
      { id: 'T-002', order: 2, title: 'Second', description: 'd', passes: false },
    ]);
    const run = startBuildRun(issue);
    run.materializeTasks(Stage.Build, [
      { id: 'T-001', title: 'First', order: 1 },
      { id: 'T-002', title: 'Second', order: 2 },
    ]);
    const service = makeService(run);

    const result = await runRalphLoop(change, {
      worktreePath: tempDir,
      projectPath: tempDir,
      issueId: issue.id,
      workflowApplicationService: service,
    }, { maxRetries: 0, ignoreTaskFileProgress: true, skipTaskIds: ['T-001'] });

    expect(result.success).toBe(true);
    expect(run.stageRun(Stage.Build).findTask('T-001').status).toBe('skipped');
    expect(run.stageRun(Stage.Build).findTask('T-002').status).toBe('completed');
    expect(service.completeTask).toHaveBeenNthCalledWith(1, expect.objectContaining({ taskId: 'T-001' }));
  });

  it('does not execute all-tasks-complete as a Build business check', () => {
    const runner = new ExposedBuildStageRunner({ worktreePath: tempDir, projectId: 'project-1' });
    expect(runner.checks().map(check => check.name)).toEqual(['health:build']);
  });
});
