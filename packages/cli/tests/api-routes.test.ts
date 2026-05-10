import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import http from 'node:http';
import { execFile } from 'child_process';
import { promisify } from 'util';
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
import { createProjectRoutes } from '../src/api/projects';
import { createIssueRoutes } from '../src/api/issues';
import { Stage, IssueStatus, MergeState } from '../src/types';
import { createStatusRoutes } from '../src/api/status';
import { createConfigRoutes } from '../src/api/config';
import { StageExecutionRepo } from '../src/db/stage-execution-repo';

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

describe('API Routes', () => {
  let db: DatabaseManager;
  let projectRepo: ProjectRepo;
  let issueRepo: IssueRepo;
  let configRepo: ConfigRepo;
  let projectService: ProjectService;
  let issueService: IssueService;
  let configService: ConfigService;
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
    
    projectRepo = stateManager.getProjectRepo();
    issueRepo = stateManager.getIssueRepo();
    configRepo = stateManager.getConfigRepo();
    
    const commentRepo = stateManager.getCommentRepo();
    const labelRepo = stateManager.getLabelRepo();
    projectService = new ProjectService(projectRepo, configRepo, issueRepo, labelRepo);
    issueService = new IssueService(issueRepo, commentRepo);
    configService = new ConfigService(configRepo);
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

  describe('Project Routes', () => {
    let server: http.Server;

    beforeEach(() => {
      const app = new Hono();
      app.route('/api/projects', createProjectRoutes(projectService));
      server = createTestServer(app);
    });

    describe('POST /api/projects', () => {
      it('should create a project', async () => {
        const response = await request(server)
          .post('/api/projects')
          .send({ name: 'Test Project', path: '/test/path' });

        expect(response.status).toBe(201);
        expect(response.body.success).toBe(true);
        expect(response.body.data.name).toBe('Test Project');
        expect(response.body.data.path).toBe('/test/path');
      });

      it('should require name and path', async () => {
        const response = await request(server)
          .post('/api/projects')
          .send({ name: 'Test Project' });

        expect(response.status).toBe(400);
        expect(response.body.error).toContain('required');
      });

      it('should reject duplicate project name', async () => {
        await request(server)
          .post('/api/projects')
          .send({ name: 'Test Project', path: '/test/path' });

        const response = await request(server)
          .post('/api/projects')
          .send({ name: 'Test Project', path: '/other/path' });

        expect(response.status).toBe(409);
        expect(response.body.error).toContain('already exists');
      });
    });

    describe('GET /api/projects', () => {
      it('should list projects', async () => {
        await request(server)
          .post('/api/projects')
          .send({ name: 'Project 1', path: '/path/1' });
        await request(server)
          .post('/api/projects')
          .send({ name: 'Project 2', path: '/path/2' });

        const response = await request(server).get('/api/projects');

        expect(response.status).toBe(200);
        expect(response.body.success).toBe(true);
        expect(response.body.data).toHaveLength(2);
      });
    });

    describe('GET /api/projects/:name', () => {
      it('should return project details', async () => {
        await request(server)
          .post('/api/projects')
          .send({ name: 'Test Project', path: '/test/path' });

        const response = await request(server).get('/api/projects/Test Project');

        expect(response.status).toBe(200);
        expect(response.body.data.name).toBe('Test Project');
      });

      it('should return 404 for non-existent project', async () => {
        const response = await request(server).get('/api/projects/NonExistent');

        expect(response.status).toBe(404);
      });
    });

    describe('DELETE /api/projects/:name', () => {
      it('should delete project', async () => {
        await request(server)
          .post('/api/projects')
          .send({ name: 'Test Project', path: '/test/path' });

        const response = await request(server).delete('/api/projects/Test Project');

        expect(response.status).toBe(200);
        expect(response.body.success).toBe(true);
      });
    });

    describe('POST /api/projects/:name/use', () => {
      it('should set current project', async () => {
        await request(server)
          .post('/api/projects')
          .send({ name: 'Test Project', path: '/test/path' });

        const response = await request(server).post('/api/projects/Test Project/use');

        expect(response.status).toBe(200);
        expect(response.body.data.name).toBe('Test Project');
      });
    });
  });

  describe('Issue Routes', () => {
    let server: http.Server;
    let projectId: string;
    let stageExecutionRepo: StageExecutionRepo;

    beforeEach(async () => {
      const app = new Hono();
      const eventBus = new EventBus();
      const agentRunner = new AgentRunnerService(eventBus, undefined, issueRepo, 8, undefined, undefined, projectRepo, undefined, stateManager.getIssueTaskQueueRepo());
      stageExecutionRepo = stateManager.getStageExecutionRepo();
      app.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, undefined, undefined, agentRunner, undefined, undefined, undefined, undefined, undefined, undefined, undefined, undefined, stageExecutionRepo));
      server = createTestServer(app);
      
      const project = await projectService.create({ name: 'Test Project', path: '/test/path' });
      projectId = project.id;
      projectService.setCurrent(project);
    });

    describe('POST /api/issues', () => {
      it('should create an issue', async () => {
        const response = await request(server)
          .post('/api/issues')
          .send({ title: 'Test Issue', body: 'Test body' });

        expect(response.status).toBe(201);
        expect(response.body.success).toBe(true);
        expect(response.body.data.title).toBe('Test Issue');
        expect(response.body.data.number).toBe(1);
      });

      it('should require title', async () => {
        const response = await request(server)
          .post('/api/issues')
          .send({ body: 'Test body' });

        expect(response.status).toBe(400);
      });

      it('should return error when no current project', async () => {
        projectService.clearCurrent();
        
        const response = await request(server)
          .post('/api/issues')
          .send({ title: 'Test Issue' });

        expect(response.status).toBe(400);
        expect(response.body.error).toContain('No active project');
      });
    });

    describe('GET /api/issues', () => {
      it('should list issues', async () => {
        await issueService.create({ projectId, title: 'Issue 1' });
        await issueService.create({ projectId, title: 'Issue 2' });

        const response = await request(server).get('/api/issues');

        expect(response.status).toBe(200);
        expect(response.body.data).toHaveLength(2);
      });

      it('should filter by stage', async () => {
        await issueService.create({ projectId, title: 'Test' });
        issueService.transitionToStageByNumber(projectId, 1, 'designing' as any);

        const response = await request(server).get('/api/issues?stage=designing');

        expect(response.status).toBe(200);
        expect(response.body.data).toHaveLength(1);
      });
    });

    describe('GET /api/issues/:number', () => {
      it('should return issue details', async () => {
        await issueService.create({ projectId, title: 'Test Issue' });

        const response = await request(server).get('/api/issues/1');

        expect(response.status).toBe(200);
        expect(response.body.data.number).toBe(1);
        expect(response.body.data.title).toBe('Test Issue');
      });

      it('should return 404 for non-existent issue', async () => {
        const response = await request(server).get('/api/issues/999');

        expect(response.status).toBe(404);
      });
    });

    describe('POST /api/issues/:number/start', () => {
      it('should enqueue start-pipeline for an issue', async () => {
        await issueService.create({ projectId, title: 'Test Issue' });

        const response = await request(server).post('/api/issues/1/start');

        expect(response.status).toBe(202);
        expect(response.body.success).toBe(true);
        expect(response.body.data.taskId).toBeDefined();
        expect(response.body.data.status).toBeDefined();
      });
    });

    describe('POST /api/issues/:number/approve', () => {
      it('should return 400 when no pending gate in memory or DB', async () => {
        await issueService.create({ projectId, title: 'Test Issue' });

        const response = await request(server).post('/api/issues/1/approve');

        expect(response.status).toBe(400);
        expect(response.body.error).toMatch(/No pending approval/);
      });

      it('should fall back to DB when hasPendingGate returns false but DB has awaiting state', async () => {
        const issue = issueService.create({ projectId, title: 'Awaiting Issue' });
        issueService.transitionToStage(issue.id, Stage.Plan);
        issueService.setStatus(issue.id, IssueStatus.Active);

        const issueRepo = stateManager.getIssueRepo();
        issueRepo.setApprovalState(issue.id, {
          stage: Stage.Plan,
          status: 'awaiting',
          output: { test: true },
          requestedAt: new Date().toISOString(),
        });

        const refreshedIssue = issueService.getByNumber(projectId, 1);
        expect(refreshedIssue?.approvalState?.status).toBe('awaiting');

        const response = await request(server).post('/api/issues/1/approve');

        expect(response.status).toBe(202);
        expect(response.body.success).toBe(true);
      });

      it('Check approval should reject when authoritative PASS review is missing', async () => {
        const issue = issueService.create({ projectId, title: 'Check Approval Issue' });
        issueService.transitionToStage(issue.id, Stage.Check);
        issueService.setStatus(issue.id, IssueStatus.Active);

        const issueRepo = stateManager.getIssueRepo();
        issueRepo.setApprovalState(issue.id, {
          stage: Stage.Check,
          status: 'awaiting',
          output: { test: true },
          requestedAt: new Date().toISOString(),
        });

        const approveApp = new Hono();
        const approveEventBus = new EventBus();
        const approveAgentRunner = new AgentRunnerService(approveEventBus, undefined, stateManager.getIssueRepo(), 8, undefined, undefined, stateManager.getProjectRepo(), undefined, stateManager.getIssueTaskQueueRepo());
        const enqueueSpy = vi.spyOn(approveAgentRunner, 'enqueue').mockReturnValue({ taskId: 'fake', status: 'pending' });
        approveApp.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, undefined, undefined, approveAgentRunner, undefined, undefined, undefined, undefined, undefined, undefined, undefined, undefined, stageExecutionRepo));
        const approveServer = createTestServer(approveApp);

        const response = await request(approveServer).post(`/api/issues/${issue.number}/approve`);

        expect(response.status).toBe(409);
        expect(response.body.success).toBe(false);
        expect(response.body.error).toContain("latest ai-review verdict");
        expect(enqueueSpy).not.toHaveBeenCalled();
      });

      it('Check approval should transition to Integrate and enqueue resume-pipeline when authoritative PASS matches snapshot', async () => {
        const issue = issueService.create({ projectId, title: 'Check Approval Ready Issue' });
        issueService.transitionToStage(issue.id, Stage.Check);
        issueService.setStatus(issue.id, IssueStatus.Active);

        const issueRepo = stateManager.getIssueRepo();
        issueRepo.setApprovalState(issue.id, {
          stage: Stage.Check,
          status: 'awaiting',
          output: { snapshotSha: 'sha-pass-001', result: 'PASS' },
          requestedAt: new Date().toISOString(),
        });

        const execution = stageExecutionRepo.create(issue.id, Stage.Check);
        stageExecutionRepo.updateCheckResults(execution.id, [
          {
            name: 'ai-review',
            status: 'pass',
            output: {
              verdict: 'PASS',
              reviewReport: '# Review\n<promise>PASS</promise>',
              snapshotSha: 'sha-pass-001',
              reviewArtifactPath: '/tmp/change/review.md',
              selfCheckArtifactPath: '/tmp/change/review-self-check.md',
            },
          },
        ]);
        stageExecutionRepo.updateStatus(execution.id, 'awaiting-approval');

        const worktreeManager = {
          getPath: vi.fn().mockReturnValue('/tmp/worktree'),
          getHeadSha: vi.fn().mockResolvedValue('sha-pass-001'),
          isWorktreeClean: vi.fn().mockResolvedValue(true),
        } as any;

        const approveApp = new Hono();
        const approveEventBus = new EventBus();
        const approveAgentRunner = new AgentRunnerService(approveEventBus, undefined, stateManager.getIssueRepo(), 8, undefined, undefined, stateManager.getProjectRepo(), undefined, stateManager.getIssueTaskQueueRepo());
        const enqueueSpy = vi.spyOn(approveAgentRunner, 'enqueue').mockReturnValue({ taskId: 'fake', status: 'pending' });
        approveApp.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, worktreeManager, undefined, approveAgentRunner, undefined, undefined, undefined, undefined, undefined, undefined, undefined, undefined, stageExecutionRepo));
        const approveServer = createTestServer(approveApp);

        const response = await request(approveServer).post(`/api/issues/${issue.number}/approve`);

        expect(response.status).toBe(202);
        expect(response.body.success).toBe(true);
        expect(enqueueSpy).toHaveBeenCalledTimes(1);
        expect(enqueueSpy).toHaveBeenCalledWith(issue.id, 'resume-pipeline');
      });

      it('Direct merge for non-Integrate issue should return bypass error', async () => {
        const issue = issueService.create({ projectId, title: 'Direct Merge Test' });
        issueService.transitionToStage(issue.id, Stage.Check);
        issueService.setStatus(issue.id, IssueStatus.Active);

        const mergeApp = new Hono();
        const mergeEventBus = new EventBus();
        const mergeAgentRunner = new AgentRunnerService(mergeEventBus, undefined, stateManager.getIssueRepo(), 8, undefined, undefined, stateManager.getProjectRepo(), undefined, stateManager.getIssueTaskQueueRepo());
        mergeApp.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, undefined, undefined, mergeAgentRunner));
        const mergeServer = createTestServer(mergeApp);

        const response = await request(mergeServer).post(`/api/issues/${issue.number}/merge`);

        expect(response.status).toBe(409);
        expect(response.body.error).toContain('Direct merge is not allowed');
        expect(response.body.error).toContain('check');
      });
    });

    describe('POST /api/issues/:number/skip-to-review', () => {
      it('should transition to review stage and trigger pipeline', async () => {
        const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-skip-test-'));

        try {
          const project = await projectService.create({ name: 'SkipTest', path: tmpDir });
          projectService.setCurrent(project);

          const issue = issueService.create({ projectId: project.id, title: 'Skip Issue' });
          const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issue.number}-test`);
          fs.mkdirSync(changeDir, { recursive: true });
          fs.writeFileSync(path.join(changeDir, 'tasks.json'), JSON.stringify({ version: 1, tasks: [] }));

          const skipApp = new Hono();
          const skipEventBus = new EventBus();
          const skipAgentRunner = new AgentRunnerService(skipEventBus, undefined, stateManager.getIssueRepo(), 8, undefined, undefined, stateManager.getProjectRepo(), undefined, stateManager.getIssueTaskQueueRepo());
          skipApp.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, undefined, undefined, skipAgentRunner));
          const skipServer = createTestServer(skipApp);

          const response = await request(skipServer).post(`/api/issues/${issue.number}/skip-to-review`);

          expect(response.status).toBe(202);
          expect(response.body.success).toBe(true);

          const issueRepo = stateManager.getIssueRepo();
          const updated = issueRepo.findById(issue.id);
          expect(updated?.stage).toBe(Stage.Check);
        } finally {
          fs.rmSync(tmpDir, { recursive: true, force: true });
        }
      });
    });

    describe('POST /api/issues/:number/reopen', () => {
      it('should preserve stage and resume pipeline for blocked issue', async () => {
        const issue = issueService.create({ projectId, title: 'Blocked Issue' });
        const issueRepo = stateManager.getIssueRepo();
        issueRepo.updateStage(issue.id, Stage.Build);
        issueRepo.updateStatus(issue.id, IssueStatus.Blocked);

        const reopenApp = new Hono();
        const reopenEventBus = new EventBus();
        const reopenAgentRunner = new AgentRunnerService(reopenEventBus, undefined, stateManager.getIssueRepo(), 8, undefined, undefined, undefined, undefined, stateManager.getIssueTaskQueueRepo());
        const enqueueSpy = vi.spyOn(reopenAgentRunner, 'enqueue').mockReturnValue({ taskId: 'fake', status: 'pending' });
        reopenApp.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, undefined, undefined, reopenAgentRunner));
        const reopenServer = createTestServer(reopenApp);

        const response = await request(reopenServer).post(`/api/issues/${issue.number}/reopen`);

        expect(response.status).toBe(202);
        expect(response.body.success).toBe(true);
        expect(response.body.data.message).toContain('resume-pipeline');

        expect(enqueueSpy).toHaveBeenCalledTimes(1);
        expect(enqueueSpy).toHaveBeenCalledWith(issue.id, 'resume-pipeline');
      });

      it('should enqueue resume-pipeline even when reopen recovery restores awaiting approval', async () => {
        const issue = issueService.create({ projectId, title: 'Awaiting Review Issue' });
        const issueRepo = stateManager.getIssueRepo();
        issueRepo.updateStage(issue.id, Stage.Check);
        issueRepo.updateStatus(issue.id, IssueStatus.Blocked);

        const reopenApp = new Hono();
        const reopenEventBus = new EventBus();
        const reopenAgentRunner = new AgentRunnerService(reopenEventBus, undefined, stateManager.getIssueRepo(), 8, undefined, undefined, undefined, undefined, stateManager.getIssueTaskQueueRepo());
        const enqueueSpy = vi.spyOn(reopenAgentRunner, 'enqueue').mockReturnValue({ taskId: 'fake', status: 'pending' });
        reopenApp.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, undefined, undefined, reopenAgentRunner));
        const reopenServer = createTestServer(reopenApp);

        const response = await request(reopenServer).post(`/api/issues/${issue.number}/reopen`);

        expect(response.status).toBe(202);
        expect(response.body.success).toBe(true);
        expect(response.body.data.message).toContain('resume-pipeline');
        expect(enqueueSpy).toHaveBeenCalledTimes(1);
        expect(enqueueSpy).toHaveBeenCalledWith(issue.id, 'resume-pipeline');

        const reopened = issueRepo.findById(issue.id);
        expect(reopened?.status).toBe(IssueStatus.Active);
        expect(reopened?.stage).toBe(Stage.Check);
        expect(reopened?.approvalState?.status).toBe('awaiting');
      });
    });

    describe('POST /api/issues/:number/reject', () => {
      it('clears check checkpoint and stale review artifacts before restarting from build', async () => {
        const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-reject-check-test-'));

        try {
          const project = await projectService.create({ name: 'RejectCheckTest', path: tmpDir });
          projectService.setCurrent(project);

          const issue = issueService.create({ projectId: project.id, title: 'Reject Check Issue' });
          issueService.transitionToStage(issue.id, Stage.Check);
          issueService.setStatus(issue.id, IssueStatus.Active);

          const issueRepo = stateManager.getIssueRepo();
          issueRepo.setApprovalState(issue.id, {
            stage: Stage.Check,
            status: 'awaiting',
            output: null,
            requestedAt: new Date().toISOString(),
          });

          const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issue.number}-test`);
          fs.mkdirSync(changeDir, { recursive: true });
          fs.writeFileSync(path.join(changeDir, 'tasks.json'), JSON.stringify({ version: 1, tasks: [] }));
          fs.writeFileSync(path.join(changeDir, 'review.md'), '# stale review');
          fs.writeFileSync(path.join(changeDir, 'review-self-check.md'), '# stale self check');

          const checkpointRepo = stateManager.getPipelineCheckpointRepo();
          checkpointRepo.upsert(issue.number, 'check', ['review', 'review-self-check'], null);

          const worktreeManager = {
            getPath: vi.fn().mockReturnValue(tmpDir),
          } as any;

          const rejectApp = new Hono();
          const rejectEventBus = new EventBus();
          const rejectAgentRunner = new AgentRunnerService(rejectEventBus, undefined, stateManager.getIssueRepo(), 8, undefined, undefined, stateManager.getProjectRepo(), worktreeManager, stateManager.getIssueTaskQueueRepo());
          const enqueueSpy = vi.spyOn(rejectAgentRunner, 'enqueue').mockReturnValue({ taskId: 'fake', status: 'pending' });
          rejectApp.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, worktreeManager, undefined, rejectAgentRunner, undefined, undefined, undefined, undefined, undefined, stateManager.getPipelineCheckpointRepo()));
          const rejectServer = createTestServer(rejectApp);

          const response = await request(rejectServer)
            .post(`/api/issues/${issue.number}/reject`)
            .send({ message: 'rerun review on latest code' });

          expect(response.status).toBe(202);
          expect(response.body.success).toBe(true);
          expect(enqueueSpy).toHaveBeenCalledWith(issue.id, 'resume-pipeline');
          expect(issueRepo.findById(issue.id)?.stage).toBe(Stage.Build);
          expect(checkpointRepo.get(issue.number, 'check')).toBeNull();
          expect(fs.existsSync(path.join(changeDir, 'review.md'))).toBe(false);
          expect(fs.existsSync(path.join(changeDir, 'review-self-check.md'))).toBe(false);
        } finally {
          fs.rmSync(tmpDir, { recursive: true, force: true });
        }
      });
    });

    describe('POST /api/issues/:number/rerun', () => {
      it('clears check checkpoint and stale review artifacts before rerunning check stage', async () => {
        const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-rerun-check-test-'));

        try {
          const project = await projectService.create({ name: 'RerunCheckTest', path: tmpDir });
          projectService.setCurrent(project);

          const issue = issueService.create({ projectId: project.id, title: 'Rerun Check Issue' });
          issueService.transitionToStage(issue.id, Stage.Check);
          issueService.setStatus(issue.id, IssueStatus.Active);

          const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issue.number}-test`);
          fs.mkdirSync(changeDir, { recursive: true });
          fs.writeFileSync(path.join(changeDir, 'tasks.json'), JSON.stringify({ version: 1, tasks: [] }));
          fs.writeFileSync(path.join(changeDir, 'review.md'), '# stale review');
          fs.writeFileSync(path.join(changeDir, 'review-self-check.md'), '# stale self check');

          const checkpointRepo = stateManager.getPipelineCheckpointRepo();
          checkpointRepo.upsert(issue.number, 'check', ['review', 'review-self-check'], null);

          const worktreeManager = {
            getPath: vi.fn().mockReturnValue(tmpDir),
          } as any;

          const rerunApp = new Hono();
          const rerunEventBus = new EventBus();
          const rerunAgentRunner = new AgentRunnerService(rerunEventBus, undefined, stateManager.getIssueRepo(), 8, undefined, undefined, stateManager.getProjectRepo(), worktreeManager, stateManager.getIssueTaskQueueRepo());
          const enqueueSpy = vi.spyOn(rerunAgentRunner, 'enqueue').mockReturnValue({ taskId: 'fake', status: 'pending' });
          rerunApp.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, worktreeManager, undefined, rerunAgentRunner, undefined, undefined, undefined, undefined, undefined, stateManager.getPipelineCheckpointRepo()));
          const rerunServer = createTestServer(rerunApp);

          const response = await request(rerunServer).post(`/api/issues/${issue.number}/rerun`);

          expect(response.status).toBe(202);
          expect(response.body.success).toBe(true);
          expect(enqueueSpy).toHaveBeenCalledWith(issue.id, 'resume-pipeline');
          expect(checkpointRepo.get(issue.number, 'check')).toBeNull();
          expect(fs.existsSync(path.join(changeDir, 'review.md'))).toBe(false);
          expect(fs.existsSync(path.join(changeDir, 'review-self-check.md'))).toBe(false);
        } finally {
          fs.rmSync(tmpDir, { recursive: true, force: true });
        }
      });
    });

    describe('POST /api/issues/:number/comments', () => {
      it('should add a comment to an issue', async () => {
        const issue = issueService.create({ projectId, title: 'Comment Test' });

        const response = await request(server)
          .post(`/api/issues/${issue.number}/comments`)
          .send({ body: 'Test comment' });

        expect(response.status).toBe(201);
        expect(response.body.success).toBe(true);
        expect(response.body.data.body).toBe('Test comment');
        expect(response.body.data.issueId).toBe(issue.id);
      });

      it('should require body', async () => {
        const issue = issueService.create({ projectId, title: 'Comment Test' });

        const response = await request(server)
          .post(`/api/issues/${issue.number}/comments`)
          .send({});

        expect(response.status).toBe(400);
        expect(response.body.error).toContain('body is required');
      });

      it('should return 404 when issue not found', async () => {
        const response = await request(server)
          .post('/api/issues/999/comments')
          .send({ body: 'Test comment' });

        expect(response.status).toBe(404);
      });
    });

    describe('DELETE /api/issues/:number/comments/:commentId', () => {
      it('should delete a comment that belongs to the issue', async () => {
        const issue = issueService.create({ projectId, title: 'Delete Comment Test' });
        const comment = issueService.createComment(issue.id, 'Comment to delete');

        const response = await request(server)
          .delete(`/api/issues/${issue.number}/comments/${comment.id}`);

        expect(response.status).toBe(200);
        expect(response.body.success).toBe(true);
        expect(response.body.data.message).toContain(`Deleted comment ${comment.id} from issue #${issue.number}`);

        const comments = issueService.getCommentsByIssue(issue.id);
        expect(comments.find(c => c.id === comment.id)).toBeUndefined();
      });

      it('should return 404 when comment does not exist', async () => {
        const issue = issueService.create({ projectId, title: 'Delete Comment Test' });

        const response = await request(server)
          .delete(`/api/issues/${issue.number}/comments/non-existent-id`);

        expect(response.status).toBe(404);
        expect(response.body.error).toContain('Comment not found');
      });

      it('should return 404 when trying to delete a comment from another issue', async () => {
        const issue1 = issueService.create({ projectId, title: 'Issue 1' });
        const issue2 = issueService.create({ projectId, title: 'Issue 2' });
        const commentOnIssue1 = issueService.createComment(issue1.id, 'Comment on issue 1');

        const response = await request(server)
          .delete(`/api/issues/${issue2.number}/comments/${commentOnIssue1.id}`);

        expect(response.status).toBe(404);
        expect(response.body.error).toContain('Comment not found');

        const commentsOnIssue1 = issueService.getCommentsByIssue(issue1.id);
        expect(commentsOnIssue1.find(c => c.id === commentOnIssue1.id)).toBeDefined();
      });

      it('should return 404 when issue does not exist', async () => {
        const response = await request(server)
          .delete('/api/issues/999/comments/some-id');

        expect(response.status).toBe(404);
        expect(response.body.error).toContain('Issue #999 not found');
      });

      it('should return 400 when no active project', async () => {
        projectService.clearCurrent();

        const response = await request(server)
          .delete('/api/issues/1/comments/some-id');

        expect(response.status).toBe(400);
        expect(response.body.error).toContain('No active project');
      });
    });
  });

  describe('Status Routes', () => {
    let server: http.Server;

    beforeEach(() => {
      const app = new Hono();
      app.route('/api', createStatusRoutes(projectService, issueService));
      server = createTestServer(app);
    });

    describe('GET /api/status', () => {
      it('should return error when no current project', async () => {
        const response = await request(server).get('/api/status');

        expect(response.status).toBe(400);
        expect(response.body.error).toContain('No active project');
      });

      it('should return current project status', async () => {
        const project = await projectService.create({ name: 'Test Project', path: '/test/path' });
        projectService.setCurrent(project);

        const response = await request(server).get('/api/status');

        expect(response.status).toBe(200);
        expect(response.body.success).toBe(true);
        expect(response.body.data.name).toBe('Test Project');
      });

      it('should return llm.configured false when no llmConfig provided', async () => {
        const project = await projectService.create({ name: 'Test Project', path: '/test/path' });
        projectService.setCurrent(project);

        const response = await request(server).get('/api/status');

        expect(response.status).toBe(200);
        expect(response.body.data.llm).toBeDefined();
        expect(response.body.data.llm.configured).toBe(false);
        expect(response.body.data.llm.provider).toBeUndefined();
        expect(response.body.data.llm.model).toBeUndefined();
      });

      it('should not expose apiKey in llm status', async () => {
        const project = await projectService.create({ name: 'Test Project', path: '/test/path' });
        projectService.setCurrent(project);

        const response = await request(server).get('/api/status');

        expect(response.status).toBe(200);
        const llmJson = JSON.stringify(response.body.data.llm);
        expect(llmJson).not.toContain('apiKey');
      });

      it('should return llm.configured false when llmConfig has no apiKey', async () => {
        const noKeyApp = new Hono();
        noKeyApp.route('/api', createStatusRoutes(projectService, issueService, { model: 'anthropic/claude-sonnet-4-20250514' }));
        const noKeyServer = createTestServer(noKeyApp);

        const project = await projectService.create({ name: 'Test Project', path: '/test/path' });
        projectService.setCurrent(project);

        const response = await request(noKeyServer).get('/api/status');

        expect(response.status).toBe(200);
        expect(response.body.data.llm.configured).toBe(false);
      });
    });

    describe('GET /api/status?all=true', () => {
      it('should return all projects status', async () => {
        await projectService.create({ name: 'Project 1', path: '/path/1' });
        await projectService.create({ name: 'Project 2', path: '/path/2' });

        const response = await request(server).get('/api/status?all=true');

        expect(response.status).toBe(200);
        expect(response.body.data).toHaveLength(2);
      });
    });
  });

  describe('Config Routes', () => {
    let server: http.Server;

    beforeEach(() => {
      const app = new Hono();
      app.route('/api/config', createConfigRoutes(configService));
      server = createTestServer(app);
    });

    describe('GET /api/config', () => {
      it('should return config', async () => {
        const response = await request(server).get('/api/config');

        expect(response.status).toBe(200);
        expect(response.body.success).toBe(true);
        expect(response.body.data.agentTimeout).toBeDefined();
      });
    });

    describe('PUT /api/config/:key', () => {
      it('should update config value', async () => {
        const response = await request(server)
          .put('/api/config/agent.timeout')
          .send({ value: 2000000 });

        expect(response.status).toBe(200);
        expect(response.body.success).toBe(true);
      });

      it('should validate agent timeout minimum', async () => {
        const response = await request(server)
          .put('/api/config/agent.timeout')
          .send({ value: 1000 });

        expect(response.status).toBe(400);
      });
    });

    describe('GET /api/config/list', () => {
      it('should return all config values', async () => {
        const response = await request(server).get('/api/config/list');

        expect(response.status).toBe(200);
        expect(response.body.success).toBe(true);
        expect(response.body.data).toBeDefined();
      });
    });
  });

  describe('Issue Retry/Restart Routes', () => {
    let server: http.Server;
    let projectId: string;

    function createBlockedIssue(title: string) {
      const issue = issueService.create({ projectId, title });
      const issueRepo = stateManager.getIssueRepo();
      issueRepo.updateStage(issue.id, Stage.Build);
      issueRepo.blockIssue(issue.id, `Build 中断 — ${title}`);
      issueRepo.updateRetryCount(issue.id, 3);
      return issue;
    }

    function createRetryServer(agentRunner: AgentRunnerService) {
      const app = new Hono();
      app.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, undefined, undefined, agentRunner));
      return createTestServer(app);
    }

    beforeEach(async () => {
      const project = await projectService.create({ name: 'Test Project', path: '/test/path' });
      projectId = project.id;
      projectService.setCurrent(project);
    });

    describe('POST /api/issues/:number/retry', () => {
      it('should reject retry for merged blocked issue and require manual intervention', async () => {
        const issue = createBlockedIssue('Merged Blocked');
        issueRepo.updateStage(issue.id, Stage.Done);
        issueRepo.update(issue.id, { mergeState: MergeState.Merged });
        const eventBus = new EventBus();
        const agentRunner = new AgentRunnerService(eventBus, undefined, stateManager.getIssueRepo(), 8);
        server = createRetryServer(agentRunner);

        const response = await request(server).post(`/api/issues/${issue.number}/retry`);

        expect(response.status).toBe(409);
        expect(response.body.error).toContain('manual intervention');

        const updated = stateManager.getIssueRepo().findById(issue.id);
        expect(updated?.stage).toBe(Stage.Done);
        expect(updated?.status).toBe(IssueStatus.Blocked);
      });

      it('should reject retry for integrate-stage blocked issue and require manual intervention', async () => {
        const issue = createBlockedIssue('Integrate Blocked');
        issueRepo.updateStage(issue.id, Stage.Integrate);
        const eventBus = new EventBus();
        const agentRunner = new AgentRunnerService(eventBus, undefined, stateManager.getIssueRepo(), 8);
        server = createRetryServer(agentRunner);

        const response = await request(server).post(`/api/issues/${issue.number}/retry`);

        expect(response.status).toBe(409);
        expect(response.body.error).toContain('manual intervention');

        const updated = stateManager.getIssueRepo().findById(issue.id);
        expect(updated?.stage).toBe(Stage.Integrate);
        expect(updated?.status).toBe(IssueStatus.Blocked);
      });

      it('should retry a blocked issue — no checkpoint falls back to draft reset', async () => {
        const issue = createBlockedIssue('Retry Test');
        const eventBus = new EventBus();
        const agentRunner = new AgentRunnerService(eventBus, undefined, stateManager.getIssueRepo(), 8);
        server = createRetryServer(agentRunner);

        const response = await request(server).post(`/api/issues/${issue.number}/retry`);

        expect(response.status).toBe(200);
        expect(response.body.success).toBe(true);
        expect(response.body.data.message).toContain('no checkpoint found');

        const issueRepo = stateManager.getIssueRepo();
        const updated = issueRepo.findById(issue.id);
        expect(updated?.status).toBe(IssueStatus.Active);
        expect(updated?.stage).toBe(Stage.Backlog);
        expect(updated?.blockedReason).toBeUndefined();
        expect(updated?.retryCount).toBe(0);
      });

      it('should retry from checkpoint when worktree has tasks.json', async () => {
        const tmpRetryDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-retry-test-'));

        try {
          const retryProject = await projectService.create({ name: 'RetryCheckpoint', path: tmpRetryDir });
          projectService.setCurrent(retryProject);

          const issue = issueService.create({ projectId: retryProject.id, title: 'Retry Checkpoint' });
          const issueRepo = stateManager.getIssueRepo();
          issueRepo.updateStage(issue.id, Stage.Build);
          issueRepo.blockIssue(issue.id, 'Build interrupted');
          issueRepo.updateRetryCount(issue.id, 2);

          const changeDir = path.join(tmpRetryDir, 'openspec', 'changes', `${issue.number}-test-change`);
          fs.mkdirSync(changeDir, { recursive: true });
          fs.writeFileSync(path.join(changeDir, 'tasks.json'), JSON.stringify({ version: 1, tasks: [{ id: 'T-001', passes: true }] }));

          const eventBus = new EventBus();
          const agentRunner = new AgentRunnerService(eventBus, undefined, stateManager.getIssueRepo(), 8, undefined, undefined, undefined, undefined, stateManager.getIssueTaskQueueRepo());
          const enqueueSpy = vi.spyOn(agentRunner, 'enqueue').mockReturnValue({ taskId: 'fake', status: 'pending' });

          const mockWm = {
            getPath: () => tmpRetryDir,
            exists: () => true,
          } as any;

          const retryApp = new Hono();
          retryApp.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, mockWm, undefined, agentRunner, undefined, undefined, undefined, undefined, undefined, stateManager.getPipelineCheckpointRepo()));
          const retryServer = createTestServer(retryApp);

          const response = await request(retryServer).post(`/api/issues/${issue.number}/retry`);

          expect(response.status).toBe(202);
          expect(response.body.success).toBe(true);
          expect(response.body.data.message).toContain('retrying from checkpoint');

          const updated = issueRepo.findById(issue.id);
          expect(updated?.status).toBe(IssueStatus.Active);
          expect(updated?.blockedReason).toBeUndefined();
          expect(updated?.retryCount).toBe(0);

          expect(enqueueSpy).toHaveBeenCalledTimes(1);
          expect(enqueueSpy).toHaveBeenCalledWith(issue.id, 'resume-pipeline');
        } finally {
          fs.rmSync(tmpRetryDir, { recursive: true, force: true });
        }
      });

      it('should return 409 when issue is not blocked', async () => {
        await issueService.create({ projectId, title: 'Active Issue' });
        const eventBus = new EventBus();
        const agentRunner = new AgentRunnerService(eventBus);
        server = createRetryServer(agentRunner);

        const response = await request(server).post('/api/issues/1/retry');

        expect(response.status).toBe(409);
        expect(response.body.error).toContain('not blocked');
      });

      it('should retry even when issue has a running slot (queue handles concurrency)', async () => {
        const issue = createBlockedIssue('Running Agent');
        const eventBus = new EventBus();
        const agentRunner = new AgentRunnerService(eventBus, undefined, stateManager.getIssueRepo(), 8, undefined, undefined, undefined, undefined, stateManager.getIssueTaskQueueRepo());
        (agentRunner as any).runningSlots.set(issue.id, { id: 'fake-task', issueId: issue.id });
        server = createRetryServer(agentRunner);

        const response = await request(server).post(`/api/issues/${issue.number}/retry`);

        expect(response.status).toBe(200);
        expect(response.body.success).toBe(true);
        expect(response.body.data.message).toContain('no checkpoint found');
      });

      it('should return 404 when issue not found', async () => {
        const eventBus = new EventBus();
        const agentRunner = new AgentRunnerService(eventBus);
        server = createRetryServer(agentRunner);

        const response = await request(server).post('/api/issues/999/retry');

        expect(response.status).toBe(404);
      });

      it('should reset to backlog when no checkpoint found', async () => {
        const issue = createBlockedIssue('No Checkpoint');
        const eventBus = new EventBus();
        const agentRunner = new AgentRunnerService(eventBus, undefined, stateManager.getIssueRepo(), 8);
        server = createRetryServer(agentRunner);

        const response = await request(server).post(`/api/issues/${issue.number}/retry`);

        expect(response.status).toBe(200);
        expect(response.body.data.message).toContain('no checkpoint found');
        expect(response.body.data.message).toContain('reset to draft');

        const issueRepo = stateManager.getIssueRepo();
        const updated = issueRepo.findById(issue.id);
        expect(updated?.stage).toBe(Stage.Backlog);
        expect(updated?.status).toBe(IssueStatus.Active);
      });
    });

    describe('POST /api/issues/:number/restart', () => {
      it('should reject restart for merged blocked issue and require manual intervention', async () => {
        const issue = createBlockedIssue('Merged Restart');
        issueRepo.updateStage(issue.id, Stage.Done);
        issueRepo.update(issue.id, { mergeState: MergeState.Merged });
        const eventBus = new EventBus();
        const agentRunner = new AgentRunnerService(eventBus);
        server = createRetryServer(agentRunner);

        const response = await request(server).post(`/api/issues/${issue.number}/restart`);

        expect(response.status).toBe(409);
        expect(response.body.error).toContain('manual intervention');

        const updated = stateManager.getIssueRepo().findById(issue.id);
        expect(updated?.stage).toBe(Stage.Done);
        expect(updated?.status).toBe(IssueStatus.Blocked);
      });

      it('should reject restart for integrate-stage blocked issue and require manual intervention', async () => {
        const issue = createBlockedIssue('Integrate Restart');
        issueRepo.updateStage(issue.id, Stage.Integrate);
        const eventBus = new EventBus();
        const agentRunner = new AgentRunnerService(eventBus);
        server = createRetryServer(agentRunner);

        const response = await request(server).post(`/api/issues/${issue.number}/restart`);

        expect(response.status).toBe(409);
        expect(response.body.error).toContain('manual intervention');

        const updated = stateManager.getIssueRepo().findById(issue.id);
        expect(updated?.stage).toBe(Stage.Integrate);
        expect(updated?.status).toBe(IssueStatus.Blocked);
      });

      it('should restart a blocked issue to backlog and return 200', async () => {
        const issue = createBlockedIssue('Restart Test');
        const eventBus = new EventBus();
        const agentRunner = new AgentRunnerService(eventBus);
        server = createRetryServer(agentRunner);

        const response = await request(server).post(`/api/issues/${issue.number}/restart`);

        expect(response.status).toBe(200);
        expect(response.body.success).toBe(true);
        expect(response.body.data.message).toContain('reset to draft');
        expect(response.body.data.message).toContain('start to begin again');

        const issueRepo = stateManager.getIssueRepo();
        const updated = issueRepo.findById(issue.id);
        expect(updated?.stage).toBe(Stage.Backlog);
        expect(updated?.status).toBe(IssueStatus.Active);
        expect(updated?.blockedReason).toBeUndefined();
        expect(updated?.retryCount).toBe(0);
        expect(updated?.approvalState).toBeUndefined();
      });

      it('should return 409 when issue is not blocked', async () => {
        await issueService.create({ projectId, title: 'Active Issue' });
        const eventBus = new EventBus();
        const agentRunner = new AgentRunnerService(eventBus);
        server = createRetryServer(agentRunner);

        const response = await request(server).post('/api/issues/1/restart');

        expect(response.status).toBe(409);
        expect(response.body.error).toContain('not blocked');
      });

      it('should return 409 when agent is already running', async () => {
        const issue = createBlockedIssue('Running Restart');
        const eventBus = new EventBus();
        const agentRunner = new AgentRunnerService(eventBus, undefined, stateManager.getIssueRepo(), 8, undefined, undefined, undefined, undefined, stateManager.getIssueTaskQueueRepo());
        (agentRunner as any).runningSlots.set(issue.id, { id: 'fake-task', issueId: issue.id });
        server = createRetryServer(agentRunner);

        const response = await request(server).post(`/api/issues/${issue.number}/restart`);

        expect(response.status).toBe(409);
        expect(response.body.error).toContain('already has a running task');
      });

      it('should return 404 when issue not found', async () => {
        const eventBus = new EventBus();
        const agentRunner = new AgentRunnerService(eventBus);
        server = createRetryServer(agentRunner);

        const response = await request(server).post('/api/issues/999/restart');

        expect(response.status).toBe(404);
      });
    });

    describe('POST /api/issues/:number/start rejects blocked', () => {
      it('should return 400 when trying to start a blocked issue', async () => {
        const issue = createBlockedIssue('Blocked Start');
        const eventBus = new EventBus();
        const agentRunner = new AgentRunnerService(eventBus);
        server = createRetryServer(agentRunner);

        const response = await request(server).post(`/api/issues/${issue.number}/start`);

        expect(response.status).toBe(400);
        expect(response.body.error).toContain('blocked');
        expect(response.body.error).toContain('retry');
      });
    });

    describe('GET /api/issues/:number returns blockedReason', () => {
      it('should return blockedReason for blocked issue', async () => {
        const issue = createBlockedIssue('Show Reason');
        const eventBus = new EventBus();
        const agentRunner = new AgentRunnerService(eventBus);
        server = createRetryServer(agentRunner);

        const response = await request(server).get(`/api/issues/${issue.number}`);

        expect(response.status).toBe(200);
        expect(response.body.data.blockedReason).toContain('Build 中断');
        expect(response.body.data.retryCount).toBe(3);
      });

      it('should return undefined blockedReason for non-blocked issue', async () => {
        await issueService.create({ projectId, title: 'Normal Issue' });
        const eventBus = new EventBus();
        const agentRunner = new AgentRunnerService(eventBus);
        server = createRetryServer(agentRunner);

        const response = await request(server).get('/api/issues/1');

        expect(response.status).toBe(200);
        expect(response.body.data.blockedReason).toBeUndefined();
      });
    });
  });

  describe('Agent Status Routes', () => {
    let server: http.Server;

    beforeEach(async () => {
      const app = new Hono();
      const eventBus = new EventBus();
      const agentRunner = new AgentRunnerService(eventBus, undefined, stateManager.getIssueRepo(), 8);
      const { createAgentRoutes } = await import('../src/api/agent');
      app.route('/api/agent', createAgentRoutes(agentRunner));
      server = createTestServer(app);
    });

    it('should return blockedIssues array in agent status', async () => {
      const response = await request(server).get('/api/agent/status');

      expect(response.status).toBe(200);
      expect(response.body.success).toBe(true);
      expect(response.body.data.blockedIssues).toBeDefined();
      expect(Array.isArray(response.body.data.blockedIssues)).toBe(true);
    });

    it('should return blocked issues with reason and retryCount', async () => {
      const project = await projectService.create({ name: 'AgentTest', path: '/test/path' });
      const issue = issueService.create({ projectId: project.id, title: 'Blocked' });
      const issueRepo = stateManager.getIssueRepo();
      issueRepo.blockIssue(issue.id, 'Test blocked reason');
      issueRepo.updateRetryCount(issue.id, 2);

      const response = await request(server).get('/api/agent/status');

      expect(response.status).toBe(200);
      const blocked = response.body.data.blockedIssues;
      expect(blocked).toHaveLength(1);
      expect(blocked[0].issueNumber).toBe(issue.number);
      expect(blocked[0].blockedReason).toBe('Test blocked reason');
      expect(blocked[0].retryCount).toBe(2);
      expect(blocked[0].stage).toBeDefined();
    });

    it('should return empty blockedIssues when none blocked', async () => {
      const response = await request(server).get('/api/agent/status');

      expect(response.status).toBe(200);
      expect(response.body.data.blockedIssues).toEqual([]);
    });
  });

  describe('POST /api/issues/:number/merge', () => {
    let server: http.Server;

    afterEach(() => {
      server?.close();
    });

    function createMergeApp(worktreeManager: any): http.Server {
      const app = new Hono();
      app.route('/api/issues', createIssueRoutes(
        issueService,
        projectService,
        stateManager,
        worktreeManager,
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
      ));
      return createTestServer(app);
    }

    it('rejects direct merge when issue is not in Integrate stage with 409', async () => {
      const project = await projectService.create({ name: 'Test Project', path: '/tmp/test-project' });
      projectService.setCurrent(project);
      const issue = await issueService.create({ projectId: project.id, title: 'Merge Me' });
      const worktreeManager = {
        exists: vi.fn().mockReturnValue(true),
      };
      server = createMergeApp(worktreeManager);

      const response = await request(server).post(`/api/issues/${issue.number}/merge`);

      expect(response.status).toBe(409);
      expect(response.body.success).toBe(false);
      expect(response.body.error).toContain('Direct merge is not allowed');
      expect(response.body.error).toContain('Use Check approval');
    });

it('allows merge when issue is in Integrate stage and enqueues resume-pipeline', async () => {
      const project = await projectService.create({ name: 'Test Project', path: '/tmp/test-project' });
      projectService.setCurrent(project);
      const issue = await issueService.create({ projectId: project.id, title: 'Merge Me' });
      const issueRepo = stateManager.getIssueRepo();
      issueRepo.updateStage(issue.id, Stage.Integrate);
      issueRepo.setMergeState(issue.id, MergeState.Merged);

      const eventBus = new EventBus();
      const agentRunner = new AgentRunnerService(eventBus, undefined, issueRepo, 8, undefined, undefined, undefined, undefined, stateManager.getIssueTaskQueueRepo());

      const app = new Hono();
      app.route('/api/issues', createIssueRoutes(
        issueService,
        projectService,
        stateManager,
        undefined,
        undefined,
        agentRunner,
      ));
      server = createTestServer(app);

      const response = await request(server).post(`/api/issues/${issue.number}/merge`);

      expect(response.status).toBe(202);
      expect(response.body.success).toBe(true);
      expect(response.body.data.message).toContain('routed to Integrate');
    });

    it('returns error when AgentRunnerService not configured', async () => {
      const project = await projectService.create({ name: 'Test Project', path: '/tmp/test-project' });
      projectService.setCurrent(project);
      const issue = await issueService.create({ projectId: project.id, title: 'Merge Me' });
      const issueRepo = stateManager.getIssueRepo();
      issueRepo.updateStage(issue.id, Stage.Integrate);
      issueRepo.setMergeState(issue.id, MergeState.Merged);

      const app = new Hono();
      app.route('/api/issues', createIssueRoutes(
        issueService,
        projectService,
        stateManager,
        undefined,
        undefined,
        undefined,
      ));
      server = createTestServer(app);

      const response = await request(server).post(`/api/issues/${issue.number}/merge`);

      expect(response.status).toBe(500);
      expect(response.body.error).toContain('AgentRunnerService not configured');
    });
  });

  describe('Issue Commits Routes', () => {
    let server: http.Server;
    let projectId: string;
    let tmpDir: string;
    let repoDir: string;

    async function initGitRepo(dir: string): Promise<void> {
      const execAsync = promisify(execFile);
      await execAsync('git', ['init', '-b', 'main'], { cwd: dir });
      await execAsync('git', ['config', 'user.email', 'test@test.com'], { cwd: dir });
      await execAsync('git', ['config', 'user.name', 'Test'], { cwd: dir });
      fs.writeFileSync(path.join(dir, 'README.md'), 'init');
      await execAsync('git', ['add', '-A'], { cwd: dir });
      await execAsync('git', ['commit', '-m', 'init'], { cwd: dir });
    }

    beforeEach(async () => {
      tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-commits-test-'));
      repoDir = path.join(tmpDir, 'repo');
      fs.mkdirSync(repoDir);
      await initGitRepo(repoDir);

      const { WorktreeManager } = await import('../src/git/worktree-manager');
      const app = new Hono();
      const eventBus = new EventBus();
      const agentRunner = new AgentRunnerService(eventBus);
      const wm = new WorktreeManager();
      app.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, wm, undefined, undefined, agentRunner));
      server = createTestServer(app);

      const project = await projectService.create({ name: 'Test Project', path: repoDir });
      projectId = project.id;
      projectService.setCurrent(project);
    });

    afterEach(() => {
      server?.close();
      fs.rmSync(tmpDir, { recursive: true, force: true });
    });

    describe('GET /api/issues/:number/commits', () => {
      it('should return 400 when no active project', async () => {
        projectService.clearCurrent();
        await issueService.create({ projectId, title: 'Test Issue' });

        const response = await request(server).get('/api/issues/1/commits');

        expect(response.status).toBe(400);
        expect(response.body.error).toContain('No active project');
      });

      it('should return 404 when issue not found', async () => {
        const response = await request(server).get('/api/issues/999/commits');

        expect(response.status).toBe(404);
        expect(response.body.error).toContain('not found');
      });

      it('should return unavailable when no draft worktree exists', async () => {
        await issueService.create({ projectId, title: 'Test Issue' });

        const response = await request(server).get('/api/issues/1/commits');

        expect(response.status).toBe(200);
        expect(response.body.data.available).toBe(false);
        expect(response.body.data.reason).toBe('not_started');
      });

      it('should return commits with correct fields', async () => {
        await issueService.create({ projectId, title: 'Test Issue' });

        const git = promisify(execFile);
        await git('git', ['checkout', '-b', 'mo/issue-1'], { cwd: repoDir });
        fs.writeFileSync(path.join(repoDir, 'test.txt'), 'hello');
        await git('git', ['add', '-A'], { cwd: repoDir });
        await git('git', ['commit', '-m', 'add test file'], { cwd: repoDir });
        await git('git', ['checkout', 'main'], { cwd: repoDir });

        const worktreeDir = path.join(os.homedir(), '.mohist', 'projects', 'test-project', 'worktrees', 'issue-1');
        fs.mkdirSync(worktreeDir, { recursive: true });

        const response = await request(server).get('/api/issues/1/commits');

        expect(response.status).toBe(200);
        expect(response.body.data.commits.length).toBeGreaterThanOrEqual(1);

        const commit = response.body.data.commits[0];
        expect(commit.hash).toBeDefined();
        expect(commit.message).toBe('add test file');
        expect(commit.author).toBe('Test');
        expect(commit.date).toBeDefined();
        expect(typeof commit.filesChanged).toBe('number');
        expect(typeof commit.additions).toBe('number');
        expect(typeof commit.deletions).toBe('number');

        fs.rmSync(worktreeDir, { recursive: true, force: true });
      });
    });

    describe('GET /api/issues/:number/commits/:hash/diff', () => {
      it('should return 400 when no active project', async () => {
        projectService.clearCurrent();
        await issueService.create({ projectId, title: 'Test Issue' });

        const response = await request(server).get('/api/issues/1/commits/abc1234/diff');

        expect(response.status).toBe(400);
        expect(response.body.error).toContain('No active project');
      });

      it('should return 400 for invalid hash format', async () => {
        await issueService.create({ projectId, title: 'Test Issue' });

        const response = await request(server).get('/api/issues/1/commits/not-a-hash/diff');

        expect(response.status).toBe(400);
        expect(response.body.error).toContain('Invalid commit hash');
      });

      it('should return 404 when issue not found', async () => {
        const response = await request(server).get('/api/issues/999/commits/abc1234/diff');

        expect(response.status).toBe(404);
        expect(response.body.error).toContain('not found');
      });

      it('should return unavailable when no draft worktree exists', async () => {
        await issueService.create({ projectId, title: 'Test Issue' });

        const response = await request(server).get('/api/issues/1/commits/abc1234/diff');

        expect(response.status).toBe(200);
        expect(response.body.data.available).toBe(false);
        expect(response.body.data.reason).toBe('not_started');
      });
    });
  });
});
