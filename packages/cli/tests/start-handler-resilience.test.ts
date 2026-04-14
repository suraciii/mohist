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
    agentRunner = new AgentRunnerService(eventBus);
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

  it('should keep stage as Draft when worktree creation fails', async () => {
    const issue = await setupProjectAndIssue();
    const worktreeManager = createMockWorktreeManager();
    worktreeManager.create.mockRejectedValue(new Error('git fetch failed: gnutls_handshake error'));

    const app = new Hono();
    app.route('/api/issues', createIssueRoutes(
      issueService, projectService, stateManager,
      worktreeManager as any, undefined, undefined, agentRunner,
    ));
    const server = createTestServer(app);

    const response = await request(server).post(`/api/issues/${issue.number}/start`);

    expect(response.status).toBe(500);
    expect(response.body.success).toBe(false);
    expect(response.body.error).toContain('git fetch failed');

    const updatedIssue = issueService.getByNumber(projectId, issue.number);
    expect(updatedIssue?.stage).toBe(Stage.Draft);
  });

  it('should keep stage as Draft when agentRunner is not configured', async () => {
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
    expect(updatedIssue?.stage).toBe(Stage.Draft);

    expect(worktreeManager.create).not.toHaveBeenCalled();
  });

  it('should rollback stage to Draft when error occurs after stage transition', async () => {
    const issue = await setupProjectAndIssue();
    const worktreeManager = createMockWorktreeManager();

    const errorSpy = vi.spyOn(console, 'error').mockImplementation(() => {});

    const app = new Hono();
    app.route('/api/issues', createIssueRoutes(
      issueService, projectService, stateManager,
      worktreeManager as any, undefined, undefined, agentRunner,
    ));
    const server = createTestServer(app);

    const originalStart = agentRunner.start.bind(agentRunner);
    vi.spyOn(agentRunner, 'isRunning').mockReturnValue(false);
    vi.spyOn(agentRunner, 'start').mockImplementation((() => {
      throw new Error('agent start unexpected failure');
    }) as any);

    const response = await request(server).post(`/api/issues/${issue.number}/start`);

    expect(response.status).toBe(500);
    expect(response.body.success).toBe(false);
    expect(response.body.error).toContain('agent start unexpected failure');

    const updatedIssue = issueService.getByNumber(projectId, issue.number);
    expect(updatedIssue?.stage).toBe(Stage.Draft);

    errorSpy.mockRestore();
  });

  it('should log error but return original error when rollback fails', async () => {
    const issue = await setupProjectAndIssue();
    const worktreeManager = createMockWorktreeManager();

    const errorSpy = vi.spyOn(console, 'error').mockImplementation(() => {});

    vi.spyOn(agentRunner, 'isRunning').mockReturnValue(false);
    vi.spyOn(agentRunner, 'start').mockImplementation((() => {
      throw new Error('agent start failed');
    }) as any);

    const originalTransition = issueService.transitionToStage.bind(issueService);
    let transitionCallCount = 0;
    vi.spyOn(issueService, 'transitionToStage').mockImplementation((id: string, stage: Stage) => {
      transitionCallCount++;
      if (transitionCallCount === 1) {
        return originalTransition(id, stage);
      }
      throw new Error('rollback DB locked');
    });

    const app = new Hono();
    app.route('/api/issues', createIssueRoutes(
      issueService, projectService, stateManager,
      worktreeManager as any, undefined, undefined, agentRunner,
    ));
    const server = createTestServer(app);

    const response = await request(server).post(`/api/issues/${issue.number}/start`);

    expect(response.status).toBe(500);
    expect(response.body.error).toContain('agent start failed');

    expect(errorSpy).toHaveBeenCalledWith(
      expect.stringContaining('Failed to rollback stage to Draft'),
      expect.any(Error)
    );

    errorSpy.mockRestore();
  });

  it('should not delete worktree on rollback', async () => {
    const issue = await setupProjectAndIssue();
    const worktreeManager = createMockWorktreeManager();

    vi.spyOn(agentRunner, 'isRunning').mockReturnValue(false);
    vi.spyOn(agentRunner, 'start').mockImplementation((() => {
      throw new Error('agent start failed');
    }) as any);

    const app = new Hono();
    app.route('/api/issues', createIssueRoutes(
      issueService, projectService, stateManager,
      worktreeManager as any, undefined, undefined, agentRunner,
    ));
    const server = createTestServer(app);

    await request(server).post(`/api/issues/${issue.number}/start`);

    expect(worktreeManager.create).toHaveBeenCalledTimes(1);
    expect(worktreeManager.remove).not.toHaveBeenCalled();
  });

  it('should succeed normally when worktree and agent work', async () => {
    const issue = await setupProjectAndIssue();
    const worktreeManager = createMockWorktreeManager();

    vi.spyOn(agentRunner, 'isRunning').mockReturnValue(false);
    vi.spyOn(agentRunner, 'start').mockReturnValue({
      started: true,
      queued: false,
      queuePosition: 0,
    } as any);

    const app = new Hono();
    app.route('/api/issues', createIssueRoutes(
      issueService, projectService, stateManager,
      worktreeManager as any, undefined, undefined, agentRunner,
    ));
    const server = createTestServer(app);

    const response = await request(server).post(`/api/issues/${issue.number}/start`);

    expect(response.status).toBe(200);
    expect(response.body.success).toBe(true);
    expect(response.body.data.issue.stage).toBe(Stage.Plan);

    expect(worktreeManager.create).toHaveBeenCalledTimes(1);
  });
});
