import { Hono } from 'hono';
import { ApiResponse, Config } from '../types';
import { ConfigService } from '../services';

export function createConfigRoutes(configService: ConfigService): Hono {
  const app = new Hono();

  app.get('/', async (c) => {
    try {
      const config = configService.getConfig();

      const response: ApiResponse<Partial<Config>> = {
        success: true,
        data: config
      };
      return c.json(response);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      };
      return c.json(response, 500);
    }
  });

  app.put('/:key', async (c) => {
    try {
      const key = c.req.param('key');
      const { value } = await c.req.json();

      if (value === undefined) {
        const response: ApiResponse = {
          success: false,
          error: 'value is required'
        };
        return c.json(response, 400);
      }

      const validation = configService.validate(key, String(value));
      if (!validation.valid) {
        const response: ApiResponse = {
          success: false,
          error: validation.error
        };
        return c.json(response, 400);
      }

      configService.set(key, value);

      const config = configService.getConfig();

      const response: ApiResponse<Partial<Config>> = {
        success: true,
        data: config
      };
      return c.json(response);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      };
      return c.json(response, 500);
    }
  });

  app.get('/list', async (c) => {
    try {
      const allConfig = configService.getAll();
      
      const safeConfig: Record<string, string> = {};
      for (const [key, value] of Object.entries(allConfig)) {
        if (key.includes('token')) {
          safeConfig[key] = '***';
        } else {
          safeConfig[key] = value;
        }
      }

      const response: ApiResponse<Record<string, string>> = {
        success: true,
        data: safeConfig
      };
      return c.json(response);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      };
      return c.json(response, 500);
    }
  });

  return app;
}
