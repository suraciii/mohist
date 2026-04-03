import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
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
import { Stage, IssueStatus } from '../src/types';

describe('E2E: Single Issue Complete Flow', () => {
  let db: DatabaseManager;
  let app: express.Express;
  let projectId: string;

  beforeEach(() => {
    db = resetDatabase({ inMemory: true });
    initializeDatabase(db);
    
    const projectRepo = new ProjectRepo(db);
    const issueRepo = new IssueRepo(db);
    const configRepo = new ConfigRepo(db);
    
    const projectService = new ProjectService(projectRepo, configRepo);
    const issueService = new IssueService(issueRepo);
    const configService = new ConfigService(configRepo);
    
    const stateManager = new StateManager();
    
    app = express();
    app.use(express.json());
    app.use('/api/projects', createProjectRoutes(stateManager));
    app.use('/api/issues', createIssueRoutes(stateManager));
    app.use('/api', createStatusRoutes(stateManager));
    
    const project = projectService.create({ name: 'E2E Test Project', path: '/test/e2e' });
    projectId = project.id;
    projectService.setCurrent(project);
  });

  afterEach(() => {
    closeDatabase();
  });

  describe('Complete Issue Workflow', () => {
    it('should complete full workflow: create -> start -> verify stage progression', async () => {
      // Step 1: Create an issue
      const createResponse = await request(app)
        .post('/api/issues')
        .send({ title: 'E2E Test Issue', body: 'Test the complete workflow' });
      
      expect(createResponse.status).toBe(201);
      expect(createResponse.body.success).toBe(true);
      expect(createResponse.body.data.number).toBe(1);
      expect(createResponse.body.data.stage).toBe(Stage.Draft);

      // Step 2: Verify issue appears in list
      const listResponse = await request(app).get('/api/issues');
      expect(listResponse.status).toBe(200);
      expect(listResponse.body.data).toHaveLength(1);

      // Step 3: Start processing (moves to plan)
      const startResponse = await request(app).post('/api/issues/1/start');
      expect(startResponse.status).toBe(200);
      expect(startResponse.body.success).toBe(true);
      expect(startResponse.body.data.issue.stage).toBe(Stage.Plan);

      // Step 4: Verify show endpoint
      const showResponse1 = await request(app).get('/api/issues/1');
      expect(showResponse1.status).toBe(200);
      expect(showResponse1.body.data.stage).toBe(Stage.Plan);

      // Step 5: Verify status shows plan issue
      const statusResponse = await request(app).get('/api/status');
      expect(statusResponse.status).toBe(200);
      expect(statusResponse.body.data.issuesByStage.plan).toBe(1);
    });

    it('should prevent starting a non-draft issue', async () => {
      await request(app)
        .post('/api/issues')
        .send({ title: 'Start Test Issue' });

      await request(app).post('/api/issues/1/start');

      const startAgainResponse = await request(app).post('/api/issues/1/start');
      expect(startAgainResponse.status).toBe(400);
      expect(startAgainResponse.body.error).toMatch(/not in draft stage|blocked/i);
    });
  });

  describe('Multi-Issue Workflow', () => {
    it('should handle multiple issues independently', async () => {
      // Create 3 issues
      await request(app).post('/api/issues').send({ title: 'Issue 1' });
      await request(app).post('/api/issues').send({ title: 'Issue 2' });
      await request(app).post('/api/issues').send({ title: 'Issue 3' });

      // Start only issue 2
      await request(app).post('/api/issues/2/start');

      // Verify issues are at different stages
      const response = await request(app).get('/api/issues');
      const issues = response.body.data;

      const issue1 = issues.find((i: any) => i.number === 1);
      const issue2 = issues.find((i: any) => i.number === 2);
      const issue3 = issues.find((i: any) => i.number === 3);

      expect(issue1.stage).toBe(Stage.Draft);
      expect(issue2.stage).toBe(Stage.Plan);
      expect(issue3.stage).toBe(Stage.Draft);

      // Filter by stage
      const draftResponse = await request(app).get('/api/issues?stage=draft');
      expect(draftResponse.body.data).toHaveLength(2);

      const planResponse = await request(app).get('/api/issues?stage=plan');
      expect(planResponse.body.data).toHaveLength(1);
    });
  });

  describe('Project Status', () => {
    it('should track issue counts by stage', async () => {
      // Create multiple issues at different stages
      await request(app).post('/api/issues').send({ title: 'Draft 1' });
      await request(app).post('/api/issues').send({ title: 'Draft 2' });
      
      await request(app).post('/api/issues').send({ title: 'Plan' });
      await request(app).post('/api/issues/3/start');

      // Get status
      const statusResponse = await request(app).get('/api/status');
      expect(statusResponse.status).toBe(200);

      const status = statusResponse.body.data;
      expect(status.issuesByStage.draft).toBe(2);
      expect(status.issuesByStage.plan).toBe(1);
      expect(status.issues).toBe(3);
    });
  });
});
