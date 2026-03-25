import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import express from 'express';
import request from 'supertest';
import { resetDatabase, closeDatabase } from '../src/db/database';
import { runMigrations } from '../src/db/migrations';
import { DatabaseManager } from '../src/db/database';
import { ProjectRepo } from '../src/db/project-repo';
import { IssueRepo } from '../src/db/issue-repo';
import { TaskRepo } from '../src/db/task-repo';
import { ConfigRepo } from '../src/db/config-repo';
import { ProjectService } from '../src/services/project-service';
import { IssueService } from '../src/services/issue-service';
import { WorkflowService } from '../src/services/workflow-service';
import { ConfigService } from '../src/services/config-service';
import { StateManager } from '../src/server/state-manager';
import { TaskQueue } from '../src/server/task-queue';
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
    runMigrations(db);
    
    const projectRepo = new ProjectRepo(db);
    const issueRepo = new IssueRepo(db);
    const taskRepo = new TaskRepo(db);
    const configRepo = new ConfigRepo(db);
    
    const projectService = new ProjectService(projectRepo, configRepo);
    const issueService = new IssueService(issueRepo, taskRepo);
    const configService = new ConfigService(configRepo);
    
    const stateManager = new StateManager();
    const taskQueue = new TaskQueue(taskRepo);
    
    app = express();
    app.use(express.json());
    app.use('/api/projects', createProjectRoutes(stateManager));
    app.use('/api/issues', createIssueRoutes(stateManager, taskQueue));
    app.use('/api', createStatusRoutes(stateManager, taskQueue));
    
    const project = projectService.create({ name: 'E2E Test Project', path: '/test/e2e' });
    projectId = project.id;
    projectService.setCurrent(project);
  });

  afterEach(() => {
    closeDatabase();
  });

  describe('Complete Issue Workflow', () => {
    it('should complete full workflow: create -> start -> approve design -> approve implementation', async () => {
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

      // Step 3: Start processing (moves to designing)
      const startResponse = await request(app).post('/api/issues/1/start');
      expect(startResponse.status).toBe(200);
      expect(startResponse.body.success).toBe(true);
      expect(startResponse.body.data.issue.stage).toBe(Stage.Designing);
      expect(startResponse.body.data.taskId).toBeDefined();

      // Step 4: Simulate agent completing design (move to waiting-design-review)
      // In real flow, this would be done by the agent
      const showResponse1 = await request(app).get('/api/issues/1');
      expect(showResponse1.status).toBe(200);

      // Manually transition to simulate agent completion
      const issueService = new IssueService(
        new IssueRepo(db),
        new TaskRepo(db)
      );
      issueService.transitionToStageByNumber(projectId, 1, Stage.WaitingDesignReview);

      // Step 5: Approve design (moves to implementing)
      const approveDesignResponse = await request(app).post('/api/issues/1/approve');
      expect(approveDesignResponse.status).toBe(200);
      expect(approveDesignResponse.body.success).toBe(true);
      expect(approveDesignResponse.body.data.issue.stage).toBe(Stage.Implementing);

      // Step 6: Simulate agent completing implementation
      issueService.transitionToStageByNumber(projectId, 1, Stage.WaitingReview);

      // Step 7: Approve implementation (moves to done)
      const approveImplResponse = await request(app).post('/api/issues/1/approve');
      expect(approveImplResponse.status).toBe(200);
      expect(approveImplResponse.body.success).toBe(true);
      expect(approveImplResponse.body.data.issue.stage).toBe(Stage.Done);

      // Step 8: Verify final state
      const finalResponse = await request(app).get('/api/issues/1');
      expect(finalResponse.status).toBe(200);
      expect(finalResponse.body.data.stage).toBe(Stage.Done);
      expect(finalResponse.body.data.progress.percentage).toBe(100);

      // Step 9: Verify status shows completed issue
      const statusResponse = await request(app).get('/api/status');
      expect(statusResponse.status).toBe(200);
      expect(statusResponse.body.data.issuesByStage.done).toBe(1);
    });

    it('should handle pause/resume during workflow', async () => {
      // Create and start issue
      await request(app)
        .post('/api/issues')
        .send({ title: 'Pause Test Issue' });
      
      await request(app).post('/api/issues/1/start');

      // Pause the issue
      const pauseResponse = await request(app).post('/api/issues/1/pause');
      expect(pauseResponse.status).toBe(200);
      expect(pauseResponse.body.data.issue.status).toBe(IssueStatus.Paused);

      // Verify cannot start a paused issue
      const startPausedResponse = await request(app).post('/api/issues/1/start');
      expect(startPausedResponse.status).toBe(400);
      expect(startPausedResponse.body.error).toContain('paused');

      // Resume the issue
      const resumeResponse = await request(app).post('/api/issues/1/resume');
      expect(resumeResponse.status).toBe(200);
      expect(resumeResponse.body.data.issue.status).toBe(IssueStatus.Active);

      // Verify issue can be worked on again
      const showResponse = await request(app).get('/api/issues/1');
      expect(showResponse.body.data.status).toBe(IssueStatus.Active);
    });

    it('should prevent invalid stage transitions', async () => {
      // Create issue
      await request(app)
        .post('/api/issues')
        .send({ title: 'Invalid Transition Test' });

      // Try to approve without being at review stage
      const approveResponse = await request(app).post('/api/issues/1/approve');
      expect(approveResponse.status).toBe(400);
      expect(approveResponse.body.error).toContain('does not require approval');

      // Start processing
      await request(app).post('/api/issues/1/start');

      // Try to start again (already in designing)
      const startAgainResponse = await request(app).post('/api/issues/1/start');
      expect(startAgainResponse.status).toBe(400);
      expect(startAgainResponse.body.error).toContain('not in draft stage');
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
      expect(issue2.stage).toBe(Stage.Designing);
      expect(issue3.stage).toBe(Stage.Draft);

      // Filter by stage
      const draftResponse = await request(app).get('/api/issues?stage=draft');
      expect(draftResponse.body.data).toHaveLength(2);

      const designingResponse = await request(app).get('/api/issues?stage=designing');
      expect(designingResponse.body.data).toHaveLength(1);
    });
  });

  describe('Project Status', () => {
    it('should track issue counts by stage', async () => {
      // Create multiple issues at different stages
      await request(app).post('/api/issues').send({ title: 'Draft 1' });
      await request(app).post('/api/issues').send({ title: 'Draft 2' });
      
      await request(app).post('/api/issues').send({ title: 'Designing' });
      await request(app).post('/api/issues/3/start');

      // Get status
      const statusResponse = await request(app).get('/api/status');
      expect(statusResponse.status).toBe(200);

      const status = statusResponse.body.data;
      expect(status.issuesByStage.draft).toBe(2);
      expect(status.issuesByStage.designing).toBe(1);
      expect(status.issues).toBe(3);
    });
  });
});
