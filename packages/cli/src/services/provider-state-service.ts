import { load, getProviderConfig } from '../config/config-loader';
import { getBuiltinProviders } from '../config/builtin-providers';
import { getModelsByProvider } from '../config/builtin-models';

export interface ProviderListItem {
  id: string;
  name: string;
  baseURL: string | null;
  configured: boolean;
  source: 'config' | 'env' | 'none';
  isBuiltin: boolean;
  isDefault: boolean;
  apiKeyMasked: string | null;
}

export interface ProviderModelGroup {
  id: string;
  name: string;
  configured: boolean;
  models: Array<{
    id: string;
    name: string;
    badges: string[];
    contextWindow: number;
  }>;
}

function maskApiKey(apiKey: string): string {
  if (apiKey.length <= 8) return '********';
  return apiKey.slice(0, 4) + '*'.repeat(apiKey.length - 8) + apiKey.slice(-4);
}

function getDefaultProviderId(config: { model?: string }): string | null {
  if (!config.model) return null;
  const slashIndex = config.model.indexOf('/');
  if (slashIndex === -1) return null;
  return config.model.slice(0, slashIndex);
}

export class ProviderStateService {
  private providersSnapshot: ProviderListItem[] = [];
  private modelsSnapshot: ProviderModelGroup[] = [];

  async warm(): Promise<void> {
    await this.rebuildSnapshots();
  }

  async refresh(): Promise<void> {
    await this.rebuildSnapshots();
  }

  getProviders(): ProviderListItem[] {
    return this.providersSnapshot;
  }

  getProviderModelGroups(): ProviderModelGroup[] {
    return this.modelsSnapshot;
  }

  private async rebuildSnapshots(): Promise<void> {
    const config = load();
    const allProviders = await getBuiltinProviders();
    const defaultProviderId = getDefaultProviderId(config);
    const customProviders = config.provider ?? {};

    const providers: ProviderListItem[] = [];
    for (const id of Object.keys(allProviders)) {
      const resolved = getProviderConfig(config, id);
      providers.push({
        id,
        name: resolved.name,
        baseURL: resolved.baseURL,
        configured: resolved.source !== 'none',
        source: resolved.source === 'builtin' ? 'none' : resolved.source,
        isBuiltin: true,
        isDefault: id === defaultProviderId,
        apiKeyMasked: resolved.apiKey ? maskApiKey(resolved.apiKey) : null,
      });
    }

    for (const id of Object.keys(customProviders)) {
      if (allProviders[id]) continue;
      const resolved = getProviderConfig(config, id);
      providers.push({
        id,
        name: resolved.name,
        baseURL: resolved.baseURL,
        configured: resolved.source !== 'none',
        source: resolved.source === 'builtin' ? 'none' : resolved.source,
        isBuiltin: false,
        isDefault: id === defaultProviderId,
        apiKeyMasked: resolved.apiKey ? maskApiKey(resolved.apiKey) : null,
      });
    }

    const modelGroups: ProviderModelGroup[] = [];
    for (const id of Object.keys(allProviders)) {
      const resolved = getProviderConfig(config, id);
      if (resolved.source === 'none') continue;
      const builtinModels = await getModelsByProvider(id, config);
      modelGroups.push({
        id,
        name: resolved.name,
        configured: true,
        models: builtinModels.map(m => ({
          id: m.id,
          name: m.name,
          badges: m.badges,
          contextWindow: m.contextWindow,
        })),
      });
    }

    for (const id of Object.keys(customProviders)) {
      if (allProviders[id]) continue;
      const resolved = getProviderConfig(config, id);
      const models = customProviders[id]?.models ?? [];
      modelGroups.push({
        id,
        name: resolved.name,
        configured: resolved.source !== 'none',
        models: models.map(m => ({
          id: `${id}/${m}`,
          name: m,
          badges: [],
          contextWindow: 0,
        })),
      });
    }

    this.providersSnapshot = providers;
    this.modelsSnapshot = modelGroups;
  }
}