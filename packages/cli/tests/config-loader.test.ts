import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import * as fs from 'node:fs';
import * as path from 'node:path';
import * as os from 'node:os';
import { load, getProviderConfig, writeConfig } from '../src/config/config-loader';
import type { ConfigInfo } from '../src/config/config-schema';

describe('ConfigLoader', () => {
  let tmpDir: string;
  let configPath: string;
  const savedEnv: Record<string, string | undefined> = {};

  beforeEach(() => {
    tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-test-'));
    configPath = path.join(tmpDir, 'config.jsonc');
  });

  afterEach(() => {
    fs.rmSync(tmpDir, { recursive: true, force: true });
    for (const [key, val] of Object.entries(savedEnv)) {
      if (val === undefined) {
        delete process.env[key];
      } else {
        process.env[key] = val;
      }
    }
  });

  function setEnv(key: string, value: string) {
    savedEnv[key] = process.env[key];
    process.env[key] = value;
  }

  describe('load', () => {
    it('should return empty object when file does not exist', () => {
      const config = load(path.join(tmpDir, 'nonexistent.jsonc'));
      expect(config).toEqual({});
    });

    it('should parse JSONC line comments', () => {
      fs.writeFileSync(configPath, `{
  // This is a line comment
  "model": "anthropic/claude-sonnet-4-20250514"
}`);
      const config = load(configPath);
      expect(config.model).toBe('anthropic/claude-sonnet-4-20250514');
    });

    it('should parse JSONC block comments', () => {
      fs.writeFileSync(configPath, `{
  /* Block
     comment */
  "model": "openai/gpt-4"
}`);
      const config = load(configPath);
      expect(config.model).toBe('openai/gpt-4');
    });

    it('should throw on invalid JSON syntax', () => {
      fs.writeFileSync(configPath, `{ invalid json }`);
      expect(() => load(configPath)).toThrow(configPath);
    });

    it('should throw on Zod validation error with field path', () => {
      fs.writeFileSync(configPath, `{ "model": 123 }`);
      expect(() => load(configPath)).toThrow('model');
    });

    it('should parse empty JSON object', () => {
      fs.writeFileSync(configPath, `{}`);
      const config = load(configPath);
      expect(config._version).toBeDefined();
      expect(config._version).toBeTypeOf('number');
      const { _version, ...rest } = config;
      expect(rest).toEqual({});
    });

    it('should parse full config with all fields', () => {
      fs.writeFileSync(configPath, `{
  "$schema": "https://mohist.dev/schema",
  "model": "glm/glm-4-flash",
  "provider": {
    "glm": {
      "apiKey": "sk-test-key",
      "baseURL": "https://custom.example.com/v1"
    }
  },
  "server": { "port": 4000 },
  "agent": { "timeout": 600000, "maxConcurrent": 4 }
}`);
      const config = load(configPath);
      expect(config.model).toBe('glm/glm-4-flash');
      expect(config.provider?.glm?.apiKey).toBe('sk-test-key');
      expect(config.server?.port).toBe(4000);
      expect(config.agent?.timeout).toBe(600000);
    });

    it('should ignore unknown fields', () => {
      fs.writeFileSync(configPath, `{
  "model": "anthropic/test",
  "unknownField": "should be stripped"
}`);
      const config = load(configPath);
      expect(config.model).toBe('anthropic/test');
      expect((config as Record<string, unknown>).unknownField).toBeUndefined();
    });
  });

  describe('getProviderConfig', () => {
    it('should return builtin provider info for glm', () => {
      const resolved = getProviderConfig({}, 'glm');
      expect(resolved.sdk).toBe('openai-compatible');
      expect(resolved.name).toBe('智谱 GLM');
      expect(resolved.baseURL).toBe('https://open.bigmodel.cn/api/paas/v4');
      expect(resolved.envVars).toEqual(['GLM_API_KEY']);
    });

    it('should return undefined for unknown provider', () => {
      const resolved = getProviderConfig({}, 'unknown-provider');
      expect(resolved.sdk).toBe('openai-compatible');
      expect(resolved.name).toBe('unknown-provider');
    });

    it('should prefer config apiKey over env var', () => {
      setEnv('OPENAI_API_KEY', 'sk-env-key');
      const config: ConfigInfo = {
        provider: { openai: { apiKey: 'sk-file-key' } },
      };
      const resolved = getProviderConfig(config, 'openai');
      expect(resolved.apiKey).toBe('sk-file-key');
      expect(resolved.source).toBe('config');
    });

    it('should use env var when config has no apiKey', () => {
      setEnv('ANTHROPIC_API_KEY', 'sk-env-key');
      const resolved = getProviderConfig({}, 'anthropic');
      expect(resolved.apiKey).toBe('sk-env-key');
      expect(resolved.source).toBe('env');
    });

    it('should return null apiKey when neither config nor env var set', () => {
      delete process.env['ANTHROPIC_API_KEY'];
      const resolved = getProviderConfig({}, 'anthropic');
      expect(resolved.apiKey).toBeNull();
      expect(resolved.source).toBe('none');
    });

    it('should prefer config baseURL over builtin', () => {
      const config: ConfigInfo = {
        provider: { openai: { apiKey: 'sk-test', baseURL: 'https://proxy.example.com/v1' } },
      };
      const resolved = getProviderConfig(config, 'openai');
      expect(resolved.baseURL).toBe('https://proxy.example.com/v1');
    });

    it('should use builtin baseURL when config has no baseURL', () => {
      const config: ConfigInfo = {
        provider: { glm: { apiKey: 'sk-test' } },
      };
      const resolved = getProviderConfig(config, 'glm');
      expect(resolved.baseURL).toBe('https://open.bigmodel.cn/api/paas/v4');
    });

    it('should use openai-compatible for unregistered provider in config', () => {
      const config: ConfigInfo = {
        provider: { 'my-custom': { apiKey: 'sk-test', baseURL: 'https://my-api.com/v1' } },
      };
      const resolved = getProviderConfig(config, 'my-custom');
      expect(resolved.sdk).toBe('openai-compatible');
      expect(resolved.baseURL).toBe('https://my-api.com/v1');
    });

    it('should check multiple env vars for a provider', () => {
      delete process.env['MOONSHOT_API_KEY'];
      setEnv('KIMI_API_KEY', 'sk-kimi-key');
      const resolved = getProviderConfig({}, 'kimi');
      expect(resolved.apiKey).toBe('sk-kimi-key');
      expect(resolved.source).toBe('env');
    });

    it('should return zhipuai-coding-plan with openai-compatible SDK and correct baseURL', () => {
      const resolved = getProviderConfig({}, 'zhipuai-coding-plan');
      expect(resolved.sdk).toBe('openai-compatible');
      expect(resolved.name).toBe('智谱 Coding Plan');
      expect(resolved.baseURL).toBe('https://open.bigmodel.cn/api/coding/paas/v4');
      expect(resolved.envVars).toEqual(['ZHIPU_API_KEY']);
    });

    it('should return kimi-for-coding with anthropic SDK and correct baseURL', () => {
      const resolved = getProviderConfig({}, 'kimi-for-coding');
      expect(resolved.sdk).toBe('anthropic');
      expect(resolved.name).toBe('Kimi For Coding');
      expect(resolved.baseURL).toBe('https://api.kimi.com/coding/v1');
      expect(resolved.envVars).toEqual(['KIMI_API_KEY', 'MOONSHOT_API_KEY']);
    });

    it('should return minimax-for-coding with anthropic SDK and correct baseURL', () => {
      const resolved = getProviderConfig({}, 'minimax-for-coding');
      expect(resolved.sdk).toBe('anthropic');
      expect(resolved.name).toBe('MiniMax Coding');
      expect(resolved.baseURL).toBe('https://api.minimax.io/anthropic/v1');
      expect(resolved.envVars).toEqual(['MINIMAX_API_KEY']);
    });
  });

  describe('writeConfig', () => {
    it('should write valid JSON to file', () => {
      const config: ConfigInfo = {
        model: 'anthropic/claude-sonnet-4-20250514',
        provider: { anthropic: { apiKey: 'sk-test' } },
      };
      writeConfig(config, configPath);

      const content = fs.readFileSync(configPath, 'utf-8');
      const parsed = JSON.parse(content);
      expect(parsed.model).toBe('anthropic/claude-sonnet-4-20250514');
      expect(parsed.provider.anthropic.apiKey).toBe('sk-test');
    });

    it('should write atomically (file exists with correct content)', () => {
      writeConfig({ model: 'test/model' }, configPath);
      expect(fs.existsSync(configPath)).toBe(true);
      const parsed = JSON.parse(fs.readFileSync(configPath, 'utf-8'));
      expect(parsed.model).toBe('test/model');
    });

    it('should create parent directory if missing', () => {
      const nestedDir = path.join(tmpDir, '.mohist');
      const nestedPath = path.join(nestedDir, 'config.jsonc');
      fs.mkdirSync(nestedDir, { recursive: true });
      writeConfig({ model: 'test/model' }, nestedPath);
      expect(fs.existsSync(nestedPath)).toBe(true);
    });

    it('should overwrite existing config', () => {
      writeConfig({ model: 'old/model' }, configPath);
      writeConfig({ model: 'new/model' }, configPath);
      const parsed = JSON.parse(fs.readFileSync(configPath, 'utf-8'));
      expect(parsed.model).toBe('new/model');
    });

    it('should not leave tmp files on successful write', () => {
      writeConfig({ model: 'test/model' }, configPath);
      const files = fs.readdirSync(tmpDir);
      const tmpFiles = files.filter((f) => f.includes('.tmp.'));
      expect(tmpFiles).toHaveLength(0);
    });

    it('should write with expectedVersion successfully when versions match', () => {
      writeConfig({ model: 'old/model' }, configPath);
      const firstLoad = load(configPath);
      const version = firstLoad._version;
      expect(version).toBeDefined();

      writeConfig({ model: 'new/model' }, configPath, { expectedVersion: version });
      const secondLoad = load(configPath);
      expect(secondLoad.model).toBe('new/model');
      expect(secondLoad._version).toBeGreaterThan(version!);
    });

    it('should throw ConfigConflictError when expectedVersion does not match', () => {
      writeConfig({ model: 'old/model' }, configPath);
      const firstLoad = load(configPath);
      const version = firstLoad._version;
      expect(version).toBeDefined();

      // Simulate an external update that advances the version
      writeConfig({ model: 'intermediate/model' }, configPath);

      expect(() => {
        writeConfig({ model: 'stale/model' }, configPath, { expectedVersion: version });
      }).toThrow(/Config version conflict/);
    });

    it('should write without expectedVersion when option is omitted', () => {
      writeConfig({ model: 'first/model' }, configPath);
      const firstLoad = load(configPath);
      const version = firstLoad._version;

      writeConfig({ model: 'second/model' }, configPath);
      const secondLoad = load(configPath);
      expect(secondLoad.model).toBe('second/model');
      expect(secondLoad._version).toBeGreaterThan(version!);
    });
  });
});
