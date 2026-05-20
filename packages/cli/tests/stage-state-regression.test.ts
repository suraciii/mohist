import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { DatabaseManager } from '../src/db/database';
import { initializeDatabase } from '../src/db/migrations';
import { ProjectRepo } from '../src/db/project-repo';
import { IssueRepo } from '../src/db/issue-repo';
import { StageExecutionRepo } from '../src/db/stage-execution-repo';
import { Stage } from '../src/types';
import { StageStateService } from '../src/services/stage-state-service';

describe('stage-state regression: multi-execution latest-state resolution', () => {
  let db: DatabaseManager;
  let stageStateService: StageStateService;
  let stageExecutionRepo: StageExecutionRepo;
  let issueId: string;

  beforeEach(() => {
    db = new DatabaseManager({ inMemory: true });
    initializeDatabase(db);

    const projectRepo = new ProjectRepo(db);
    const project = projectRepo.create({ name: 'Test', path: '/test' });

    const issueRepo = new IssueRepo(db);
    const issue = issueRepo.create({ number: 1, projectId: project.id, title: 'Test Issue' });
    issueId = issue.id;

    stageStateService = new StageStateService(db);
    stageExecutionRepo = new StageExecutionRepo(db);
  });

  afterEach(() => {
    db.close();
  });

  it('resolves to latest current task state across multiple stage executions for the same issue and stage', () => {
    stageExecutionRepo.create(issueId, Stage.Build);
    stageStateService.ensureStage(issueId, Stage.Build);

    stageStateService.upsertTask(issueId, Stage.Build, {
      taskId: 'T-001',
      title: 'Compile code',
      status: 'failed',
      source: 'dynamic',
      attempts: 1,
      duration: 1000,
    });

    const compileTask = stageStateService.getStageState(issueId, Stage.Build)!.tasks.find(t => t.taskId === 'T-001');
    expect(compileTask).toBeDefined();
    expect(compileTask!.status).toBe('failed');
    expect(compileTask!.attempts).toBe(1);

    stageExecutionRepo.create(issueId, Stage.Build);
    stageStateService.ensureStage(issueId, Stage.Build);

    stageStateService.upsertTask(issueId, Stage.Build, {
      taskId: 'T-001',
      title: 'Compile code',
      status: 'completed',
      source: 'dynamic',
      attempts: 2,
      duration: 3000,
    });

    const state = stageStateService.getStageState(issueId, Stage.Build);
    expect(state).not.toBeNull();

    const task = state!.tasks.find(t => t.taskId === 'T-001');
    expect(task).toBeDefined();
    expect(task!.status).toBe('completed');
    expect(task!.attempts).toBe(2);
    expect(task!.duration).toBe(3000);

    const executions = stageExecutionRepo.findByIssueId(issueId);
    expect(executions.length).toBe(2);

    expect(state!.attempts).toBe(1);
  });

  it('resolves to latest check state after fix-recheck cycle without reading only the first execution', () => {
    stageExecutionRepo.create(issueId, Stage.Check);
    stageStateService.ensureStage(issueId, Stage.Check);

    stageStateService.upsertCheck(issueId, Stage.Check, {
      checkName: 'build-test',
      status: 'failed',
      message: 'Test suite failed',
    });

    const firstCheck = stageStateService.getStageState(issueId, Stage.Check)!.checks[0];
    expect(firstCheck.status).toBe('failed');

    stageExecutionRepo.create(issueId, Stage.Check);
    stageStateService.ensureStage(issueId, Stage.Check);

    stageStateService.upsertCheck(issueId, Stage.Check, {
      checkName: 'build-test',
      status: 'passed',
      message: 'All tests passed',
    });

    const state = stageStateService.getStageState(issueId, Stage.Check);
    expect(state).not.toBeNull();

    const check = state!.checks.find(c => c.checkName === 'build-test');
    expect(check).toBeDefined();
    expect(check!.status).toBe('passed');
    expect(check!.message).toBe('All tests passed');
    expect(check!.runCount).toBe(2);

    const executions = stageExecutionRepo.findByIssueId(issueId);
    expect(executions.length).toBe(2);

    expect(state!.tasks.length).toBe(0);
  });

  it('API-level test: stage-state endpoint returns latest state after multiple executions', async () => {
    stageStateService.ensureStage(issueId, Stage.Plan);

    stageStateService.upsertTask(issueId, Stage.Plan, {
      taskId: 'proposal',
      title: 'Write proposal',
      status: 'completed',
      attempts: 1,
    });

    stageStateService.upsertTask(issueId, Stage.Plan, {
      taskId: 'proposal',
      title: 'Write proposal',
      status: 'failed',
      attempts: 1,
    });

    const states = stageStateService.getIssueStageState(issueId);
    const planState = states.find(s => s.stage === Stage.Plan);
    expect(planState).toBeDefined();

    const proposalTask = planState!.tasks.find(t => t.taskId === 'proposal');
    expect(proposalTask).toBeDefined();
    expect(proposalTask!.status).toBe('failed');
    expect(planState!.tasks.length).toBe(1);
  });

});

describe('stage-state regression: dynamic fix tasks', () => {
  let db: DatabaseManager;
  let stageStateService: StageStateService;
  let issueId: string;

  beforeEach(() => {
    db = new DatabaseManager({ inMemory: true });
    initializeDatabase(db);

    const projectRepo = new ProjectRepo(db);
    const project = projectRepo.create({ name: 'Test', path: '/test' });

    const issueRepo = new IssueRepo(db);
    const issue = issueRepo.create({ number: 1, projectId: project.id, title: 'Test Issue' });
    issueId = issue.id;

    stageStateService = new StageStateService(db);
  });

  afterEach(() => {
    db.close();
  });

  it('includes dynamic fix-check-health task in stage-state output', () => {
    stageStateService.ensureStage(issueId, Stage.Check);

    stageStateService.upsertTask(issueId, Stage.Check, {
      taskId: 'fix-check-health',
      title: 'Fix check health issues',
      status: 'completed',
      source: 'dynamic',
      order: 100,
    });

    const state = stageStateService.getStageState(issueId, Stage.Check);
    expect(state).not.toBeNull();

    const fixTask = state!.tasks.find(t => t.taskId === 'fix-check-health');
    expect(fixTask).toBeDefined();
    expect(fixTask!.status).toBe('completed');
    expect(fixTask!.source).toBe('dynamic');

    const staticTasks = state!.tasks.filter(t => t.source === 'static');
    expect(staticTasks.length).toBe(0);

    expect(state!.tasks.length).toBe(1);
  });

  it('includes dynamic fix-build-health task in build stage output', () => {
    stageStateService.ensureStage(issueId, Stage.Build);

    stageStateService.upsertTask(issueId, Stage.Build, {
      taskId: 'T-001',
      title: 'Compile code',
      status: 'completed',
      source: 'dynamic',
      order: 1,
    });

    stageStateService.upsertTask(issueId, Stage.Build, {
      taskId: 'fix-build-health',
      title: 'Fix build health issues',
      status: 'running',
      source: 'dynamic',
      order: 100,
    });

    const state = stageStateService.getStageState(issueId, Stage.Build);
    expect(state).not.toBeNull();

    const fixTask = state!.tasks.find(t => t.taskId === 'fix-build-health');
    expect(fixTask).toBeDefined();
    expect(fixTask!.status).toBe('running');
    expect(fixTask!.source).toBe('dynamic');

    const compileTask = state!.tasks.find(t => t.taskId === 'T-001');
    expect(compileTask).toBeDefined();
    expect(compileTask!.status).toBe('completed');
  });

  it('includes fix-plan-health task alongside static plan tasks', () => {
    stageStateService.ensureStage(issueId, Stage.Plan);

    stageStateService.upsertTask(issueId, Stage.Plan, {
      taskId: 'fix-plan-health',
      title: 'Fix plan health issues',
      status: 'completed',
      source: 'dynamic',
      order: 50,
    });

    const state = stageStateService.getStageState(issueId, Stage.Plan);
    expect(state).not.toBeNull();

    const fixTask = state!.tasks.find(t => t.taskId === 'fix-plan-health');
    expect(fixTask).toBeDefined();
    expect(fixTask!.source).toBe('dynamic');

    expect(state!.tasks.length).toBe(1);
  });

  it('fix-review-findings task appears in stage state', () => {
    stageStateService.ensureStage(issueId, Stage.Check);

    stageStateService.upsertTask(issueId, Stage.Check, {
      taskId: 'fix-review-findings',
      title: 'Fix review findings',
      status: 'completed',
      source: 'dynamic',
      order: 50,
      output: { fixesApplied: 3 },
    });

    const state = stageStateService.getStageState(issueId, Stage.Check);
    const fixTask = state!.tasks.find(t => t.taskId === 'fix-review-findings');
    expect(fixTask).toBeDefined();
    expect(fixTask!.status).toBe('completed');
    expect(fixTask!.output).toEqual({ fixesApplied: 3 });
  });
});

describe('stage-state regression: tasks.json build task mirroring', () => {
  let db: DatabaseManager;
  let stageStateService: StageStateService;
  let issueId: string;
  let projectPath: string;

  beforeEach(() => {
    db = new DatabaseManager({ inMemory: true });
    initializeDatabase(db);
    projectPath = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-stage-state-regression-'));

    const projectRepo = new ProjectRepo(db);
    const project = projectRepo.create({ name: 'Test', path: projectPath });

    const issueRepo = new IssueRepo(db);
    const issue = issueRepo.create({ number: 1, projectId: project.id, title: 'Test Issue' });
    issueId = issue.id;

    stageStateService = new StageStateService(db);
  });

  afterEach(() => {
    db.close();
    fs.rmSync(projectPath, { recursive: true, force: true });
  });

  it('mirrors tasks.json-style build tasks into normalized stage-state rows', () => {
    stageStateService.ensureStage(issueId, Stage.Build);

    const tasksJsonTasks = [
      { id: 'T-001', title: 'Add persistence service', passes: true, order: 1, attempts: 1 },
      { id: 'T-002', title: 'Expose API endpoint', passes: true, order: 2, attempts: 1 },
      { id: 'T-003', title: 'Add regression tests', passes: false, order: 3, attempts: 1 },
    ];

    for (const t of tasksJsonTasks) {
      stageStateService.upsertTask(issueId, Stage.Build, {
        taskId: t.id,
        title: t.title,
        status: t.passes ? 'completed' : 'pending',
        source: 'dynamic',
        order: t.order,
        attempts: t.attempts,
      });
    }

    const state = stageStateService.getStageState(issueId, Stage.Build);
    expect(state).not.toBeNull();
    expect(state!.tasks.length).toBe(3);

    const t1 = state!.tasks.find(t => t.taskId === 'T-001');
    expect(t1).toBeDefined();
    expect(t1!.status).toBe('completed');

    const t2 = state!.tasks.find(t => t.taskId === 'T-002');
    expect(t2).toBeDefined();
    expect(t2!.status).toBe('completed');

    const t3 = state!.tasks.find(t => t.taskId === 'T-003');
    expect(t3).toBeDefined();
    expect(t3!.status).toBe('pending');
  });

  it('mirrors tasks.json build tasks with normalized status not exposing passes field', () => {
    stageStateService.ensureStage(issueId, Stage.Build);

    stageStateService.upsertTask(issueId, Stage.Build, {
      taskId: 'T-001',
      title: 'Add persistence',
      status: 'completed',
      source: 'dynamic',
      order: 1,
    });

    const state = stageStateService.getIssueStageState(issueId);
    const buildState = state.find(s => s.stage === Stage.Build);
    expect(buildState).toBeDefined();

    for (const task of buildState!.tasks) {
      expect((task as any).passes).toBeUndefined();
      expect(['pending', 'running', 'completed', 'failed', 'skipped']).toContain(task.status);
    }
  });

  it('mirrors failed dynamic tasks as failed and preserves error output', () => {
    stageStateService.ensureStage(issueId, Stage.Build);

    stageStateService.upsertTask(issueId, Stage.Build, {
      taskId: 'T-002',
      title: 'Expose endpoint',
      status: 'failed',
      source: 'dynamic',
      order: 2,
      attempts: 3,
      output: { error: 'TypeScript build failed' },
    });

    const state = stageStateService.getStageState(issueId, Stage.Build);
    expect(state).not.toBeNull();

    const task = state!.tasks.find(t => t.taskId === 'T-002');
    expect(task).toBeDefined();
    expect(task!.status).toBe('failed');
    expect(task!.attempts).toBe(3);
    expect(task!.output).toEqual({ error: 'TypeScript build failed' });
  });

  it('updates mirrored build tasks when tasks.json changes between executions', () => {
    stageStateService.ensureStage(issueId, Stage.Build);

    stageStateService.upsertTask(issueId, Stage.Build, {
      taskId: 'T-001',
      title: 'Add persistence',
      status: 'pending',
      source: 'dynamic',
      order: 1,
    });

    stageStateService.ensureStage(issueId, Stage.Build);

    stageStateService.upsertTask(issueId, Stage.Build, {
      taskId: 'T-001',
      title: 'Add persistence',
      status: 'completed',
      source: 'dynamic',
      order: 1,
      attempts: 2,
      duration: 5000,
    });

    const state = stageStateService.getStageState(issueId, Stage.Build);
    const task = state!.tasks.find(t => t.taskId === 'T-001');
    expect(task).toBeDefined();
    expect(task!.status).toBe('completed');
    expect(task!.attempts).toBe(2);
    expect(task!.duration).toBe(5000);
  });
});
