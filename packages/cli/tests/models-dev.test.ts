import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import * as fs from 'node:fs';
import * as path from 'node:path';
import * as os from 'node:os';

describe('ModelsDev', () => {
  const originalFetch = global.fetch;
  let tmpDir: string;

  beforeEach(() => {
    tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-models-dev-'));
  });

  afterEach(() => {
    vi.restoreAllMocks();
    global.fetch = originalFetch;
    if (fs.existsSync(tmpDir)) {
      fs.rmSync(tmpDir, { recursive: true, force: true });
    }
  });

  describe('snapshot loading', () => {
    it('should load models from snapshot via ModelsDev.get()', async () => {
      const { ModelsDev } = await import('../src/config/models-dev');
      const result = await ModelsDev.get();
      expect(result).toBeDefined();
      expect(typeof result).toBe('object');
      expect(result['anthropic']).toBeDefined();
      expect(result['openai']).toBeDefined();
    });
  });

  describe('refresh behavior', () => {
    it('should not throw when network fetch fails', async () => {
      global.fetch = vi.fn().mockResolvedValue({ ok: false, text: async () => '' });
      const { ModelsDev } = await import('../src/config/models-dev');
      await expect(ModelsDev.refresh()).resolves.not.toThrow();
    });

    it('should skip refresh when cache is fresh', async () => {
      const cacheDir = path.join(os.homedir(), '.mohist', 'cache');
      fs.mkdirSync(cacheDir, { recursive: true, mode: 0o700 });
      const cacheFile = path.join(cacheDir, 'models.json');
      fs.writeFileSync(cacheFile, JSON.stringify({ anthropic: { id: 'anthropic', name: 'Anthropic', env: [], models: {} } }), { mode: 0o600 });
      const mtime = Date.now() - 60000;
      fs.utimesSync(cacheFile, mtime / 1000, mtime / 1000);
      global.fetch = vi.fn().mockResolvedValue({ ok: true, text: async () => '{}' });
      const { ModelsDev } = await import('../src/config/models-dev');
      await ModelsDev.refresh();
      expect(global.fetch).not.toHaveBeenCalled();
      fs.rmSync(cacheFile, { force: true });
    });
  });

  describe('fully-qualified model IDs', () => {
    it('should return provider ID as part of model ID in getModelsByProvider', async () => {
      const { getModelsByProvider } = await import('../src/config/builtin-models');
      const models = await getModelsByProvider('anthropic');
      expect(models.length).toBeGreaterThan(0);
      const model = models.find(m => m.id.includes('/'));
      expect(model).toBeDefined();
      expect(model!.id.startsWith('anthropic/')).toBe(true);
    });

    it('should return fully-qualified model ID in getModelById', async () => {
      const { getModelById } = await import('../src/config/builtin-models');
      const model = await getModelById('anthropic/claude-sonnet-4-6');
      expect(model).toBeDefined();
      expect(model!.id).toBe('anthropic/claude-sonnet-4-6');
      expect(model!.provider).toBe('anthropic');
    });

    it('should return undefined for bare model ID without provider', async () => {
      const { getModelById } = await import('../src/config/builtin-models');
      const model = await getModelById('claude-sonnet-4-6');
      expect(model).toBeUndefined();
    });
  });
});