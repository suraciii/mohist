import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
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
import { WorkflowApplicationService } from '../src/services/workflow-application-service';
import { WorkflowRunService } from '../src/services/workflow-run-service';
import { StateManager } from '../src/server/state-manager';
import { CommentRepo } from '../src/db/comment-repo';
import { LabelRepo } from '../src/db/label-repo';
import { createIssueRoutes } from '../src/api/issues';
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
        res.write(Buffer.from(value));
      }
    }
    res.end();
  });
}

describe('#215 recovery regression: Plan fails while generating tasks.json', () => {
  let db: DatabaseManager;
  let issueRepo: IssueRepo;
  let projectService: ProjectService;
  let issueService: IssueService;
  let stateManager: StateManager;
  let savedApiKeys: Record<string, string | undefined> = {};
  let tmpDir: string;
  let projectCounter = 0;

  beforeEach(() => {
    savedApiKeys = {};
    for (const key of Object.keys(process.env)) {
      if (key.endsWith('_API_KEY')) {
        savedApiKeys[key] = process.env[key];
        delete process.env[key];
      }
    }

    tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), `mohist-215-regression-${Date.now()}-`));
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
    fs.rmSync(tmpDir, { recursive: true, force: true });
    for (const [key, val] of Object.entries(savedApiKeys)) {
      if (val === undefined) {
        delete process.env[key];
      } else {
        process.env[key] = val;
      }
    }
  });

  function setupApp() {
    const app = new Hono();
    const eventBus = new EventBus();
    const agentRunner = new AgentRunnerService(eventBus, undefined, issueRepo, 8, undefined, undefined, undefined, undefined, stateManager.getIssueTaskQueueRepo());
    app.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, undefined, undefined, agentRunner, undefined, undefined, undefined, undefined, undefined, undefined, undefined, undefined, undefined, new WorkflowRunService(db)));
    return { app, agentRunner };
  }

  function recordFailedPlanWork(issueId: string, issueNumber: number): void {
    const workflowApplicationService = new WorkflowApplicationService(db);
    workflowApplicationService.startWorkflow({ issueId, issueNumber });
    workflowApplicationService.startTaskAttempt({ issueId, stage: Stage.Plan, taskId: 'proposal', evidence: { executionId: 'plan-proposal-failed' } });
    workflowApplicationService.completeTask({ issueId, stage: Stage.Plan, taskId: 'proposal', result: { status: 'failed', error: 'Plan stage failed' } });
    issueRepo.updateStage(issueId, Stage.Plan);
    issueRepo.updateStatus(issueId, IssueStatus.Blocked);
    issueRepo.updateBlockedReason(issueId, 'Plan stage failed');
  }

  function createChangeDir(issueNumber: number): string {
    const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issueNumber}-test-change`);
    fs.mkdirSync(changeDir, { recursive: true });
    return changeDir;
  }

  function nextProjectName(): string {
    projectCounter++;
    return `TestProject-${projectCounter}`;
  }

  describe('Retry after Plan failure before tasks.json exists', () => {
    it('accepts retry when latest WorkflowRun failed in Plan while generating tasks.json', async () => {
      const project = await projectService.create({ name: nextProjectName(), path: tmpDir });
      projectService.setCurrent(project);

      const issue = issueService.create({ projectId: project.id, title: 'Plan fails before tasks.json' });
      issueRepo.updateStage(issue.id, Stage.Plan);
      issueRepo.updateStatus(issue.id, IssueStatus.Blocked);
      issueRepo.updateBlockedReason(issue.id, 'Plan stage failed');
      recordFailedPlanWork(issue.id, issue.number);

      createChangeDir(issue.number);

      const { app, agentRunner } = setupApp();
      const server = createTestServer(app);

      const enqueueSpy = vi.spyOn(agentRunner, 'enqueue').mockReturnValue({ taskId: 'fake-task', status: 'pending' });

      const response = await request(server).post(`/api/issues/${issue.number}/retry`);

      expect(response.status).toBe(202);
      expect(response.body.success).toBe(true);
      expect(enqueueSpy).toHaveBeenCalledWith(issue.id, 'resume-pipeline');

      enqueueSpy.mockRestore();
    });

    it('does not require tasks.json to exist for retry to succeed', async () => {
      const project = await projectService.create({ name: nextProjectName(), path: tmpDir });
      projectService.setCurrent(project);

      const issue = issueService.create({ projectId: project.id, title: 'Plan fails before tasks.json' });
      issueRepo.updateStage(issue.id, Stage.Plan);
      issueRepo.updateStatus(issue.id, IssueStatus.Blocked);
      issueRepo.updateBlockedReason(issue.id, 'Plan stage failed');
      recordFailedPlanWork(issue.id, issue.number);

      const changeDir = createChangeDir(issue.number);

      const tasksJsonPath = path.join(changeDir, 'tasks.json');
      expect(fs.existsSync(tasksJsonPath)).toBe(false);

      const { app, agentRunner } = setupApp();
      const server = createTestServer(app);

      const enqueueSpy = vi.spyOn(agentRunner, 'enqueue').mockReturnValue({ taskId: 'fake-task', status: 'pending' });

      const response = await request(server).post(`/api/issues/${issue.number}/retry`);

      expect(response.status).toBe(202);
      expect(response.body.success).toBe(true);
      expect(fs.existsSync(tasksJsonPath)).toBe(false);

      enqueueSpy.mockRestore();
    });

    it('retry does not return checkpoint-required error when WorkflowRun has failed work', async () => {
      const project = await projectService.create({ name: nextProjectName(), path: tmpDir });
      projectService.setCurrent(project);

      const issue = issueService.create({ projectId: project.id, title: 'Plan fails before tasks.json' });
      issueRepo.updateStage(issue.id, Stage.Plan);
      issueRepo.updateStatus(issue.id, IssueStatus.Blocked);
      issueRepo.updateBlockedReason(issue.id, 'Plan stage failed');
      recordFailedPlanWork(issue.id, issue.number);

      createChangeDir(issue.number);

      const { app } = setupApp();
      const server = createTestServer(app);

      const response = await request(server).post(`/api/issues/${issue.number}/retry`);

      expect(response.status).toBe(202);
      expect(response.body.error).toBeUndefined();
    });
  });

  describe('Rerun Stage after Plan failure before tasks.json exists', () => {
    it('rerun restarts Plan from the first Plan work, not from tasks or self-review', async () => {
      const project = await projectService.create({ name: nextProjectName(), path: tmpDir });
      projectService.setCurrent(project);

      const issue = issueService.create({ projectId: project.id, title: 'Plan fails before tasks.json' });
      issueRepo.updateStage(issue.id, Stage.Plan);
      issueRepo.updateStatus(issue.id, IssueStatus.Blocked);
      issueRepo.updateBlockedReason(issue.id, 'Plan stage failed');
      recordFailedPlanWork(issue.id, issue.number);

      createChangeDir(issue.number);

      const { app } = setupApp();
      const server = createTestServer(app);

      const response = await request(server).post(`/api/issues/${issue.number}/rerun`);

      expect(response.status).toBe(202);
      expect(response.body.success).toBe(true);
      expect(response.body.data.message).toContain('rerun');
      expect(response.body.data.message.toLowerCase()).toContain('plan');
    });

    it('rerun does not reference earlier stages like Build or Check in the response', async () => {
      const project = await projectService.create({ name: nextProjectName(), path: tmpDir });
      projectService.setCurrent(project);

      const issue = issueService.create({ projectId: project.id, title: 'Plan fails before tasks.json' });
      issueRepo.updateStage(issue.id, Stage.Plan);
      issueRepo.updateStatus(issue.id, IssueStatus.Blocked);
      issueRepo.updateBlockedReason(issue.id, 'Plan stage failed');
      recordFailedPlanWork(issue.id, issue.number);

      createChangeDir(issue.number);

      const { app } = setupApp();
      const server = createTestServer(app);

      const response = await request(server).post(`/api/issues/${issue.number}/rerun`);

      expect(response.status).toBe(202);
      const msg = response.body.data.message.toLowerCase();
      expect(msg).not.toContain('build');
      expect(msg).not.toContain('check');
    });
  });

  describe('Retry and Rerun preserve stage', () => {
    it('retry preserves the current stage (Plan) and does not reset to Backlog', async () => {
      const project = await projectService.create({ name: nextProjectName(), path: tmpDir });
      projectService.setCurrent(project);

      const issue = issueService.create({ projectId: project.id, title: 'Plan fails before tasks.json' });
      issueRepo.updateStage(issue.id, Stage.Plan);
      issueRepo.updateStatus(issue.id, IssueStatus.Blocked);
      issueRepo.updateBlockedReason(issue.id, 'Plan stage failed');
      recordFailedPlanWork(issue.id, issue.number);

      createChangeDir(issue.number);

      const { app } = setupApp();
      const server = createTestServer(app);

      const response = await request(server).post(`/api/issues/${issue.number}/retry`);

      expect(response.status).toBe(202);
      const updatedIssue = issueRepo.findById(issue.id);
      expect(updatedIssue?.stage).toBe(Stage.Plan);
      expect(updatedIssue?.stage).not.toBe(Stage.Backlog);
    });

    it('rerun preserves the current stage (Plan) and does not reset to Backlog', async () => {
      const project = await projectService.create({ name: nextProjectName(), path: tmpDir });
      projectService.setCurrent(project);

      const issue = issueService.create({ projectId: project.id, title: 'Plan fails before tasks.json' });
      issueRepo.updateStage(issue.id, Stage.Plan);
      issueRepo.updateStatus(issue.id, IssueStatus.Blocked);
      issueRepo.updateBlockedReason(issue.id, 'Plan stage failed');
      recordFailedPlanWork(issue.id, issue.number);

      createChangeDir(issue.number);

      const { app } = setupApp();
      const server = createTestServer(app);

      const response = await request(server).post(`/api/issues/${issue.number}/rerun`);

      expect(response.status).toBe(202);
      const updatedIssue = issueRepo.findById(issue.id);
      expect(updatedIssue?.stage).toBe(Stage.Plan);
      expect(updatedIssue?.stage).not.toBe(Stage.Backlog);
    });
  });

  describe('Post-tasks.json Plan failure recovery', () => {
    it('retry retries failed Plan work even when tasks.json already exists', async () => {
      const project = await projectService.create({ name: nextProjectName(), path: tmpDir });
      projectService.setCurrent(project);

      const issue = issueService.create({ projectId: project.id, title: 'Plan fails after tasks.json' });
      issueRepo.updateStage(issue.id, Stage.Plan);
      issueRepo.updateStatus(issue.id, IssueStatus.Blocked);
      issueRepo.updateBlockedReason(issue.id, 'Plan stage failed');
      recordFailedPlanWork(issue.id, issue.number);

      const changeDir = createChangeDir(issue.number);
      fs.writeFileSync(
        path.join(changeDir, 'tasks.json'),
        JSON.stringify({ version: 1, tasks: [{ id: 'T-001', title: 'Test task', passes: true }] })
      );

      const { app, agentRunner } = setupApp();
      const server = createTestServer(app);

      const enqueueSpy = vi.spyOn(agentRunner, 'enqueue').mockReturnValue({ taskId: 'fake-task', status: 'pending' });

      const response = await request(server).post(`/api/issues/${issue.number}/retry`);

      expect(response.status).toBe(202);
      expect(enqueueSpy).toHaveBeenCalledWith(issue.id, 'resume-pipeline');

      enqueueSpy.mockRestore();
    });
  });
});
