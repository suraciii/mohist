import { describe, it, expect, beforeEach, afterEach, vi, beforeAll } from 'vitest';
import http from 'node:http';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import { Hono } from 'hono';
import request from 'supertest';
import { DatabaseManager } from '../src/db/database';
import { ProjectRepo } from '../src/db/project-repo';
import { IssueRepo } from '../src/db/issue-repo';
import { ConfigRepo } from '../src/db/config-repo';
import { ProjectService } from '../src/services/project-service';
import { IssueService } from '../src/services/issue-service';
import { ConfigService } from '../src/services/config-service';
import { EventBus, AgentRunnerService } from '../src/services';
import { StateManager } from '../src/server/state-manager';
import { CommentRepo } from '../src/db/comment-repo';
import { LabelRepo } from '../src/db/label-repo';
import { createIssueRoutes } from '../src/api/issues';
import { Stage, IssueStatus, MergeState } from '../src/types';

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

describe('Recovery routing regression tests', () => {
  let db: DatabaseManager;
  let issueRepo: IssueRepo;
  let projectService: ProjectService;
  let issueService: IssueService;
  let stateManager: StateManager;
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

    const projectRepo = stateManager.getProjectRepo();
    issueRepo = stateManager.getIssueRepo();
    const configRepo = stateManager.getConfigRepo();
    const commentRepo = stateManager.getCommentRepo();
    const labelRepo = stateManager.getLabelRepo();
    projectService = new ProjectService(projectRepo, configRepo, issueRepo, labelRepo);
    issueService = new IssueService(issueRepo, commentRepo);
  });

  afterEach(() => {
    db.close();
    for (const [key, val] of Object.entries(savedApiKeys)) {
      if (val === undefined) {
        delete process.env[key];
      } else {
        process.env[key] = val;
      }
    }
  });

  describe('IssueService.reopen()', () => {
    it('rejects blocked issues', async () => {
      const project = await projectService.create({ name: 'Test Project', path: '/test/path' });
      projectService.setCurrent(project);

      const issue = issueService.create({ projectId: project.id, title: 'Blocked Issue' });
      issueRepo.updateStage(issue.id, Stage.Build);
      issueRepo.updateStatus(issue.id, IssueStatus.Blocked);

      const result = issueService.reopen(project.id, issue.number);

      expect(result).toBeNull();

      const updated = issueRepo.findById(issue.id);
      expect(updated?.status).toBe(IssueStatus.Blocked);
      expect(updated?.stage).toBe(Stage.Build);
    });

    it('rejects paused issues', async () => {
      const project = await projectService.create({ name: 'Test Project', path: '/test/path' });
      projectService.setCurrent(project);

      const issue = issueService.create({ projectId: project.id, title: 'Paused Issue' });
      issueRepo.updateStage(issue.id, Stage.Plan);
      issueRepo.updateStatus(issue.id, IssueStatus.Paused);

      const result = issueService.reopen(project.id, issue.number);

      expect(result).toBeNull();

      const updated = issueRepo.findById(issue.id);
      expect(updated?.status).toBe(IssueStatus.Paused);
    });

    it('rejects interrupted issues', async () => {
      const project = await projectService.create({ name: 'Test Project', path: '/test/path' });
      projectService.setCurrent(project);

      const issue = issueService.create({ projectId: project.id, title: 'Interrupted Issue' });
      issueRepo.updateStage(issue.id, Stage.Build);
      issueRepo.updateStatus(issue.id, IssueStatus.Interrupted);

      const result = issueService.reopen(project.id, issue.number);

      expect(result).toBeNull();

      const updated = issueRepo.findById(issue.id);
      expect(updated?.status).toBe(IssueStatus.Interrupted);
    });

    it('succeeds for closed issues', async () => {
      const project = await projectService.create({ name: 'Test Project', path: '/test/path' });
      projectService.setCurrent(project);

      const issue = issueService.create({ projectId: project.id, title: 'Closed Issue' });
      issueRepo.updateStage(issue.id, Stage.Plan);
      issueRepo.updateStatus(issue.id, IssueStatus.Closed);

      const result = issueService.reopen(project.id, issue.number);

      expect(result).not.toBeNull();
      expect(result?.status).toBe(IssueStatus.Active);
    });
  });

  describe('POST /api/issues/:number/reopen', () => {
    let server: http.Server;

    beforeEach(async () => {
      const app = new Hono();
      const eventBus = new EventBus();
      const agentRunner = new AgentRunnerService(eventBus, undefined, issueRepo, 8, undefined, undefined, undefined, undefined, stateManager.getIssueTaskQueueRepo());
      app.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, undefined, undefined, agentRunner));
      server = createTestServer(app);

      const project = await projectService.create({ name: 'Test Project', path: '/test/path' });
      projectService.setCurrent(project);
    });

    it('reopens a closed issue successfully', async () => {
      const issue = issueService.create({ projectId: projectService.getCurrentId()!, title: 'Closed Issue' });
      issueRepo.updateStatus(issue.id, IssueStatus.Closed);
      issueRepo.updateStage(issue.id, Stage.Plan);

      const response = await request(server).post(`/api/issues/${issue.number}/reopen`);

      expect(response.status).toBe(200);
      expect(response.body.success).toBe(true);
      expect(response.body.data.issue.status).toBe(IssueStatus.Active);
    });

    it('returns 404 when issue is not reopenable (blocked)', async () => {
      const issue = issueService.create({ projectId: projectService.getCurrentId()!, title: 'Blocked Issue' });
      issueRepo.updateStatus(issue.id, IssueStatus.Blocked);
      issueRepo.updateStage(issue.id, Stage.Build);

      const response = await request(server).post(`/api/issues/${issue.number}/reopen`);

      expect(response.status).toBe(404);
      expect(response.body.error).toContain('not reopenable');
    });

    it('returns 404 when issue is not reopenable (paused)', async () => {
      const issue = issueService.create({ projectId: projectService.getCurrentId()!, title: 'Paused Issue' });
      issueRepo.updateStatus(issue.id, IssueStatus.Paused);
      issueRepo.updateStage(issue.id, Stage.Plan);

      const response = await request(server).post(`/api/issues/${issue.number}/reopen`);

      expect(response.status).toBe(404);
      expect(response.body.error).toContain('not reopenable');
    });

    it('returns 404 when issue is not reopenable (interrupted)', async () => {
      const issue = issueService.create({ projectId: projectService.getCurrentId()!, title: 'Interrupted Issue' });
      issueRepo.updateStatus(issue.id, IssueStatus.Interrupted);
      issueRepo.updateStage(issue.id, Stage.Build);

      const response = await request(server).post(`/api/issues/${issue.number}/reopen`);

      expect(response.status).toBe(404);
      expect(response.body.error).toContain('not reopenable');
    });

    it('does not auto-enqueue resume-pipeline for reopened closed issues', async () => {
      const issue = issueService.create({ projectId: projectService.getCurrentId()!, title: 'Closed Issue' });
      issueRepo.updateStatus(issue.id, IssueStatus.Closed);
      issueRepo.updateStage(issue.id, Stage.Plan);

      const enqueueSpy = vi.spyOn(AgentRunnerService.prototype, 'enqueue').mockReturnValue({ taskId: 'fake', status: 'pending' });

      const response = await request(server).post(`/api/issues/${issue.number}/reopen`);

      expect(response.status).toBe(200);
      expect(enqueueSpy).not.toHaveBeenCalled();

      enqueueSpy.mockRestore();
    });
  });

  describe('POST /api/issues/:number/resume', () => {
    let server: http.Server;

    beforeEach(async () => {
      const app = new Hono();
      const eventBus = new EventBus();
      const agentRunner = new AgentRunnerService(eventBus, undefined, issueRepo, 8, undefined, undefined, undefined, undefined, stateManager.getIssueTaskQueueRepo());
      const enqueueSpy = vi.spyOn(agentRunner, 'enqueue').mockReturnValue({ taskId: 'fake', status: 'pending' });
      app.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, undefined, undefined, agentRunner));
      server = createTestServer(app);

      const project = await projectService.create({ name: 'Test Project', path: '/test/path' });
      projectService.setCurrent(project);
    });

    it('resumes a paused issue and preserves stage', async () => {
      const issue = issueService.create({ projectId: projectService.getCurrentId()!, title: 'Paused Issue' });
      issueRepo.updateStatus(issue.id, IssueStatus.Paused);
      issueRepo.updateStage(issue.id, Stage.Plan);

      const response = await request(server).post(`/api/issues/${issue.number}/resume`);

      expect(response.status).toBe(202);
      expect(response.body.success).toBe(true);
      expect(response.body.data.issue.status).toBe(IssueStatus.Active);

      const updated = issueRepo.findById(issue.id);
      expect(updated?.stage).toBe(Stage.Plan);
      expect(updated?.status).toBe(IssueStatus.Active);
    });

    it('resumes an interrupted issue and preserves stage', async () => {
      const issue = issueService.create({ projectId: projectService.getCurrentId()!, title: 'Interrupted Issue' });
      issueRepo.updateStatus(issue.id, IssueStatus.Interrupted);
      issueRepo.updateStage(issue.id, Stage.Build);

      const response = await request(server).post(`/api/issues/${issue.number}/resume`);

      expect(response.status).toBe(202);
      expect(response.body.success).toBe(true);

      const updated = issueRepo.findById(issue.id);
      expect(updated?.stage).toBe(Stage.Build);
      expect(updated?.status).toBe(IssueStatus.Active);
    });

    it('returns 409 when trying to resume a blocked issue', async () => {
      const issue = issueService.create({ projectId: projectService.getCurrentId()!, title: 'Blocked Issue' });
      issueRepo.updateStatus(issue.id, IssueStatus.Blocked);
      issueRepo.updateStage(issue.id, Stage.Build);

      const response = await request(server).post(`/api/issues/${issue.number}/resume`);

      expect(response.status).toBe(409);
      expect(response.body.error).toContain('cannot be resumed');
    });

    it('returns 409 when trying to resume a closed issue', async () => {
      const issue = issueService.create({ projectId: projectService.getCurrentId()!, title: 'Closed Issue' });
      issueRepo.updateStatus(issue.id, IssueStatus.Closed);
      issueRepo.updateStage(issue.id, Stage.Done);

      const response = await request(server).post(`/api/issues/${issue.number}/resume`);

      expect(response.status).toBe(409);
      expect(response.body.error).toContain('cannot be resumed');
    });

    it('enqueues resume-pipeline when resuming', async () => {
      const issue = issueService.create({ projectId: projectService.getCurrentId()!, title: 'Paused Issue' });
      issueRepo.updateStatus(issue.id, IssueStatus.Paused);
      issueRepo.updateStage(issue.id, Stage.Plan);

      const resumeApp = new Hono();
      const eventBus = new EventBus();
      const agentRunner = new AgentRunnerService(eventBus, undefined, issueRepo, 8, undefined, undefined, undefined, undefined, stateManager.getIssueTaskQueueRepo());
      const spy = vi.spyOn(agentRunner, 'enqueue').mockReturnValue({ taskId: 'fake-task', status: 'pending' });
      resumeApp.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, undefined, undefined, agentRunner));
      const resumeServer = createTestServer(resumeApp);

      const response = await request(resumeServer).post(`/api/issues/${issue.number}/resume`);

      expect(response.status).toBe(202);
      expect(spy).toHaveBeenCalledWith(issue.id, 'resume-pipeline');

      spy.mockRestore();
    });
  });

  describe('POST /api/issues/:number/restart (deprecated)', () => {
    let server: http.Server;

    beforeEach(async () => {
      const app = new Hono();
      const eventBus = new EventBus();
      const agentRunner = new AgentRunnerService(eventBus, undefined, issueRepo, 8, undefined, undefined, undefined, undefined, stateManager.getIssueTaskQueueRepo());
      app.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, undefined, undefined, agentRunner));
      server = createTestServer(app);

      const project = await projectService.create({ name: 'Test Project', path: '/test/path' });
      projectService.setCurrent(project);
    });

    it('returns 410 deprecation error without mutating stage or status', async () => {
      const issue = issueService.create({ projectId: projectService.getCurrentId()!, title: 'Blocked Issue' });
      issueRepo.updateStage(issue.id, Stage.Build);
      issueRepo.updateStatus(issue.id, IssueStatus.Blocked);
      issueRepo.updateBlockedReason(issue.id, 'Build failed');

      const response = await request(server).post(`/api/issues/${issue.number}/restart`);

      expect(response.status).toBe(410);
      expect(response.body.success).toBe(false);
      expect(response.body.error).toContain('restart has been removed');
      expect(response.body.error).toContain('retry');
      expect(response.body.error).toContain('rerun');

      const updated = issueRepo.findById(issue.id);
      expect(updated?.stage).toBe(Stage.Build);
      expect(updated?.status).toBe(IssueStatus.Blocked);
      expect(updated?.blockedReason).toBe('Build failed');
    });

    it('returns deprecation error even for closed issues', async () => {
      const issue = issueService.create({ projectId: projectService.getCurrentId()!, title: 'Closed Issue' });
      issueRepo.updateStatus(issue.id, IssueStatus.Closed);
      issueRepo.updateStage(issue.id, Stage.Done);

      const response = await request(server).post(`/api/issues/${issue.number}/restart`);

      expect(response.status).toBe(410);
      expect(response.body.error).toContain('restart has been removed');
    });

    it('returns deprecation error for active issues', async () => {
      const issue = issueService.create({ projectId: projectService.getCurrentId()!, title: 'Active Issue' });
      issueRepo.updateStatus(issue.id, IssueStatus.Active);
      issueRepo.updateStage(issue.id, Stage.Plan);

      const response = await request(server).post(`/api/issues/${issue.number}/restart`);

      expect(response.status).toBe(410);
      expect(response.body.error).toContain('restart has been removed');
    });
  });

  describe('POST /api/issues/:number/retry', () => {
    let server: http.Server;

    beforeEach(async () => {
      const project = await projectService.create({ name: 'Test Project', path: '/test/path' });
      projectService.setCurrent(project);
    });

    function createBlockedIssue(title: string, stage: Stage = Stage.Build) {
      const issue = issueService.create({ projectId: projectService.getCurrentId()!, title });
      issueRepo.updateStage(issue.id, stage);
      issueRepo.blockIssue(issue.id, `Test block — ${title}`);
      issueRepo.updateRetryCount(issue.id, 3);
      return issue;
    }

    function createRetryApp() {
      const app = new Hono();
      const eventBus = new EventBus();
      const agentRunner = new AgentRunnerService(eventBus, undefined, issueRepo, 8, undefined, undefined, undefined, undefined, stateManager.getIssueTaskQueueRepo());
      app.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, undefined, undefined, agentRunner));
      return createTestServer(app);
    }

    it('rejects retry when no checkpoint exists (no backlog reset)', async () => {
      const issue = createBlockedIssue('No Checkpoint');

      server = createRetryApp();
      const response = await request(server).post(`/api/issues/${issue.number}/retry`);

      expect(response.status).toBe(409);
      expect(response.body.error).toContain('checkpoint');
      expect(response.body.error).toContain('rerun');

      const updated = issueRepo.findById(issue.id);
      expect(updated?.stage).toBe(Stage.Build);
      expect(updated?.status).toBe(IssueStatus.Blocked);
    });

    it('rejects retry when no checkpoint exists - does not reset to backlog', async () => {
      const issue = createBlockedIssue('No Backlog Reset');

      server = createRetryApp();
      const response = await request(server).post(`/api/issues/${issue.number}/retry`);

      expect(response.status).toBe(409);

      const updated = issueRepo.findById(issue.id);
      expect(updated?.stage).toBe(Stage.Build);
      expect(updated?.stage).not.toBe(Stage.Backlog);
    });

    it('returns 409 when issue is not blocked', async () => {
      const issue = issueService.create({ projectId: projectService.getCurrentId()!, title: 'Active Issue' });
      issueRepo.updateStatus(issue.id, IssueStatus.Active);
      issueRepo.updateStage(issue.id, Stage.Plan);

      server = createRetryApp();
      const response = await request(server).post(`/api/issues/${issue.number}/retry`);

      expect(response.status).toBe(409);
      expect(response.body.error).toContain('not blocked');
    });

    it('rejects retry for merged issues', async () => {
      const issue = createBlockedIssue('Merged Issue');
      issueRepo.updateStage(issue.id, Stage.Done);
      issueRepo.update(issue.id, { mergeState: MergeState.Merged });

      server = createRetryApp();
      const response = await request(server).post(`/api/issues/${issue.number}/retry`);

      expect(response.status).toBe(409);
      expect(response.body.error).toContain('manual intervention');
    });

    it('rejects retry for integrating issues', async () => {
      const issue = createBlockedIssue('Integrating Issue');
      issueRepo.updateStage(issue.id, Stage.Integrate);

      server = createRetryApp();
      const response = await request(server).post(`/api/issues/${issue.number}/retry`);

      expect(response.status).toBe(409);
      expect(response.body.error).toContain('manual intervention');
    });

    it('succeeds with checkpoint and enqueues resume-pipeline', async () => {
      const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-retry-'));

      try {
        const testDb = new DatabaseManager({ inMemory: true });
        const testStateManager = new StateManager(testDb);
        const testProjectRepo = testStateManager.getProjectRepo();
        const testIssueRepo = testStateManager.getIssueRepo();
        const testConfigRepo = testStateManager.getConfigRepo();
        const testCommentRepo = testStateManager.getCommentRepo();
        const testLabelRepo = testStateManager.getLabelRepo();
        const testProjectService = new ProjectService(testProjectRepo, testConfigRepo, testIssueRepo, testLabelRepo);
        const testIssueService = new IssueService(testIssueRepo, testCommentRepo);

        const project = await testProjectService.create({ name: 'RetryCheckpoint', path: tmpDir });
        testProjectService.setCurrent(project);

        const issue = testIssueService.create({ projectId: project.id, title: 'Retry With Checkpoint' });
        testIssueRepo.updateStage(issue.id, Stage.Build);
        testIssueRepo.blockIssue(issue.id, 'Build interrupted');

        const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issue.number}-test-change`);
        fs.mkdirSync(changeDir, { recursive: true });
        fs.writeFileSync(path.join(changeDir, 'tasks.json'), JSON.stringify({ version: 1, tasks: [{ id: 'T-001', passes: true }] }));

        const app = new Hono();
        const eventBus = new EventBus();
        const agentRunner = new AgentRunnerService(eventBus, undefined, testIssueRepo, 8, undefined, undefined, undefined, undefined, testStateManager.getIssueTaskQueueRepo());
        const enqueueSpy = vi.spyOn(agentRunner, 'enqueue').mockReturnValue({ taskId: 'fake', status: 'pending' });

        const mockWm = {
          getPath: () => tmpDir,
          exists: () => true,
        } as any;

        app.route('/api/issues', createIssueRoutes(testIssueService, testProjectService, testStateManager, mockWm, undefined, agentRunner, undefined, undefined, undefined, undefined, undefined, testStateManager.getPipelineCheckpointRepo()));
        server = createTestServer(app);

        const response = await request(server).post(`/api/issues/${issue.number}/retry`);

        expect(response.status).toBe(202);
        expect(enqueueSpy).toHaveBeenCalledWith(issue.id, 'resume-pipeline');

        const updated = testIssueRepo.findById(issue.id);
        expect(updated?.status).toBe(IssueStatus.Active);

        testDb.close();
      } finally {
        fs.rmSync(tmpDir, { recursive: true, force: true });
      }
    });
  });

  describe('GET /api/issues/:number/start guidance', () => {
    let server: http.Server;

    beforeEach(async () => {
      const app = new Hono();
      const eventBus = new EventBus();
      const agentRunner = new AgentRunnerService(eventBus, undefined, issueRepo, 8, undefined, undefined, undefined, undefined, stateManager.getIssueTaskQueueRepo());
      app.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, undefined, undefined, agentRunner));
      server = createTestServer(app);

      const project = await projectService.create({ name: 'Test Project', path: '/test/path' });
      projectService.setCurrent(project);
    });

    it('start on blocked issue recommends retry/rerun, not restart', async () => {
      const issue = issueService.create({ projectId: projectService.getCurrentId()!, title: 'Blocked Issue' });
      issueRepo.updateStage(issue.id, Stage.Build);
      issueRepo.blockIssue(issue.id, 'Build failed');

      const response = await request(server).post(`/api/issues/${issue.number}/start`);

      expect(response.status).toBe(400);
      expect(response.body.error).toContain('retry');
      expect(response.body.error).toContain('rerun');
      expect(response.body.error).not.toContain('restart');
    });

    it('start on closed issue recommends reopen', async () => {
      const issue = issueService.create({ projectId: projectService.getCurrentId()!, title: 'Closed Issue' });
      issueRepo.updateStatus(issue.id, IssueStatus.Closed);

      const response = await request(server).post(`/api/issues/${issue.number}/start`);

      expect(response.status).toBe(400);
      expect(response.body.error).toContain('reopen');
    });
  });
});