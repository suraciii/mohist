import { describe, it, expect, beforeEach, vi } from 'vitest';
import http from 'node:http';
import { Hono } from 'hono';
import request from 'supertest';
import { DatabaseManager } from '../src/db/database';
import { ProjectRepo } from '../src/db/project-repo';
import { IssueRepo } from '../src/db/issue-repo';
import { ProjectService } from '../src/services/project-service';
import { IssueService } from '../src/services/issue-service';
import { EventBus, AgentRunnerService } from '../src/services';
import { StateManager } from '../src/server/state-manager';
import { CommentRepo } from '../src/db/comment-repo';
import { LabelRepo } from '../src/db/label-repo';
import { ConfigRepo } from '../src/db/config-repo';
import { ConfigService } from '../src/services/config-service';
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

describe('POST /api/issues/:number/rebase', () => {
  let db: DatabaseManager;
  let projectService: ProjectService;
  let issueService: IssueService;
  let stateManager: StateManager;
  let projectId: string;

  beforeEach(() => {
    db = new DatabaseManager({ inMemory: true });
    stateManager = new StateManager(db);
    const projectRepo = stateManager.getProjectRepo();
    const issueRepo = stateManager.getIssueRepo();
    const commentRepo = stateManager.getCommentRepo();
    const labelRepo = stateManager.getLabelRepo();
    const configRepo = stateManager.getConfigRepo();
    projectService = new ProjectService(projectRepo, configRepo, issueRepo, labelRepo);
    issueService = new IssueService(issueRepo, commentRepo);
  });

  function makeServer(opts: { worktreeManager?: any; agentRunner?: AgentRunnerService; mergeQueue?: any } = {}) {
    const app = new Hono();
    const eventBus = new EventBus();
    const agentRunner = opts.agentRunner ?? new AgentRunnerService(eventBus);
    app.route('/api/issues', createIssueRoutes(
      issueService, projectService, stateManager,
      opts.worktreeManager, undefined, undefined, agentRunner,
      undefined, undefined, undefined, undefined,
      opts.mergeQueue,
    ));
    return createTestServer(app);
  }

  beforeEach(async () => {
    const project = await projectService.create({ name: 'Test Project', path: '/test/path' });
    projectId = project.id;
    projectService.setCurrent(project);
  });

  afterEach(() => {
    db.close();
  });

  describe('precondition checks', () => {
    it('should return 404 for non-existent issue', async () => {
      const server = makeServer();
      const res = await request(server).post('/api/issues/999/rebase');
      expect(res.status).toBe(404);
      expect(res.body.error).toContain('not found');
    });

    it('should return 400 for backlog stage', async () => {
      issueService.create({ projectId, title: 'Backlog Issue' });
      const server = makeServer();
      const res = await request(server).post('/api/issues/1/rebase');
      expect(res.status).toBe(400);
      expect(res.body.error).toContain('Rebase not available');
      expect(res.body.error).toContain('backlog');
    });

    it('should return 400 for explore stage', async () => {
      const issue = issueService.create({ projectId, title: 'Explore Issue' });
      issueService.transitionToStage(issue.id, Stage.Explore);
      const server = makeServer();
      const res = await request(server).post('/api/issues/1/rebase');
      expect(res.status).toBe(400);
      expect(res.body.error).toContain('Rebase not available');
    });

    it('should return 400 when worktree does not exist', async () => {
      const issue = issueService.create({ projectId, title: 'Plan Issue' });
      issueService.transitionToStage(issue.id, Stage.Plan);
      issueService.setStatus(issue.id, IssueStatus.Active);

      const wtManager = {
        exists: vi.fn().mockReturnValue(false),
      };
      const server = makeServer({ worktreeManager: wtManager });
      const res = await request(server).post('/api/issues/1/rebase');
      expect(res.status).toBe(400);
      expect(res.body.error).toContain('Worktree not found');
    });

    it('should return 409 when agent is running', async () => {
      const issue = issueService.create({ projectId, title: 'Build Issue' });
      issueService.transitionToStage(issue.id, Stage.Build);
      issueService.setStatus(issue.id, IssueStatus.Active);

      const eventBus = new EventBus();
      const agentRunner = new AgentRunnerService(eventBus);

      vi.spyOn(agentRunner, 'isRunning').mockReturnValue(true);

      const wtManager = {
        exists: vi.fn().mockReturnValue(true),
      };
      const server = makeServer({ worktreeManager: wtManager, agentRunner });
      const res = await request(server).post('/api/issues/1/rebase');
      expect(res.status).toBe(409);
      expect(res.body.error).toContain('Agent is running');
    });
  });

  describe('done stage', () => {
    it('should delegate to mergeQueue.retry', async () => {
      const issue = issueService.create({ projectId, title: 'Done Issue' });
      issueService.transitionToStage(issue.id, Stage.Done);

      const mergeQueue = { retry: vi.fn().mockReturnValue(true) };
      const server = makeServer({ mergeQueue });
      const res = await request(server).post('/api/issues/1/rebase');
      expect(res.status).toBe(200);
      expect(res.body.data.rebased).toBe(true);
      expect(mergeQueue.retry).toHaveBeenCalledWith(1);
    });

    it('should return 409 when mergeQueue.retry returns false', async () => {
      const issue = issueService.create({ projectId, title: 'Done Issue' });
      issueService.transitionToStage(issue.id, Stage.Done);

      const mergeQueue = { retry: vi.fn().mockReturnValue(false) };
      const server = makeServer({ mergeQueue });
      const res = await request(server).post('/api/issues/1/rebase');
      expect(res.status).toBe(409);
    });

    it('should return 500 when mergeQueue is not configured', async () => {
      const issue = issueService.create({ projectId, title: 'Done Issue' });
      issueService.transitionToStage(issue.id, Stage.Done);

      const server = makeServer();
      const res = await request(server).post('/api/issues/1/rebase');
      expect(res.status).toBe(500);
      expect(res.body.error).toContain('MergeQueue');
    });
  });

  describe('fast-forward check', () => {
    it('should return "Already up to date" when canFastForward is true', async () => {
      const issue = issueService.create({ projectId, title: 'Plan Issue' });
      issueService.transitionToStage(issue.id, Stage.Plan);
      issueService.setStatus(issue.id, IssueStatus.Active);

      const wtManager = {
        exists: vi.fn().mockReturnValue(true),
        canFastForward: vi.fn().mockResolvedValue(true),
      };
      const server = makeServer({ worktreeManager: wtManager });
      const res = await request(server).post('/api/issues/1/rebase');
      expect(res.status).toBe(200);
      expect(res.body.data.rebased).toBe(false);
      expect(res.body.data.message).toContain('Already up to date');
    });
  });

  describe('rebase conflict', () => {
    it('should return 409 with conflict files when rebase fails', async () => {
      const issue = issueService.create({ projectId, title: 'Build Issue' });
      issueService.transitionToStage(issue.id, Stage.Build);
      issueService.setStatus(issue.id, IssueStatus.Active);

      const wtManager = {
        exists: vi.fn().mockReturnValue(true),
        canFastForward: vi.fn().mockResolvedValue(false),
        rebaseOntoMaster: vi.fn().mockResolvedValue({
          success: false,
          conflicts: ['src/foo.ts', 'src/bar.ts'],
        }),
      };
      const server = makeServer({ worktreeManager: wtManager });
      const res = await request(server).post('/api/issues/1/rebase');
      expect(res.status).toBe(409);
      expect(res.body.error).toContain('conflicts');
      expect(res.body.data.conflicts).toEqual(['src/foo.ts', 'src/bar.ts']);
    });
  });

  describe('successful rebase', () => {
    it('should return rebased: true on successful rebase', async () => {
      const issue = issueService.create({ projectId, title: 'Build Issue' });
      issueService.transitionToStage(issue.id, Stage.Build);
      issueService.setStatus(issue.id, IssueStatus.Active);

      const wtManager = {
        exists: vi.fn().mockReturnValue(true),
        canFastForward: vi.fn().mockResolvedValue(false),
        rebaseOntoMaster: vi.fn().mockResolvedValue({ success: true, conflicts: [] }),
        getPath: vi.fn().mockReturnValue('/tmp/worktree'),
      };
      const server = makeServer({ worktreeManager: wtManager });
      const res = await request(server).post('/api/issues/1/rebase');
      expect(res.status).toBe(200);
      expect(res.body.data.rebased).toBe(true);
      expect(res.body.data.message).toContain('Rebase successful');
    });
  });
});
