import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import http from 'node:http';
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
import { IssueStartPrerequisiteRepo } from '../src/db/issue-start-prerequisite-repo';
import { IssuePrerequisiteService } from '../src/services/issue-prerequisite-service';
import { IssueTaskQueueRepo } from '../src/db/issue-task-queue-repo';

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

describe('Issue Prerequisites API', () => {
  let db: DatabaseManager;
  let projectRepo: ProjectRepo;
  let issueRepo: IssueRepo;
  let configRepo: ConfigRepo;
  let projectService: ProjectService;
  let issueService: IssueService;
  let stateManager: StateManager;
  let prerequisiteService: IssuePrerequisiteService;
  let issueTaskQueueRepo: IssueTaskQueueRepo;

  beforeEach(async () => {
    db = new DatabaseManager({ inMemory: true });
    stateManager = new StateManager(db);
    projectRepo = stateManager.getProjectRepo();
    issueRepo = stateManager.getIssueRepo();
    configRepo = stateManager.getConfigRepo();
    const commentRepo = stateManager.getCommentRepo();
    const labelRepo = stateManager.getLabelRepo();
    projectService = new ProjectService(projectRepo, configRepo, issueRepo, labelRepo);
    issueService = new IssueService(issueRepo, commentRepo);
    prerequisiteService = new IssuePrerequisiteService(issueRepo, stateManager.getIssueStartPrerequisiteRepo());
    issueTaskQueueRepo = stateManager.getIssueTaskQueueRepo();
  });

  afterEach(() => {
    db.close();
  });

  describe('POST /api/issues/:number/prerequisites', () => {
    it('should declare a start prerequisite between two issues', async () => {
      const project = await projectService.create({ name: `project-${Date.now()}`, path: `/tmp/${Date.now()}` });
      projectService.setCurrent(project);
      const issue200 = issueService.create({ projectId: project.id, title: 'Issue #200' });
      const issue201 = issueService.create({ projectId: project.id, title: 'Issue #201' });

      const app = new Hono();
      const agentRunner = new AgentRunnerService(
        new EventBus(), undefined, issueRepo, 8,
        undefined, undefined, projectRepo, undefined, issueTaskQueueRepo
      );
      app.route('/api/issues', createIssueRoutes(
        issueService,
        projectService,
        stateManager,
        undefined,
        undefined,
        agentRunner,
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
        prerequisiteService,
      ));
      const server = createTestServer(app);

      const response = await request(server)
        .post(`/api/issues/${issue201.number}/prerequisites`)
        .send({ prerequisiteNumber: issue200.number });

      expect(response.status).toBe(200);
      expect(response.body.success).toBe(true);
      expect(response.body.data.issue.prerequisites).toBeDefined();
      expect(response.body.data.issue.prerequisites.some((p: any) => p.number === issue200.number)).toBe(true);
      expect(response.body.data.issue.startEligibility.startable).toBe(false);
      expect(response.body.data.issue.startEligibility.waitingForDelivery.some((w: any) => w.number === issue200.number)).toBe(true);
    });

    it('should return 400 for circular prerequisite declaration', async () => {
      const project = await projectService.create({ name: `project-${Date.now()}`, path: `/tmp/${Date.now()}` });
      projectService.setCurrent(project);
      const issue200 = issueService.create({ projectId: project.id, title: 'Issue #200' });
      const issue201 = issueService.create({ projectId: project.id, title: 'Issue #201' });

      const app = new Hono();
      const agentRunner = new AgentRunnerService(
        new EventBus(), undefined, issueRepo, 8,
        undefined, undefined, projectRepo, undefined, issueTaskQueueRepo
      );
      app.route('/api/issues', createIssueRoutes(
        issueService,
        projectService,
        stateManager,
        undefined,
        undefined,
        agentRunner,
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
        prerequisiteService,
      ));
      const server = createTestServer(app);

      await request(server)
        .post(`/api/issues/${issue201.number}/prerequisites`)
        .send({ prerequisiteNumber: issue200.number });

      const response = await request(server)
        .post(`/api/issues/${issue200.number}/prerequisites`)
        .send({ prerequisiteNumber: issue201.number });

      expect(response.status).toBe(400);
      expect(response.body.success).toBe(false);
      expect(response.body.data.reason).toBe('circular-prerequisite');
    });

    it('should return 400 for same-issue prerequisite', async () => {
      const project = await projectService.create({ name: `project-${Date.now()}`, path: `/tmp/${Date.now()}` });
      projectService.setCurrent(project);
      const issue200 = issueService.create({ projectId: project.id, title: 'Issue #200' });

      const app = new Hono();
      const agentRunner = new AgentRunnerService(
        new EventBus(), undefined, issueRepo, 8,
        undefined, undefined, projectRepo, undefined, issueTaskQueueRepo
      );
      app.route('/api/issues', createIssueRoutes(
        issueService,
        projectService,
        stateManager,
        undefined,
        undefined,
        agentRunner,
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
        prerequisiteService,
      ));
      const server = createTestServer(app);

      const response = await request(server)
        .post(`/api/issues/${issue200.number}/prerequisites`)
        .send({ prerequisiteNumber: issue200.number });

      expect(response.status).toBe(400);
      expect(response.body.success).toBe(false);
      expect(response.body.data.reason).toBe('same-issue');
    });

    it('should return 404 when declaring prerequisite for non-existent issue', async () => {
      const project = await projectService.create({ name: `project-${Date.now()}`, path: `/tmp/${Date.now()}` });
      projectService.setCurrent(project);

      const app = new Hono();
      const agentRunner = new AgentRunnerService(
        new EventBus(), undefined, issueRepo, 8,
        undefined, undefined, projectRepo, undefined, issueTaskQueueRepo
      );
      app.route('/api/issues', createIssueRoutes(
        issueService,
        projectService,
        stateManager,
        undefined,
        undefined,
        agentRunner,
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
        prerequisiteService,
      ));
      const server = createTestServer(app);

      const response = await request(server)
        .post('/api/issues/999/prerequisites')
        .send({ prerequisiteNumber: 200 });

      expect(response.status).toBe(404);
    });

    it('should return 400 when prerequisite issue does not exist', async () => {
      const project = await projectService.create({ name: `project-${Date.now()}`, path: `/tmp/${Date.now()}` });
      projectService.setCurrent(project);
      const issue201 = issueService.create({ projectId: project.id, title: 'Issue #201' });

      const app = new Hono();
      const agentRunner = new AgentRunnerService(
        new EventBus(), undefined, issueRepo, 8,
        undefined, undefined, projectRepo, undefined, issueTaskQueueRepo
      );
      app.route('/api/issues', createIssueRoutes(
        issueService,
        projectService,
        stateManager,
        undefined,
        undefined,
        agentRunner,
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
        prerequisiteService,
      ));
      const server = createTestServer(app);

      const response = await request(server)
        .post(`/api/issues/${issue201.number}/prerequisites`)
        .send({ prerequisiteNumber: 999 });

      expect(response.status).toBe(400);
      expect(response.body.error).toContain('not found');
    });
  });

  describe('DELETE /api/issues/:number/prerequisites/:prerequisiteNumber', () => {
    it('should remove a declared prerequisite', async () => {
      const project = await projectService.create({ name: `project-${Date.now()}`, path: `/tmp/${Date.now()}` });
      projectService.setCurrent(project);
      const issue200 = issueService.create({ projectId: project.id, title: 'Issue #200' });
      const issue201 = issueService.create({ projectId: project.id, title: 'Issue #201' });

      const app = new Hono();
      const agentRunner = new AgentRunnerService(
        new EventBus(), undefined, issueRepo, 8,
        undefined, undefined, projectRepo, undefined, issueTaskQueueRepo
      );
      app.route('/api/issues', createIssueRoutes(
        issueService,
        projectService,
        stateManager,
        undefined,
        undefined,
        agentRunner,
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
        prerequisiteService,
      ));
      const server = createTestServer(app);

      await request(server)
        .post(`/api/issues/${issue201.number}/prerequisites`)
        .send({ prerequisiteNumber: issue200.number });

      const response = await request(server)
        .delete(`/api/issues/${issue201.number}/prerequisites/${issue200.number}`);

      expect(response.status).toBe(200);
      expect(response.body.success).toBe(true);
      expect(response.body.data.issue.prerequisites).toHaveLength(0);
    });

    it('should return 404 when removing non-existent prerequisite', async () => {
      const project = await projectService.create({ name: `project-${Date.now()}`, path: `/tmp/${Date.now()}` });
      projectService.setCurrent(project);
      const issue201 = issueService.create({ projectId: project.id, title: 'Issue #201' });

      const app = new Hono();
      const agentRunner = new AgentRunnerService(
        new EventBus(), undefined, issueRepo, 8,
        undefined, undefined, projectRepo, undefined, issueTaskQueueRepo
      );
      app.route('/api/issues', createIssueRoutes(
        issueService,
        projectService,
        stateManager,
        undefined,
        undefined,
        agentRunner,
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
        prerequisiteService,
      ));
      const server = createTestServer(app);

      const response = await request(server)
        .delete(`/api/issues/${issue201.number}/prerequisites/999`);

      expect(response.status).toBe(404);
    });
  });

  describe('POST /api/issues/:number/start - prerequisite guard', () => {
    it('should reject start when prerequisite issue is not delivered', async () => {
      const project = await projectService.create({ name: `project-${Date.now()}`, path: `/tmp/${Date.now()}` });
      projectService.setCurrent(project);
      const issue200 = issueService.create({ projectId: project.id, title: 'Issue #200' });
      const issue201 = issueService.create({ projectId: project.id, title: 'Issue #201' });

      prerequisiteService.declarePrerequisite(project.id, issue201.number, issue200.number);

      const app = new Hono();
      const agentRunner = new AgentRunnerService(
        new EventBus(), undefined, issueRepo, 8,
        undefined, undefined, projectRepo, undefined, issueTaskQueueRepo
      );
      app.route('/api/issues', createIssueRoutes(
        issueService,
        projectService,
        stateManager,
        undefined,
        undefined,
        agentRunner,
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
        prerequisiteService,
      ));
      const server = createTestServer(app);

      const response = await request(server)
        .post(`/api/issues/${issue201.number}/start`);

      expect(response.status).toBe(400);
      expect(response.body.success).toBe(false);
      expect(response.body.error).toContain('waiting for prerequisite');
      expect(response.body.data.startEligibility).toBeDefined();
      expect(response.body.data.startEligibility.startable).toBe(false);
      expect(response.body.data.startEligibility.waitingForDelivery.some((w: any) => w.number === issue200.number)).toBe(true);
    });

    it('should allow start when all prerequisite issues are delivered', async () => {
      const project = await projectService.create({ name: `project-${Date.now()}`, path: `/tmp/${Date.now()}` });
      projectService.setCurrent(project);
      const issue200 = issueService.create({ projectId: project.id, title: 'Issue #200' });
      const issue201 = issueService.create({ projectId: project.id, title: 'Issue #201' });

      prerequisiteService.declarePrerequisite(project.id, issue201.number, issue200.number);

      issueRepo.updateStage(issue200.id, Stage.Done);
      issueRepo.updateStatus(issue200.id, IssueStatus.Completed);
      issueRepo.setMergeState(issue200.id, MergeState.Merged);

      const app = new Hono();
      const agentRunner = new AgentRunnerService(
        new EventBus(), undefined, issueRepo, 8,
        undefined, undefined, projectRepo, undefined, issueTaskQueueRepo
      );
      app.route('/api/issues', createIssueRoutes(
        issueService,
        projectService,
        stateManager,
        undefined,
        undefined,
        agentRunner,
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
        prerequisiteService,
      ));
      const server = createTestServer(app);

      const response = await request(server)
        .post(`/api/issues/${issue201.number}/start`);

      expect(response.status).toBe(202);
      expect(response.body.success).toBe(true);
    });

    it('should not enqueue start-pipeline when start is rejected', async () => {
      const project = await projectService.create({ name: `project-${Date.now()}`, path: `/tmp/${Date.now()}` });
      projectService.setCurrent(project);
      const issue200 = issueService.create({ projectId: project.id, title: 'Issue #200' });
      const issue201 = issueService.create({ projectId: project.id, title: 'Issue #201' });

      prerequisiteService.declarePrerequisite(project.id, issue201.number, issue200.number);

      const app = new Hono();
      const agentRunner = new AgentRunnerService(
        new EventBus(), undefined, issueRepo, 8,
        undefined, undefined, projectRepo, undefined, issueTaskQueueRepo
      );
      const enqueueSpy = vi.spyOn(agentRunner, 'enqueue');
      app.route('/api/issues', createIssueRoutes(
        issueService,
        projectService,
        stateManager,
        undefined,
        undefined,
        agentRunner,
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
        prerequisiteService,
      ));
      const server = createTestServer(app);

      await request(server)
        .post(`/api/issues/${issue201.number}/start`);

      expect(enqueueSpy).not.toHaveBeenCalled();
    });

    it('should reject non-backlog starts through shared startEligibility data', async () => {
      const project = await projectService.create({ name: `project-${Date.now()}`, path: `/tmp/${Date.now()}` });
      projectService.setCurrent(project);
      const issue = issueService.create({ projectId: project.id, title: 'Plan Issue' });
      issueService.transitionToStage(issue.id, Stage.Plan);

      const app = new Hono();
      const agentRunner = new AgentRunnerService(
        new EventBus(), undefined, issueRepo, 8,
        undefined, undefined, projectRepo, undefined, issueTaskQueueRepo
      );
      const enqueueSpy = vi.spyOn(agentRunner, 'enqueue');
      app.route('/api/issues', createIssueRoutes(
        issueService,
        projectService,
        stateManager,
        undefined,
        undefined,
        agentRunner,
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
        prerequisiteService,
      ));
      const server = createTestServer(app);

      const response = await request(server)
        .post(`/api/issues/${issue.number}/start`);

      expect(response.status).toBe(400);
      expect(response.body.data.startEligibility).toMatchObject({
        startable: false,
        reason: 'not-startable-lifecycle',
      });
      expect(response.body.error).toContain('Only backlog issues can be started');
      expect(enqueueSpy).not.toHaveBeenCalled();
    });
  });

  describe('GET /api/issues/:number - includes prerequisites and startEligibility', () => {
    it('should return prerequisites and startEligibility for issue with prerequisites', async () => {
      const project = await projectService.create({ name: `project-${Date.now()}`, path: `/tmp/${Date.now()}` });
      projectService.setCurrent(project);
      const issue200 = issueService.create({ projectId: project.id, title: 'Issue #200' });
      const issue201 = issueService.create({ projectId: project.id, title: 'Issue #201' });

      prerequisiteService.declarePrerequisite(project.id, issue201.number, issue200.number);

      const app = new Hono();
      const agentRunner = new AgentRunnerService(
        new EventBus(), undefined, issueRepo, 8,
        undefined, undefined, projectRepo, undefined, issueTaskQueueRepo
      );
      app.route('/api/issues', createIssueRoutes(
        issueService,
        projectService,
        stateManager,
        undefined,
        undefined,
        agentRunner,
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
        prerequisiteService,
      ));
      const server = createTestServer(app);

      const response = await request(server)
        .get(`/api/issues/${issue201.number}`);

      expect(response.status).toBe(200);
      expect(response.body.data.prerequisites).toBeDefined();
      expect(response.body.data.prerequisites).toHaveLength(1);
      expect(response.body.data.prerequisites[0].number).toBe(issue200.number);
      expect(response.body.data.startEligibility).toBeDefined();
      expect(response.body.data.startEligibility.startable).toBe(false);
      expect(response.body.data.startEligibility.waitingForDelivery).toHaveLength(1);
    });

    it('should indicate delivered state for delivered prerequisites', async () => {
      const project = await projectService.create({ name: `project-${Date.now()}`, path: `/tmp/${Date.now()}` });
      projectService.setCurrent(project);
      const issue200 = issueService.create({ projectId: project.id, title: 'Issue #200' });
      const issue201 = issueService.create({ projectId: project.id, title: 'Issue #201' });

      prerequisiteService.declarePrerequisite(project.id, issue201.number, issue200.number);

      issueRepo.updateStage(issue200.id, Stage.Done);
      issueRepo.updateStatus(issue200.id, IssueStatus.Completed);
      issueRepo.setMergeState(issue200.id, MergeState.Merged);

      const app = new Hono();
      const agentRunner = new AgentRunnerService(
        new EventBus(), undefined, issueRepo, 8,
        undefined, undefined, projectRepo, undefined, issueTaskQueueRepo
      );
      app.route('/api/issues', createIssueRoutes(
        issueService,
        projectService,
        stateManager,
        undefined,
        undefined,
        agentRunner,
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
        prerequisiteService,
      ));
      const server = createTestServer(app);

      const response = await request(server)
        .get(`/api/issues/${issue201.number}`);

      expect(response.status).toBe(200);
      expect(response.body.data.prerequisites[0].delivered).toBe(true);
      expect(response.body.data.startEligibility.startable).toBe(true);
    });
  });
});
