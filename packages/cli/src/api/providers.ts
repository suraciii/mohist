import { Hono } from 'hono';
import type { Context, Next } from 'hono';
import { generateText } from 'ai';
import { ApiResponse, ConfigConflictError } from '../types';
import { load, getProviderConfig, writeConfig } from '../config/config-loader';
import { BUILTIN_PROVIDERS, getBuiltinProviders } from '../config/builtin-providers';
import { ProviderConfigSchema } from '../config/config-schema';
import { getModelsByProvider } from '../config/builtin-models';
import type { EventBus } from '../services/event-bus';
import { RateLimiter } from '../utils/rate-limiter';
import { maskSensitiveData } from '../utils/sensitive-data';
import { createAnthropic } from '@ai-sdk/anthropic';
import { createOpenAI } from '@ai-sdk/openai';

export interface ProviderListItem {
  id: string;
  name: string;
  baseURL: string | null;
  models: string[];
  configured: boolean;
  source: 'config' | 'env' | 'none';
  isBuiltin: boolean;
  isDefault: boolean;
  apiKeyMasked: string | null;
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

export function createProviderRoutes(eventBus?: EventBus, rateLimiter?: RateLimiter): Hono {
  const app = new Hono();
  const limiter = rateLimiter ?? new RateLimiter(60 * 1000, 30);

  async function rateLimitMiddleware(c: Context, next: Next): Promise<Response | void> {
    const ip = c.req.header('x-forwarded-for')?.split(',')[0]?.trim()
      || c.req.header('x-real-ip')
      || 'unknown';

    const result = limiter.check(ip);
    if (!result.allowed) {
      c.header('Retry-After', String(result.retryAfter ?? 1));
      return c.json<ApiResponse>(
        { success: false, error: 'Too Many Requests' },
        429
      );
    }

    return next();
  }

  app.get('/', async (c) => {
    try {
      const config = load();
      const allProviders = await getBuiltinProviders();
      const defaultProviderId = getDefaultProviderId(config);
      const providerList: ProviderListItem[] = [];

      for (const id of Object.keys(allProviders)) {
        const resolved = getProviderConfig(config, id);
        const builtinModels = await getModelsByProvider(id, config);
        providerList.push({
          id,
          name: resolved.name,
          baseURL: resolved.baseURL,
          models: builtinModels.map(m => m.id),
          configured: resolved.source !== 'none',
          source: resolved.source === 'builtin' ? 'none' : resolved.source,
          isBuiltin: true,
          isDefault: id === defaultProviderId,
          apiKeyMasked: resolved.apiKey ? maskApiKey(resolved.apiKey) : null,
        });
      }

      const customProviders = config.provider ?? {};
      for (const id of Object.keys(customProviders)) {
        if (allProviders[id]) continue;
        const resolved = getProviderConfig(config, id);
        providerList.push({
          id,
          name: resolved.name,
          baseURL: resolved.baseURL,
          models: (customProviders[id]?.models ?? []).map(m => `${id}/${m}`),
          configured: resolved.source !== 'none',
          source: resolved.source === 'builtin' ? 'none' : resolved.source,
          isBuiltin: false,
          isDefault: id === defaultProviderId,
          apiKeyMasked: resolved.apiKey ? maskApiKey(resolved.apiKey) : null,
        });
      }

      const response: ApiResponse<ProviderListItem[]> = {
        success: true,
        data: providerList,
      };
      return c.json(response);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? maskSensitiveData({ message: error.message }).message : 'Unknown error',
      };
      return c.json(response, 500);
    }
  });

  app.get('/models', async (c) => {
    try {
      const config = load();
      const allProviders = await getBuiltinProviders();
      const customProviders = config.provider ?? {};

      const providerGroups: Array<{
        id: string;
        name: string;
        configured: boolean;
        models: Array<{
          id: string;
          name: string;
          badges: string[];
          contextWindow: number;
        }>;
      }> = [];

      for (const id of Object.keys(allProviders)) {
        const resolved = getProviderConfig(config, id);
        if (resolved.source === 'none') continue;
        const builtinModels = await getModelsByProvider(id, config);
        providerGroups.push({
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
        providerGroups.push({
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

      const response: ApiResponse<typeof providerGroups> = {
        success: true,
        data: providerGroups,
      };
      return c.json(response);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? maskSensitiveData({ message: error.message }).message : 'Unknown error',
      };
      return c.json(response, 500);
    }
  });

  app.post('/test', rateLimitMiddleware, async (c) => {
    const body = await c.req.json();

    try {
      const config = load();

      let resolvedConfig: {
        sdk: string;
        name: string;
        apiKey: string | null;
        baseURL: string | null;
      };

      if (body.id) {
        const savedConfig = getProviderConfig(config, body.id);
        resolvedConfig = {
          sdk: body.sdk ?? savedConfig.sdk,
          name: body.name ?? savedConfig.name,
          apiKey: body.apiKey ?? savedConfig.apiKey,
          baseURL: body.baseURL ?? savedConfig.baseURL,
        };
      } else {
        if (!body.apiKey || !body.baseURL || !body.sdk) {
          const response: ApiResponse = {
            success: false,
            error: 'apiKey, baseURL, and sdk are required when id is not provided',
          };
          return c.json(response, 400);
        }
        resolvedConfig = {
          sdk: body.sdk,
          name: body.name ?? 'Custom Provider',
          apiKey: body.apiKey,
          baseURL: body.baseURL,
        };
      }

      if (!resolvedConfig.apiKey) {
        const response: ApiResponse = {
          success: false,
          error: 'API key is required',
        };
        return c.json(response, 400);
      }

      if (!resolvedConfig.baseURL) {
        const response: ApiResponse = {
          success: false,
          error: 'baseURL is required',
        };
        return c.json(response, 400);
      }

      const models = body.models as string[] | undefined;
      const modelToTest = models?.[0] ?? 'default';

      const sdkOpts: Record<string, string> = { apiKey: resolvedConfig.apiKey };
      if (resolvedConfig.baseURL) {
        sdkOpts.baseURL = resolvedConfig.baseURL;
      }

      let model;
      try {
        switch (resolvedConfig.sdk) {
          case 'anthropic':
            model = createAnthropic(sdkOpts)(modelToTest);
            break;
          case 'openai':
            model = createOpenAI(sdkOpts)(modelToTest);
            break;
          case 'openai-compatible':
          default:
            model = createOpenAI(sdkOpts)(modelToTest);
            break;
        }
      } catch (err) {
        const response: ApiResponse = {
          success: false,
          error: err instanceof Error ? maskSensitiveData({ message: err.message }).message : 'Failed to create model',
        };
        return c.json(response, 400);
      }

      try {
        await generateText({
          model,
          messages: [{ role: 'user', content: 'hi' }],
          maxOutputTokens: 1,
        });
      } catch (err) {
        let userMessage: string;

        if (err instanceof Error) {
          const error = err as Error & { status?: number; code?: string };
          if (error.status === 401 || error.status === 403) {
            userMessage = 'Authentication failed';
          } else if (error.code === 'ECONNREFUSED' || error.code === 'ENOTFOUND' || error.code === 'ENETUNREACH' || error.code === 'EAI_AGAIN') {
            userMessage = 'Connection failed';
          } else if (error.name === 'TimeoutError' || error.code === 'ETIMEDOUT' || error.name === 'AI_TimeoutError') {
            userMessage = 'Connection timeout';
          } else {
            userMessage = error.message || 'Unknown error';
          }
        } else {
          userMessage = 'Unknown error';
        }

        const response: ApiResponse = {
          success: false,
          error: userMessage,
        };
        return c.json(response, 400);
      }

      const response: ApiResponse<{ success: boolean }> = {
        success: true,
        data: { success: true },
      };
      return c.json(response, 200);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? maskSensitiveData({ message: error.message }).message : 'Unknown error',
      };
      return c.json(response, 400);
    }
  });

  app.post('/:id', async (c) => {
    const id = c.req.param('id');
    const body = await c.req.json();

    try {
      if (!body.apiKey || typeof body.apiKey !== 'string' || body.apiKey.trim() === '') {
        const response: ApiResponse = {
          success: false,
          error: 'apiKey is required and cannot be empty',
        };
        return c.json(response, 400);
      }

      const isBuiltin = !!BUILTIN_PROVIDERS[id];
      const isCustom = !isBuiltin;

      if (isCustom) {
        const idPattern = /^[a-z0-9-]+$/;
        if (!idPattern.test(id)) {
          const response: ApiResponse = {
            success: false,
            error: 'Invalid provider ID format. Use lowercase letters, numbers, and hyphens only (a-z, 0-9, -)',
          };
          return c.json(response, 400);
        }

        if (!body.baseURL || typeof body.baseURL !== 'string') {
          const response: ApiResponse = {
            success: false,
            error: 'baseURL is required for custom providers',
          };
          return c.json(response, 400);
        }

        try {
          new URL(body.baseURL);
        } catch {
          const response: ApiResponse = {
            success: false,
            error: 'Invalid baseURL format. Must be a valid URL',
          };
          return c.json(response, 400);
        }

        if (!Array.isArray(body.models) || body.models.length === 0) {
          const response: ApiResponse = {
            success: false,
            error: 'models must be a non-empty array for custom providers',
          };
          return c.json(response, 400);
        }
      }

      const validated = ProviderConfigSchema.safeParse(body);
      if (!validated.success) {
        const errors = validated.error.issues.map((i) => `${i.path.join('.')}: ${i.message}`).join(', ');
        const response: ApiResponse = {
          success: false,
          error: `Validation failed: ${errors}`,
        };
        return c.json(response, 400);
      }

      const config = load();
      if (!config.provider) {
        config.provider = {};
      }

      config.provider[id] = {
        ...(config.provider[id] || {}),
        apiKey: body.apiKey.trim(),
        ...(body.name ? { name: body.name } : {}),
        ...(body.baseURL ? { baseURL: body.baseURL } : {}),
        ...(body.models ? { models: body.models } : {}),
        ...(body.sdk ? { sdk: body.sdk } : {}),
      };

      const writeOptions = body.expectedVersion !== undefined
        ? { expectedVersion: body.expectedVersion as number }
        : undefined;

      try {
        writeConfig(config, undefined, writeOptions);
      } catch (error) {
        if (error instanceof ConfigConflictError) {
          const response: ApiResponse = {
            success: false,
            error: 'Configuration version conflict',
          };
          return c.json({ ...response, currentVersion: error.currentVersion }, 409);
        }
        throw error;
      }

      const updatedProvider = config.provider[id];
      if (eventBus) {
        eventBus.emit('config:providers:changed', {
          providers: [{ id, ...updatedProvider }],
        });
      }

      const response: ApiResponse<{ id: string; configured: boolean; version: number }> = {
        success: true,
        data: { id, configured: true, version: config._version! },
      };
      return c.json(response, 200);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? maskSensitiveData({ message: error.message }).message : 'Unknown error',
      };
      return c.json(response, 500);
    }
  });

  app.delete('/:id', async (c) => {
    const id = c.req.param('id');

    try {
      const config = load();

      if (!config.provider || !config.provider[id]) {
        c.status(204);
        return c.body(null);
      }

      delete config.provider[id];

      writeConfig(config);

      if (eventBus) {
        eventBus.emit('config:providers:changed', {
          providers: [{ id }],
        });
      }

      const response: ApiResponse<{ id: string }> = {
        success: true,
        data: { id },
      };
      return c.json(response, 200);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? maskSensitiveData({ message: error.message }).message : 'Unknown error',
      };
      return c.json(response, 500);
    }
  });

  return app;
}
