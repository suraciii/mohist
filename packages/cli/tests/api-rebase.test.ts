import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import http from 'node:http';
import { Hono } from 'hono';
import request from 'supertest';
import { DatabaseManager } from '../src/db/database';
import { ProjectService } from '../src/services/project-service';
import { IssueService } from '../src/services/issue-service';
import { EventBus, AgentRunnerService } from '../src/services';
import { StateManager } from '../src/server/state-manager';
import { ConfigRepo } from '../src/db/config-repo';
import { ConfigService } from '../src/services/config-service';
import { createIssueRoutes } from '../src/api/issues';
import { Stage, IssueStatus } from '../src/types';
import { WorkflowRunRepo } from '../src/db/workflow-run-repo';
import { WorkflowRunService } from '../src/services/workflow-run-service';

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

describe('POST /api/issues/:number/rebase', () => {
  let db: DatabaseManager;
  let projectService: ProjectService;
  let issueService: IssueService;
  let stateManager: StateManager;
  let projectId: string;

  beforeEach(() => {
    db = new DatabaseManager({ inMemory: true });
    stateManager = new StateManager(db);
    const projectRepo = stateManager.getProjectRepo();
    const issueRepo = stateManager.getIssueRepo();
    const commentRepo = stateManager.getCommentRepo();
    const labelRepo = stateManager.getLabelRepo();
    const configRepo = stateManager.getConfigRepo();
    projectService = new ProjectService(projectRepo, configRepo, issueRepo, labelRepo);
    issueService = new IssueService(issueRepo, commentRepo);
  });

  function makeServer() {
    const app = new Hono();
    const eventBus = new EventBus();
    const agentRunner = new AgentRunnerService(eventBus);
    vi.spyOn(agentRunner, 'enqueue').mockReturnValue({
      taskId: 'task-123',
      status: 'pending' as const,
      queuePosition: 0,
    });
    app.route('/api/issues', createIssueRoutes(
      issueService, projectService, stateManager,
      null, undefined, agentRunner,
      undefined, undefined, undefined, undefined,
    ));
    return { server: createTestServer(app), agentRunner };
  }

  beforeEach(async () => {
    const project = await projectService.create({ name: 'Test Project', path: '/test/path' });
    projectId = project.id;
    projectService.setCurrent(project);
  });

  afterEach(() => {
    vi.restoreAllMocks();
    db.close();
  });

  describe('precondition checks', () => {
    it('should return 404 for non-existent issue', async () => {
      const { server } = makeServer();
      const res = await request(server).post('/api/issues/999/rebase');
      expect(res.status).toBe(404);
      expect(res.body.error).toContain('not found');
    });

    it('should return 400 for backlog stage', async () => {
      issueService.create({ projectId, title: 'Backlog Issue' });
      const { server } = makeServer();
      const res = await request(server).post('/api/issues/1/rebase');
      expect(res.status).toBe(400);
      expect(res.body.error).toContain('Rebase not available');
      expect(res.body.error).toContain('backlog');
    });
  });

  describe('done stage', () => {
    it('should reject Done stage rebase after workflow completion', async () => {
      const issue = issueService.create({ projectId, title: 'Done Issue' });
      issueService.transitionToStage(issue.id, Stage.Done);

      const { server, agentRunner } = makeServer();
      const res = await request(server).post('/api/issues/1/rebase');
      expect(res.status).toBe(409);
      expect(res.body.error).toContain('done');
      expect(agentRunner.enqueue).not.toHaveBeenCalled();
    });
  });

  describe('non-Done stages with active WorkflowRun', () => {
    function makeServerWithWorkflow() {
      const app = new Hono();
      const eventBus = new EventBus();
      const agentRunner = new AgentRunnerService(eventBus);
      vi.spyOn(agentRunner, 'enqueue').mockReturnValue({
        taskId: 'task-123',
        status: 'pending' as const,
        queuePosition: 0,
      });
      const workflowRunService = new WorkflowRunService(db);
      app.route('/api/issues', createIssueRoutes(
        issueService, projectService, stateManager,
        null, undefined, agentRunner,
        undefined, undefined, undefined, undefined,
        undefined,
        undefined,
        undefined,
        undefined,
        undefined,
        workflowRunService,
      ));
      return { server: createTestServer(app), agentRunner, workflowRunService };
    }

    it('POST /api/issues/:number/rebase schedules rebase-branch through WorkflowRun for Build stage', async () => {
      const issue = issueService.create({ projectId, title: 'Build Issue' });
      issueService.transitionToStage(issue.id, Stage.Build);
      issueService.setStatus(issue.id, IssueStatus.Active);

      const { server, agentRunner, workflowRunService } = makeServerWithWorkflow();

      workflowRunService.startRun(issue.id, issue.number);

      const res = await request(server).post(`/api/issues/${issue.number}/rebase`);

      expect(res.status).toBe(202);
      expect(res.body.data.taskId).toBe('rebase-branch');
      expect(res.body.data.status).toBe('pending');
      expect(res.body.data.message).toContain('Rebase branch task scheduled');
      expect(agentRunner.enqueue).not.toHaveBeenCalled();
    });

    it('POST /api/issues/:number/rebase with active workflow does NOT call agentRunner.enqueue', async () => {
      const issue = issueService.create({ projectId, title: 'Check Issue' });
      issueService.transitionToStage(issue.id, Stage.Check);
      issueService.setStatus(issue.id, IssueStatus.Active);

      const { server, agentRunner, workflowRunService } = makeServerWithWorkflow();

      workflowRunService.startRun(issue.id, issue.number);

      const res = await request(server).post(`/api/issues/${issue.number}/rebase`);

      expect(res.status).toBe(202);
      expect(agentRunner.enqueue).not.toHaveBeenCalled();
      expect(res.body.data.taskId).toBe('rebase-branch');
    });

    it('duplicate rebase click returns same taskId without creating duplicate', async () => {
      const issue = issueService.create({ projectId, title: 'Plan Issue' });
      issueService.transitionToStage(issue.id, Stage.Plan);
      issueService.setStatus(issue.id, IssueStatus.Active);

      const { server, workflowRunService } = makeServerWithWorkflow();

      workflowRunService.startRun(issue.id, issue.number);

      const res1 = await request(server).post(`/api/issues/${issue.number}/rebase`);
      expect(res1.status).toBe(202);
      const firstTaskId = res1.body.data.taskId;

      const res2 = await request(server).post(`/api/issues/${issue.number}/rebase`);
      expect(res2.status).toBe(202);
      expect(res2.body.data.taskId).toBe(firstTaskId);
    });
  });

  describe('enqueue fallback when no active WorkflowRun', () => {
    it('should enqueue rebase task and return 202 when no WorkflowRun exists', async () => {
      const issue = issueService.create({ projectId, title: 'Build Issue' });
      issueService.transitionToStage(issue.id, Stage.Build);
      issueService.setStatus(issue.id, IssueStatus.Active);

      const { server, agentRunner } = makeServer();
      const res = await request(server)
        .post('/api/issues/1/rebase')
        .send({ reEvalPlan: true });
      expect(res.status).toBe(202);
      expect(res.body.data.taskId).toBe('task-123');
      expect(res.body.data.status).toBe('pending');
      expect(res.body.data.queuePosition).toBe(0);
      expect(agentRunner.enqueue).toHaveBeenCalledWith(issue.id, 'rebase', { reEvalPlan: true });
    });

    it('should enqueue rebase with empty payload when no body and no WorkflowRun', async () => {
      const issue = issueService.create({ projectId, title: 'Plan Issue' });
      issueService.transitionToStage(issue.id, Stage.Plan);
      issueService.setStatus(issue.id, IssueStatus.Active);

      const { server, agentRunner } = makeServer();
      const res = await request(server).post('/api/issues/1/rebase');
      expect(res.status).toBe(202);
      expect(agentRunner.enqueue).toHaveBeenCalledWith(issue.id, 'rebase', {});
    });
  });
});
