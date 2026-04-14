import { ModelsDev, type ModelsDevModel } from './models-dev';
import { load } from './config-loader';
import type { ConfigInfo } from './config-schema';


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


function modelFromModelsDev(providerId: string, model: ModelsDevModel): ModelMetadata {
  const badges: string[] = [];
  if (model.status === 'beta') badges.push('beta');
  if (model.status === 'deprecated') badges.push('deprecated');
  if (model.experimental) badges.push('experimental');

  return {
    id: `${providerId}/${model.id}`,
    name: model.name,
    provider: providerId,
    contextWindow: model.limit?.context ?? 0,
    badges,
    description: undefined,
    variants: undefined,
  };
}

export async function getModelsByProvider(
  providerId: string,
  config?: ConfigInfo
): Promise<ModelMetadata[]> {
  const effectiveConfig = config ?? load();
  const configModels = effectiveConfig.provider?.[providerId]?.models;

  if (configModels && Array.isArray(configModels) && configModels.length > 0) {
    return configModels.map((modelId) => ({
      id: `${providerId}/${modelId}`,
      name: modelId,
      provider: providerId,
      contextWindow: 0,
      badges: [],
    }));
  }

  const allProviders = await ModelsDev.get();
  const provider = allProviders[providerId];
  if (!provider) {
    return [];
  }

  return Object.values(provider.models).map((model) =>
    modelFromModelsDev(providerId, model)
  );
}

export async function getModelById(fullId: string): Promise<ModelMetadata | undefined> {
  const slashIndex = fullId.indexOf('/');
  if (slashIndex === -1) {
    return undefined;
  }
  const providerId = fullId.slice(0, slashIndex);
  const modelId = fullId.slice(slashIndex + 1);

  const allProviders = await ModelsDev.get();
  const provider = allProviders[providerId];
  if (!provider) {
    return undefined;
  }

  const model = provider.models[modelId];
  if (!model) {
    return undefined;
  }

  return modelFromModelsDev(providerId, model);
}
