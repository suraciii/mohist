import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { resolveModel, LlmError } from '../src/agent-runtime/llm';
import type { ConfigInfo } from '../src/config/config-schema';

describe('resolveModel', () => {
  const savedEnv: Record<string, string | undefined> = {};

  beforeEach(() => {
    savedEnv['ANTHROPIC_API_KEY'] = process.env['ANTHROPIC_API_KEY'];
    savedEnv['OPENAI_API_KEY'] = process.env['OPENAI_API_KEY'];
    savedEnv['ZHIPU_API_KEY'] = process.env['ZHIPU_API_KEY'];
    savedEnv['DEEPSEEK_API_KEY'] = process.env['DEEPSEEK_API_KEY'];
    savedEnv['MINIMAX_API_KEY'] = process.env['MINIMAX_API_KEY'];
    savedEnv['KIMI_API_KEY'] = process.env['KIMI_API_KEY'];
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

  it('should create anthropic model via createAnthropic', async () => {
    const config: ConfigInfo = {
      provider: { anthropic: { apiKey: 'sk-ant-test' } },
      model: 'anthropic/claude-sonnet-4-6',
    };
    const model = await resolveModel(config);
    expect(model).toBeDefined();
    expect(model.modelId).toBe('claude-sonnet-4-6');
  });

  it('should create openai model via createOpenAI', async () => {
    const config: ConfigInfo = {
      provider: { openai: { apiKey: 'sk-openai-test' } },
      model: 'openai/gpt-4o-2024-05-13',
    };
    const model = await resolveModel(config);
    expect(model).toBeDefined();
    expect(model.modelId).toBe('gpt-4o-2024-05-13');
  });

  it('should create openai-compatible model with baseURL', async () => {
    const config: ConfigInfo = {
      provider: { zhipuai: { apiKey: 'sk-zhipu-test' } },
      model: 'zhipuai/glm-4.5-flash',
    };
    const model = await resolveModel(config);
    expect(model).toBeDefined();
    expect(model.modelId).toBe('glm-4.5-flash');
  });

  it('should treat unregistered provider as openai-compatible', async () => {
    const config: ConfigInfo = {
      provider: {
        'my-custom': { apiKey: 'sk-custom', baseURL: 'https://api.custom.com/v1' },
      },
      model: 'my-custom/custom-model',
    };
    const model = await resolveModel(config);
    expect(model).toBeDefined();
    expect(model.modelId).toBe('custom-model');
  });

  it('should use env var for anthropic when no config apiKey', async () => {
    process.env['ANTHROPIC_API_KEY'] = 'sk-ant-from-env';
    const config: ConfigInfo = {
      model: 'anthropic/claude-sonnet-4-6',
    };
    const model = await resolveModel(config);
    expect(model).toBeDefined();
  });

  it('should select latest configured provider model when no model configured', async () => {
    process.env['ANTHROPIC_API_KEY'] = 'sk-ant-test';
    const config: ConfigInfo = {
      provider: { anthropic: { apiKey: 'sk-ant-test' } },
    };
    const model = await resolveModel(config);
    expect(model).toBeDefined();
    expect(model.modelId).toBe('claude-sonnet-4-6');
  });

  it('should throw on model string without slash', async () => {
    const config: ConfigInfo = {
      provider: { anthropic: { apiKey: 'sk-test' } },
      model: 'just-a-model-name',
    };
    await expect(resolveModel(config)).rejects.toThrow('Invalid model format');
  });

  it('should throw on model string with empty provider', async () => {
    const config: ConfigInfo = {
      provider: { anthropic: { apiKey: 'sk-test' } },
      model: '/model-id',
    };
    await expect(resolveModel(config)).rejects.toThrow('Invalid model format');
  });

  it('should throw on model string with empty model id', async () => {
    const config: ConfigInfo = {
      provider: { anthropic: { apiKey: 'sk-test' } },
      model: 'anthropic/',
    };
    await expect(resolveModel(config)).rejects.toThrow('Invalid model format');
  });

  it('should throw on missing apiKey with providerID and env var hint', async () => {
    delete process.env['ANTHROPIC_API_KEY'];
    const config: ConfigInfo = {
      model: 'anthropic/claude-sonnet-4-6',
    };
    await expect(resolveModel(config)).rejects.toThrow(/anthropic/i);
    await expect(resolveModel(config)).rejects.toThrow(/ANTHROPIC_API_KEY/i);
  });

  it('should throw on missing apiKey with config path hint', async () => {
    delete process.env['DEEPSEEK_API_KEY'];
    const config: ConfigInfo = {
      model: 'deepseek/deepseek-chat',
    };
    await expect(resolveModel(config)).rejects.toThrow(/deepseek/i);
    await expect(resolveModel(config)).rejects.toThrow(/config\.jsonc/i);
  });

  it('should work with deepseek provider', async () => {
    const config: ConfigInfo = {
      provider: { deepseek: { apiKey: 'sk-ds-test' } },
      model: 'deepseek/deepseek-chat',
    };
    const model = await resolveModel(config);
    expect(model).toBeDefined();
    expect(model.modelId).toBe('deepseek-chat');
  });

  it('should create zhipuai-coding-plan model via createOpenAI', async () => {
    const config: ConfigInfo = {
      provider: { 'zhipuai-coding-plan': { apiKey: 'sk-zhipu-test' } },
      model: 'zhipuai-coding-plan/glm-5.1',
    };
    const model = await resolveModel(config);
    expect(model).toBeDefined();
    expect(model.modelId).toBe('glm-5.1');
  });

  it('should create kimi-for-coding model via createOpenAI', async () => {
    const config: ConfigInfo = {
      provider: { 'kimi-for-coding': { apiKey: 'sk-kimi-test' } },
      model: 'kimi-for-coding/kimi-k2-0905-preview',
    };
    const model = await resolveModel(config);
    expect(model).toBeDefined();
    expect(model.modelId).toBe('kimi-k2-0905-preview');
  });

  it('should create minimax-coding-plan model via createOpenAI', async () => {
    const config: ConfigInfo = {
      provider: { 'minimax-coding-plan': { apiKey: 'sk-minimax-test' } },
      model: 'minimax-coding-plan/MiniMax-M2.5',
    };
    const model = await resolveModel(config);
    expect(model).toBeDefined();
    expect(model.modelId).toBe('MiniMax-M2.5');
  });

  describe('LlmError', () => {
    it('should throw LlmError with code LLM_NOT_CONFIGURED when apiKey is missing', async () => {
      delete process.env['ANTHROPIC_API_KEY'];
      const config: ConfigInfo = {
        model: 'anthropic/claude-sonnet-4-6',
      };
      try {
        await resolveModel(config);
        expect.fail('should have thrown');
      } catch (error) {
        expect(error).toBeInstanceOf(LlmError);
        expect(error).toBeInstanceOf(Error);
        expect((error as LlmError).code).toBe('LLM_NOT_CONFIGURED');
        expect((error as LlmError).name).toBe('LlmError');
        expect((error as LlmError).message).toContain('anthropic');
      }
    });

    it('should throw plain Error for invalid model format', async () => {
      const config: ConfigInfo = {
        provider: { anthropic: { apiKey: 'sk-test' } },
        model: 'no-slash-here',
      };
      try {
        await resolveModel(config);
        expect.fail('should have thrown');
      } catch (error) {
        expect(error).toBeInstanceOf(Error);
        expect(error).not.toBeInstanceOf(LlmError);
        expect((error as Error).message).toContain('Invalid model format');
      }
    });
  });
});