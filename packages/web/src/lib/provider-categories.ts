export type ProviderCategory = 'recommended' | 'coding-plan' | 'china' | 'international'

export type ProviderRegion = 'china' | 'international'

export interface ProviderCategoryInfo {
  category: ProviderCategory
  region: ProviderRegion
}

export interface GroupedProvider<T = unknown> {
  key: string
  label: string
  providers: T[]
}

export const PROVIDER_CATEGORIES: Record<string, ProviderCategoryInfo> = {
  openai: { category: 'recommended', region: 'international' },
  anthropic: { category: 'recommended', region: 'international' },
  deepseek: { category: 'recommended', region: 'china' },
  google: { category: 'recommended', region: 'international' },
  groq: { category: 'recommended', region: 'international' },
  mistral: { category: 'recommended', region: 'international' },

  'kimi-for-coding': { category: 'coding-plan', region: 'china' },
  'minimax-coding-plan': { category: 'coding-plan', region: 'china' },
  'minimax-cn-coding-plan': { category: 'coding-plan', region: 'china' },
  'zhipuai-coding-plan': { category: 'coding-plan', region: 'china' },
  'alibaba-coding-plan': { category: 'coding-plan', region: 'china' },
  'alibaba-coding-plan-cn': { category: 'coding-plan', region: 'china' },
  'tencent-coding-plan': { category: 'coding-plan', region: 'china' },
  'xiaomi-token-plan-ams': { category: 'coding-plan', region: 'china' },
  'xiaomi-token-plan-cn': { category: 'coding-plan', region: 'china' },
  'xiaomi-token-plan-sgp': { category: 'coding-plan', region: 'china' },
  'zai-coding-plan': { category: 'coding-plan', region: 'china' },
  'kuae-cloud-coding-plan': { category: 'coding-plan', region: 'china' },

  zhipuai: { category: 'china', region: 'china' },
  alibaba: { category: 'china', region: 'china' },
  'alibaba-cn': { category: 'china', region: 'china' },
  minimax: { category: 'china', region: 'china' },
  'minimax-cn': { category: 'china', region: 'china' },
  moonshotai: { category: 'china', region: 'china' },
  'moonshotai-cn': { category: 'china', region: 'china' },
  xiaomi: { category: 'china', region: 'china' },
  stepfun: { category: 'china', region: 'china' },
  bailing: { category: 'china', region: 'china' },
  siliconflow: { category: 'china', region: 'china' },
  'siliconflow-cn': { category: 'china', region: 'china' },
  'tencent-tokenhub': { category: 'china', region: 'china' },
  modelscope: { category: 'china', region: 'china' },
  iflowcn: { category: 'china', region: 'china' },
  jiekou: { category: 'china', region: 'china' },
  'qihang-ai': { category: 'china', region: 'china' },
  'qiniu-ai': { category: 'china', region: 'china' },
  morph: { category: 'china', region: 'china' },
  'aihubmix': { category: 'china', region: 'china' },
  nova: { category: 'china', region: 'china' },
  moark: { category: 'china', region: 'china' },
  vivgrid: { category: 'china', region: 'china' },

  xai: { category: 'international', region: 'international' },
  perplexity: { category: 'international', region: 'international' },
  'perplexity-agent': { category: 'international', region: 'international' },
  togetherai: { category: 'international', region: 'international' },
  'fireworks-ai': { category: 'international', region: 'international' },
  cohere: { category: 'international', region: 'international' },
  'amazon-bedrock': { category: 'international', region: 'international' },
  azure: { category: 'international', region: 'international' },
  'azure-cognitive-services': { category: 'international', region: 'international' },
  'google-vertex': { category: 'international', region: 'international' },
  'google-vertex-anthropic': { category: 'international', region: 'international' },
  'github-copilot': { category: 'international', region: 'international' },
  'github-models': { category: 'international', region: 'international' },
  huggingface: { category: 'international', region: 'international' },
  cerebras: { category: 'international', region: 'international' },
  deepinfra: { category: 'international', region: 'international' },
  nvidia: { category: 'international', region: 'international' },
  ollama: { category: 'international', region: 'international' },
  'ollama-cloud': { category: 'international', region: 'international' },
  openrouter: { category: 'international', region: 'international' },
  upstage: { category: 'international', region: 'international' },
  friendli: { category: 'international', region: 'international' },
  novita: { category: 'international', region: 'international' },
  'novita-ai': { category: 'international', region: 'international' },
  baseten: { category: 'international', region: 'international' },
  nebius: { category: 'international', region: 'international' },
  chutes: { category: 'international', region: 'international' },
  venice: { category: 'international', region: 'international' },
  v0: { category: 'international', region: 'international' },
  vercel: { category: 'international', region: 'international' },
  poe: { category: 'international', region: 'international' },
  cloudflare: { category: 'international', region: 'international' },
  'cloudflare-workers-ai': { category: 'international', region: 'international' },
  'cloudflare-ai-gateway': { category: 'international', region: 'international' },
  gitlab: { category: 'international', region: 'international' },
  digitalocean: { category: 'international', region: 'international' },
  vultr: { category: 'international', region: 'international' },
  scaleway: { category: 'international', region: 'international' },
  ovhcloud: { category: 'international', region: 'international' },
  stackit: { category: 'international', region: 'international' },
}

const CATEGORY_META: Record<ProviderCategory, { order: number; label: string }> = {
  recommended: { order: 0, label: 'Recommended' },
  'coding-plan': { order: 1, label: 'Coding Plan' },
  china: { order: 2, label: 'China' },
  international: { order: 3, label: 'International' },
}

export function getProviderCategory(providerId: string): ProviderCategoryInfo {
  return PROVIDER_CATEGORIES[providerId] ?? { category: 'international', region: 'international' }
}

export function getCategoryMeta(category: ProviderCategory): { order: number; label: string } {
  return CATEGORY_META[category]
}
