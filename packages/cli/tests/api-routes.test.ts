import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import express from 'express';
import request from 'supertest';
import { resetDatabase, closeDatabase } from '../src/db/database';
import { initializeDatabase } from '../src/db/migrations';
import { DatabaseManager } from '../src/db/database';
import { ProjectRepo } from '../src/db/project-repo';
import { IssueRepo } from '../src/db/issue-repo';
import { ConfigRepo } from '../src/db/config-repo';
import { ProjectService } from '../src/services/project-service';
import { IssueService } from '../src/services/issue-service';
import { ConfigService } from '../src/services/config-service';
import { StateManager } from '../src/server/state-manager';
import { createProjectRoutes } from '../src/api/projects';
import { createIssueRoutes } from '../src/api/issues';
import { createStatusRoutes } from '../src/api/status';
import { createConfigRoutes } from '../src/api/config';

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
    db = resetDatabase({ inMemory: true });
    initializeDatabase(db);
    
    stateManager = new StateManager();
    
    projectRepo = stateManager.getProjectRepo();
    issueRepo = stateManager.getIssueRepo();
    configRepo = stateManager.getConfigRepo();
    
    projectService = new ProjectService(projectRepo, configRepo);
    issueService = new IssueService(issueRepo);
    configService = new ConfigService(configRepo);
  });

  afterEach(() => {
    closeDatabase();
  });

  describe('Project Routes', () => {
    let app: express.Express;

    beforeEach(() => {
      app = express();
      app.use(express.json());
      app.use('/api/projects', createProjectRoutes(stateManager));
    });

    describe('POST /api/projects', () => {
      it('should create a project', async () => {
        const response = await request(app)
          .post('/api/projects')
          .send({ name: 'Test Project', path: '/test/path' });

        expect(response.status).toBe(201);
        expect(response.body.success).toBe(true);
        expect(response.body.data.name).toBe('Test Project');
        expect(response.body.data.path).toBe('/test/path');
      });

      it('should require name and path', async () => {
        const response = await request(app)
          .post('/api/projects')
          .send({ name: 'Test Project' });

        expect(response.status).toBe(400);
        expect(response.body.error).toContain('required');
      });

      it('should reject duplicate project name', async () => {
        await request(app)
          .post('/api/projects')
          .send({ name: 'Test Project', path: '/test/path' });

        const response = await request(app)
          .post('/api/projects')
          .send({ name: 'Test Project', path: '/other/path' });

        expect(response.status).toBe(409);
        expect(response.body.error).toContain('already exists');
      });
    });

    describe('GET /api/projects', () => {
      it('should list projects', async () => {
        await request(app)
          .post('/api/projects')
          .send({ name: 'Project 1', path: '/path/1' });
        await request(app)
          .post('/api/projects')
          .send({ name: 'Project 2', path: '/path/2' });

        const response = await request(app).get('/api/projects');

        expect(response.status).toBe(200);
        expect(response.body.success).toBe(true);
        expect(response.body.data).toHaveLength(2);
      });
    });

    describe('GET /api/projects/:name', () => {
      it('should return project details', async () => {
        await request(app)
          .post('/api/projects')
          .send({ name: 'Test Project', path: '/test/path' });

        const response = await request(app).get('/api/projects/Test Project');

        expect(response.status).toBe(200);
        expect(response.body.data.name).toBe('Test Project');
      });

      it('should return 404 for non-existent project', async () => {
        const response = await request(app).get('/api/projects/NonExistent');

        expect(response.status).toBe(404);
      });
    });

    describe('DELETE /api/projects/:name', () => {
      it('should delete project', async () => {
        await request(app)
          .post('/api/projects')
          .send({ name: 'Test Project', path: '/test/path' });

        const response = await request(app).delete('/api/projects/Test Project');

        expect(response.status).toBe(200);
        expect(response.body.success).toBe(true);
      });
    });

    describe('POST /api/projects/:name/use', () => {
      it('should set current project', async () => {
        await request(app)
          .post('/api/projects')
          .send({ name: 'Test Project', path: '/test/path' });

        const response = await request(app).post('/api/projects/Test Project/use');

        expect(response.status).toBe(200);
        expect(response.body.data.name).toBe('Test Project');
      });
    });
  });

  describe('Issue Routes', () => {
    let app: express.Express;
    let projectId: string;

    beforeEach(async () => {
      app = express();
      app.use(express.json());
      app.use('/api/issues', createIssueRoutes(stateManager));
      
      const project = projectService.create({ name: 'Test Project', path: '/test/path' });
      projectId = project.id;
      projectService.setCurrent(project);
    });

    describe('POST /api/issues', () => {
      it('should create an issue', async () => {
        const response = await request(app)
          .post('/api/issues')
          .send({ title: 'Test Issue', body: 'Test body' });

        expect(response.status).toBe(201);
        expect(response.body.success).toBe(true);
        expect(response.body.data.title).toBe('Test Issue');
        expect(response.body.data.number).toBe(1);
      });

      it('should require title', async () => {
        const response = await request(app)
          .post('/api/issues')
          .send({ body: 'Test body' });

        expect(response.status).toBe(400);
      });

      it('should return error when no current project', async () => {
        projectService.clearCurrent();
        
        const response = await request(app)
          .post('/api/issues')
          .send({ title: 'Test Issue' });

        expect(response.status).toBe(400);
        expect(response.body.error).toContain('No active project');
      });
    });

    describe('GET /api/issues', () => {
      it('should list issues', async () => {
        issueService.create({ projectId, title: 'Issue 1' });
        issueService.create({ projectId, title: 'Issue 2' });

        const response = await request(app).get('/api/issues');

        expect(response.status).toBe(200);
        expect(response.body.data).toHaveLength(2);
      });

      it('should filter by stage', async () => {
        issueService.create({ projectId, title: 'Test' });
        issueService.transitionToStageByNumber(projectId, 1, 'designing' as any);

        const response = await request(app).get('/api/issues?stage=designing');

        expect(response.status).toBe(200);
        expect(response.body.data).toHaveLength(1);
      });
    });

    describe('GET /api/issues/:number', () => {
      it('should return issue details', async () => {
        issueService.create({ projectId, title: 'Test Issue' });

        const response = await request(app).get('/api/issues/1');

        expect(response.status).toBe(200);
        expect(response.body.data.number).toBe(1);
        expect(response.body.data.title).toBe('Test Issue');
      });

      it('should return 404 for non-existent issue', async () => {
        const response = await request(app).get('/api/issues/999');

        expect(response.status).toBe(404);
      });
    });

    describe('POST /api/issues/:number/start', () => {
      it('should start processing an issue', async () => {
        issueService.create({ projectId, title: 'Test Issue' });

        const response = await request(app).post('/api/issues/1/start');

        expect(response.status).toBe(200);
        expect(response.body.success).toBe(true);
        expect(response.body.data.issue.stage).toBe('plan');
      });
    });
  });

  describe('Status Routes', () => {
    let app: express.Express;

    beforeEach(() => {
      app = express();
      app.use(express.json());
      app.use('/api', createStatusRoutes(stateManager));
    });

    describe('GET /api/status', () => {
      it('should return error when no current project', async () => {
        const response = await request(app).get('/api/status');

        expect(response.status).toBe(400);
        expect(response.body.error).toContain('No active project');
      });

      it('should return current project status', async () => {
        const project = projectService.create({ name: 'Test Project', path: '/test/path' });
        projectService.setCurrent(project);

        const response = await request(app).get('/api/status');

        expect(response.status).toBe(200);
        expect(response.body.success).toBe(true);
        expect(response.body.data.name).toBe('Test Project');
      });
    });

    describe('GET /api/status?all=true', () => {
      it('should return all projects status', async () => {
        projectService.create({ name: 'Project 1', path: '/path/1' });
        projectService.create({ name: 'Project 2', path: '/path/2' });

        const response = await request(app).get('/api/status?all=true');

        expect(response.status).toBe(200);
        expect(response.body.data).toHaveLength(2);
      });
    });
  });

  describe('Config Routes', () => {
    let app: express.Express;

    beforeEach(() => {
      app = express();
      app.use(express.json());
      app.use('/api/config', createConfigRoutes(configService));
    });

    describe('GET /api/config', () => {
      it('should return config', async () => {
        const response = await request(app).get('/api/config');

        expect(response.status).toBe(200);
        expect(response.body.success).toBe(true);
        expect(response.body.data.serverPort).toBeDefined();
      });
    });

    describe('PUT /api/config/:key', () => {
      it('should update config value', async () => {
        const response = await request(app)
          .put('/api/config/server.port')
          .send({ value: 4000 });

        expect(response.status).toBe(200);
        expect(response.body.success).toBe(true);
      });

      it('should validate port range', async () => {
        const response = await request(app)
          .put('/api/config/server.port')
          .send({ value: 99999 });

        expect(response.status).toBe(400);
      });
    });

    describe('GET /api/config/list', () => {
      it('should return all config values', async () => {
        const response = await request(app).get('/api/config/list');

        expect(response.status).toBe(200);
        expect(response.body.success).toBe(true);
        expect(response.body.data).toBeDefined();
      });
    });
  });
});
