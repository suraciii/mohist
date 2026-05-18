import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import fs from 'fs';
import os from 'os';
import path from 'path';
import { DatabaseManager } from '../src/db/database';
import { initializeDatabase } from '../src/db/migrations';
import { IssueRepo } from '../src/db/issue-repo';
import { ProjectRepo } from '../src/db/project-repo';
import { WorkflowRunRepo } from '../src/db/workflow-run-repo';
import { WorkflowRun } from '../src/workflow/domain';
import { Stage } from '../src/types';

describe('WorkflowRunRepo aggregate persistence', () => {
  let db: DatabaseManager;
  let repo: WorkflowRunRepo;
  let issueId: string;
  let issueNumber: number;
  let tempDir: string;

  beforeEach(() => {
    db = new DatabaseManager({ inMemory: true });
    initializeDatabase(db);
    repo = new WorkflowRunRepo(db);

    const project = new ProjectRepo(db).create({ name: 'Repo Test', path: '/tmp/repo-test' });
    const issue = new IssueRepo(db).create({ number: 188, projectId: project.id, title: 'Persist aggregate' });
    issueId = issue.id;
    issueNumber = issue.number;
    tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'workflow-run-repo-'));
  });

  afterEach(() => {
    db.close();
    fs.rmSync(tempDir, { recursive: true, force: true });
  });

  it('creates or reuses one active aggregate run for the issue', () => {
    const first = repo.createOrLoadActiveAggregate({ issueId, issueNumber, startedBy: 'tester' });
    const second = repo.createOrLoadActiveAggregate({ issueId, issueNumber, startedBy: 'tester' });

    const rows = db.all<{ id: string }>(
      `SELECT id FROM workflow_runs WHERE issue_id = ? AND status = 'running'`,
      [issueId],
    );

    expect(second.id).toBe(first.id);
    expect(rows).toHaveLength(1);
    expect(second.snapshot().stageRuns.map(stage => stage.stage)).toEqual([Stage.Plan, Stage.Build, Stage.Check, Stage.Integrate]);
  });

  it('loads an aggregate snapshot with ordered stages, tasks, checks, approval, and delivery metadata', () => {
    const { run } = WorkflowRun.startWorkflow({ id: 'wr_188_snapshot', issueId, issueNumber });
    for (const taskId of ['proposal', 'specs', 'design', 'tasks', 'self-review']) {
      run.completeTask(Stage.Plan, taskId, { status: 'completed' });
    }
    for (const checkName of ['proposal-complete', 'specs-complete', 'design-complete', 'tasks-valid', 'self-review-passed', 'health:plan']) {
      run.recordCheckResult(Stage.Plan, { name: checkName, status: 'pass' });
    }
    run.approveStage(Stage.Plan, { output: { approved: true } });
    run.materializeTasks(Stage.Build, [{ id: 'T-001', title: 'Build task', order: 0 }]);

    repo.saveAggregate(run, 'tester');

    const loaded = repo.loadActiveAggregate(issueId)!;
    const snapshot = loaded.snapshot();
    const plan = snapshot.stageRuns[0];
    const build = snapshot.stageRuns.find(stage => stage.stage === Stage.Build)!;

    expect(snapshot.currentStage).toBe(Stage.Build);
    expect(plan.stage).toBe(Stage.Plan);
    expect(plan.tasks.map(task => task.id)).toEqual(['proposal', 'specs', 'design', 'tasks', 'self-review']);
    expect(plan.checks.map(check => check.name)).toEqual(['proposal-complete', 'specs-complete', 'design-complete', 'tasks-valid', 'self-review-passed', 'health:plan']);
    expect(plan.approval).toMatchObject({ status: 'approved', output: { approved: true } });
    expect(build.tasks).toHaveLength(1);
    expect(build.buildWorkSourceState).toMatchObject({
      evaluated: true,
      tasks: [{ id: 'T-001', title: 'Build task', order: 0 }],
    });
  });

  it('saves run, stage, task, check, approval, failure, and freeze changes in one aggregate transaction', () => {
    const { run } = WorkflowRun.startWorkflow({ id: 'wr_188_freeze', issueId, issueNumber });
    for (const taskId of ['proposal', 'specs', 'design', 'tasks', 'self-review']) {
      run.completeTask(Stage.Plan, taskId, { status: 'completed' });
    }
    for (const checkName of ['proposal-complete', 'specs-complete', 'design-complete', 'tasks-valid', 'self-review-passed', 'health:plan']) {
      run.recordCheckResult(Stage.Plan, { name: checkName, status: 'pass' });
    }
    run.approveStage(Stage.Plan, { output: { approved: true } });
    run.materializeTasks(Stage.Build, [{ id: 'T-001', title: 'Build task', order: 0 }]);
    run.completeTask(Stage.Build, 'T-001', { status: 'completed' });
    run.recordCheckResult(Stage.Build, { name: 'health:build', status: 'pass' });
    run.completeTask(Stage.Check, 'ai-review', { status: 'completed' });
    run.recordCheckResult(Stage.Check, { name: 'health:check', status: 'pass' });
    run.recordCheckResult(Stage.Check, { name: 'review-passed', status: 'pass', output: { verdict: 'PASS', snapshotSha: 'head' } });
    run.recordCheckResult(Stage.Check, {
      name: 'merge-ready',
      status: 'pass',
      output: {
        kind: 'merge-ready',
        targetBranch: 'main',
        strategy: 'squash',
        baseSha: 'base',
        candidateHeadSha: 'head',
        mergeBaseSha: 'base',
        canMerge: true,
        conflictFiles: [],
        checkedAt: '2026-05-15T00:00:00.000Z',
      },
    });
    run.approveStage(Stage.Check, { output: { approved: true } });
    run.completeTask(Stage.Integrate, 'integrate:spec-sync', { status: 'completed' });
    run.completeTask(Stage.Integrate, 'integrate:archive-change', { status: 'completed', output: { archivePath: 'openspec/changes/archive/188-persist-aggregate' } });
    run.completeTask(Stage.Integrate, 'integrate:merge', {
      status: 'completed',
      output: { targetBranch: 'main', baseSha: 'base', candidateHeadSha: 'head', landedSha: 'landed', rebased: true },
    });
    run.recordCheckResult(Stage.Integrate, { name: 'health:integrate', status: 'fail', message: 'post merge failed' });

    repo.saveAggregate(run, 'tester');
    const loaded = repo.loadAggregateById(run.id)!;
    const snapshot = loaded.snapshot();
    const integrate = snapshot.stageRuns.find(stage => stage.stage === Stage.Integrate)!;

    expect(snapshot.status).toBe('failed');
    expect(snapshot.failure?.reason).toBe('post-merge-health-failed');
    expect(integrate.failure?.reason).toBe('post-merge-health-failed');
    expect(integrate.freezePoint?.delivery).toMatchObject({ landedSha: 'landed', targetBranch: 'main' });
    expect(integrate.checks.find(check => check.name === 'health:integrate')).toMatchObject({ status: 'failed', message: 'post merge failed' });
  });

  it('repairs missing static Plan and Integrate rows idempotently when loading active runs', () => {
    const now = new Date().toISOString();
    db.run(
      `INSERT INTO workflow_runs (id, issue_id, issue_number, status, current_stage, created_at, updated_at)
       VALUES (?, ?, ?, 'running', 'plan', ?, ?)`,
      ['wr_partial_static', issueId, issueNumber, now, now],
    );
    for (const [stage, order] of [[Stage.Plan, 0], [Stage.Build, 1], [Stage.Check, 2], [Stage.Integrate, 3]] as const) {
      db.run(
        `INSERT INTO workflow_stage_runs (id, workflow_run_id, stage, status, stage_order, created_at, updated_at)
         VALUES (?, ?, ?, ?, ?, ?, ?)`,
        [`wr_partial_static/${stage}`, 'wr_partial_static', stage, stage === Stage.Plan ? 'running' : 'pending', order, now, now],
      );
    }

    repo.loadActiveAggregate(issueId);
    repo.loadActiveAggregate(issueId);

    const planTaskCount = db.get<{ count: number }>(
      `SELECT COUNT(*) AS count FROM workflow_tasks WHERE stage_run_id = ?`,
      ['wr_partial_static/plan'],
    )!.count;
    const integrateTaskCount = db.get<{ count: number }>(
      `SELECT COUNT(*) AS count FROM workflow_tasks WHERE stage_run_id = ?`,
      ['wr_partial_static/integrate'],
    )!.count;
    const integrateCheckCount = db.get<{ count: number }>(
      `SELECT COUNT(*) AS count FROM workflow_checks WHERE stage_run_id = ?`,
      ['wr_partial_static/integrate'],
    )!.count;

    expect(planTaskCount).toBe(5);
    expect(integrateTaskCount).toBe(3);
    expect(integrateCheckCount).toBe(1);
  });

  it('repairs Build task identities from tasks.json without inventing completion evidence', () => {
    const run = repo.createOrLoadActiveAggregate({ issueId, issueNumber });
    const tasksPath = path.join(tempDir, 'tasks.json');
    fs.writeFileSync(tasksPath, JSON.stringify({
      version: 1,
      tasks: [
        { id: 'T-001', title: 'Persist aggregate', order: 2, passes: true, attempts: 3, durations: [100, 200] },
        { id: 'T-002', title: 'Expose aggregate', order: 1, passes: false },
      ],
    }), 'utf-8');

    repo.loadAggregateById(run.id, { tasksPath });
    repo.loadAggregateById(run.id, { tasksPath });

    const loaded = repo.loadAggregateById(run.id)!;
    const build = loaded.snapshot().stageRuns.find(stage => stage.stage === Stage.Build)!;
    const taskRows = db.all<{ task_id: string }>(
      `SELECT task_id FROM workflow_tasks WHERE stage_run_id = ? ORDER BY task_order ASC, task_id ASC`,
      [`${run.id}/build`],
    );

    expect(taskRows.map(row => row.task_id)).toEqual(['T-002', 'T-001']);
    expect(build.tasks).toHaveLength(2);
    expect(build.tasks.find(task => task.id === 'T-001')).toMatchObject({
      status: 'pending',
      attempts: 0,
      duration: 0,
      output: null,
    });
    expect(build.tasks.find(task => task.id === 'T-002')).toMatchObject({
      status: 'pending',
      attempts: 0,
      output: null,
    });
  });

  it('repairs rerun-cleared Build task identities from tasks.json before dispatch', () => {
    const { run } = WorkflowRun.startWorkflow({ id: 'wr_188_build_rerun', issueId, issueNumber });
    for (const taskId of ['proposal', 'specs', 'design', 'tasks', 'self-review']) {
      run.completeTask(Stage.Plan, taskId, { status: 'completed' });
    }
    for (const checkName of ['proposal-complete', 'specs-complete', 'design-complete', 'tasks-valid', 'self-review-passed', 'health:plan']) {
      run.recordCheckResult(Stage.Plan, { name: checkName, status: 'pass' });
    }
    run.approveStage(Stage.Plan);
    run.materializeTasks(Stage.Build, [
      { id: 'T-001', title: 'Persist aggregate', order: 1 },
      { id: 'T-002', title: 'Expose aggregate', order: 2 },
    ]);
    run.rerunStage(Stage.Build);
    repo.saveAggregate(run, 'tester');

    const tasksPath = path.join(tempDir, 'tasks.json');
    fs.writeFileSync(tasksPath, JSON.stringify({
      version: 1,
      tasks: [
        { id: 'T-001', title: 'Persist aggregate', order: 1, passes: true, attempts: 1, durations: [50] },
        { id: 'T-002', title: 'Expose aggregate', order: 2, passes: true, attempts: 2, durations: [75, 25] },
      ],
    }), 'utf-8');

    const loaded = repo.loadAggregateById(run.id, { tasksPath })!;
    const build = loaded.snapshot().stageRuns.find(stage => stage.stage === Stage.Build)!;

    expect(build.tasks.map(task => [task.id, task.status, task.attempts, task.output])).toEqual([
      ['T-001', 'pending', 0, null],
      ['T-002', 'pending', 0, null],
    ]);
    expect(loaded.nextWork()).toMatchObject({ kind: 'task', stage: Stage.Build, taskId: 'T-001' });
  });

  it('persists removal of generated repair tasks when rerunning a stage', () => {
    const { run } = WorkflowRun.startWorkflow({ id: 'wr_188_rerun', issueId, issueNumber });
    for (const taskId of ['proposal', 'specs', 'design', 'tasks', 'self-review']) {
      run.completeTask(Stage.Plan, taskId, { status: 'completed' });
    }
    for (const checkName of ['proposal-complete', 'specs-complete', 'design-complete', 'tasks-valid']) {
      run.recordCheckResult(Stage.Plan, { name: checkName, status: 'pass' });
    }
    run.recordCheckResult(Stage.Plan, { name: 'self-review-passed', status: 'pass', output: { verdict: 'PASS' } });
    run.recordCheckResult(Stage.Plan, { name: 'health:plan', status: 'pass' });
    run.approveStage(Stage.Plan);
    run.materializeTasks(Stage.Build, [{ id: 'T-001', title: 'Build task', order: 0 }]);
    run.completeTask(Stage.Build, 'T-001', { status: 'completed' });
    run.recordCheckResult(Stage.Build, { name: 'health:build', status: 'pass' });
    run.completeTask(Stage.Check, 'ai-review', { status: 'completed' });
    run.recordCheckResult(Stage.Check, { name: 'health:check', status: 'pass' });
    run.recordCheckResult(Stage.Check, { name: 'review-passed', status: 'fail', output: { verdict: 'FAIL' } });
    run.completeTask(Stage.Check, 'fix-review-findings', { status: 'completed' });

    run.rerunStage(Stage.Check);
    repo.saveAggregate(run, 'tester');

    const loaded = repo.loadAggregateById(run.id)!;
    const checkStage = loaded.snapshot().stageRuns.find(stage => stage.stage === Stage.Check)!;
    const taskRows = db.all<{ task_id: string }>(
      `SELECT task_id FROM workflow_tasks WHERE stage_run_id = ? ORDER BY task_order ASC, task_id ASC`,
      [`${run.id}/check`],
    );

    expect(taskRows.map(row => row.task_id)).toEqual(['ai-review']);
    expect(checkStage.tasks.map(task => task.id)).toEqual(['ai-review']);
  });
});
