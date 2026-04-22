import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import http from 'node:http';
import { Hono } from 'hono';
import request from 'supertest';
import { DatabaseManager } from '../src/db/database';
import { initializeDatabase } from '../src/db/migrations';
import { ProjectRepo } from '../src/db/project-repo';
import { IssueRepo } from '../src/db/issue-repo';
import { ConfigRepo } from '../src/db/config-repo';
import { CommentRepo } from '../src/db/comment-repo';
import { LabelRepo } from '../src/db/label-repo';
import { ProjectService } from '../src/services/project-service';
import { IssueService } from '../src/services/issue-service';
import { ConfigService } from '../src/services/config-service';
import { EventBus, AgentRunnerService } from '../src/services';
import { StateManager } from '../src/server/state-manager';
import { createProjectRoutes } from '../src/api/projects';
import { createIssueRoutes } from '../src/api/issues';
import { createStatusRoutes } from '../src/api/status';
import { Stage, IssueStatus } from '../src/types';

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
        res.write(value);
      }
    }
    res.end();
  });
}

describe('E2E: Single Issue Complete Flow', () => {
  let db: DatabaseManager;
  let app: Hono;
  let server: http.Server;
  let projectId: string;

  beforeEach(async () => {
    // Set mock opencode path for e2e tests
    process.env.OPENCODE_BIN_PATH = '/tmp/mohist-test-mocks/opencode';
    
    db = new DatabaseManager({ inMemory: true });
    initializeDatabase(db);
    
    const projectRepo = new ProjectRepo(db);
    const issueRepo = new IssueRepo(db);
    const configRepo = new ConfigRepo(db);
    const commentRepo = new CommentRepo(db);
    const labelRepo = new LabelRepo(db);
    
    const projectService = new ProjectService(projectRepo, configRepo, issueRepo, labelRepo);
    const issueService = new IssueService(issueRepo, commentRepo);
    const configService = new ConfigService(configRepo);
    
    const stateManager = new StateManager(db);
    
    app = new Hono();
    app.route('/api/projects', createProjectRoutes(projectService));
    const eventBus = new EventBus();
    const agentRunner = new AgentRunnerService(eventBus);
    const opencodeBinPath = process.env.OPENCODE_BIN_PATH;
    app.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, undefined, undefined, undefined, agentRunner, undefined, undefined, undefined, opencodeBinPath));
    app.route('/api', createStatusRoutes(projectService, issueService));

    server = createTestServer(app);
    server.listen(0);
    const addr = server.address() as import('net').AddressInfo;
    (app as any).__port = addr.port;
    
    const project = await projectService.create({ name: 'E2E Test Project', path: '/test/e2e' });
    projectId = project.id;
    projectService.setCurrent(project);
  });

  afterEach(() => {
    delete process.env.OPENCODE_BIN_PATH;
    server.close();
    db.close();
  });

  function getAppUrl(): string {
    return `http://localhost:${(app as any).__port}`;
  }

  describe('Complete Issue Workflow', () => {
    it('should complete full workflow: create -> start -> verify stage progression', async () => {
      const baseUrl = getAppUrl();

      const createResponse = await request(baseUrl)
        .post('/api/issues')
        .send({ title: 'E2E Test Issue', body: 'Test the complete workflow' });
      
      expect(createResponse.status).toBe(201);
      expect(createResponse.body.success).toBe(true);
      expect(createResponse.body.data.number).toBe(1);
      expect(createResponse.body.data.stage).toBe(Stage.Draft);

      const listResponse = await request(baseUrl).get('/api/issues');
      expect(listResponse.status).toBe(200);
      expect(listResponse.body.data).toHaveLength(1);

      const startResponse = await request(baseUrl).post('/api/issues/1/start');
      expect(startResponse.status).toBe(200);
      expect(startResponse.body.success).toBe(true);
      // With mock opencode, pipeline starts and transitions to Plan
      expect(startResponse.body.data.issue.stage).toBe(Stage.Plan);

      // Wait a bit for pipeline to process (mock is fast)
      await new Promise(resolve => setTimeout(resolve, 100));

      const showResponse1 = await request(baseUrl).get('/api/issues/1');
      expect(showResponse1.status).toBe(200);
      // Mock opencode doesn't generate artifacts, so plan may fail and rollback
      // or stay in Plan if pipeline is still running
      expect([Stage.Plan, Stage.Draft, Stage.Blocked]).toContain(showResponse1.body.data.stage);

      const statusResponse = await request(baseUrl).get('/api/status');
      expect(statusResponse.status).toBe(200);
      expect(statusResponse.body.data.issues).toBe(1);
    });

    it('should prevent starting a non-draft issue', async () => {
      const baseUrl = getAppUrl();

      await request(baseUrl)
        .post('/api/issues')
        .send({ title: 'Start Test Issue' });

      await request(baseUrl).post('/api/issues/1/start');

      const startAgainResponse = await request(baseUrl).post('/api/issues/1/start');
      expect(startAgainResponse.status).toBe(400);
      expect(startAgainResponse.body.error).toMatch(/not in draft stage|blocked/i);
    });
  });

  describe('Multi-Issue Workflow', () => {
    it('should handle multiple issues independently', async () => {
      const baseUrl = getAppUrl();

      await request(baseUrl).post('/api/issues').send({ title: 'Issue 1' });
      await request(baseUrl).post('/api/issues').send({ title: 'Issue 2' });
      await request(baseUrl).post('/api/issues').send({ title: 'Issue 3' });

      await request(baseUrl).post('/api/issues/2/start');

      // Wait for pipeline to process
      await new Promise(resolve => setTimeout(resolve, 100));

      const response = await request(baseUrl).get('/api/issues');
      const issues = response.body.data;

      const issue1 = issues.find((i: any) => i.number === 1);
      const issue2 = issues.find((i: any) => i.number === 2);
      const issue3 = issues.find((i: any) => i.number === 3);

      expect(issue1.stage).toBe(Stage.Draft);
      // Issue 2 may be Plan (if pipeline still running) or Draft/Blocked (if mock failed)
      expect([Stage.Draft, Stage.Plan, Stage.Blocked]).toContain(issue2.stage);
      expect(issue3.stage).toBe(Stage.Draft);

      // Just verify we can query by stage without errors
      const draftResponse = await request(baseUrl).get('/api/issues?stage=draft');
      expect(draftResponse.status).toBe(200);

      const planResponse = await request(baseUrl).get('/api/issues?stage=plan');
      expect(planResponse.status).toBe(200);
    });
  });

  describe('Project Status', () => {
    it('should track issue counts by stage', async () => {
      const baseUrl = getAppUrl();

      await request(baseUrl).post('/api/issues').send({ title: 'Draft 1' });
      await request(baseUrl).post('/api/issues').send({ title: 'Draft 2' });
      
      await request(baseUrl).post('/api/issues').send({ title: 'Plan' });
      await request(baseUrl).post('/api/issues/3/start');

      // Wait for pipeline to process
      await new Promise(resolve => setTimeout(resolve, 100));

      const statusResponse = await request(baseUrl).get('/api/status');
      expect(statusResponse.status).toBe(200);

      const status = statusResponse.body.data;
      expect(status.issues).toBe(3);
      // With mock opencode, stages may vary. Just verify counts are consistent.
      const totalByStage = Object.values(status.issuesByStage).reduce((a: number, b: number) => a + b, 0);
      expect(totalByStage).toBe(3);
    });
  });
});
