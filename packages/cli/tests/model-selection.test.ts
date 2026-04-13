import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import http from 'node:http';
import * as fs from 'node:fs';
import * as path from 'node:path';
import * as os from 'node:os';
import { Hono } from 'hono';
import request from 'supertest';
import { DatabaseManager } from '../src/db/database';
import { StateManager } from '../src/server/state-manager';
import { ProjectService } from '../src/services/project-service';
import { ExploreService } from '../src/services/explore-service';
import { EventBus } from '../src/services/event-bus';
import { clearConfigCache } from '../src/config/config-loader';
import { createExploreRoutes } from '../src/api/explore';
import { createProviderRoutes } from '../src/api/providers';

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

describe('Model Selection API', () => {
  let tmpDir: string;
  let configPath: string;
  let db: DatabaseManager;
  let stateManager: StateManager;
  let projectService: ProjectService;
  let exploreService: ExploreService;
  let eventBus: EventBus;
  let server: http.Server;
  let app: Hono;

  beforeEach(() => {
    tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-model-test-'));
    configPath = path.join(tmpDir, 'config.jsonc');
    fs.writeFileSync(configPath, JSON.stringify({}));
    clearConfigCache();

    process.env.ANTHROPIC_API_KEY = 'test-anthropic-api-key';
    process.env.OPENAI_API_KEY = 'test-openai-api-key';

    db = new DatabaseManager({ inMemory: true });
    stateManager = new StateManager(db);
    eventBus = new EventBus();
    
    const projectRepo = stateManager.getProjectRepo();
    const configRepo = stateManager.getConfigRepo();
    const issueRepo = stateManager.getIssueRepo();
    const labelRepo = stateManager.getLabelRepo();
    projectService = new ProjectService(projectRepo, configRepo, issueRepo, labelRepo);
    exploreService = new ExploreService(
      stateManager.getExploreSessionRepo(),
      stateManager.getExploreMessageRepo()
    );

    app = new Hono();
    app.route('/api/explore', createExploreRoutes(
      exploreService,
      issueRepo,
      projectService,
      stateManager.getExploreSessionRepo(),
      eventBus
    ));
    app.route('/api/providers', createProviderRoutes(eventBus));
    server = createTestServer(app);
  });

  afterEach(() => {
    server.close();
    db.close();
    fs.rmSync(tmpDir, { recursive: true, force: true });
    clearConfigCache();
    delete process.env.ANTHROPIC_API_KEY;
    delete process.env.OPENAI_API_KEY;
  });

  afterEach(() => {
    server.close();
    db.close();
    fs.rmSync(tmpDir, { recursive: true, force: true });
    clearConfigCache();
  });

  describe('GET /api/providers/models', () => {
    it('should return models grouped by provider', async () => {
      const response = await request(server).get('/api/providers/models');

      expect(response.status).toBe(200);
      expect(response.body.success).toBe(true);
      expect(Array.isArray(response.body.data)).toBe(true);

      const providerGroups = response.body.data;
      expect(providerGroups.length).toBeGreaterThan(0);

      const anthropicProvider = providerGroups.find((p: { id: string }) => p.id === 'anthropic');
      expect(anthropicProvider).toBeDefined();
      expect(anthropicProvider.configured).toBe(true);
      expect(Array.isArray(anthropicProvider.models)).toBe(true);
      expect(anthropicProvider.models.length).toBeGreaterThan(0);

      const model = anthropicProvider.models[0];
      expect(model).toHaveProperty('id');
      expect(model).toHaveProperty('name');
      expect(model).toHaveProperty('badges');
      expect(model).toHaveProperty('contextWindow');
    });

    it('should include model metadata', async () => {
      const response = await request(server).get('/api/providers/models');

      expect(response.status).toBe(200);
      const anthropicProvider = response.body.data.find((p: { id: string }) => p.id === 'anthropic');
      const claudeModel = anthropicProvider.models.find((m: { id: string }) => m.id === 'claude-opus-4-20250514');

      expect(claudeModel).toBeDefined();
      expect(claudeModel.name).toBe('Claude Opus 4');
      expect(claudeModel.badges).toContain('latest');
      expect(claudeModel.contextWindow).toBe(200000);
    });

    it('should include openai provider with correct models', async () => {
      const response = await request(server).get('/api/providers/models');

      expect(response.status).toBe(200);
      const openaiProvider = response.body.data.find((p: { id: string }) => p.id === 'openai');

      expect(openaiProvider).toBeDefined();
      const modelIds = openaiProvider.models.map((m: { id: string }) => m.id);
      expect(modelIds).toContain('gpt-4o');
      expect(modelIds).toContain('gpt-4o-mini');
    });
  });

  describe('POST /api/explore/:id/model', () => {
    let projectId: string;
    let sessionId: string;

    beforeEach(async () => {
      const project = await projectService.create({ name: 'Test Project', path: '/test/path' });
      projectId = project.id;
      projectService.setCurrent(project);
      const session = exploreService.createSession({ projectId, title: 'Test Session' });
      sessionId = session.id;
    });

    it('should update session model', async () => {
      const response = await request(server)
        .post(`/api/explore/${sessionId}/model`)
        .send({ model: 'claude-sonnet-4-20250514', variant: 'latest' });

      expect(response.status).toBe(200);
      expect(response.body.success).toBe(true);
      expect(response.body.data.model).toBe('claude-sonnet-4-20250514');
      expect(response.body.data.variant).toBe('latest');
    });

    it('should update session model without variant', async () => {
      const response = await request(server)
        .post(`/api/explore/${sessionId}/model`)
        .send({ model: 'gpt-4o' });

      expect(response.status).toBe(200);
      expect(response.body.success).toBe(true);
      expect(response.body.data.model).toBe('gpt-4o');
      expect(response.body.data.variant).toBeUndefined();
    });

    it('should return 400 for invalid model', async () => {
      const response = await request(server)
        .post(`/api/explore/${sessionId}/model`)
        .send({ model: 'invalid-model-xyz' });

      expect(response.status).toBe(400);
      expect(response.body.success).toBe(false);
      expect(response.body.error).toContain('Invalid model');
    });

    it('should return 400 when model is missing', async () => {
      const response = await request(server)
        .post(`/api/explore/${sessionId}/model`)
        .send({});

      expect(response.status).toBe(400);
      expect(response.body.success).toBe(false);
      expect(response.body.error).toContain('model is required');
    });

    it('should return 400 when model is not a string', async () => {
      const response = await request(server)
        .post(`/api/explore/${sessionId}/model`)
        .send({ model: 123 });

      expect(response.status).toBe(400);
      expect(response.body.success).toBe(false);
    });

    it('should return 404 for non-existent session', async () => {
      const response = await request(server)
        .post('/api/explore/non-existent-session-id/model')
        .send({ model: 'claude-sonnet-4-20250514' });

      expect(response.status).toBe(404);
      expect(response.body.success).toBe(false);
      expect(response.body.error).toContain('Session not found');
    });

    it('should accept all valid builtin models', async () => {
      const validModels = [
        'claude-opus-4-20250514',
        'claude-sonnet-4-20250514',
        'claude-haiku-4-20250514',
        'gpt-4o',
        'gpt-4o-mini',
        'glm-4-flash',
        'glm-4-plus',
        'deepseek-chat',
        'qwen-max',
      ];

      for (const model of validModels) {
        const response = await request(server)
          .post(`/api/explore/${sessionId}/model`)
          .send({ model });

        expect(response.status).toBe(200);
        expect(response.body.data.model).toBe(model);
      }
    });

    it('should persist model after update', async () => {
      await request(server)
        .post(`/api/explore/${sessionId}/model`)
        .send({ model: 'claude-sonnet-4-20250514' });

      const getResponse = await request(server).get(`/api/explore/${sessionId}`);

      expect(getResponse.status).toBe(200);
      expect(getResponse.body.data.session.model).toBe('claude-sonnet-4-20250514');
    });
  });
});