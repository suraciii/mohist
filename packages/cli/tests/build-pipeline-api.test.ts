import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import http from 'node:http';
import { Hono } from 'hono';
import request from 'supertest';
import { DatabaseManager } from '../src/db/database';
import { IssueRepo } from '../src/db/issue-repo';
import { WorkflowLogRepo } from '../src/db/workflow-log-repo';
import { CommentRepo } from '../src/db/comment-repo';
import { LabelRepo } from '../src/db/label-repo';
import { ConfigRepo } from '../src/db/config-repo';
import { ProjectRepo } from '../src/db/project-repo';
import { ProjectService } from '../src/services/project-service';
import { IssueService } from '../src/services/issue-service';
import { EventBus } from '../src/services/event-bus';
import { StateManager } from '../src/server/state-manager';
import { AgentRunnerService } from '../src/services/agent-runner-service';
import { createIssueRoutes } from '../src/api/issues';
import { Stage } from '../src/types';

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

describe('Tasks API with real filesystem', () => {
  let db: DatabaseManager;
  let projectService: ProjectService;
  let issueService: IssueService;
  let stateManager: StateManager;
  let server: http.Server;
  let tempDir: string;
  let savedApiKeys: Record<string, string | undefined> = {};

  beforeEach(async () => {
    savedApiKeys = {};
    for (const key of Object.keys(process.env)) {
      if (key.endsWith('_API_KEY')) {
        savedApiKeys[key] = process.env[key];
        delete process.env[key];
      }
    }

    tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-tasks-api-test-'));

    db = new DatabaseManager({ inMemory: true });
    stateManager = new StateManager(db);
    const configRepo = stateManager.getConfigRepo();
    const projectRepo = stateManager.getProjectRepo();
    const issueRepo = stateManager.getIssueRepo();
    const commentRepo = stateManager.getCommentRepo();
    const labelRepo = stateManager.getLabelRepo();
    projectService = new ProjectService(projectRepo, configRepo, issueRepo, labelRepo);
    issueService = new IssueService(issueRepo, commentRepo);

    const app = new Hono();
    const eventBus = new EventBus();
    const agentRunner = new AgentRunnerService(eventBus);
    const workflowLogRepo = new WorkflowLogRepo(db);
    app.route('/api/issues', createIssueRoutes(
      issueService,
      projectService,
      stateManager,
      undefined,
      undefined,
      undefined,
      agentRunner,
      workflowLogRepo,
    ));
    server = createTestServer(app);

    const project = await projectService.create({ name: 'Test Project', path: tempDir });
    projectService.setCurrent(project);
  });

  afterEach(() => {
    db.close();
    fs.rmSync(tempDir, { recursive: true, force: true });
    for (const [key, val] of Object.entries(savedApiKeys)) {
      if (val === undefined) {
        delete process.env[key];
      } else {
        process.env[key] = val;
      }
    }
  });

  describe('GET /api/issues/:number/tasks', () => {
    it('should return tasks from tasks.json when change exists', async () => {
      const changeDir = path.join(tempDir, 'openspec', 'changes', '1-test');
      fs.mkdirSync(changeDir, { recursive: true });
      const tasksFile = {
        version: 1,
        tasks: [
          { id: 'T-001', title: 'Task 1', passes: true, attempts: 1 },
          { id: 'T-002', title: 'Task 2', passes: false, attempts: 0 },
        ],
      };
      fs.writeFileSync(path.join(changeDir, 'tasks.json'), JSON.stringify(tasksFile));

      await issueService.create({ projectId: projectService.getCurrentId()!, title: 'Test Issue' });

      const response = await request(server).get('/api/issues/1/tasks');

      expect(response.status).toBe(200);
      expect(response.body.success).toBe(true);
      expect(response.body.data.tasks).toHaveLength(2);
      expect(response.body.data.tasks[0].id).toBe('T-001');
      expect(response.body.data.tasks[1].id).toBe('T-002');
    });

    it('should return task details with passes and attempts', async () => {
      const changeDir = path.join(tempDir, 'openspec', 'changes', '1-test');
      fs.mkdirSync(changeDir, { recursive: true });
      const tasksFile = {
        version: 1,
        tasks: [
          { id: 'T-001', title: 'Completed Task', passes: true, attempts: 2 },
          { id: 'T-002', title: 'Failed Task', passes: false, attempts: 3, error: 'timeout' },
        ],
      };
      fs.writeFileSync(path.join(changeDir, 'tasks.json'), JSON.stringify(tasksFile));

      await issueService.create({ projectId: projectService.getCurrentId()!, title: 'Test Issue' });

      const response = await request(server).get('/api/issues/1/tasks');

      expect(response.status).toBe(200);
      expect(response.body.data.tasks[0].passes).toBe(true);
      expect(response.body.data.tasks[0].attempts).toBe(2);
      expect(response.body.data.tasks[1].passes).toBe(false);
      expect(response.body.data.tasks[1].attempts).toBe(3);
      expect(response.body.data.tasks[1].error).toBe('timeout');
    });
  });

  describe('GET /api/issues/:number/build-status with real fs', () => {
    it('should return correct progress when tasks exist', async () => {
      const changeDir = path.join(tempDir, 'openspec', 'changes', '1-test');
      fs.mkdirSync(changeDir, { recursive: true });
      const tasksFile = {
        version: 1,
        tasks: [
          { id: 'T-001', title: 'Task 1', passes: true, attempts: 1 },
          { id: 'T-002', title: 'Task 2', passes: false, attempts: 0 },
          { id: 'T-003', title: 'Task 3', passes: false, attempts: 1, error: 'failed' },
        ],
      };
      fs.writeFileSync(path.join(changeDir, 'tasks.json'), JSON.stringify(tasksFile));

      await issueService.create({ projectId: projectService.getCurrentId()!, title: 'Test Issue' });
      issueService.transitionToStageByNumber(projectService.getCurrentId()!, 1, Stage.Build);

      const response = await request(server).get('/api/issues/1/build-status');

      expect(response.status).toBe(200);
      expect(response.body.data.status).toBe('running');
      expect(response.body.data.progress.completed).toBe(1);
      expect(response.body.data.progress.failed).toBe(1);
      expect(response.body.data.progress.total).toBe(3);
      expect(response.body.data.progress.currentTask).toBe('T-002');
      expect(response.body.data.tasks).toHaveLength(3);
    });
  });
});
