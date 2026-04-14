import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import http from 'node:http';
import * as fs from 'node:fs';
import * as path from 'node:path';
import * as os from 'node:os';
import { Hono } from 'hono';
import request from 'supertest';
import { createProviderRoutes } from '../src/api/providers';
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

describe('Provider Routes', () => {
  let tmpDir: string;
  let configPath: string;
  let eventBus: EventBus;
  let rateLimiter: RateLimiter;
  let server: http.Server;
  let app: Hono;

  beforeEach(() => {
    tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-providers-test-'));
    configPath = path.join(tmpDir, 'config.jsonc');
    clearConfigCache();
    
    eventBus = new EventBus();
    rateLimiter = new RateLimiter(60 * 1000, 30);
    app = new Hono();
    app.route('/api/providers', createProviderRoutes(eventBus, rateLimiter));
    server = createTestServer(app);
  });

  afterEach(() => {
    server.close();
    fs.rmSync(tmpDir, { recursive: true, force: true });
    clearConfigCache();
  });

  describe('GET /api/providers', () => {
    it('should return provider list with correct structure', async () => {
      const response = await request(server).get('/api/providers');

      expect(response.status).toBe(200);
      expect(response.body.success).toBe(true);
      expect(Array.isArray(response.body.data)).toBe(true);

      const provider = response.body.data[0];
      expect(provider).toHaveProperty('id');
      expect(provider).toHaveProperty('name');
      expect(provider).toHaveProperty('baseURL');
      expect(provider).toHaveProperty('models');
      expect(provider).toHaveProperty('configured');
      expect(provider).toHaveProperty('source');
      expect(provider).toHaveProperty('isBuiltin');
      expect(provider).toHaveProperty('isDefault');
      expect(provider).toHaveProperty('apiKeyMasked');
    });

    it('should include builtin providers', async () => {
      const response = await request(server).get('/api/providers');

      expect(response.status).toBe(200);
      const ids = response.body.data.map((p: { id: string }) => p.id);
      expect(ids).toContain('anthropic');
      expect(ids).toContain('openai');
    });

    it('should mask apiKey values', async () => {
      const response = await request(server).get('/api/providers');

      expect(response.status).toBe(200);
      for (const provider of response.body.data) {
        if (provider.apiKeyMasked) {
          expect(provider.apiKeyMasked).not.toMatch(/^[a-zA-Z0-9_-]{20,}$/);
          expect(provider.apiKeyMasked).toContain('*');
        }
      }
    });

    it('should return models in fully-qualified ID format (provider/model-id)', async () => {
      const response = await request(server).get('/api/providers');

      expect(response.status).toBe(200);
      const anthropicProvider = response.body.data.find((p: { id: string }) => p.id === 'anthropic');
      expect(anthropicProvider).toBeDefined();
      expect(Array.isArray(anthropicProvider.models)).toBe(true);
      expect(anthropicProvider.models.length).toBeGreaterThan(0);
      const modelId = anthropicProvider.models[0];
      expect(typeof modelId).toBe('string');
      expect(modelId).toContain('/');
      expect(modelId.startsWith('anthropic/')).toBe(true);
    });
  });

  describe('POST /api/providers/:id - save config', () => {
    it('should save custom provider config', async () => {
      const response = await request(server)
        .post('/api/providers/my-custom-provider')
        .send({
          apiKey: 'sk-test-key-12345678',
          baseURL: 'https://api.example.com/v1',
          sdk: 'openai-compatible',
          models: ['gpt-4', 'gpt-3.5-turbo'],
          name: 'My Custom Provider',
        });

      expect(response.status).toBe(200);
      expect(response.body.success).toBe(true);
      expect(response.body.data.id).toBe('my-custom-provider');
      expect(response.body.data.configured).toBe(true);
      expect(response.body.data.version).toBeDefined();
    });

    it('should update existing provider config', async () => {
      await request(server)
        .post('/api/providers/my-custom-provider')
        .send({
          apiKey: 'sk-old-key',
          baseURL: 'https://api.example.com/v1',
          models: ['gpt-4'],
        });

      const response = await request(server)
        .post('/api/providers/my-custom-provider')
        .send({
          apiKey: 'sk-new-key',
          baseURL: 'https://api.example.com/v1',
          models: ['gpt-4', 'gpt-3.5-turbo'],
        });

      expect(response.status).toBe(200);
      expect(response.body.success).toBe(true);
    });

    it('should emit config:providers:changed event', async () => {
      const listener = vi.fn();
      eventBus.on('config:providers:changed', listener);

      await request(server)
        .post('/api/providers/my-custom-provider')
        .send({
          apiKey: 'sk-test-key',
          baseURL: 'https://api.example.com/v1',
          models: ['gpt-4'],
        });

      expect(listener).toHaveBeenCalledTimes(1);
      expect(listener).toHaveBeenCalledWith({
        providers: expect.arrayContaining([
          expect.objectContaining({ id: 'my-custom-provider' }),
        ]),
      });
    });
  });

  describe('POST /api/providers/:id - validation errors', () => {
    it('should return 400 when apiKey is missing', async () => {
      const response = await request(server)
        .post('/api/providers/my-custom-provider')
        .send({
          baseURL: 'https://api.example.com/v1',
          models: ['gpt-4'],
        });

      expect(response.status).toBe(400);
      expect(response.body.success).toBe(false);
      expect(response.body.error).toContain('apiKey');
    });

    it('should return 400 when apiKey is empty', async () => {
      const response = await request(server)
        .post('/api/providers/my-custom-provider')
        .send({
          apiKey: '   ',
          baseURL: 'https://api.example.com/v1',
          models: ['gpt-4'],
        });

      expect(response.status).toBe(400);
      expect(response.body.success).toBe(false);
    });

    it('should return 400 for invalid provider ID format', async () => {
      const response = await request(server)
        .post('/api/providers/INVALID_PROVIDER')
        .send({
          apiKey: 'sk-test-key',
          baseURL: 'https://api.example.com/v1',
          models: ['gpt-4'],
        });

      expect(response.status).toBe(400);
      expect(response.body.error).toContain('Invalid provider ID format');
    });

    it('should return 400 when baseURL is missing for custom provider', async () => {
      const response = await request(server)
        .post('/api/providers/my-custom-provider')
        .send({
          apiKey: 'sk-test-key',
          models: ['gpt-4'],
        });

      expect(response.status).toBe(400);
      expect(response.body.error).toContain('baseURL');
    });

    it('should return 400 when models is empty for custom provider', async () => {
      const response = await request(server)
        .post('/api/providers/my-custom-provider')
        .send({
          apiKey: 'sk-test-key',
          baseURL: 'https://api.example.com/v1',
          models: [],
        });

      expect(response.status).toBe(400);
      expect(response.body.error).toContain('models');
    });
  });

  describe('DELETE /api/providers/:id', () => {
    it('should delete provider config', async () => {
      await request(server)
        .post('/api/providers/my-custom-provider')
        .send({
          apiKey: 'sk-test-key',
          baseURL: 'https://api.example.com/v1',
          models: ['gpt-4'],
        });

      const response = await request(server).delete('/api/providers/my-custom-provider');

      expect(response.status).toBe(200);
      expect(response.body.success).toBe(true);
      expect(response.body.data.id).toBe('my-custom-provider');
    });

    it('should return 204 when provider does not exist', async () => {
      const response = await request(server).delete('/api/providers/non-existent-provider');

      expect(response.status).toBe(204);
    });

    it('should emit config:providers:changed event', async () => {
      await request(server)
        .post('/api/providers/my-custom-provider')
        .send({
          apiKey: 'sk-test-key',
          baseURL: 'https://api.example.com/v1',
          models: ['gpt-4'],
        });

      const listener = vi.fn();
      eventBus.on('config:providers:changed', listener);

      await request(server).delete('/api/providers/my-custom-provider');

      expect(listener).toHaveBeenCalledTimes(1);
      expect(listener).toHaveBeenCalledWith({
        providers: expect.arrayContaining([
          expect.objectContaining({ id: 'my-custom-provider' }),
        ]),
      });
    });
  });

  describe('POST /api/providers/test - connection test', () => {
    it('should return 400 when apiKey is missing', async () => {
      const response = await request(server)
        .post('/api/providers/test')
        .send({
          baseURL: 'https://api.example.com/v1',
          sdk: 'openai-compatible',
        });

      expect(response.status).toBe(400);
      expect(response.body.success).toBe(false);
    });

    it('should return 400 when baseURL is missing', async () => {
      const response = await request(server)
        .post('/api/providers/test')
        .send({
          apiKey: 'sk-test-key',
          sdk: 'openai-compatible',
        });

      expect(response.status).toBe(400);
      expect(response.body.success).toBe(false);
    });

    it('should return 400 when sdk is missing', async () => {
      const response = await request(server)
        .post('/api/providers/test')
        .send({
          apiKey: 'sk-test-key',
          baseURL: 'https://api.example.com/v1',
        });

      expect(response.status).toBe(400);
      expect(response.body.success).toBe(false);
    });


  });
});