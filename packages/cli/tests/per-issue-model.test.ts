import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import http from 'node:http';
import { Hono } from 'hono';
import request from 'supertest';
import { DatabaseManager } from '../src/db/database';
import { initializeDatabase, getSchemaVersion } from '../src/db/migrations';
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
import { createIssueRoutes } from '../src/api/issues';

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

describe('Per-issue model override - DB', () => {
  let db: DatabaseManager;
  let repo: IssueRepo;
  let projectId: string;

  beforeEach(() => {
    db = new DatabaseManager({ inMemory: true });
    initializeDatabase(db);

    const projectRepo = new ProjectRepo(db);
    const project = projectRepo.create({ name: 'Test Project', path: '/test' });
    projectId = project.id;

    repo = new IssueRepo(db);
  });

  afterEach(() => {
    db.close();
  });

  describe('migration v15 - model column', () => {
    it('should have model column after initialization', () => {
      const tableInfo = db.all<{ name: string }>("PRAGMA table_info(issues)");
      const hasModel = tableInfo.some(col => col.name === 'model');
      expect(hasModel).toBe(true);
    });

    it('should default model to NULL for new issues', () => {
      const issue = repo.create({ number: 1, projectId, title: 'Test' });
      const row = db.get<{ model: string | null }>(
        'SELECT model FROM issues WHERE id = ?',
        [issue.id]
      );
      expect(row?.model).toBeNull();
    });

    it('should set schema version to 15', () => {
      expect(getSchemaVersion(db)).toBe(15);
    });
  });

  describe('IssueRepo.updateModel', () => {
    it('should set model and refresh updated_at', async () => {
      const issue = repo.create({ number: 1, projectId, title: 'Test' });
      const originalUpdatedAt = issue.updatedAt;

      await new Promise(r => setTimeout(r, 10));
      const updated = repo.updateModel(issue.id, 'anthropic/claude-sonnet-4-20250514');

      expect(updated).not.toBeNull();
      expect(updated!.model).toBe('anthropic/claude-sonnet-4-20250514');
      expect(updated!.updatedAt).not.toBe(originalUpdatedAt);
    });

    it('should clear model when passed null', () => {
      const issue = repo.create({ number: 1, projectId, title: 'Test' });
      repo.updateModel(issue.id, 'anthropic/claude-sonnet-4-20250514');

      const cleared = repo.updateModel(issue.id, null);
      expect(cleared).not.toBeNull();
      expect(cleared!.model).toBeUndefined();
    });

    it('should return null for non-existent issue', () => {
      const result = repo.updateModel('nonexistent-id', 'openai/gpt-4o');
      expect(result).toBeNull();
    });
  });

  describe('IssueRepo.findById returns model', () => {
    it('should return model field when set', () => {
      const issue = repo.create({ number: 1, projectId, title: 'Test' });
      repo.updateModel(issue.id, 'anthropic/claude-sonnet-4-20250514');

      const found = repo.findById(issue.id);
      expect(found).not.toBeNull();
      expect(found!.model).toBe('anthropic/claude-sonnet-4-20250514');
    });

    it('should return undefined model when not set', () => {
      const issue = repo.create({ number: 1, projectId, title: 'Test' });

      const found = repo.findById(issue.id);
      expect(found).not.toBeNull();
      expect(found!.model).toBeUndefined();
    });
  });

  describe('IssueRepo.update with model', () => {
    it('should update model via generic update method', () => {
      const issue = repo.create({ number: 1, projectId, title: 'Test' });
      const updated = repo.update(issue.id, { model: 'openai/gpt-4o' });

      expect(updated).not.toBeNull();
      expect(updated!.model).toBe('openai/gpt-4o');
    });

    it('should clear model via generic update method with null', () => {
      const issue = repo.create({ number: 1, projectId, title: 'Test' });
      repo.update(issue.id, { model: 'openai/gpt-4o' });

      const cleared = repo.update(issue.id, { model: null });
      expect(cleared).not.toBeNull();
      expect(cleared!.model).toBeUndefined();
    });
  });
});

describe('Per-issue model override - API', () => {
  let db: DatabaseManager;
  let projectService: ProjectService;
  let issueService: IssueService;
  let stateManager: StateManager;
  let projectId: string;
  let server: http.Server;

  beforeEach(async () => {
    db = new DatabaseManager({ inMemory: true });
    stateManager = new StateManager(db);

    const projectRepo = stateManager.getProjectRepo();
    const issueRepo = stateManager.getIssueRepo();
    const configRepo = stateManager.getConfigRepo();
    const commentRepo = stateManager.getCommentRepo();
    const labelRepo = stateManager.getLabelRepo();

    projectService = new ProjectService(projectRepo, configRepo, issueRepo, labelRepo);
    issueService = new IssueService(issueRepo, commentRepo);

    const eventBus = new EventBus();
    const agentRunner = new AgentRunnerService(eventBus);
    const app = new Hono();
    app.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, undefined, undefined, undefined, agentRunner));
    server = createTestServer(app);

    const project = await projectService.create({ name: 'Test Project', path: '/test/path' });
    projectId = project.id;
    projectService.setCurrent(project);
  });

  afterEach(() => {
    db.close();
  });

  describe('PATCH /api/issues/:number - model', () => {
    it('should set model with valid format', async () => {
      await issueService.create({ projectId, title: 'Test Issue' });

      const response = await request(server)
        .patch('/api/issues/1')
        .send({ model: 'anthropic/claude-sonnet-4-20250514' });

      expect(response.status).toBe(200);
      expect(response.body.success).toBe(true);
      expect(response.body.data.model).toBe('anthropic/claude-sonnet-4-20250514');
    });

    it('should clear model with null', async () => {
      await issueService.create({ projectId, title: 'Test Issue' });

      await request(server)
        .patch('/api/issues/1')
        .send({ model: 'anthropic/claude-sonnet-4-20250514' });

      const response = await request(server)
        .patch('/api/issues/1')
        .send({ model: null });

      expect(response.status).toBe(200);
      expect(response.body.success).toBe(true);
      expect(response.body.data.model).toBeUndefined();
    });

    it('should reject model without / character', async () => {
      await issueService.create({ projectId, title: 'Test Issue' });

      const response = await request(server)
        .patch('/api/issues/1')
        .send({ model: 'invalid-model' });

      expect(response.status).toBe(400);
      expect(response.body.error).toContain('Invalid model format');
    });

    it('should preserve model on other PATCH updates', async () => {
      await issueService.create({ projectId, title: 'Test Issue' });

      await request(server)
        .patch('/api/issues/1')
        .send({ model: 'anthropic/claude-sonnet-4-20250514' });

      const response = await request(server)
        .patch('/api/issues/1')
        .send({ title: 'Updated Title' });

      expect(response.status).toBe(200);
      expect(response.body.data.model).toBe('anthropic/claude-sonnet-4-20250514');
      expect(response.body.data.title).toBe('Updated Title');
    });
  });
});
