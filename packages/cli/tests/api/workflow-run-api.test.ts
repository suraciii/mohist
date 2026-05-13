import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import http from 'node:http';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { Hono } from 'hono';
import request from 'supertest';
import { DatabaseManager } from '../../src/db/database';
import { initializeDatabase } from '../../src/db/migrations';
import { ProjectRepo } from '../../src/db/project-repo';
import { IssueRepo } from '../../src/db/issue-repo';
import { ConfigRepo } from '../../src/db/config-repo';
import { CommentRepo } from '../../src/db/comment-repo';
import { LabelRepo } from '../../src/db/label-repo';
import { ProjectService } from '../../src/services/project-service';
import { IssueService } from '../../src/services/issue-service';
import { StateManager } from '../../src/server/state-manager';
import { StageStateService } from '../../src/services/stage-state-service';
import { WorkflowRunService } from '../../src/services/workflow-run-service';
import { createIssueRoutes } from '../../src/api/issues';
import { Stage } from '../../src/types';
import type { WorkflowRunWithStageRuns } from '../../src/db/workflow-run-repo';

function createTestServer(app: Hono): http.Server {
  return http.createServer(async (req, res) => {
    const chunks: Buffer[] = [];
    for await (const chunk of req) chunks.push(chunk);
    const bodyStr = chunks.length > 0 ? Buffer.concat(chunks).toString() : undefined;
    const initHeaders: Record<string, string> = {};
    for (const [key, value] of Object.entries(req.headers)) {
      if (typeof value === 'string') initHeaders[key] = value;
      else if (Array.isArray(value)) initHeaders[key] = value.join(', ');
    }
    const response = await app.fetch(new Request(`http://localhost${req.url}`, {
      method: req.method,
      headers: initHeaders,
      body: bodyStr,
    }));
    res.writeHead(response.status, Object.fromEntries(response.headers.entries()));
    if (response.body) {
      const reader = response.body.getReader();
      while (true) {
        const { done, value } = await reader.read();
        if (done) break;
        res.write(Buffer.from(value));
      }
    }
    res.end();
  });
}

describe('GET /api/issues/:number/workflow-run', () => {
  let db: DatabaseManager;
  let stateManager: StateManager;
  let projectService: ProjectService;
  let issueService: IssueService;
  let workflowRunService: WorkflowRunService;
  let stageStateService: StageStateService;
  let server: http.Server;
  let savedApiKeys: Record<string, string | undefined> = {};
  let tempDirs: string[] = [];

  beforeEach(() => {
    savedApiKeys = {};
    for (const key of Object.keys(process.env)) {
      if (key.endsWith('_API_KEY')) {
        savedApiKeys[key] = process.env[key];
        delete process.env[key];
      }
    }

    db = new DatabaseManager({ inMemory: true });
    initializeDatabase(db);
    stateManager = new StateManager(db);

    const configRepo = stateManager.getConfigRepo();
    const projectRepo = stateManager.getProjectRepo();
    const issueRepo = stateManager.getIssueRepo();
    const commentRepo = stateManager.getCommentRepo();
    const labelRepo = stateManager.getLabelRepo();

    projectService = new ProjectService(projectRepo, configRepo, issueRepo, labelRepo);
    issueService = new IssueService(issueRepo, commentRepo);
    stageStateService = new StageStateService(db);
    workflowRunService = new WorkflowRunService(db);
  });

  afterEach(() => {
    server?.close();
    db.close();
    for (const dir of tempDirs) {
      fs.rmSync(dir, { recursive: true, force: true });
    }
    tempDirs = [];
    for (const [key, val] of Object.entries(savedApiKeys)) {
      if (val === undefined) {
        delete process.env[key];
      } else {
        process.env[key] = val;
      }
    }
  });

  function makeProjectPath(): string {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-workflow-run-'));
    tempDirs.push(dir);
    return dir;
  }

  function createApp(): http.Server {
    const app = new Hono();
    app.route('/api/issues', createIssueRoutes(
      issueService,
      projectService,
      stateManager,
      undefined,
      undefined,
      undefined,
      undefined,
      undefined,
      undefined,
      undefined,
      undefined,
      undefined,
      undefined,
      undefined,
      undefined,
      undefined,
      stageStateService,
      workflowRunService,
    ));
    return createTestServer(app);
  }

  function insertWorkflowTask(run: WorkflowRunWithStageRuns, stage: Stage, input: {
    taskId: string;
    title: string;
    status?: string;
    reason?: string | null;
    causedByType?: string | null;
    causedByCheckName?: string | null;
  }): void {
    const stageRun = run.stageRuns.find(candidate => candidate.stage === stage)!;
    const now = new Date().toISOString();
    db.run(
      `INSERT INTO workflow_tasks
       (id, workflow_run_id, stage_run_id, task_id, title, status, task_order, attempts, duration, artifacts, output,
        reason, caused_by_type, caused_by_check_name, caused_by_task_id, created_at, updated_at)
       VALUES (?, ?, ?, ?, ?, ?, ?, 0, 0, '[]', NULL, ?, ?, ?, NULL, ?, ?)`,
      [
        `${stageRun.id}/${input.taskId}`,
        run.id,
        stageRun.id,
        input.taskId,
        input.title,
        input.status ?? 'pending',
        stageRun.tasks.length,
        input.reason ?? null,
        input.causedByType ?? null,
        input.causedByCheckName ?? null,
        now,
        now,
      ],
    );
  }

  function setWorkflowApproval(run: WorkflowRunWithStageRuns, stage: Stage, input: {
    status: string;
    output: unknown;
    requestedAt: string;
  }): void {
    const stageRun = run.stageRuns.find(candidate => candidate.stage === stage)!;
    db.run(
      `UPDATE workflow_stage_runs
       SET approval_status = ?, approval_output = ?, approval_requested_at = ?, updated_at = ?
       WHERE id = ?`,
      [input.status, JSON.stringify(input.output), input.requestedAt, new Date().toISOString(), stageRun.id],
    );
  }

  it('returns 404 when no WorkflowRun exists', async () => {
    const project = await projectService.create({ name: 'Test', path: '/tmp/test' });
    projectService.setCurrent(project);
    const issue = await issueService.create({ projectId: project.id, title: 'Test Issue' });

    server = createApp();

    const response = await request(server).get(`/api/issues/${issue.number}/workflow-run`);

    expect(response.status).toBe(404);
    expect(response.body.success).toBe(false);
  });

  it('returns ordered StageRuns with tasks, checks, and approval snapshots', async () => {
    const project = await projectService.create({ name: 'Test', path: '/tmp/test' });
    projectService.setCurrent(project);
    const issue = await issueService.create({ projectId: project.id, title: 'Test Issue' });

    const seededRun = workflowRunService.startRun(issue.id, issue.number);
    setWorkflowApproval(seededRun, Stage.Plan, {
      status: 'awaiting',
      output: { result: 'PASS' },
      requestedAt: '2026-01-01T00:00:00.000Z',
    });

    const currentRun = workflowRunService.getActiveRunForIssue(issue.id)!;

    server = createApp();

    const response = await request(server).get(`/api/issues/${issue.number}/workflow-run`);

    expect(response.status).toBe(200);
    expect(response.body.success).toBe(true);

    const data = response.body.data;
    expect(data.issueId).toBe(issue.id);
    expect(data.issueNumber).toBe(issue.number);
    expect(data.id).toBe(currentRun.id);
    expect(data.status).toBe('running');
    expect(data.currentStage).toBe('plan');

    expect(data.stageRuns).toHaveLength(4);
    expect(data.stageRuns[0].stage).toBe('plan');
    expect(data.stageRuns[0].status).toBe('running');
    expect(data.stageRuns[1].stage).toBe('build');
    expect(data.stageRuns[2].stage).toBe('check');
    expect(data.stageRuns[3].stage).toBe('integrate');

    const planStageRun = data.stageRuns[0];
    expect(planStageRun.tasks.length).toBe(5);
    expect(planStageRun.checks.length).toBe(6);
    expect(planStageRun.approval).not.toBeNull();
    if (planStageRun.approval) {
      expect(planStageRun.approval.status).toBe('awaiting');
    }

    const taskIds = planStageRun.tasks.map((t: any) => t.taskId);
    expect(taskIds).toContain('proposal');
    expect(taskIds).toContain('specs');
    expect(taskIds).toContain('design');
    expect(taskIds).toContain('tasks');
    expect(taskIds).toContain('self-review');

    const checkNames = planStageRun.checks.map((c: any) => c.checkName);
    expect(checkNames).toContain('proposal-complete');
    expect(checkNames).toContain('specs-complete');
    expect(checkNames).toContain('design-complete');
    expect(checkNames).toContain('tasks-valid');
    expect(checkNames).toContain('self-review-passed');
  });

  it('Build tasks materialize from tasks.json into the same WorkflowRun', async () => {
    const projectPath = makeProjectPath();
    const project = await projectService.create({ name: 'Test', path: projectPath });
    projectService.setCurrent(project);
    const issue = await issueService.create({ projectId: project.id, title: 'Test Issue' });

    const changeDir = path.join(projectPath, 'openspec', 'changes', `${issue.number}-test-issue`);
    fs.mkdirSync(changeDir, { recursive: true });
    fs.writeFileSync(path.join(changeDir, 'tasks.json'), JSON.stringify({
      version: 1,
      tasks: [
        { id: 'T-001', order: 1, title: 'Add persistence', description: 'Persist stage state', passes: true, attempts: 1 },
        { id: 'T-002', order: 2, title: 'Expose endpoint', description: 'Serve stage state', passes: false, attempts: 1, error: 'TypeScript build failed' },
      ],
    }));

    const run = workflowRunService.startRun(issue.id, issue.number);
    insertWorkflowTask(run, Stage.Build, { taskId: 'T-001', title: 'Add persistence' });
    insertWorkflowTask(run, Stage.Build, { taskId: 'T-002', title: 'Expose endpoint' });

    server = createApp();

    const response = await request(server).get(`/api/issues/${issue.number}/workflow-run`);

    expect(response.status).toBe(200);
    const buildStageRun = response.body.data.stageRuns.find((s: any) => s.stage === 'build');

    expect(buildStageRun.tasks).toEqual(expect.arrayContaining([
      expect.objectContaining({ taskId: 'T-001', title: 'Add persistence', status: 'pending' }),
      expect.objectContaining({ taskId: 'T-002', title: 'Expose endpoint', status: 'pending' }),
    ]));
  });

  it('does not reconstruct WorkflowRun from stage_executions, workflow_log, or checkpoints', async () => {
    const project = await projectService.create({ name: 'Test', path: '/tmp/test' });
    projectService.setCurrent(project);
    const issue = await issueService.create({ projectId: project.id, title: 'Test Issue' });

    workflowRunService.startRun(issue.id, issue.number);

    const now = new Date().toISOString();
    db.run(
      `INSERT INTO stage_executions (id, issue_id, stage, status, task_results, check_results, created_at, updated_at)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?)`,
      [
        'exec-fake',
        issue.id,
        Stage.Build,
        'failed',
        JSON.stringify([{ taskId: 'T-FAKE', title: 'Fake from execution', status: 'completed', artifacts: [], attempts: 1, duration: 100 }]),
        JSON.stringify([{ name: 'fake-check', status: 'pass', message: 'Fake check' }]),
        now,
        now,
      ],
    );
    db.run(
      `INSERT INTO workflow_log (id, issue_id, session_id, event_type, data, created_at)
       VALUES (?, ?, ?, ?, ?, ?)`,
      [
        'log-fake',
        issue.id,
        null,
        'task_completed',
        JSON.stringify({ taskId: 'T-FAKE-LOG', title: 'Fake from log', status: 'completed' }),
        now,
      ],
    );
    db.run(
      `INSERT INTO pipeline_checkpoint (issue_number, stage, completed_steps, next_step, updated_at)
       VALUES (?, ?, ?, ?, ?)`,
      [
        issue.number,
        'build',
        JSON.stringify([{ step: 'task', index: 99 }]),
        null,
        now,
      ],
    );

    server = createApp();

    const response = await request(server).get(`/api/issues/${issue.number}/workflow-run`);

    expect(response.status).toBe(200);
    const buildStageRun = response.body.data.stageRuns.find((s: any) => s.stage === 'build');

    const hasFakeFromExecution = buildStageRun.tasks.some((t: any) => t.taskId === 'T-FAKE');
    const hasFakeFromLog = buildStageRun.tasks.some((t: any) => t.taskId === 'T-FAKE-LOG');
    expect(hasFakeFromExecution).toBe(false);
    expect(hasFakeFromLog).toBe(false);
    expect(buildStageRun.tasks.length).toBe(0);
  });
});

describe('GET /api/issues/:number/stage-state backed by WorkflowRun', () => {
  let db: DatabaseManager;
  let stateManager: StateManager;
  let projectService: ProjectService;
  let issueService: IssueService;
  let workflowRunService: WorkflowRunService;
  let stageStateService: StageStateService;
  let server: http.Server;
  let savedApiKeys: Record<string, string | undefined> = {};
  let tempDirs: string[] = [];

  beforeEach(() => {
    savedApiKeys = {};
    for (const key of Object.keys(process.env)) {
      if (key.endsWith('_API_KEY')) {
        savedApiKeys[key] = process.env[key];
        delete process.env[key];
      }
    }

    db = new DatabaseManager({ inMemory: true });
    initializeDatabase(db);
    stateManager = new StateManager(db);

    const configRepo = stateManager.getConfigRepo();
    const projectRepo = stateManager.getProjectRepo();
    const issueRepo = stateManager.getIssueRepo();
    const commentRepo = stateManager.getCommentRepo();
    const labelRepo = stateManager.getLabelRepo();

    projectService = new ProjectService(projectRepo, configRepo, issueRepo, labelRepo);
    issueService = new IssueService(issueRepo, commentRepo);
    stageStateService = new StageStateService(db);
    workflowRunService = new WorkflowRunService(db);
  });

  afterEach(() => {
    server?.close();
    db.close();
    for (const dir of tempDirs) {
      fs.rmSync(dir, { recursive: true, force: true });
    }
    tempDirs = [];
    for (const [key, val] of Object.entries(savedApiKeys)) {
      if (val === undefined) {
        delete process.env[key];
      } else {
        process.env[key] = val;
      }
    }
  });

  function createApp(): http.Server {
    const app = new Hono();
    app.route('/api/issues', createIssueRoutes(
      issueService,
      projectService,
      stateManager,
      undefined,
      undefined,
      undefined,
      undefined,
      undefined,
      undefined,
      undefined,
      undefined,
      undefined,
      undefined,
      undefined,
      undefined,
      undefined,
      stageStateService,
      workflowRunService,
    ));
    return createTestServer(app);
  }

  function insertWorkflowTask(run: WorkflowRunWithStageRuns, stage: Stage, input: {
    taskId: string;
    title: string;
    status?: string;
    reason?: string | null;
    causedByType?: string | null;
    causedByCheckName?: string | null;
  }): void {
    const stageRun = run.stageRuns.find(candidate => candidate.stage === stage)!;
    const now = new Date().toISOString();
    db.run(
      `INSERT INTO workflow_tasks
       (id, workflow_run_id, stage_run_id, task_id, title, status, task_order, attempts, duration, artifacts, output,
        reason, caused_by_type, caused_by_check_name, caused_by_task_id, created_at, updated_at)
       VALUES (?, ?, ?, ?, ?, ?, ?, 0, 0, '[]', NULL, ?, ?, ?, NULL, ?, ?)`,
      [
        `${stageRun.id}/${input.taskId}`,
        run.id,
        stageRun.id,
        input.taskId,
        input.title,
        input.status ?? 'pending',
        stageRun.tasks.length,
        input.reason ?? null,
        input.causedByType ?? null,
        input.causedByCheckName ?? null,
        now,
        now,
      ],
    );
  }

  it('stage-state API returns WorkflowRun-backed data when WorkflowRun exists', async () => {
    const project = await projectService.create({ name: 'Test', path: '/tmp/test' });
    projectService.setCurrent(project);
    const issue = await issueService.create({ projectId: project.id, title: 'Test Issue' });

    workflowRunService.startRun(issue.id, issue.number);

    server = createApp();

    const response = await request(server).get(`/api/issues/${issue.number}/stage-state`);

    expect(response.status).toBe(200);
    expect(response.body.success).toBe(true);

    const data = response.body.data;
    expect(data.issueId).toBe(issue.id);
    expect(data.stages.length).toBe(4);

    const planStage = data.stages.find((s: any) => s.stage === 'plan');
    expect(planStage).toBeDefined();
    expect(planStage.tasks.length).toBe(5);
    expect(planStage.checks.length).toBe(6);
  });

  it('stage_executions, workflow_log, session logs, and checkpoints are not promoted to tasks/checks', async () => {
    const project = await projectService.create({ name: 'Test', path: '/tmp/test' });
    projectService.setCurrent(project);
    const issue = await issueService.create({ projectId: project.id, title: 'Test Issue' });

    workflowRunService.startRun(issue.id, issue.number);

    const now = new Date().toISOString();
    db.run(
      `INSERT INTO stage_executions (id, issue_id, stage, status, task_results, check_results, created_at, updated_at)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?)`,
      [
        'exec-only',
        issue.id,
        Stage.Check,
        'completed',
        JSON.stringify([{ taskId: 'T-EVIDENCE', title: 'Evidence task', status: 'completed', artifacts: [], attempts: 1, duration: 100 }]),
        JSON.stringify([{ name: 'check-evidence', status: 'pass', message: 'Evidence check' }]),
        now,
        now,
      ],
    );
    db.run(
      `INSERT INTO workflow_log (id, issue_id, session_id, event_type, data, created_at)
       VALUES (?, ?, ?, ?, ?, ?)`,
      [
        'log-only',
        issue.id,
        null,
        'task_completed',
        JSON.stringify({ taskId: 'T-LOG', title: 'Log task', status: 'completed' }),
        now,
      ],
    );

    server = createApp();

    const response = await request(server).get(`/api/issues/${issue.number}/stage-state`);

    expect(response.status).toBe(200);
    const checkStage = response.body.data.stages.find((s: any) => s.stage === 'check');

    const hasEvidenceTask = checkStage.tasks.some((t: any) => t.taskId === 'T-EVIDENCE');
    const hasLogTask = checkStage.tasks.some((t: any) => t.taskId === 'T-LOG');
    const hasEvidenceCheck = checkStage.checks.some((c: any) => c.checkName === 'check-evidence');
    expect(hasEvidenceTask).toBe(false);
    expect(hasLogTask).toBe(false);
    expect(hasEvidenceCheck).toBe(false);
  });

  it('runtime-added repair/rebase/retry/conflict tasks appear in stage-state task list', async () => {
    const project = await projectService.create({ name: 'Test', path: '/tmp/test' });
    projectService.setCurrent(project);
    const issue = await issueService.create({ projectId: project.id, title: 'Test Issue' });

    const run = workflowRunService.startRun(issue.id, issue.number);
    insertWorkflowTask(run, Stage.Check, {
      taskId: 'fix-review-findings',
      title: 'Fix review findings',
      status: 'completed',
      reason: 'Added after review passed failed',
      causedByType: 'check-failure',
      causedByCheckName: 'ai-review',
    });
    insertWorkflowTask(run, Stage.Integrate, {
      taskId: 'rebase-branch',
      title: 'Rebase branch',
      status: 'pending',
      reason: 'Added because target branch moved',
      causedByType: 'branch-changed',
    });

    server = createApp();

    const response = await request(server).get(`/api/issues/${issue.number}/stage-state`);

    expect(response.status).toBe(200);
    const checkStage = response.body.data.stages.find((s: any) => s.stage === 'check');
    const integrateStage = response.body.data.stages.find((s: any) => s.stage === 'integrate');

    const fixTask = checkStage.tasks.find((t: any) => t.taskId === 'fix-review-findings');
    expect(fixTask).toBeDefined();
    expect(fixTask.reason).toBe('Added after review passed failed');
    expect(fixTask.causedBy?.type).toBe('check-failure');

    const rebaseTask = integrateStage.tasks.find((t: any) => t.taskId === 'rebase-branch');
    expect(rebaseTask).toBeDefined();
    expect(rebaseTask.causedBy?.type).toBe('branch-changed');
  });

  it('stage-state ignores newer cancelled WorkflowRun and uses the latest live run', async () => {
    const project = await projectService.create({ name: 'Test', path: '/tmp/test' });
    projectService.setCurrent(project);
    const issue = await issueService.create({ projectId: project.id, title: 'Test Issue' });

    const activeRun = workflowRunService.startRun(issue.id, issue.number);
    const now = new Date().toISOString();
    db.run(
      `UPDATE workflow_runs SET status = 'running', current_stage = ?, updated_at = ? WHERE id = ?`,
      [Stage.Check, now, activeRun.id],
    );
    db.run(
      `UPDATE workflow_stage_runs SET status = 'passed', completed_at = ?, updated_at = ?
       WHERE workflow_run_id = ? AND stage = ?`,
      [now, now, activeRun.id, Stage.Plan],
    );
    db.run(
      `UPDATE workflow_stage_runs SET status = 'running', started_at = ?, completed_at = NULL, updated_at = ?
       WHERE workflow_run_id = ? AND stage = ?`,
      [now, now, activeRun.id, Stage.Check],
    );

    const cancelledRunId = 'wr_cancelled_newer';
    db.run(
      `INSERT INTO workflow_runs (id, issue_id, issue_number, status, current_stage, started_by, created_at, updated_at)
       VALUES (?, ?, ?, 'cancelled', ?, 'test', ?, ?)`,
      [cancelledRunId, issue.id, issue.number, Stage.Plan, '2999-01-01T00:00:00.000Z', now],
    );
    db.run(
      `INSERT INTO workflow_stage_runs (id, workflow_run_id, stage, status, stage_order, approval_status, approval_requested_at, created_at, updated_at)
       VALUES (?, ?, ?, 'awaiting-approval', 0, 'awaiting', ?, ?, ?)`,
      [`${cancelledRunId}/${Stage.Plan}`, cancelledRunId, Stage.Plan, now, now, now],
    );

    server = createApp();

    const response = await request(server).get(`/api/issues/${issue.number}/stage-state`);

    expect(response.status).toBe(200);
    const planStage = response.body.data.stages.find((s: any) => s.stage === Stage.Plan);
    const checkStage = response.body.data.stages.find((s: any) => s.stage === Stage.Check);
    expect(planStage.status).toBe('passed');
    expect(checkStage.status).toBe('running');
  });
});
