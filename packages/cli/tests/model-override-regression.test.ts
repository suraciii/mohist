import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import http from 'node:http';
import { Hono } from 'hono';
import request from 'supertest';
import { DatabaseManager } from '../src/db/database';
import { initializeDatabase } from '../src/db/migrations';
import { ProjectRepo } from '../src/db/project-repo';
import { IssueRepo } from '../src/db/issue-repo';
import { ConfigRepo } from '../src/db/config-repo';
import { CommentRepo } from '../src/db/comment-repo';
import { LabelRepo } from '../src/db/label-repo';
import { ProjectService } from '../src/services/project-service';
import { IssueService } from '../src/services/issue-service';
import { EventBus, AgentRunnerService } from '../src/services';
import { StateManager } from '../src/server/state-manager';
import { createIssueRoutes } from '../src/api/issues';
import { resolveStageModel, isValidModelId, EXECUTABLE_MODEL_STAGES } from '../src/config/model-resolution';
import type { ConfigInfo } from '../src/config/config-schema';
import * as path from 'node:path';
import * as fs from 'node:fs';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const discoverySourcePath = path.resolve(__dirname, '../src/services/opencode-discovery-service.ts');

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

describe('model-override-regression: resolveStageModel precedence', () => {
  const baseConfig: ConfigInfo = {
    opencode: {
      model: 'global/default-model',
      stageModels: {
        build: 'global/build-model',
        plan: 'global/plan-model',
      },
    },
  };

  it('issue stage model overrides all lower levels', () => {
    const override = {
      stageModels: { build: 'issue/build-model' },
      model: 'issue/default-model',
    };
    expect(resolveStageModel('build', baseConfig, override)).toBe('issue/build-model');
  });

  it('issue default model applies when stage override is unset', () => {
    const override = { model: 'issue/default-model' };
    expect(resolveStageModel('build', baseConfig, override)).toBe('issue/default-model');
  });

  it('global stage model applies when no issue override', () => {
    expect(resolveStageModel('build', baseConfig)).toBe('global/build-model');
  });

  it('global default model applies when no issue override and no global stage model', () => {
    expect(resolveStageModel('check', baseConfig)).toBe('global/default-model');
  });

  it('undefined fallback when no overrides and no global config', () => {
    const emptyConfig: ConfigInfo = {};
    expect(resolveStageModel('build', emptyConfig)).toBeUndefined();
  });

  it('issue stage model overrides issue default model', () => {
    const override = {
      stageModels: { plan: 'issue/plan-model' },
      model: 'issue/default-model',
    };
    expect(resolveStageModel('plan', baseConfig, override)).toBe('issue/plan-model');
  });

  it('issue default overrides global stage model', () => {
    const override = { model: 'issue/default-model' };
    expect(resolveStageModel('build', baseConfig, override)).toBe('issue/default-model');
  });

  it('null issue model is treated as absent, falling back to global', () => {
    const override = { model: null };
    expect(resolveStageModel('build', baseConfig, override)).toBe('global/build-model');
  });

  it('null issue stageModels is treated as absent, falling back to global', () => {
    const override = { stageModels: null, model: 'issue/default-model' };
    expect(resolveStageModel('build', baseConfig, override)).toBe('issue/default-model');
  });

  it('issue stage model for integrate stage works', () => {
    const override = {
      stageModels: { integrate: 'issue/integrate-model' },
      model: 'issue/default-model',
    };
    expect(resolveStageModel('integrate', baseConfig, override)).toBe('issue/integrate-model');
  });

  it('preserves global-only behavior when issue override is undefined', () => {
    const config: ConfigInfo = {
      opencode: { model: 'global-model', stageModels: { explore: 'global/explore-model' } },
    };
    expect(resolveStageModel('explore', config, undefined)).toBe('global/explore-model');
    expect(resolveStageModel('build', config, undefined)).toBe('global-model');
  });

  it('EXECUTABLE_MODEL_STAGES includes backlog, plan, build, check, integrate, done', () => {
    expect(EXECUTABLE_MODEL_STAGES).toContain('backlog');
    expect(EXECUTABLE_MODEL_STAGES).toContain('plan');
    expect(EXECUTABLE_MODEL_STAGES).toContain('build');
    expect(EXECUTABLE_MODEL_STAGES).toContain('check');
    expect(EXECUTABLE_MODEL_STAGES).toContain('integrate');
    expect(EXECUTABLE_MODEL_STAGES).toContain('done');
    expect(EXECUTABLE_MODEL_STAGES).not.toContain('explore');
    expect(EXECUTABLE_MODEL_STAGES).not.toContain('draft');
  });
});

describe('model-override-regression: isValidModelId', () => {
  it('accepts valid provider/model strings', () => {
    expect(isValidModelId('anthropic/claude-opus-4-20250514')).toBe(true);
    expect(isValidModelId('openai/gpt-4o')).toBe(true);
    expect(isValidModelId('a/b')).toBe(true);
  });

  it('rejects strings without slash', () => {
    expect(isValidModelId('invalid-model')).toBe(false);
  });

  it('rejects slash at start (empty provider)', () => {
    expect(isValidModelId('/model')).toBe(false);
  });

  it('rejects slash at end (empty model)', () => {
    expect(isValidModelId('provider/')).toBe(false);
  });

  it('rejects empty string', () => {
    expect(isValidModelId('')).toBe(false);
  });
});

describe('model-override-regression: IssueRepo stageModels', () => {
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

  it('creates issue with stageModels and reads them back', () => {
    const issue = repo.create({
      number: 1,
      projectId,
      title: 'Test',
      stageModels: { build: 'anthropic/claude-opus-4-20250514' },
    });
    expect(issue.stageModels).toEqual({ build: 'anthropic/claude-opus-4-20250514' });

    const found = repo.findById(issue.id);
    expect(found?.stageModels).toEqual({ build: 'anthropic/claude-opus-4-20250514' });
  });

  it('sets stageModels via update', () => {
    const issue = repo.create({ number: 1, projectId, title: 'Test' });
    const updated = repo.update(issue.id, {
      stageModels: { plan: 'openai/gpt-4o', build: 'anthropic/claude-sonnet-4-20250514' },
    });
    expect(updated?.stageModels).toEqual({
      plan: 'openai/gpt-4o',
      build: 'anthropic/claude-sonnet-4-20250514',
    });
  });

  it('replaces stageModels via update', () => {
    const issue = repo.create({
      number: 1,
      projectId,
      title: 'Test',
      stageModels: { build: 'old/model' },
    });
    const updated = repo.update(issue.id, {
      stageModels: { check: 'new/model' },
    });
    expect(updated?.stageModels).toEqual({ check: 'new/model' });
    expect(updated?.stageModels?.build).toBeUndefined();
  });

  it('clears stageModels with null', () => {
    const issue = repo.create({
      number: 1,
      projectId,
      title: 'Test',
      stageModels: { build: 'model/a' },
    });
    const cleared = repo.update(issue.id, { stageModels: null });
    expect(cleared?.stageModels).toBeUndefined();
  });

  it('clears stageModels with empty object (normalized to null)', () => {
    const issue = repo.create({
      number: 1,
      projectId,
      title: 'Test',
      stageModels: { build: 'model/a' },
    });
    const cleared = repo.update(issue.id, { stageModels: {} });
    expect(cleared?.stageModels).toBeUndefined();
  });

  it('returns undefined stageModels when not set', () => {
    const issue = repo.create({ number: 1, projectId, title: 'Test' });
    expect(issue.stageModels).toBeUndefined();
    const found = repo.findById(issue.id);
    expect(found?.stageModels).toBeUndefined();
  });

  it('handles malformed stage_models JSON gracefully', () => {
    const issue = repo.create({ number: 1, projectId, title: 'Test' });
    db.run('UPDATE issues SET stage_models = ? WHERE id = ?', ['not-json', issue.id]);
    const found = repo.findById(issue.id);
    expect(found).not.toBeNull();
    expect(found?.stageModels).toBeUndefined();
  });

  it('handles non-object stage_models JSON gracefully', () => {
    const issue = repo.create({ number: 1, projectId, title: 'Test' });
    db.run('UPDATE issues SET stage_models = ? WHERE id = ?', ['["array"]', issue.id]);
    const found = repo.findById(issue.id);
    expect(found).not.toBeNull();
    expect(found?.stageModels).toBeUndefined();
  });

  it('persists both model and stageModels together', () => {
    const issue = repo.create({
      number: 1,
      projectId,
      title: 'Test',
      model: 'anthropic/claude-default',
      stageModels: { build: 'anthropic/claude-build' },
    });
    const found = repo.findById(issue.id);
    expect(found?.model).toBe('anthropic/claude-default');
    expect(found?.stageModels).toEqual({ build: 'anthropic/claude-build' });
  });

  it('findByNumber returns stageModels', () => {
    repo.create({
      number: 1,
      projectId,
      title: 'Test',
      stageModels: { build: 'test/model' },
    });
    const found = repo.findByNumber(projectId, 1);
    expect(found?.stageModels).toEqual({ build: 'test/model' });
  });

  it('findAll returns stageModels', () => {
    repo.create({
      number: 1,
      projectId,
      title: 'Test',
      stageModels: { plan: 'a/b' },
    });
    const all = repo.findAll({ projectId });
    expect(all).toHaveLength(1);
    expect(all[0].stageModels).toEqual({ plan: 'a/b' });
  });
});

describe('model-override-regression: API stageModels', () => {
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

  describe('POST /api/issues with stageModels', () => {
    it('creates issue with stageModels', async () => {
      const response = await request(server)
        .post('/api/issues')
        .send({
          title: 'Test',
          model: 'anthropic/claude-default',
          stageModels: { build: 'anthropic/claude-opus-4-20250514' },
        });
      expect(response.status).toBe(201);
      expect(response.body.data.model).toBe('anthropic/claude-default');
      expect(response.body.data.stageModels).toEqual({ build: 'anthropic/claude-opus-4-20250514' });
    });

    it('creates issue without stageModels when omitted', async () => {
      const response = await request(server)
        .post('/api/issues')
        .send({ title: 'Test' });
      expect(response.status).toBe(201);
      expect(response.body.data.stageModels).toBeUndefined();
    });
  });

  describe('PATCH /api/issues/:number stageModels', () => {
    it('sets stageModels', async () => {
      await issueService.create({ projectId, title: 'Test Issue' });
      const response = await request(server)
        .patch('/api/issues/1')
        .send({ stageModels: { plan: 'anthropic/claude-opus-4-20250514' } });
      expect(response.status).toBe(200);
      expect(response.body.data.stageModels).toEqual({ plan: 'anthropic/claude-opus-4-20250514' });
    });

    it('replaces stageModels', async () => {
      await issueService.create({ projectId, title: 'Test Issue' });
      await request(server)
        .patch('/api/issues/1')
        .send({ stageModels: { build: 'old/model' } });
      const response = await request(server)
        .patch('/api/issues/1')
        .send({ stageModels: { check: 'new/model' } });
      expect(response.status).toBe(200);
      expect(response.body.data.stageModels).toEqual({ check: 'new/model' });
    });

    it('clears stageModels with null', async () => {
      await issueService.create({ projectId, title: 'Test Issue' });
      await request(server)
        .patch('/api/issues/1')
        .send({ stageModels: { build: 'test/model' } });
      const response = await request(server)
        .patch('/api/issues/1')
        .send({ stageModels: null });
      expect(response.status).toBe(200);
      expect(response.body.data.stageModels).toBeUndefined();
    });
  });

  describe('GET /api/issues/:number includes stageModels', () => {
    it('returns stageModels when present', async () => {
      await issueService.create({
        projectId,
        title: 'Test',
        stageModels: { build: 'anthropic/claude-build' },
      });
      const response = await request(server).get('/api/issues/1');
      expect(response.status).toBe(200);
      expect(response.body.data.stageModels).toEqual({ build: 'anthropic/claude-build' });
    });
  });
});

describe('model-override-regression: API validation errors', () => {
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

  describe('POST /api/issues validation', () => {
    it('rejects invalid model format on create', async () => {
      const response = await request(server)
        .post('/api/issues')
        .send({ title: 'Test', model: 'no-slash' });
      expect(response.status).toBe(400);
      expect(response.body.error).toContain('Invalid model format');
    });

    it.each([123, {}, []])('rejects non-string model on create: %p', async (model) => {
      const response = await request(server)
        .post('/api/issues')
        .send({ title: 'Test', model });

      expect(response.status).toBe(400);
      expect(response.body.error).toContain('Invalid model format');
    });

    it('rejects invalid stageModels value on create', async () => {
      const response = await request(server)
        .post('/api/issues')
        .send({ title: 'Test', stageModels: { build: 'no-slash' } });
      expect(response.status).toBe(400);
      expect(response.body.error).toContain('Invalid model for stage');
    });

    it('rejects non-object stageModels on create', async () => {
      const response = await request(server)
        .post('/api/issues')
        .send({ title: 'Test', stageModels: 'invalid' });
      expect(response.status).toBe(400);
      expect(response.body.error).toContain('stageModels must be an object');
    });

    it('rejects array stageModels on create', async () => {
      const response = await request(server)
        .post('/api/issues')
        .send({ title: 'Test', stageModels: ['bad'] });
      expect(response.status).toBe(400);
      expect(response.body.error).toContain('stageModels must be an object');
    });

    it('rejects empty provider in stageModels value on create', async () => {
      const response = await request(server)
        .post('/api/issues')
        .send({ title: 'Test', stageModels: { build: '/model' } });
      expect(response.status).toBe(400);
    });

    it('rejects empty model id in stageModels value on create', async () => {
      const response = await request(server)
        .post('/api/issues')
        .send({ title: 'Test', stageModels: { build: 'provider/' } });
      expect(response.status).toBe(400);
    });

    it('does not persist issue after create validation error', async () => {
      const response = await request(server)
        .post('/api/issues')
        .send({ title: 'Test', model: 123 });

      expect(response.status).toBe(400);
      expect(stateManager.getIssueRepo().findAll({ projectId })).toHaveLength(0);
    });
  });

  describe('PATCH /api/issues/:number validation', () => {
    beforeEach(async () => {
      await issueService.create({ projectId, title: 'Test Issue' });
    });

    it('rejects invalid model format on update', async () => {
      const response = await request(server)
        .patch('/api/issues/1')
        .send({ model: 'no-slash' });
      expect(response.status).toBe(400);
      expect(response.body.error).toContain('Invalid model format');
    });

    it.each([123, {}, []])('rejects non-string model on update: %p', async (model) => {
      const response = await request(server)
        .patch('/api/issues/1')
        .send({ model });

      expect(response.status).toBe(400);
      expect(response.body.error).toContain('Invalid model format');
    });

    it('rejects invalid stageModels value on update', async () => {
      const response = await request(server)
        .patch('/api/issues/1')
        .send({ stageModels: { plan: 'bad-format' } });
      expect(response.status).toBe(400);
      expect(response.body.error).toContain('Invalid model for stage');
    });

    it('rejects non-object stageModels on update', async () => {
      const response = await request(server)
        .patch('/api/issues/1')
        .send({ stageModels: 'bad' });
      expect(response.status).toBe(400);
      expect(response.body.error).toContain('stageModels must be an object');
    });

    it('rejects array stageModels on update', async () => {
      const response = await request(server)
        .patch('/api/issues/1')
        .send({ stageModels: ['bad'] });
      expect(response.status).toBe(400);
      expect(response.body.error).toContain('stageModels must be an object');
    });

    it('does not persist data after validation error', async () => {
      await request(server)
        .patch('/api/issues/1')
        .send({ model: 'anthropic/valid-model' });

      await request(server)
        .patch('/api/issues/1')
        .send({ stageModels: { build: 'no-slash' } });

      const getResp = await request(server).get('/api/issues/1');
      expect(getResp.body.data.model).toBe('anthropic/valid-model');
      expect(getResp.body.data.stageModels).toBeUndefined();
    });
  });
});

describe('model-override-regression: discovery uses opencode models CLI', () => {
  const fileContent = fs.readFileSync(discoverySourcePath, 'utf-8');

  it('discovery source calls execFile with models arg, not acp', () => {
    expect(fileContent).toContain("'models'");
    expect(fileContent).not.toContain("'acp'");
  });

  it('discovery cache TTL is 30 minutes', () => {
    expect(fileContent).toContain('30 * 60 * 1000');
  });

  it('discovery does not reference ACP newSession or initialize', () => {
    expect(fileContent).not.toContain('newSession');
    expect(fileContent).not.toContain('initialize');
    expect(fileContent).not.toContain('acp');
  });

  it('discovery uses isValidModelId to filter output lines', () => {
    expect(fileContent).toContain('isValidModelId');
  });

  it('discovery uses execFile from child_process', () => {
    expect(fileContent).toContain('execFile');
    expect(fileContent).toContain('child_process');
  });
});
