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

function buildProvidersFromSnapshot(): Record<string, BuiltinProvider> {
  const providers: Record<string, BuiltinProvider> = {};
  try {
    const mod = require('./models-snapshot.js') as { snapshot: Record<string, { npm?: string; name: string; api?: string; env: string[] }> };
    for (const [id, provider] of Object.entries(mod.snapshot)) {
      providers[id] = {
        sdk: npmToSdk(provider.npm),
        name: provider.name,
        baseURL: provider.api ?? null,
        envVars: provider.env,
      };
    }
  } catch {
    // snapshot not available
  }
  for (const [id, fallback] of Object.entries(FALLBACK_PROVIDERS)) {
    if (!providers[id]) {
      providers[id] = fallback;
    }
  }
  return providers;
}

export const BUILTIN_PROVIDERS: Record<string, BuiltinProvider> = buildProvidersFromSnapshot();

let asyncProvidersCache: Record<string, BuiltinProvider> | null = null;
let asyncCachePromise: Promise<Record<string, BuiltinProvider>> | null = null;

export function clearBuiltinProvidersCache(): void {
  asyncProvidersCache = null;
  asyncCachePromise = null;
}

export async function getBuiltinProviders(): Promise<Record<string, BuiltinProvider>> {
  if (asyncProvidersCache) {
    return asyncProvidersCache;
  }
  if (!asyncCachePromise) {
    asyncCachePromise = doGetBuiltinProviders();
  }
  const result = await asyncCachePromise;
  asyncProvidersCache = result;
  return result;
}

async function doGetBuiltinProviders(): Promise<Record<string, BuiltinProvider>> {
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

  asyncProvidersCache = providers;
  return providers;
}
