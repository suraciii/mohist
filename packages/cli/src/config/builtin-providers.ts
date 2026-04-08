import type { SdkType } from './config-schema';

export interface BuiltinProvider {
  sdk: SdkType;
  name: string;
  baseURL: string | null;
  envVars: string[];
}

export const BUILTIN_PROVIDERS: Record<string, BuiltinProvider> = {
  anthropic: {
    sdk: 'anthropic',
    name: 'Anthropic',
    baseURL: null,
    envVars: ['ANTHROPIC_API_KEY'],
  },
  openai: {
    sdk: 'openai',
    name: 'OpenAI',
    baseURL: null,
    envVars: ['OPENAI_API_KEY'],
  },
  glm: {
    sdk: 'openai-compatible',
    name: '智谱 GLM',
    baseURL: 'https://open.bigmodel.cn/api/paas/v4',
    envVars: ['GLM_API_KEY'],
  },
  kimi: {
    sdk: 'openai-compatible',
    name: 'Moonshot Kimi',
    baseURL: 'https://api.moonshot.cn/v1',
    envVars: ['MOONSHOT_API_KEY', 'KIMI_API_KEY'],
  },
  minimax: {
    sdk: 'openai-compatible',
    name: 'MiniMax',
    baseURL: 'https://api.minimax.chat/v1',
    envVars: ['MINIMAX_API_KEY'],
  },
  deepseek: {
    sdk: 'openai-compatible',
    name: 'DeepSeek',
    baseURL: 'https://api.deepseek.com',
    envVars: ['DEEPSEEK_API_KEY'],
  },
  qwen: {
    sdk: 'openai-compatible',
    name: '通义千问',
    baseURL: 'https://dashscope.aliyuncs.com/compatible-mode/v1',
    envVars: ['QWEN_API_KEY', 'DASHSCOPE_API_KEY'],
  },
};
