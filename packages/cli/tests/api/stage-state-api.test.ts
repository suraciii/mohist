import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import http from 'node:http';
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
import { createIssueRoutes } from '../../src/api/issues';
import { Stage } from '../../src/types';

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

describe('GET /api/issues/:number/stage-state', () => {
  let db: DatabaseManager;
  let stateManager: StateManager;
  let projectService: ProjectService;
  let issueService: IssueService;
  let stageStateService: StageStateService;
  let server: http.Server;
  let savedApiKeys: Record<string, string | undefined> = {};

  beforeEach(() => {
    savedApiKeys = {};
    for (const key of Object.keys(process.env)) {
      if (key.endsWith('_API_KEY')) {
        savedApiKeys[key] = process.env[key];
        delete process.env[key];
      }
    }

    db = new DatabaseManager({ inMemory: true });
    stateManager = new StateManager(db);

    const configRepo = stateManager.getConfigRepo();
    const projectRepo = stateManager.getProjectRepo();
    const issueRepo = stateManager.getIssueRepo();
    const commentRepo = stateManager.getCommentRepo();
    const labelRepo = stateManager.getLabelRepo();

    projectService = new ProjectService(projectRepo, configRepo, issueRepo, labelRepo);
    issueService = new IssueService(issueRepo, commentRepo);
    stageStateService = new StageStateService(db);
  });

  afterEach(() => {
    server?.close();
    db.close();
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
    ));
    return createTestServer(app);
  }

  it('returns 404 for a non-existent issue', async () => {
    const project = await projectService.create({ name: 'Test', path: '/tmp/test' });
    projectService.setCurrent(project);

    server = createApp();

    const response = await request(server).get('/api/issues/9999/stage-state');

    expect(response.status).toBe(404);
    expect(response.body.success).toBe(false);
    expect(response.body.error).toContain('not found');
  });

  it('returns empty stages array when no stage state exists', async () => {
    const project = await projectService.create({ name: 'Test', path: '/tmp/test' });
    projectService.setCurrent(project);
    const issue = await issueService.create({ projectId: project.id, title: 'Test Issue' });

    server = createApp();

    const response = await request(server).get(`/api/issues/${issue.number}/stage-state`);

    expect(response.status).toBe(200);
    expect(response.body.success).toBe(true);
    expect(response.body.data.issueId).toBe(issue.id);
    expect(response.body.data.issueNumber).toBe(issue.number);
    expect(response.body.data.stages).toEqual([]);
  });

  it('returns normalized stage state with tasks, checks, approval, and updatedAt', async () => {
    const project = await projectService.create({ name: 'Test', path: '/tmp/test' });
    projectService.setCurrent(project);
    const issue = await issueService.create({ projectId: project.id, title: 'Test Issue' });

    stageStateService.ensureStage(issue.id, Stage.Plan);
    stageStateService.upsertTask(issue.id, Stage.Plan, {
      taskId: 'read-context',
      title: 'Read context files',
      status: 'completed',
      source: 'static',
      order: 1,
      attempts: 1,
      duration: 5000,
    });

    server = createApp();

    const response = await request(server).get(`/api/issues/${issue.number}/stage-state`);

    expect(response.status).toBe(200);
    expect(response.body.success).toBe(true);

    const data = response.body.data;
    expect(data.issueId).toBe(issue.id);
    expect(data.issueNumber).toBe(issue.number);
    expect(data.stages.length).toBe(1);

    const planStage = data.stages[0];
    expect(planStage.stage).toBe('plan');
    expect(planStage.status).toBe('pending');
    expect(planStage.updatedAt).toBeTruthy();
    expect(Array.isArray(planStage.tasks)).toBe(true);
    expect(Array.isArray(planStage.checks)).toBe(true);
    expect(planStage.approval).toBeNull();

    const readContextTask = planStage.tasks.find((t: any) => t.taskId === 'read-context');
    expect(readContextTask).toBeDefined();
    expect(readContextTask.status).toBe('completed');
    expect(readContextTask.source).toBe('static');
    expect(readContextTask.order).toBe(1);
    expect(readContextTask.attempts).toBe(1);
    expect(readContextTask.duration).toBe(5000);
    expect(readContextTask.updatedAt).toBeTruthy();
  });

  it('returns normalized check statuses without exposing pass/fail variants', async () => {
    const project = await projectService.create({ name: 'Test', path: '/tmp/test' });
    projectService.setCurrent(project);
    const issue = await issueService.create({ projectId: project.id, title: 'Test Issue' });

    stageStateService.ensureStage(issue.id, Stage.Check);
    stageStateService.upsertCheck(issue.id, Stage.Check, {
      checkName: 'ai-review',
      status: 'passed',
      message: 'All good',
    });
    stageStateService.upsertCheck(issue.id, Stage.Check, {
      checkName: 'build-test',
      status: 'failed',
      message: 'Tests failed',
    });

    server = createApp();

    const response = await request(server).get(`/api/issues/${issue.number}/stage-state`);

    expect(response.status).toBe(200);
    const checkStage = response.body.data.stages.find((s: any) => s.stage === 'check');
    expect(checkStage).toBeDefined();

    const aiReviewCheck = checkStage.checks.find((c: any) => c.checkName === 'ai-review');
    expect(aiReviewCheck.status).toBe('passed');
    expect(aiReviewCheck.message).toBe('All good');
    expect(aiReviewCheck.updatedAt).toBeTruthy();

    const buildTestCheck = checkStage.checks.find((c: any) => c.checkName === 'build-test');
    expect(buildTestCheck.status).toBe('failed');
    expect(buildTestCheck.message).toBe('Tests failed');
  });

  it('returns approval state when set', async () => {
    const project = await projectService.create({ name: 'Test', path: '/tmp/test' });
    projectService.setCurrent(project);
    const issue = await issueService.create({ projectId: project.id, title: 'Test Issue' });

    stageStateService.ensureStage(issue.id, Stage.Plan);
    stageStateService.setApproval(issue.id, Stage.Plan, {
      status: 'awaiting',
      requestedAt: '2026-01-01T00:00:00.000Z',
    });

    server = createApp();

    const response = await request(server).get(`/api/issues/${issue.number}/stage-state`);

    expect(response.status).toBe(200);
    const planStage = response.body.data.stages[0];
    expect(planStage.approval).not.toBeNull();
    expect(planStage.approval.status).toBe('awaiting');
    expect(planStage.approval.requestedAt).toBe('2026-01-01T00:00:00.000Z');
  });

  it('returns multiple stages for an issue', async () => {
    const project = await projectService.create({ name: 'Test', path: '/tmp/test' });
    projectService.setCurrent(project);
    const issue = await issueService.create({ projectId: project.id, title: 'Test Issue' });

    stageStateService.ensureStage(issue.id, Stage.Plan);
    stageStateService.ensureStage(issue.id, Stage.Build);

    server = createApp();

    const response = await request(server).get(`/api/issues/${issue.number}/stage-state`);

    expect(response.status).toBe(200);
    expect(response.body.data.stages.length).toBe(2);
    const stages = response.body.data.stages.map((s: any) => s.stage);
    expect(stages).toContain('plan');
    expect(stages).toContain('build');
  });

  it('returns 500 when StageStateService is not configured', async () => {
    const project = await projectService.create({ name: 'Test', path: '/tmp/test' });
    projectService.setCurrent(project);
    const issue = await issueService.create({ projectId: project.id, title: 'Test Issue' });

    const app = new Hono();
    app.route('/api/issues', createIssueRoutes(
      issueService,
      projectService,
      stateManager,
    ));
    server = createTestServer(app);

    const response = await request(server).get(`/api/issues/${issue.number}/stage-state`);

    expect(response.status).toBe(500);
    expect(response.body.success).toBe(false);
    expect(response.body.error).toContain('StageStateService');
  });

  it('does not expose tasks.json passes field or CheckResult pass/fail variants', async () => {
    const project = await projectService.create({ name: 'Test', path: '/tmp/test' });
    projectService.setCurrent(project);
    const issue = await issueService.create({ projectId: project.id, title: 'Test Issue' });

    stageStateService.ensureStage(issue.id, Stage.Plan);

    server = createApp();

    const response = await request(server).get(`/api/issues/${issue.number}/stage-state`);

    expect(response.status).toBe(200);
    const planStage = response.body.data.stages[0];

    for (const task of planStage.tasks) {
      expect(task).not.toHaveProperty('passes');
      expect(typeof task.status).toBe('string');
      expect(['pending', 'running', 'completed', 'failed', 'skipped']).toContain(task.status);
    }

    for (const check of planStage.checks) {
      expect(check).not.toHaveProperty('pass');
      expect(check).not.toHaveProperty('fail');
      expect(typeof check.status).toBe('string');
      expect(['pending', 'running', 'passed', 'failed', 'error']).toContain(check.status);
    }
  });
});
