import { createAnthropic } from '@ai-sdk/anthropic';
import { createOpenAI } from '@ai-sdk/openai';
import type { LanguageModelV3 } from '@ai-sdk/provider';
import type { ConfigInfo } from '../config/config-schema';
import { getProviderConfig, getConfigPath, load } from '../config/config-loader';
import { getBuiltinProviders } from '../config/builtin-providers';
import { ModelsDev } from '../config/models-dev';

export type LlmConfig = ConfigInfo;

export class LlmError extends Error {
  code: string;
  constructor(code: string, message: string) {
    super(message);
    this.code = code;
    this.name = 'LlmError';
  }
}

async function resolveDefaultModel(config?: LlmConfig): Promise<string> {
  const effectiveConfig = config ?? load();

  if (effectiveConfig.model) {
    return effectiveConfig.model;
  }

  const builtinProviders = await getBuiltinProviders();
  const configuredProviders: Array<{ providerId: string; releaseDate: Date | null }> = [];

  for (const [providerId] of Object.entries(builtinProviders)) {
    const resolved = getProviderConfig(effectiveConfig, providerId);

    if (resolved.source === 'none') {
      continue;
    }

    if (!resolved.apiKey) {
      continue;
    }

    const allModelsDev = await ModelsDev.get();
    const providerModels = allModelsDev[providerId];

    if (!providerModels || Object.keys(providerModels.models).length === 0) {
      continue;
    }

    const latestReleaseDate = Object.values(providerModels.models).reduce<Date | null>(
      (latest, model) => {
        const releaseDate = new Date(model.release_date);
        if (!latest || releaseDate > latest) {
          return releaseDate;
        }
        return latest;
      },
      null
    );

    configuredProviders.push({ providerId, releaseDate: latestReleaseDate });
  }

  if (configuredProviders.length === 0) {
    throw new Error('No LLM provider configured. Configure a provider in config or set an API key environment variable.');
  }

  configuredProviders.sort((a, b) => {
    if (a.releaseDate === null) return 1;
    if (b.releaseDate === null) return -1;
    return b.releaseDate.getTime() - a.releaseDate.getTime();
  });

  const bestProvider = configuredProviders[0];
  const allModelsDev = await ModelsDev.get();
  const providerModels = allModelsDev[bestProvider.providerId];

  if (!providerModels) {
    throw new Error('No LLM provider configured. Configure a provider in config or set an API key environment variable.');
  }

  const sortedModels = Object.values(providerModels.models).sort((a, b) => {
    const dateA = new Date(a.release_date);
    const dateB = new Date(b.release_date);
    return dateB.getTime() - dateA.getTime();
  });

  const latestModel = sortedModels[0];
  return `${bestProvider.providerId}/${latestModel.id}`;
}

export async function resolveModel(config?: LlmConfig): Promise<LanguageModelV3> {
  const modelStr = config?.model ?? await resolveDefaultModel(config);
  const slashIndex = modelStr.indexOf('/');
  if (slashIndex === -1) {
    throw new Error(
      `Invalid model format: "${modelStr}". Expected "provider/model-id" format.`
    );
  }

  const providerID = modelStr.slice(0, slashIndex);
  const modelID = modelStr.slice(slashIndex + 1);

  if (!providerID || !modelID) {
    throw new Error(
      `Invalid model format: "${modelStr}". Expected "provider/model-id" format.`
    );
  }

  const resolved = getProviderConfig(config ?? {}, providerID);

  if (!resolved.apiKey) {
    const envHint = resolved.envVars.length > 0
      ? ` or set ${resolved.envVars.join(', ')} environment variable`
      : '';
    throw new LlmError(
      'LLM_NOT_CONFIGURED',
      `API key not found for provider "${providerID}". Set provider.${providerID}.apiKey in ${getConfigPath()}${envHint}.`
    );
  }

  const sdkOpts: Record<string, string> = { apiKey: resolved.apiKey };
  if (resolved.baseURL) {
    sdkOpts.baseURL = resolved.baseURL;
  }

  switch (resolved.sdk) {
    case 'anthropic':
      return createAnthropic(sdkOpts)(modelID);
    case 'openai':
      return createOpenAI(sdkOpts)(modelID);
    case 'openai-compatible':
      return createOpenAI(sdkOpts).chat(modelID);
    default:
      return createOpenAI(sdkOpts).chat(modelID);
  }
}
