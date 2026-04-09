import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { resolveModel, LlmError } from '../src/agent-runtime/llm';
import type { ConfigInfo } from '../src/config/config-schema';

describe('resolveModel', () => {
  const savedEnv: Record<string, string | undefined> = {};

  beforeEach(() => {
    savedEnv['ANTHROPIC_API_KEY'] = process.env['ANTHROPIC_API_KEY'];
    savedEnv['OPENAI_API_KEY'] = process.env['OPENAI_API_KEY'];
    savedEnv['GLM_API_KEY'] = process.env['GLM_API_KEY'];
    savedEnv['DEEPSEEK_API_KEY'] = process.env['DEEPSEEK_API_KEY'];
  });

  afterEach(() => {
    for (const [key, val] of Object.entries(savedEnv)) {
      if (val === undefined) {
        delete process.env[key];
      } else {
        process.env[key] = val;
      }
    }
  });

  it('should create anthropic model via createAnthropic', () => {
    const config: ConfigInfo = {
      provider: { anthropic: { apiKey: 'sk-ant-test' } },
      model: 'anthropic/claude-sonnet-4-20250514',
    };
    const model = resolveModel(config);
    expect(model).toBeDefined();
    expect(model.modelId).toBe('claude-sonnet-4-20250514');
  });

  it('should create openai model via createOpenAI', () => {
    const config: ConfigInfo = {
      provider: { openai: { apiKey: 'sk-openai-test' } },
      model: 'openai/gpt-4',
    };
    const model = resolveModel(config);
    expect(model).toBeDefined();
    expect(model.modelId).toBe('gpt-4');
  });

  it('should create openai-compatible model with baseURL', () => {
    const config: ConfigInfo = {
      provider: { glm: { apiKey: 'sk-glm-test' } },
      model: 'glm/glm-4-flash',
    };
    const model = resolveModel(config);
    expect(model).toBeDefined();
    expect(model.modelId).toBe('glm-4-flash');
  });

  it('should treat unregistered provider as openai-compatible', () => {
    const config: ConfigInfo = {
      provider: {
        'my-custom': { apiKey: 'sk-custom', baseURL: 'https://api.custom.com/v1' },
      },
      model: 'my-custom/custom-model',
    };
    const model = resolveModel(config);
    expect(model).toBeDefined();
    expect(model.modelId).toBe('custom-model');
  });

  it('should use env var for anthropic when no config apiKey', () => {
    process.env['ANTHROPIC_API_KEY'] = 'sk-ant-from-env';
    const config: ConfigInfo = {
      model: 'anthropic/claude-sonnet-4-20250514',
    };
    const model = resolveModel(config);
    expect(model).toBeDefined();
  });

  it('should default to anthropic/claude-sonnet-4-20250514 when no model configured', () => {
    const config: ConfigInfo = {
      provider: { anthropic: { apiKey: 'sk-ant-test' } },
    };
    const model = resolveModel(config);
    expect(model).toBeDefined();
    expect(model.modelId).toBe('claude-sonnet-4-20250514');
  });

  it('should throw on model string without slash', () => {
    const config: ConfigInfo = {
      provider: { anthropic: { apiKey: 'sk-test' } },
      model: 'just-a-model-name',
    };
    expect(() => resolveModel(config)).toThrow('Invalid model format');
  });

  it('should throw on model string with empty provider', () => {
    const config: ConfigInfo = {
      provider: { anthropic: { apiKey: 'sk-test' } },
      model: '/model-id',
    };
    expect(() => resolveModel(config)).toThrow('Invalid model format');
  });

  it('should throw on model string with empty model id', () => {
    const config: ConfigInfo = {
      provider: { anthropic: { apiKey: 'sk-test' } },
      model: 'anthropic/',
    };
    expect(() => resolveModel(config)).toThrow('Invalid model format');
  });

  it('should throw on missing apiKey with providerID and env var hint', () => {
    delete process.env['ANTHROPIC_API_KEY'];
    const config: ConfigInfo = {
      model: 'anthropic/claude-sonnet-4-20250514',
    };
    expect(() => resolveModel(config)).toThrow(/anthropic/);
    expect(() => resolveModel(config)).toThrow(/ANTHROPIC_API_KEY/);
  });

  it('should throw on missing apiKey with config path hint', () => {
    delete process.env['DEEPSEEK_API_KEY'];
    const config: ConfigInfo = {
      model: 'deepseek/deepseek-chat',
    };
    expect(() => resolveModel(config)).toThrow(/deepseek/);
    expect(() => resolveModel(config)).toThrow(/config\.jsonc/);
  });

  it('should work with deepseek provider', () => {
    const config: ConfigInfo = {
      provider: { deepseek: { apiKey: 'sk-ds-test' } },
      model: 'deepseek/deepseek-chat',
    };
    const model = resolveModel(config);
    expect(model).toBeDefined();
    expect(model.modelId).toBe('deepseek-chat');
  });

  describe('LlmError', () => {
    it('should throw LlmError with code LLM_NOT_CONFIGURED when apiKey is missing', () => {
      delete process.env['ANTHROPIC_API_KEY'];
      const config: ConfigInfo = {
        model: 'anthropic/claude-sonnet-4-20250514',
      };
      try {
        resolveModel(config);
        expect.fail('should have thrown');
      } catch (error) {
        expect(error).toBeInstanceOf(LlmError);
        expect(error).toBeInstanceOf(Error);
        expect((error as LlmError).code).toBe('LLM_NOT_CONFIGURED');
        expect((error as LlmError).name).toBe('LlmError');
        expect((error as LlmError).message).toContain('anthropic');
      }
    });

    it('should throw plain Error for invalid model format', () => {
      const config: ConfigInfo = {
        provider: { anthropic: { apiKey: 'sk-test' } },
        model: 'no-slash-here',
      };
      try {
        resolveModel(config);
        expect.fail('should have thrown');
      } catch (error) {
        expect(error).toBeInstanceOf(Error);
        expect(error).not.toBeInstanceOf(LlmError);
        expect((error as Error).message).toContain('Invalid model format');
      }
    });
  });
});
