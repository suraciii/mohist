import { Hono } from 'hono';
import { ApiResponse, ConfigConflictError } from '../types';
import { load, writeConfig } from '../config/config-loader';

export function createOpencodeModelConfigRoutes(): Hono {
  const app = new Hono();

  app.get('/model', (c) => {
    try {
      const config = load();
      const model = config.opencode?.model ?? null;
      return c.json<ApiResponse<{ model: string | null }>>({
        success: true,
        data: { model },
      });
    } catch (error) {
      return c.json<ApiResponse>(
        { success: false, error: error instanceof Error ? error.message : 'Unknown error' },
        500,
      );
    }
  });

  app.put('/model', async (c) => {
    try {
      const body = await c.req.json();

      if (!('model' in body)) {
        return c.json<ApiResponse>(
          { success: false, error: 'model is required' },
          400,
        );
      }

      if (body.model !== null && typeof body.model !== 'string') {
        return c.json<ApiResponse>(
          { success: false, error: 'model must be a string or null' },
          400,
        );
      }

      const config = load();

      if (body.model === null) {
        if (config.opencode) {
          delete config.opencode.model;
        }
      } else {
        if (!config.opencode) {
          config.opencode = {};
        }
        config.opencode.model = body.model;
      }

      const writeOptions = body.expectedVersion !== undefined
        ? { expectedVersion: body.expectedVersion as number }
        : undefined;

      try {
        writeConfig(config, undefined, writeOptions);
      } catch (error) {
        if (error instanceof ConfigConflictError) {
          return c.json<ApiResponse>(
            { success: false, error: 'Config was modified by another process' },
            409,
          );
        }
        throw error;
      }

      return c.json<ApiResponse<{ model: string | null }>>({
        success: true,
        data: { model: body.model },
      });
    } catch (error) {
      return c.json<ApiResponse>(
        { success: false, error: error instanceof Error ? error.message : 'Unknown error' },
        500,
      );
    }
  });

  return app;
}
