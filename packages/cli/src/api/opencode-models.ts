import { Hono } from 'hono';
import { ApiResponse } from '../types';
import { getOpencodeDiscoveryService } from '../services/opencode-discovery-service';

export function createOpencodeModelsRoutes(): Hono {
  const app = new Hono();

  app.get('/models', async (c) => {
    try {
      const discoveryService = getOpencodeDiscoveryService();
      const models = await discoveryService.getAvailableModels();

      const response: ApiResponse<string[]> = {
        success: true,
        data: models,
      };
      return c.json(response);
    } catch (err) {
      const response: ApiResponse<never> = {
        success: false,
        error: 'model discovery failed',
        details: err instanceof Error ? err.message : String(err),
      };
      return c.json(response, 503);
    }
  });

  return app;
}