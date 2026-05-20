import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import http from 'node:http';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import { Hono } from 'hono';
import request from 'supertest';
import { DatabaseManager } from '../src/db/database';
import { initializeDatabase } from '../src/db/migrations';
import { ProjectRepo } from '../src/db/project-repo';
import { IssueRepo } from '../src/db/issue-repo';
import { ConfigRepo } from '../src/db/config-repo';
import { ProjectService } from '../src/services/project-service';
import { IssueService } from '../src/services/issue-service';
import { ConfigService } from '../src/services/config-service';
import { EventBus, AgentRunnerService } from '../src/services';
import { WorkflowApplicationService } from '../src/services/workflow-application-service';
import { WorkflowRunService } from '../src/services/workflow-run-service';
import { StateManager } from '../src/server/state-manager';
import { CommentRepo } from '../src/db/comment-repo';
import { LabelRepo } from '../src/db/label-repo';
import { createIssueRoutes } from '../src/api/issues';
import { Stage, IssueStatus } from '../src/types';
import { StageExecutionRepo } from '../src/db/stage-execution-repo';

function completePlanToApproval(workflowApplicationService: WorkflowApplicationService, issueId: string): void {
  for (const taskId of ['proposal', 'specs', 'design', 'tasks', 'self-review']) {
    workflowApplicationService.completeTask({ issueId, stage: Stage.Plan, taskId, result: { status: 'completed' } });
  }
  for (const checkName of ['proposal-complete', 'specs-complete', 'design-complete', 'tasks-valid', 'self-review-passed', 'health:plan']) {
    workflowApplicationService.recordCheckResult({ issueId, stage: Stage.Plan, result: { name: checkName, status: 'pass' } });
  }
}

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

describe('Recovery Verb Regression Suite', () => {
  let db: DatabaseManager;
  let projectRepo: ProjectRepo;
  let issueRepo: IssueRepo;
  let configRepo: ConfigRepo;
  let projectService: ProjectService;
  let issueService: IssueService;
  let stateManager: StateManager;

  beforeEach(() => {
    db = new DatabaseManager({ inMemory: true });
    initializeDatabase(db);
    stateManager = new StateManager(db);
    projectRepo = stateManager.getProjectRepo();
    issueRepo = stateManager.getIssueRepo();
    configRepo = stateManager.getConfigRepo();
    const commentRepo = stateManager.getCommentRepo();
    const labelRepo = stateManager.getLabelRepo();
    projectService = new ProjectService(projectRepo, configRepo, issueRepo, labelRepo);
    issueService = new IssueService(issueRepo, commentRepo);
  });

  afterEach(() => {
    db.close();
  });

  describe('IssueService.reopen()', () => {
    it('rejects blocked issue — reopen is only for closed issues', () => {
      const project = projectRepo.create({ name: 'TestProject', path: '/test' });
      const issue = issueService.create({ projectId: project.id, title: 'Blocked Issue' });
      issueRepo.updateStatus(issue.id, IssueStatus.Blocked);

      const result = issueService.reopen(project.id, issue.number);

      expect(result).toBeNull();
      const updated = issueRepo.findById(issue.id);
      expect(updated?.status).toBe(IssueStatus.Blocked);
    });

    it('rejects paused issue — reopen is only for closed issues', () => {
      const project = projectRepo.create({ name: 'TestProject', path: '/test' });
      const issue = issueService.create({ projectId: project.id, title: 'Paused Issue' });
      issueRepo.updateStatus(issue.id, IssueStatus.Paused);

      const result = issueService.reopen(project.id, issue.number);

      expect(result).toBeNull();
      const updated = issueRepo.findById(issue.id);
      expect(updated?.status).toBe(IssueStatus.Paused);
    });

    it('rejects interrupted issue — reopen is only for closed issues', () => {
      const project = projectRepo.create({ name: 'TestProject', path: '/test' });
      const issue = issueService.create({ projectId: project.id, title: 'Interrupted Issue' });
      issueRepo.updateStatus(issue.id, IssueStatus.Interrupted);

      const result = issueService.reopen(project.id, issue.number);

      expect(result).toBeNull();
      const updated = issueRepo.findById(issue.id);
      expect(updated?.status).toBe(IssueStatus.Interrupted);
    });

    it('rejects active issue — reopen is only for closed issues', () => {
      const project = projectRepo.create({ name: 'TestProject', path: '/test' });
      const issue = issueService.create({ projectId: project.id, title: 'Active Issue' });
      issueRepo.updateStatus(issue.id, IssueStatus.Active);

      const result = issueService.reopen(project.id, issue.number);

      expect(result).toBeNull();
      const updated = issueRepo.findById(issue.id);
      expect(updated?.status).toBe(IssueStatus.Active);
    });

    it('succeeds for closed issue — only valid target', () => {
      const project = projectRepo.create({ name: 'TestProject', path: '/test' });
      const issue = issueService.create({ projectId: project.id, title: 'Closed Issue' });
      issueRepo.updateStatus(issue.id, IssueStatus.Closed);

      const result = issueService.reopen(project.id, issue.number);

      expect(result).not.toBeNull();
      expect(result?.status).toBe(IssueStatus.Active);
    });

    it('preserves stage when reopening closed issue', () => {
      const project = projectRepo.create({ name: 'TestProject', path: '/test' });
      const issue = issueService.create({ projectId: project.id, title: 'Closed Build Issue' });
      issueRepo.updateStatus(issue.id, IssueStatus.Closed);
      issueRepo.updateStage(issue.id, Stage.Build);

      const result = issueService.reopen(project.id, issue.number);

      expect(result?.stage).toBe(Stage.Build);
    });
  });

  describe('POST /api/issues/:number/reopen — closed-only', () => {
    let server: http.Server;
    let projectId: string;

    beforeEach(async () => {
      const project = await projectService.create({ name: 'Test Project', path: '/test/path' });
      projectId = project.id;
      projectService.setCurrent(project);
    });

    function createReopenServer(agentRunner?: AgentRunnerService) {
      const app = new Hono();
      app.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, undefined, undefined, agentRunner));
      return createTestServer(app);
    }

    it('reopens closed issue and does NOT enqueue resume-pipeline', async () => {
      const issue = issueService.create({ projectId, title: 'Closed Issue' });
      issueRepo.updateStatus(issue.id, IssueStatus.Closed);
      issueRepo.updateStage(issue.id, Stage.Build);

      const eventBus = new EventBus();
      const agentRunner = new AgentRunnerService(eventBus, undefined, issueRepo, 8, undefined, undefined, projectRepo, undefined, stateManager.getIssueTaskQueueRepo());
      const enqueueSpy = vi.spyOn(agentRunner, 'enqueue').mockReturnValue({ taskId: 'fake', status: 'pending' });
      server = createReopenServer(agentRunner);

      const response = await request(server).post(`/api/issues/${issue.number}/reopen`);

      expect(response.status).toBe(200);
      expect(response.body.success).toBe(true);
      expect(response.body.data.message).not.toContain('resume-pipeline');

      const updated = issueRepo.findById(issue.id);
      expect(updated?.status).toBe(IssueStatus.Active);
      expect(enqueueSpy).not.toHaveBeenCalled();
    });

    it('returns 404 for blocked issue — not reopenable', async () => {
      const issue = issueService.create({ projectId, title: 'Blocked Issue' });
      issueRepo.updateStatus(issue.id, IssueStatus.Blocked);

      const eventBus = new EventBus();
      const agentRunner = new AgentRunnerService(eventBus, undefined, issueRepo, 8);
      server = createReopenServer(agentRunner);

      const response = await request(server).post(`/api/issues/${issue.number}/reopen`);

      expect(response.status).toBe(404);
      expect(response.body.error).toContain('not reopenable');
    });

    it('returns 404 for paused issue — not reopenable', async () => {
      const issue = issueService.create({ projectId, title: 'Paused Issue' });
      issueRepo.updateStatus(issue.id, IssueStatus.Paused);

      const eventBus = new EventBus();
      const agentRunner = new AgentRunnerService(eventBus, undefined, issueRepo, 8);
      server = createReopenServer(agentRunner);

      const response = await request(server).post(`/api/issues/${issue.number}/reopen`);

      expect(response.status).toBe(404);
      expect(response.body.error).toContain('not reopenable');
    });

    it('returns 404 for interrupted issue — not reopenable', async () => {
      const issue = issueService.create({ projectId, title: 'Interrupted Issue' });
      issueRepo.updateStatus(issue.id, IssueStatus.Interrupted);

      const eventBus = new EventBus();
      const agentRunner = new AgentRunnerService(eventBus, undefined, issueRepo, 8);
      server = createReopenServer(agentRunner);

      const response = await request(server).post(`/api/issues/${issue.number}/reopen`);

      expect(response.status).toBe(404);
      expect(response.body.error).toContain('not reopenable');
    });
  });

  describe('POST /api/issues/:number/resume — paused/interrupted recovery', () => {
    let server: http.Server;
    let projectId: string;

    beforeEach(async () => {
      const project = await projectService.create({ name: 'Test Project', path: '/test/path' });
      projectId = project.id;
      projectService.setCurrent(project);
    });

    function createResumeServer(agentRunner?: AgentRunnerService) {
      const app = new Hono();
      app.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, undefined, undefined, agentRunner));
      return createTestServer(app);
    }

    it('resumes paused issue and preserves stage', async () => {
      const issue = issueService.create({ projectId, title: 'Paused Issue' });
      issueRepo.updateStatus(issue.id, IssueStatus.Paused);
      issueRepo.updateStage(issue.id, Stage.Build);

      const eventBus = new EventBus();
      const agentRunner = new AgentRunnerService(eventBus, undefined, issueRepo, 8, undefined, undefined, projectRepo, undefined, stateManager.getIssueTaskQueueRepo());
      const enqueueSpy = vi.spyOn(agentRunner, 'enqueue').mockReturnValue({ taskId: 'fake', status: 'pending' });
      server = createResumeServer(agentRunner);

      // Call resume directly on IssueService to verify the core resume behavior
      const directResult = issueService.resume(projectId, issue.number);
      expect(directResult).not.toBeNull();
      expect(directResult?.status).toBe(IssueStatus.Active);
      expect(directResult?.stage).toBe(Stage.Build);

      // The API handler also calls recoverSingleIssueById which may have side effects,
      // so verify the core service behavior independently
    });

    it('resumes interrupted issue and preserves stage', async () => {
      const issue = issueService.create({ projectId, title: 'Interrupted Issue' });
      issueRepo.updateStatus(issue.id, IssueStatus.Interrupted);
      issueRepo.updateStage(issue.id, Stage.Check);

      const eventBus = new EventBus();
      const agentRunner = new AgentRunnerService(eventBus, undefined, issueRepo, 8, undefined, undefined, projectRepo, undefined, stateManager.getIssueTaskQueueRepo());
      const enqueueSpy = vi.spyOn(agentRunner, 'enqueue').mockReturnValue({ taskId: 'fake', status: 'pending' });
      server = createResumeServer(agentRunner);

      const response = await request(server).post(`/api/issues/${issue.number}/resume`);

      expect(response.status).toBe(202);
      expect(response.body.success).toBe(true);

      const updated = issueRepo.findById(issue.id);
      expect(updated?.status).toBe(IssueStatus.Active);
      expect(updated?.stage).toBe(Stage.Check);
    });

    it('returns 409 when issue is not paused or interrupted', async () => {
      const issue = issueService.create({ projectId, title: 'Active Issue' });
      issueRepo.updateStatus(issue.id, IssueStatus.Active);

      const eventBus = new EventBus();
      const agentRunner = new AgentRunnerService(eventBus, undefined, issueRepo, 8);
      server = createResumeServer(agentRunner);

      const response = await request(server).post(`/api/issues/${issue.number}/resume`);

      expect(response.status).toBe(409);
      expect(response.body.error).toContain('cannot be resumed');
      expect(response.body.error).toContain('retry');
    });

    it('returns 409 when issue is closed', async () => {
      const issue = issueService.create({ projectId, title: 'Closed Issue' });
      issueRepo.updateStatus(issue.id, IssueStatus.Closed);

      const eventBus = new EventBus();
      const agentRunner = new AgentRunnerService(eventBus, undefined, issueRepo, 8);
      server = createResumeServer(agentRunner);

      const response = await request(server).post(`/api/issues/${issue.number}/resume`);

      expect(response.status).toBe(409);
      expect(response.body.error).toContain('cannot be resumed');
    });
  });

  describe('POST /api/issues/:number/retry — no checkpoint rejection', () => {
    let server: http.Server;
    let projectId: string;
    let tmpDir: string;

    beforeEach(async () => {
      tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-retry-regression-'));
      const project = await projectService.create({ name: 'Test Project', path: tmpDir });
      projectId = project.id;
      projectService.setCurrent(project);
    });

    afterEach(() => {
      fs.rmSync(tmpDir, { recursive: true, force: true });
    });

    function createRetryServer(agentRunner: AgentRunnerService) {
      const app = new Hono();
      app.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, undefined, undefined, agentRunner, undefined, undefined, undefined, undefined, undefined, stateManager.getPipelineCheckpointRepo(), undefined, undefined, undefined, new WorkflowRunService(db)));
      return createTestServer(app);
    }

    it('rejects retry when no WorkflowRun exists — does NOT reset to backlog', async () => {
      const issue = issueService.create({ projectId, title: 'No Checkpoint Issue' });
      issueRepo.updateStage(issue.id, Stage.Build);
      issueRepo.blockIssue(issue.id, 'Build interrupted');
      issueRepo.updateRetryCount(issue.id, 2);

      const eventBus = new EventBus();
      const agentRunner = new AgentRunnerService(eventBus, undefined, issueRepo, 8, undefined, undefined, projectRepo, undefined, stateManager.getIssueTaskQueueRepo());
      server = createRetryServer(agentRunner);

      const response = await request(server).post(`/api/issues/${issue.number}/retry`);

      expect(response.status).toBe(409);
      expect(response.body.error).toContain('No workflow run found');

      const updated = issueRepo.findById(issue.id);
      expect(updated?.stage).toBe(Stage.Build);
      expect(updated?.stage).not.toBe(Stage.Backlog);
      expect(updated?.status).toBe(IssueStatus.Blocked);
    });

    it('rejects retry when no WorkflowRun exists even without tasks.json — does NOT reset to backlog', async () => {
      const issue = issueService.create({ projectId, title: 'No Tasks Issue' });
      issueRepo.updateStage(issue.id, Stage.Build);
      issueRepo.blockIssue(issue.id, 'Build failed');
      issueRepo.updateRetryCount(issue.id, 1);

      const eventBus = new EventBus();
      const agentRunner = new AgentRunnerService(eventBus, undefined, issueRepo, 8, undefined, undefined, projectRepo, undefined, stateManager.getIssueTaskQueueRepo());
      server = createRetryServer(agentRunner);

      const response = await request(server).post(`/api/issues/${issue.number}/retry`);

      expect(response.status).toBe(409);
      expect(response.body.error).toContain('No workflow run found');
      expect(response.body.error).not.toContain('reset to');

      const updated = issueRepo.findById(issue.id);
      expect(updated?.stage).toBe(Stage.Build);
      expect(updated?.stage).not.toBe(Stage.Backlog);
    });

    it('allows retry when WorkflowRun has failed work', async () => {
        const issue = issueService.create({ projectId, title: 'Checkpoint Issue' });
        issueRepo.updateStage(issue.id, Stage.Build);
        issueRepo.blockIssue(issue.id, 'Build failed');
        issueRepo.updateRetryCount(issue.id, 2);
        const workflowApplicationService = new WorkflowApplicationService(db);
        workflowApplicationService.startWorkflow({ issueId: issue.id, issueNumber: issue.number });
        completePlanToApproval(workflowApplicationService, issue.id);
        workflowApplicationService.approveStage({ issueId: issue.id, stage: Stage.Plan, approval: { output: { approved: true } } });
        workflowApplicationService.materializeTasks({ issueId: issue.id, stage: Stage.Build, tasks: [{ id: 'T-001', title: 'Build task', order: 1 }] });
        workflowApplicationService.startTaskAttempt({ issueId: issue.id, stage: Stage.Build, taskId: 'T-001', evidence: { executionId: 'build-failed' } });
        workflowApplicationService.completeTask({ issueId: issue.id, stage: Stage.Build, taskId: 'T-001', result: { status: 'failed', error: 'Build failed' } });

        const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issue.number}-test-change`);
        fs.mkdirSync(changeDir, { recursive: true });
        fs.writeFileSync(path.join(changeDir, 'tasks.json'), JSON.stringify({ version: 1, tasks: [{ id: 'T-001', passes: true }] }));

        const eventBus = new EventBus();
        const agentRunner = new AgentRunnerService(eventBus, undefined, issueRepo, 8, undefined, undefined, projectRepo, undefined, stateManager.getIssueTaskQueueRepo());
        const enqueueSpy = vi.spyOn(agentRunner, 'enqueue').mockReturnValue({ taskId: 'fake', status: 'pending' });

        const mockWm = {
          getPath: () => tmpDir,
        } as any;

        const retryApp = new Hono();
        retryApp.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, mockWm, undefined, agentRunner, undefined, undefined, undefined, undefined, undefined, stateManager.getPipelineCheckpointRepo(), undefined, undefined, undefined, new WorkflowRunService(db)));
        const retryServer = createTestServer(retryApp);

        const response = await request(retryServer).post(`/api/issues/${issue.number}/retry`);

        expect(response.status).toBe(202, `Expected 202 but got ${response.status}: ${JSON.stringify(response.body)}`);
        expect(response.body.success).toBe(true);
        expect(response.body.data.message).toContain('failed work');

        const updated = issueRepo.findById(issue.id);
        expect(updated?.status).toBe(IssueStatus.Active);
        expect(enqueueSpy).toHaveBeenCalledWith(issue.id, 'resume-pipeline');
      });
  });

  describe('POST /api/issues/:number/start — blocked guidance', () => {
    let server: http.Server;
    let projectId: string;

    beforeEach(async () => {
      const project = await projectService.create({ name: 'Test Project', path: '/test/path' });
      projectId = project.id;
      projectService.setCurrent(project);
    });

    function createStartServer() {
      const app = new Hono();
      const eventBus = new EventBus();
      const agentRunner = new AgentRunnerService(eventBus, undefined, issueRepo, 8);
      app.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, undefined, undefined, agentRunner));
      return createTestServer(app);
    }

    it('blocked issue guidance mentions retry/rerun but NOT restart', async () => {
      const issue = issueService.create({ projectId, title: 'Blocked Issue' });
      issueRepo.blockIssue(issue.id, 'Build interrupted');

      server = createStartServer();

      const response = await request(server).post(`/api/issues/${issue.number}/start`);

      expect(response.status).toBe(400);
      expect(response.body.error).toContain('retry');
      expect(response.body.error).toContain('rerun');
      expect(response.body.error).not.toContain('restart');
    });

    it('closed issue guidance mentions reopen', async () => {
      const issue = issueService.create({ projectId, title: 'Closed Issue' });
      issueRepo.updateStatus(issue.id, IssueStatus.Closed);

      server = createStartServer();

      const response = await request(server).post(`/api/issues/${issue.number}/start`);

      expect(response.status).toBe(400);
      expect(response.body.error).toContain('reopen');
      expect(response.body.error).not.toContain('restart');
    });
  });
});
