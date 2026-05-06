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

function createMockWorktreeManager() {
  return {
    create: vi.fn().mockResolvedValue('/fake/worktree/path'),
    exists: vi.fn().mockReturnValue(false),
    getPath: vi.fn().mockReturnValue('/fake/worktree/path'),
    remove: vi.fn().mockResolvedValue(undefined),
  };
}

describe('POST /issues/:number/start resilience', () => {
  let db: DatabaseManager;
  let stateManager: StateManager;
  let projectService: ProjectService;
  let issueService: IssueService;
  let eventBus: EventBus;
  let agentRunner: AgentRunnerService;
  let projectId: string;

  beforeEach(() => {
    db = new DatabaseManager({ inMemory: true });
    stateManager = new StateManager(db);
    const projectRepo = stateManager.getProjectRepo();
    const issueRepo = stateManager.getIssueRepo();
    const configRepo = stateManager.getConfigRepo();
    const commentRepo = stateManager.getCommentRepo();
    const labelRepo = stateManager.getLabelRepo();
    projectService = new ProjectService(projectRepo, configRepo, issueRepo, labelRepo);
    issueService = new IssueService(issueRepo, commentRepo);
    new ConfigService(configRepo);
    eventBus = new EventBus();
    agentRunner = new AgentRunnerService(eventBus, undefined, stateManager.getIssueRepo(), 8, undefined, undefined, undefined, undefined, stateManager.getIssueTaskQueueRepo());
  });

  afterEach(() => {
    agentRunner.shutdown();
    db.close();
  });

  async function setupProjectAndIssue() {
    const project = await projectService.create({ name: 'Test Project', path: '/test/path' });
    projectId = project.id;
    projectService.setCurrent(project);
    const issue = issueService.create({ projectId, title: 'Test Issue' });
    return issue;
  }

  it('should keep stage as Backlog when agentRunner is not configured', async () => {
    const issue = await setupProjectAndIssue();
    const worktreeManager = createMockWorktreeManager();

    const app = new Hono();
    app.route('/api/issues', createIssueRoutes(
      issueService, projectService, stateManager,
      worktreeManager as any, undefined, undefined, undefined,
    ));
    const server = createTestServer(app);

    const response = await request(server).post(`/api/issues/${issue.number}/start`);

    expect(response.status).toBe(500);
    expect(response.body.success).toBe(false);
    expect(response.body.error).toContain('AgentRunnerService not configured');

    const updatedIssue = issueService.getByNumber(projectId, issue.number);
    expect(updatedIssue?.stage).toBe(Stage.Backlog);

    expect(worktreeManager.create).not.toHaveBeenCalled();
  });

  it('should return 500 when enqueue throws', async () => {
    const issue = await setupProjectAndIssue();
    const worktreeManager = createMockWorktreeManager();

    const app = new Hono();
    app.route('/api/issues', createIssueRoutes(
      issueService, projectService, stateManager,
      worktreeManager as any, undefined, undefined, agentRunner,
    ));
    const server = createTestServer(app);

    vi.spyOn(agentRunner, 'enqueue').mockImplementation(() => {
      throw new Error('agent start unexpected failure');
    });

    const response = await request(server).post(`/api/issues/${issue.number}/start`);

    expect(response.status).toBe(500);
    expect(response.body.success).toBe(false);
    expect(response.body.error).toContain('agent start unexpected failure');
  });

  it('should return 202 when enqueue succeeds', async () => {
    const issue = await setupProjectAndIssue();
    const worktreeManager = createMockWorktreeManager();

    const app = new Hono();
    app.route('/api/issues', createIssueRoutes(
      issueService, projectService, stateManager,
      worktreeManager as any, undefined, undefined, agentRunner,
    ));
    const server = createTestServer(app);

    vi.spyOn(agentRunner, 'enqueue').mockReturnValue({
      taskId: 'test-task-id',
      status: 'pending',
      queuePosition: 0,
    });

    const response = await request(server).post(`/api/issues/${issue.number}/start`);

    expect(response.status).toBe(202);
    expect(response.body.success).toBe(true);
    expect(response.body.data.taskId).toBe('test-task-id');
  });
});
