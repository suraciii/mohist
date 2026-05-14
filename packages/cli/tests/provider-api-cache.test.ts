import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import http from 'node:http';
import * as fs from 'node:fs';
import * as path from 'node:path';
import * as os from 'node:os';
import { Hono } from 'hono';
import request from 'supertest';
import { createProviderRoutes } from '../src/api/providers';
import { ProviderStateService } from '../src/services/provider-state-service';
import { EventBus } from '../src/services/event-bus';
import { RateLimiter } from '../src/utils/rate-limiter';
import { clearConfigCache } from '../src/config/config-loader';

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

describe('Provider API Cache Regression', () => {
  let tmpDir: string;
  let configPath: string;
  let originalHome: string | undefined;
  let eventBus: EventBus;
  let rateLimiter: RateLimiter;
  let providerState: ProviderStateService;
  let server: http.Server;
  let app: Hono;

  beforeEach(async () => {
    tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-provider-cache-test-'));
    configPath = path.join(tmpDir, '.mohist', 'config.jsonc');
    originalHome = process.env.HOME;
    process.env.HOME = tmpDir;
    clearConfigCache();
    fs.mkdirSync(path.dirname(configPath), { recursive: true });
    fs.writeFileSync(configPath, JSON.stringify({
      provider: {
        'seed-provider': {
          apiKey: 'sk-seed-provider-key',
          baseURL: 'https://api.example.com/v1',
          sdk: 'openai-compatible',
          models: ['seed-model'],
          name: 'Seed Provider',
        },
      },
    }, null, 2));

    eventBus = new EventBus();
    rateLimiter = new RateLimiter(60 * 1000, 30);
    providerState = new ProviderStateService();
    await providerState.warm();
    app = new Hono();
    app.route('/api/providers', createProviderRoutes(eventBus, rateLimiter, providerState));
    server = createTestServer(app);
  });

  afterEach(() => {
    server.close();
    if (originalHome === undefined) delete process.env.HOME;
    else process.env.HOME = originalHome;
    fs.rmSync(tmpDir, { recursive: true, force: true });
    clearConfigCache();
  });

  describe('GET /api/providers — lightweight response', () => {
    it('provider items do NOT include models field', async () => {
      const response = await request(server).get('/api/providers');

      expect(response.status).toBe(200);
      expect(response.body.success).toBe(true);
      expect(Array.isArray(response.body.data)).toBe(true);
      expect(response.body.data.length).toBeGreaterThan(0);

      for (const provider of response.body.data) {
        expect(provider).not.toHaveProperty('models');
      }
    });

    it('provider items include required metadata fields', async () => {
      const response = await request(server).get('/api/providers');

      expect(response.status).toBe(200);
      const provider = response.body.data[0];
      expect(provider).toHaveProperty('id');
      expect(provider).toHaveProperty('name');
      expect(provider).toHaveProperty('baseURL');
      expect(provider).toHaveProperty('configured');
      expect(provider).toHaveProperty('source');
      expect(provider).toHaveProperty('isBuiltin');
      expect(provider).toHaveProperty('isDefault');
      expect(provider).toHaveProperty('apiKeyMasked');
    });

    it('builtin providers are present', async () => {
      const response = await request(server).get('/api/providers');

      expect(response.status).toBe(200);
      const ids = response.body.data.map((p: { id: string }) => p.id);
      expect(ids).toContain('anthropic');
      expect(ids).toContain('openai');
    });
  });

  describe('GET /api/providers/models — model groups from cache', () => {
    it('returns model groups with expected shape', async () => {
      const response = await request(server).get('/api/providers/models');

      expect(response.status).toBe(200);
      expect(response.body.success).toBe(true);
      expect(Array.isArray(response.body.data)).toBe(true);

      const group = response.body.data[0];
      expect(group).toHaveProperty('id');
      expect(group).toHaveProperty('name');
      expect(group).toHaveProperty('configured');
      expect(group).toHaveProperty('models');
      expect(Array.isArray(group.models)).toBe(true);
    });

    it('each model item has id, name, badges, and contextWindow', async () => {
      await request(server)
        .post('/api/providers/test-shape-provider')
        .send({
          apiKey: 'sk-test-shape-key',
          baseURL: 'https://api.example.com/v1',
          sdk: 'openai-compatible',
          models: ['model-a', 'model-b'],
          name: 'Test Shape Provider',
        });

      const response = await request(server).get('/api/providers/models');
      expect(response.status).toBe(200);

      const group = response.body.data.find((g: any) => g.id === 'test-shape-provider');
      expect(group).toBeDefined();
      expect(group.models.length).toBeGreaterThan(0);

      const model = group.models[0];
      expect(model).toHaveProperty('id');
      expect(model).toHaveProperty('name');
      expect(model).toHaveProperty('badges');
      expect(model).toHaveProperty('contextWindow');
    });

    it('custom provider model IDs are in provider/model-id format', async () => {
      await request(server)
        .post('/api/providers/test-format-provider')
        .send({
          apiKey: 'sk-test-format-key',
          baseURL: 'https://api.example.com/v1',
          sdk: 'openai-compatible',
          models: ['custom-model-1'],
          name: 'Test Format Provider',
        });

      const response = await request(server).get('/api/providers/models');
      expect(response.status).toBe(200);

      const group = response.body.data.find((g: any) => g.id === 'test-format-provider');
      expect(group).toBeDefined();
      expect(group.models.length).toBeGreaterThan(0);

      const modelId = group.models[0].id;
      expect(typeof modelId).toBe('string');
      expect(modelId).toContain('/');
      expect(modelId.startsWith('test-format-provider/')).toBe(true);
    });

    it('returns model groups for at least one provider', async () => {
      const response = await request(server).get('/api/providers/models');

      expect(response.status).toBe(200);
      expect(response.body.data.length).toBeGreaterThan(0);
    });
  });

  describe('cache refresh after config mutations', () => {
    it('POST /api/providers/:id refreshes provider state', async () => {
      const createResponse = await request(server)
        .post('/api/providers/my-cache-test-provider')
        .send({
          apiKey: 'sk-test-key-for-cache-refresh',
          baseURL: 'https://api.example.com/v1',
          sdk: 'openai-compatible',
          models: ['gpt-4', 'gpt-3.5-turbo'],
          name: 'Cache Test Provider',
        });

      expect(createResponse.status).toBe(200);
      expect(createResponse.body.success).toBe(true);

      const providersResponse = await request(server).get('/api/providers');
      expect(providersResponse.status).toBe(200);
      const ids = providersResponse.body.data.map((p: { id: string }) => p.id);
      expect(ids).toContain('my-cache-test-provider');
    });

    it('POST /api/providers/:id refreshes model groups', async () => {
      await request(server)
        .post('/api/providers/my-cache-models-provider')
        .send({
          apiKey: 'sk-test-key-models-refresh',
          baseURL: 'https://api.example.com/v1',
          sdk: 'openai-compatible',
          models: ['gpt-4o', 'gpt-4o-mini'],
          name: 'Models Refresh Provider',
        });

      const modelsResponse = await request(server).get('/api/providers/models');
      expect(modelsResponse.status).toBe(200);
      const group = modelsResponse.body.data.find((g: any) => g.id === 'my-cache-models-provider');
      expect(group).toBeDefined();
      expect(group.models.length).toBe(2);
      expect(group.models.map((m: any) => m.name)).toEqual(['gpt-4o', 'gpt-4o-mini']);
    });

    it('DELETE /api/providers/:id refreshes provider state', async () => {
      await request(server)
        .post('/api/providers/my-delete-test-provider')
        .send({
          apiKey: 'sk-delete-test-key',
          baseURL: 'https://api.example.com/v1',
          sdk: 'openai-compatible',
          models: ['gpt-4'],
          name: 'Delete Test Provider',
        });

      const preDeleteResponse = await request(server).get('/api/providers');
      const preIds = preDeleteResponse.body.data.map((p: { id: string }) => p.id);
      expect(preIds).toContain('my-delete-test-provider');

      const deleteResponse = await request(server).delete('/api/providers/my-delete-test-provider');
      expect(deleteResponse.status).toBe(200);
      expect(deleteResponse.body.success).toBe(true);

      const postDeleteResponse = await request(server).get('/api/providers');
      const postIds = postDeleteResponse.body.data.map((p: { id: string }) => p.id);
      expect(postIds).not.toContain('my-delete-test-provider');
    });

    it('DELETE /api/providers/:id refreshes model groups', async () => {
      await request(server)
        .post('/api/providers/my-delete-models-provider')
        .send({
          apiKey: 'sk-delete-models-key',
          baseURL: 'https://api.example.com/v1',
          sdk: 'openai-compatible',
          models: ['gpt-4'],
          name: 'Delete Models Provider',
        });

      const preDeleteModels = await request(server).get('/api/providers/models');
      const preGroup = preDeleteModels.body.data.find((g: any) => g.id === 'my-delete-models-provider');
      expect(preGroup).toBeDefined();

      await request(server).delete('/api/providers/my-delete-models-provider');

      const postDeleteModels = await request(server).get('/api/providers/models');
      const postGroup = postDeleteModels.body.data.find((g: any) => g.id === 'my-delete-models-provider');
      expect(postGroup).toBeUndefined();
    });
  });
});
