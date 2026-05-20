import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import fs from 'fs';
import os from 'os';
import path from 'path';
import { DatabaseManager } from '../src/db/database';
import { initializeDatabase } from '../src/db/migrations';
import { IssueRepo } from '../src/db/issue-repo';
import { ProjectRepo } from '../src/db/project-repo';
import { WorkflowRunRepo } from '../src/db/workflow-run-repo';
import { WorkflowRun, compileWorkflowDefinition, createWorkflowDefinitionSnapshot, type WorkflowDefinition } from '../src/workflow/model';
import { DEFAULT_STAGE_DEFINITIONS } from '../src/workflow/definitions/default-workflow';
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

  it('persists the workflow definition snapshot captured at run start', () => {
    const workflowDefinition: WorkflowDefinition = {
      id: 'project/custom-snapshot',
      name: 'Project custom snapshot',
      stages: [
        {
          stage: Stage.Plan,
          tasks: [{ id: 'proposal', title: 'Original proposal task' }],
          checks: [{ name: 'proposal-complete', title: 'Original proposal check' }],
          requiresApproval: false,
        },
        {
          stage: Stage.Build,
          tasks: [{ id: 'T-001', title: 'Original build task' }],
          checks: [],
        },
      ],
    };
    const workflowDefinitionSnapshot = createWorkflowDefinitionSnapshot({
      definition: workflowDefinition,
      source: { type: 'project', path: 'workflow.yaml' },
      capturedAt: '2026-05-19T00:00:00.000Z',
    });
    const { run } = WorkflowRun.startWorkflow({
      id: 'wr_188_custom_snapshot',
      issueId,
      issueNumber,
      workflowDefinitionSnapshot,
    });

    workflowDefinition.stages[0].tasks[0].title = 'Mutated proposal task';
    workflowDefinition.stages[1].tasks.push({ id: 'T-002', title: 'Mutated build task' });
    repo.saveAggregate(run, 'tester');

    const row = db.get<{ workflow_definition: string | null }>(
      'SELECT workflow_definition FROM workflow_runs WHERE id = ?',
      [run.id],
    );
    const loaded = repo.loadAggregateById(run.id)!;
    const snapshot = loaded.snapshot();

    expect(row?.workflow_definition).toContain('project/custom-snapshot');
    expect(snapshot.workflowDefinitionSnapshot.workflowId).toBe('project/custom-snapshot');
    expect(snapshot.workflowDefinitionSnapshot.source).toEqual({ type: 'project', path: 'workflow.yaml' });
    expect(snapshot.workflowDefinitionSnapshot.capturedAt).toBe('2026-05-19T00:00:00.000Z');
    expect(snapshot.stageOrder).toEqual([Stage.Plan, Stage.Build]);
    expect(snapshot.stageRuns.map(stage => stage.stage)).toEqual([Stage.Plan, Stage.Build]);
    expect(snapshot.stageRuns[0].tasks.map(task => task.title)).toEqual(['Original proposal task']);
    expect(snapshot.stageRuns[1].tasks.map(task => task.id)).toEqual(['T-001']);
  });

  it('starts a persisted aggregate from a supplied workflow definition snapshot', () => {
    const workflowDefinition: WorkflowDefinition = {
      id: 'project/custom-start',
      stages: [
        {
          stage: Stage.Plan,
          tasks: [{ id: 'design', title: 'Design', source: 'project', uses: 'mohist/agent', with: { prompt: 'Design this' } }],
          checks: [{ name: 'design-file', title: 'Design file', source: 'project', uses: 'mohist/artifact-exists', with: { path: 'design.md' } }],
          requiresApproval: false,
        },
        {
          stage: Stage.Build,
          tasks: [{ id: 'implement', title: 'Implement', source: 'project', uses: 'mohist/agent', with: { prompt: 'Implement this' } }],
          checks: [],
        },
      ],
    };
    const workflowDefinitionSnapshot = createWorkflowDefinitionSnapshot({
      definition: workflowDefinition,
      source: { type: 'project', path: '.mohist/workflow.yaml' },
      capturedAt: '2026-05-19T01:00:00.000Z',
    });

    const run = repo.createOrLoadActiveAggregate({ issueId, issueNumber, workflowDefinitionSnapshot });
    const snapshot = run.snapshot();

    expect(snapshot.workflowDefinitionSnapshot.workflowId).toBe('project/custom-start');
    expect(snapshot.workflowDefinitionSnapshot.source).toEqual({ type: 'project', path: '.mohist/workflow.yaml' });
    expect(snapshot.stageOrder).toEqual([Stage.Plan, Stage.Build]);
    expect(snapshot.stageRuns[0].tasks.map(task => task.id)).toEqual(['design']);
    expect(snapshot.stageRuns[0].checks.map(check => check.name)).toEqual(['design-file']);
  });

  it('loads an aggregate snapshot with ordered stages, tasks, checks, approval, and delivery metadata', () => {
    const { run } = WorkflowRun.startWorkflow({ id: 'wr_188_snapshot', issueId, issueNumber, definitions: DEFAULT_STAGE_DEFINITIONS });
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
    expect(build.workSourceState).toMatchObject({
      evaluated: true,
      tasks: [{ id: 'T-001', title: 'Build task', order: 0 }],
    });
  });

  it('restores task events so retry can reapply event invalidation after reload', () => {
    const { run } = WorkflowRun.startWorkflow({ id: 'wr_188_events', issueId, issueNumber, definitions: DEFAULT_STAGE_DEFINITIONS });
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
    run.recordCheckResult(Stage.Check, { name: 'review-passed', status: 'fail', message: 'Review failed' });
    run.completeTask(Stage.Check, 'fix-review-findings', { status: 'completed', events: ['code.changed'] });
    run.completeTask(Stage.Check, 'ai-review', { status: 'completed' });
    run.recordCheckResult(Stage.Check, { name: 'health:check', status: 'pass' });
    run.recordCheckResult(Stage.Check, { name: 'review-passed', status: 'pass' });

    repo.saveAggregate(run, 'tester');
    const loaded = repo.loadAggregateById(run.id)!;
    expect(loaded.stageRun(Stage.Check).findTask('fix-review-findings')?.events).toEqual(['code.changed']);

    const checkStage = loaded.stageRun(Stage.Check);
    checkStage.findTask('ai-review').status = 'completed';
    const staleReview = checkStage.findCheck('review-passed');
    staleReview.status = 'failed';
    staleReview.message = 'Review failed again from stale report';
    checkStage.status = 'failed';
    checkStage.failure = {
      reason: 'check-unrepaired',
      stage: Stage.Check,
      checkName: 'review-passed',
      message: 'Review failed again from stale report',
    };
    loaded.status = 'failed';
    loaded.failure = checkStage.failure;

    const retry = loaded.retryStage(Stage.Check);

    expect(retry.events).toContainEqual({
      type: 'task-invalidated',
      stage: Stage.Check,
      taskId: 'ai-review:2',
      reason: 'code.changed reset',
    });
    expect(loaded.stageRun(Stage.Check).findTask('ai-review')?.status).toBe('pending');
  });

  it('saves run, stage, task, check, approval, failure, and freeze changes in one aggregate transaction', () => {
    const { run } = WorkflowRun.startWorkflow({ id: 'wr_188_freeze', issueId, issueNumber, definitions: DEFAULT_STAGE_DEFINITIONS });
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
    expect(snapshot.failure?.reason).toBe('post-delivery-check-failed');
    expect(integrate.failure?.reason).toBe('post-delivery-check-failed');
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

  it('does not read tasks.json into Build when loading aggregates', () => {
    const run = repo.createOrLoadActiveAggregate({ issueId, issueNumber });
    const tasksPath = path.join(tempDir, 'tasks.json');
    fs.writeFileSync(tasksPath, JSON.stringify({
      version: 1,
      tasks: [
        { id: 'T-001', title: 'Persist aggregate', order: 2, passes: true, attempts: 3, durations: [100, 200] },
        { id: 'T-002', title: 'Expose aggregate', order: 1, passes: false },
      ],
    }), 'utf-8');

    repo.loadAggregateById(run.id);
    repo.loadAggregateById(run.id);

    const loaded = repo.loadAggregateById(run.id)!;
    const build = loaded.snapshot().stageRuns.find(stage => stage.stage === Stage.Build)!;
    const taskRows = db.all<{ task_id: string }>(
      `SELECT task_id FROM workflow_tasks WHERE stage_run_id = ? ORDER BY task_order ASC, task_id ASC`,
      [`${run.id}/build`],
    );

    expect(taskRows).toEqual([]);
    expect(build.tasks).toEqual([]);
    expect(build.workSourceState).toMatchObject({ evaluated: false });
  });

  it('persists generic work source state for non-Build stages', () => {
    const definitions = compileWorkflowDefinition({
      id: 'repo/custom-check-dynamic-source',
      stages: DEFAULT_STAGE_DEFINITIONS.map(definition => definition.stage === Stage.Check
        ? {
          stage: Stage.Check,
          tasks: [],
          tasksFrom: 'mohist/ralph-tasks',
          checks: [{ name: 'custom-check', title: 'Custom check', uses: 'mohist/health-gate' }],
        }
        : definition),
    });
    const { run } = WorkflowRun.startWorkflow({ id: 'wr_188_check_source', issueId, issueNumber, definitions });
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
    run.materializeTasks(Stage.Check, [], 'missing');

    repo.saveAggregate(run, 'tester');
    const loaded = repo.loadAggregateById(run.id)!;
    const check = loaded.snapshot().stageRuns.find(stage => stage.stage === Stage.Check)!;
    const row = db.get<{ work_source_state: string | null; build_work_source_state: string | null }>(
      `SELECT work_source_state, build_work_source_state FROM workflow_stage_runs WHERE id = ?`,
      [`${run.id}/check`],
    )!;

    expect(check.workSourceState).toMatchObject({ evaluated: true, missing: true });
    expect(row.work_source_state).toContain('"missing":true');
    expect(row.build_work_source_state).toBeNull();
  });

  it('keeps rerun-cleared Build task identities out until workflow materializes them', () => {
    const { run } = WorkflowRun.startWorkflow({ id: 'wr_188_build_rerun', issueId, issueNumber, definitions: DEFAULT_STAGE_DEFINITIONS });
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

    const loaded = repo.loadAggregateById(run.id)!;
    const build = loaded.snapshot().stageRuns.find(stage => stage.stage === Stage.Build)!;

    expect(build.tasks).toEqual([]);
    expect(build.workSourceState).toMatchObject({ evaluated: false });
    expect(loaded.nextWork()).toMatchObject({ kind: 'blocked', stage: Stage.Build, reason: { reason: 'dynamic-source-not-evaluated' } });
  });

  it('persists removal of generated repair tasks when rerunning a stage', () => {
    const { run } = WorkflowRun.startWorkflow({ id: 'wr_188_rerun', issueId, issueNumber, definitions: DEFAULT_STAGE_DEFINITIONS });
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

  it('persists workflow-policy task reset provenance without treating it as repair cause', () => {
    const { run } = WorkflowRun.startWorkflow({ id: 'wr_188_reset_provenance', issueId, issueNumber, definitions: DEFAULT_STAGE_DEFINITIONS });
    for (const taskId of ['proposal', 'specs', 'design', 'tasks', 'self-review']) {
      run.completeTask(Stage.Plan, taskId, { status: 'completed' });
    }
    for (const checkName of ['proposal-complete', 'specs-complete', 'design-complete', 'tasks-valid', 'self-review-passed', 'health:plan']) {
      run.recordCheckResult(Stage.Plan, { name: checkName, status: 'pass' });
    }
    run.approveStage(Stage.Plan);
    run.materializeTasks(Stage.Build, [{ id: 'T-001', title: 'Build task', order: 0 }]);
    run.completeTask(Stage.Build, 'T-001', { status: 'completed' });
    run.recordCheckResult(Stage.Build, { name: 'health:build', status: 'pass' });
    run.completeTask(Stage.Check, 'ai-review', { status: 'completed', output: { previous: true }, artifacts: ['review.md'] });
    run.recordCheckResult(Stage.Check, { name: 'health:check', status: 'pass' });
    run.recordCheckResult(Stage.Check, { name: 'review-passed', status: 'fail', output: { verdict: 'FAIL' } });
    run.completeTask(Stage.Check, 'fix-review-findings', { status: 'completed', events: ['code.changed'] });

    repo.saveAggregate(run, 'tester');

    const row = db.get<{
      caused_by_type: string | null;
      reset_by_type: string | null;
      reset_by_task_id: string | null;
      reset_by_event_name: string | null;
      reset_reason: string | null;
    }>(
      `SELECT caused_by_type, reset_by_type, reset_by_task_id, reset_by_event_name, reset_reason
       FROM workflow_tasks WHERE stage_run_id = ? AND task_id = ?`,
      [`${run.id}/check`, 'ai-review:1'],
    )!;
    const loaded = repo.loadAggregateById(run.id)!;
    const aiReview = loaded.stageRun(Stage.Check).findTask('ai-review');

    expect(row).toEqual({
      caused_by_type: null,
      reset_by_type: 'workflow-policy',
      reset_by_task_id: 'fix-review-findings',
      reset_by_event_name: 'code.changed',
      reset_reason: 'code.changed reset',
    });
    expect(aiReview).toMatchObject({
      status: 'pending',
      causedBy: null,
      resetBy: {
        type: 'workflow-policy',
        taskId: 'fix-review-findings',
        eventName: 'code.changed',
        message: 'code.changed reset',
      },
    });
    expect(loaded.workflowRecoverySummary()).toBe('running');
    expect(loaded.nextWork()).toEqual({ kind: 'task', stage: Stage.Check, taskId: 'ai-review:1' });
  });

  it('keeps workflow-policy reset provenance after starting the fresh attempt', () => {
    const { run } = WorkflowRun.startWorkflow({ id: 'wr_188_reset_start_attempt', issueId, issueNumber, definitions: DEFAULT_STAGE_DEFINITIONS });
    for (const taskId of ['proposal', 'specs', 'design', 'tasks', 'self-review']) {
      run.completeTask(Stage.Plan, taskId, { status: 'completed' });
    }
    for (const checkName of ['proposal-complete', 'specs-complete', 'design-complete', 'tasks-valid', 'self-review-passed', 'health:plan']) {
      run.recordCheckResult(Stage.Plan, { name: checkName, status: 'pass' });
    }
    run.approveStage(Stage.Plan);
    run.materializeTasks(Stage.Build, [{ id: 'T-001', title: 'Build task', order: 0 }]);
    run.completeTask(Stage.Build, 'T-001', { status: 'completed' });
    run.recordCheckResult(Stage.Build, { name: 'health:build', status: 'pass' });
    run.completeTask(Stage.Check, 'ai-review', { status: 'completed', output: { previous: true }, artifacts: ['review.md'] });
    run.recordCheckResult(Stage.Check, { name: 'health:check', status: 'pass' });
    run.recordCheckResult(Stage.Check, { name: 'review-passed', status: 'fail', output: { verdict: 'FAIL' } });
    run.completeTask(Stage.Check, 'fix-review-findings', { status: 'completed', events: ['code.changed'] });
    run.startTaskAttempt(Stage.Check, 'ai-review', '2026-05-20T00:00:00.000Z', { executionId: 'check-188-ai-review-1' });

    repo.saveAggregate(run, 'tester');

    const loaded = repo.loadAggregateById(run.id)!;
    const aiReview = loaded.stageRun(Stage.Check).findTask('ai-review');

    expect(aiReview).toMatchObject({
      status: 'running',
      resetBy: {
        type: 'workflow-policy',
        taskId: 'fix-review-findings',
        eventName: 'code.changed',
        message: 'code.changed reset',
      },
      latestAttempt: {
        state: 'running',
        executionId: 'check-188-ai-review-1',
      },
    });
    expect(loaded.workflowRecoverySummary()).toBe('running');
  });
});
