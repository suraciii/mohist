import { describe, it, expect, beforeEach, afterEach } from 'vitest';
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
import { createProjectRoutes } from '../src/api/projects';
import { createIssueRoutes } from '../src/api/issues';
import { createStatusRoutes } from '../src/api/status';
import { createConfigRoutes } from '../src/api/config';

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

  beforeEach(() => {
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

    beforeEach(async () => {
      const app = new Hono();
      const eventBus = new EventBus();
      const agentRunner = new AgentRunnerService(eventBus);
      app.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, undefined, undefined, undefined, agentRunner));
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
      it('should start processing an issue', async () => {
        await issueService.create({ projectId, title: 'Test Issue' });

        const response = await request(server).post('/api/issues/1/start');

        expect(response.status).toBe(200);
        expect(response.body.success).toBe(true);
        expect(response.body.data.issue.stage).toBe('plan');
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
        expect(response.body.data.serverPort).toBeDefined();
      });
    });

    describe('PUT /api/config/:key', () => {
      it('should update config value', async () => {
        const response = await request(server)
          .put('/api/config/server.port')
          .send({ value: 4000 });

        expect(response.status).toBe(200);
        expect(response.body.success).toBe(true);
      });

      it('should validate port range', async () => {
        const response = await request(server)
          .put('/api/config/server.port')
          .send({ value: 99999 });

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
});
