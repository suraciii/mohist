export interface ModelVariant {
  id: string;
  name: string;
  contextWindow?: number;
}

export interface ModelMetadata {
  id: string;
  name: string;
  provider: string;
  description?: string;
  badges: string[];
  contextWindow: number;
  variants?: ModelVariant[];
}

const ANTHROPIC_MODELS: ModelMetadata[] = [
  {
    id: 'claude-opus-4-20250514',
    name: 'Claude Opus 4',
    provider: 'anthropic',
    description: 'Most capable model for highly complex tasks',
    badges: ['latest'],
    contextWindow: 200000,
  },
  {
    id: 'claude-sonnet-4-20250514',
    name: 'Claude Sonnet 4',
    provider: 'anthropic',
    description: 'Ideal balance of intelligence and speed',
    badges: ['latest'],
    contextWindow: 200000,
  },
  {
    id: 'claude-haiku-4-20250514',
    name: 'Claude Haiku 4',
    provider: 'anthropic',
    description: 'Fastest model for lightweight tasks',
    badges: ['latest'],
    contextWindow: 200000,
  },
];

const OPENAI_MODELS: ModelMetadata[] = [
  {
    id: 'gpt-4o',
    name: 'GPT-4o',
    provider: 'openai',
    description: 'Most capable flagship model',
    badges: [],
    contextWindow: 128000,
  },
  {
    id: 'gpt-4o-mini',
    name: 'GPT-4o Mini',
    provider: 'openai',
    description: 'Fast and affordable for most tasks',
    badges: [],
    contextWindow: 128000,
  },
  {
    id: 'o3',
    name: 'OpenAI o3',
    provider: 'openai',
    description: 'Advanced reasoning model',
    badges: ['latest'],
    contextWindow: 200000,
  },
  {
    id: 'o4-mini',
    name: 'OpenAI o4-mini',
    provider: 'openai',
    description: 'Efficient reasoning model',
    badges: ['latest'],
    contextWindow: 100000,
  },
];

const GLM_MODELS: ModelMetadata[] = [
  {
    id: 'glm-4-flash',
    name: 'GLM-4 Flash',
    provider: 'glm',
    description: 'Fast processing with strong capabilities',
    badges: ['free'],
    contextWindow: 128000,
  },
  {
    id: 'glm-4-plus',
    name: 'GLM-4 Plus',
    provider: 'glm',
    description: 'Enhanced capabilities for complex tasks',
    badges: [],
    contextWindow: 128000,
  },
  {
    id: 'glm-4-air',
    name: 'GLM-4 Air',
    provider: 'glm',
    description: 'Balanced performance and speed',
    badges: [],
    contextWindow: 128000,
  },
];

const KIMI_MODELS: ModelMetadata[] = [
  {
    id: 'kimi-k2.5',
    name: 'Kimi K2.5',
    provider: 'kimi',
    description: 'Latest flagship model from Kimi',
    badges: ['latest'],
    contextWindow: 200000,
  },
  {
    id: 'kimi-k2',
    name: 'Kimi K2',
    provider: 'kimi',
    description: 'Enhanced reasoning capabilities',
    badges: [],
    contextWindow: 200000,
  },
  {
    id: 'kimi-k1.5',
    name: 'Kimi K1.5',
    provider: 'kimi',
    description: 'Strong performance for various tasks',
    badges: [],
    contextWindow: 128000,
  },
];

const MINIMAX_MODELS: ModelMetadata[] = [
  {
    id: 'minimax-text-01',
    name: 'MiniMax Text-01',
    provider: 'minimax',
    description: 'MiniMax flagship text model',
    badges: [],
    contextWindow: 100000,
  },
];

const DEEPSEEK_MODELS: ModelMetadata[] = [
  {
    id: 'deepseek-chat',
    name: 'DeepSeek Chat',
    provider: 'deepseek',
    description: 'Conversational model',
    badges: [],
    contextWindow: 64000,
  },
  {
    id: 'deepseek-reasoner',
    name: 'DeepSeek Reasoner',
    provider: 'deepseek',
    description: 'Advanced reasoning model',
    badges: ['latest'],
    contextWindow: 64000,
  },
];

const QWEN_MODELS: ModelMetadata[] = [
  {
    id: 'qwen-max',
    name: 'Qwen Max',
    provider: 'qwen',
    description: 'Most capable Qwen model',
    badges: [],
    contextWindow: 32000,
  },
  {
    id: 'qwen-plus',
    name: 'Qwen Plus',
    provider: 'qwen',
    description: 'Enhanced Qwen model',
    badges: [],
    contextWindow: 131072,
  },
  {
    id: 'qwen-turbo',
    name: 'Qwen Turbo',
    provider: 'qwen',
    description: 'Fast and efficient Qwen model',
    badges: [],
    contextWindow: 131072,
  },
];

const ZHIPUAI_CODING_MODELS: ModelMetadata[] = GLM_MODELS.map(m => ({
  ...m,
  provider: 'zhipuai-coding-plan',
  badges: m.provider === 'glm' ? ['free'] : [],
}));

const KIMI_FOR_CODING_MODELS: ModelMetadata[] = KIMI_MODELS.slice(0, 1).map(m => ({
  ...m,
  provider: 'kimi-for-coding',
}));

const MINIMAX_FOR_CODING_MODELS: ModelMetadata[] = MINIMAX_MODELS.map(m => ({
  ...m,
  provider: 'minimax-for-coding',
}));

export const BUILTIN_MODELS: ModelMetadata[] = [
  ...ANTHROPIC_MODELS,
  ...OPENAI_MODELS,
  ...GLM_MODELS,
  ...KIMI_MODELS,
  ...MINIMAX_MODELS,
  ...DEEPSEEK_MODELS,
  ...QWEN_MODELS,
  ...ZHIPUAI_CODING_MODELS,
  ...KIMI_FOR_CODING_MODELS,
  ...MINIMAX_FOR_CODING_MODELS,
];

export function getModelsByProvider(provider: string): ModelMetadata[] {
  return BUILTIN_MODELS.filter(m => m.provider === provider);
}

export function getModelById(id: string): ModelMetadata | undefined {
  return BUILTIN_MODELS.find(m => m.id === id);
}

export const PROVIDER_MODEL_COUNT: Record<string, number> = {
  anthropic: ANTHROPIC_MODELS.length,
  openai: OPENAI_MODELS.length,
  glm: GLM_MODELS.length,
  kimi: KIMI_MODELS.length,
  minimax: MINIMAX_MODELS.length,
  deepseek: DEEPSEEK_MODELS.length,
  qwen: QWEN_MODELS.length,
  'zhipuai-coding-plan': ZHIPUAI_CODING_MODELS.length,
  'kimi-for-coding': KIMI_FOR_CODING_MODELS.length,
  'minimax-for-coding': MINIMAX_FOR_CODING_MODELS.length,
};
