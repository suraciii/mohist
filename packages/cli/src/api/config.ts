import { Router, Request, Response } from 'express';
import { ApiResponse, Config } from '../types';
import { ConfigService } from '../services';

export function createConfigRoutes(configService: ConfigService): Router {
  const router = Router();

  router.get('/', (_req: Request, res: Response): void => {
    try {
      const config = configService.getConfig();

      const response: ApiResponse<Partial<Config>> = {
        success: true,
        data: config
      };
      res.json(response);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      };
      res.status(500).json(response);
    }
  });

  router.put('/:key', (req: Request, res: Response): void => {
    try {
      const key = req.params.key;
      const { value } = req.body;

      if (value === undefined) {
        const response: ApiResponse = {
          success: false,
          error: 'value is required'
        };
        res.status(400).json(response);
        return;
      }

      const validation = configService.validate(key, String(value));
      if (!validation.valid) {
        const response: ApiResponse = {
          success: false,
          error: validation.error
        };
        res.status(400).json(response);
        return;
      }

      configService.set(key, value);

      const response: ApiResponse = {
        success: true,
        data: { key, value }
      };
      res.json(response);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      };
      res.status(500).json(response);
    }
  });

  router.get('/list', (_req: Request, res: Response): void => {
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
      res.json(response);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      };
      res.status(500).json(response);
    }
  });

  return router;
}
