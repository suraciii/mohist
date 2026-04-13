import type { SdkType } from './config-schema';
import { ModelsDev } from './models-dev';

export interface BuiltinProvider {
  sdk: SdkType;
  name: string;
  baseURL: string | null;
  envVars: string[];
}

function npmToSdk(npm: string | undefined): SdkType {
  if (npm === '@ai-sdk/anthropic') return 'anthropic';
  if (npm === '@ai-sdk/openai') return 'openai';
  return 'openai-compatible';
}

const FALLBACK_PROVIDERS: Record<string, BuiltinProvider> = {
  deepseek: {
    sdk: 'openai-compatible',
    name: 'DeepSeek',
    baseURL: 'https://api.deepseek.com',
    envVars: ['DEEPSEEK_API_KEY'],
  },
};

export async function getBuiltinProviders(): Promise<Record<string, BuiltinProvider>> {
  const providers: Record<string, BuiltinProvider> = {};
  const modelsDevData = await ModelsDev.get();

  for (const [id, provider] of Object.entries(modelsDevData)) {
    providers[id] = {
      sdk: npmToSdk(provider.npm),
      name: provider.name,
      baseURL: provider.api ?? null,
      envVars: provider.env,
    };
  }

  for (const [id, fallback] of Object.entries(FALLBACK_PROVIDERS)) {
    if (!providers[id]) {
      providers[id] = fallback;
    }
  }

  return providers;
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
  deepseek: {
    sdk: 'openai-compatible',
    name: 'DeepSeek',
    baseURL: 'https://api.deepseek.com',
    envVars: ['DEEPSEEK_API_KEY'],
  },
};