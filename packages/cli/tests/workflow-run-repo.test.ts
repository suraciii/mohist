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

    expect(snapshot.currentStage).toBe(Stage.Build);
    expect(plan.stage).toBe(Stage.Plan);
    expect(plan.tasks.map(task => task.id)).toEqual(['proposal', 'specs', 'design', 'tasks', 'self-review']);
    expect(plan.checks.map(check => check.name)).toEqual(['proposal-complete', 'specs-complete', 'design-complete', 'tasks-valid', 'self-review-passed', 'health:plan']);
    expect(plan.approval).toMatchObject({ status: 'approved', output: { approved: true } });
    expect(snapshot.stageRuns.find(stage => stage.stage === Stage.Build)?.tasks).toHaveLength(1);
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
    run.recordCheckResult(Stage.Check, { name: 'review-passed', status: 'pass' });
    run.recordCheckResult(Stage.Check, { name: 'merge-ready', status: 'pass' });
    run.approveStage(Stage.Check, { output: { approved: true } });
    run.completeTask(Stage.Integrate, 'integrate:spec-sync', { status: 'completed' });
    run.completeTask(Stage.Integrate, 'integrate:archive-change', { status: 'completed' });
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

  it('materializes missing Build tasks from tasks.json definitions without using progress as runtime truth', () => {
    const run = repo.createOrLoadActiveAggregate({ issueId, issueNumber });
    const tasksPath = path.join(tempDir, 'tasks.json');
    fs.writeFileSync(tasksPath, JSON.stringify({
      version: 1,
      tasks: [
        { id: 'T-001', title: 'Persist aggregate', order: 2, passes: true, error: 'ignored old progress' },
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
    expect(build.tasks.every(task => task.status === 'pending' && task.attempts === 0 && task.output === null)).toBe(true);
  });
});
